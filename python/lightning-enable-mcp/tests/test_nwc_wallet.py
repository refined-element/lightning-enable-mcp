"""
Tests for NWC Wallet
"""

import asyncio
import base64
import hashlib
import hmac
import os
import struct
from unittest.mock import patch

import pytest
from lightning_enable_mcp.nwc_wallet import (
    NWCConfig,
    NWCWallet,
    NWCError,
    _calc_padded_len,
    _decrypt_content,
    _decrypt_nip04,
    _decrypt_nip44,
    _encrypt_nip44,
    _hkdf_expand,
)


# ---------- Deterministic test keys (no secp256k1 needed) ----------
# We use a fixed 32-byte "shared_x" value to bypass ECDH in tests.
# This lets us test all the symmetric crypto without the secp256k1 C library.
_FIXED_SHARED_X = bytes.fromhex(
    "4b6a0c7e8f9d2e1a3c5b7d9f0e2a4c6b8d0f1e3a5c7b9d1f0e2a4c6b8d0f1e"
)
_DUMMY_SECRET_KEY = b"\x01" * 32
_DUMMY_PUBKEY_HEX = "02" + "ab" * 32  # Won't be used for actual ECDH


def _nip04_encrypt_with_shared_x(plaintext: str, shared_x: bytes) -> str:
    """
    Encrypt using NIP-04 with a pre-computed shared_x (bypasses ECDH).
    """
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
    from cryptography.hazmat.backends import default_backend

    shared_secret = hashlib.sha256(shared_x).digest()
    iv = os.urandom(16)

    plaintext_bytes = plaintext.encode("utf-8")
    padding_len = 16 - (len(plaintext_bytes) % 16)
    padded = plaintext_bytes + bytes([padding_len] * padding_len)

    cipher = Cipher(algorithms.AES(shared_secret), modes.CBC(iv), backend=default_backend())
    encryptor = cipher.encryptor()
    ciphertext = encryptor.update(padded) + encryptor.finalize()

    return f"{base64.b64encode(ciphertext).decode()}?iv={base64.b64encode(iv).decode()}"


def _nip44_encrypt_with_shared_x(plaintext: str, shared_x: bytes) -> str:
    """
    Encrypt using NIP-44 v2 with a pre-computed shared_x (bypasses ECDH).
    """
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms
    from cryptography.hazmat.backends import default_backend

    conversation_key = hmac.new(b"nip44-v2", shared_x, hashlib.sha256).digest()
    nonce = os.urandom(32)
    message_keys = _hkdf_expand(conversation_key, nonce, 76)

    chacha_key = message_keys[0:32]
    chacha_nonce = message_keys[32:44]
    hmac_key = message_keys[44:76]

    plaintext_bytes = plaintext.encode("utf-8")
    padded_plaintext = struct.pack(">H", len(plaintext_bytes)) + plaintext_bytes

    chacha20_nonce = b"\x00\x00\x00\x00" + chacha_nonce
    cipher = Cipher(
        algorithms.ChaCha20(chacha_key, chacha20_nonce),
        mode=None,
        backend=default_backend(),
    )
    encryptor = cipher.encryptor()
    ciphertext = encryptor.update(padded_plaintext) + encryptor.finalize()

    mac = hmac.new(hmac_key, nonce + ciphertext, hashlib.sha256).digest()
    payload = bytes([0x02]) + nonce + ciphertext + mac

    return base64.b64encode(payload).decode()


class TestNWCConfig:
    """Tests for NWC configuration parsing."""

    def test_parse_valid_uri(self):
        """Test parsing a valid NWC URI."""
        uri = (
            "nostr+walletconnect://b889ff5b1513b641e2a139f661a661364979c5beee91842f8f0ef42ab558e9d4"
            "?relay=wss://relay.getalby.com/v1"
            "&secret=71a8c14c1407c113601079c4302dab36460f0ccd0ad506f1f2dc73b5100e4f3c"
        )

        config = NWCConfig.from_uri(uri)

        assert config.wallet_pubkey == (
            "b889ff5b1513b641e2a139f661a661364979c5beee91842f8f0ef42ab558e9d4"
        )
        assert config.relay_url == "wss://relay.getalby.com/v1"
        assert config.secret == (
            "71a8c14c1407c113601079c4302dab36460f0ccd0ad506f1f2dc73b5100e4f3c"
        )

    def test_parse_invalid_scheme(self):
        """Test parsing URI with invalid scheme raises error."""
        uri = "http://example.com"

        with pytest.raises(ValueError, match="Invalid NWC URI"):
            NWCConfig.from_uri(uri)

    def test_parse_missing_pubkey(self):
        """Test parsing URI without pubkey raises error."""
        uri = "nostr+walletconnect://?relay=wss://relay.example.com&secret=abc"

        with pytest.raises(ValueError, match="missing wallet pubkey"):
            NWCConfig.from_uri(uri)

    def test_parse_missing_relay(self):
        """Test parsing URI without relay raises error."""
        uri = (
            "nostr+walletconnect://abc123"
            "?secret=71a8c14c1407c113601079c4302dab36460f0ccd0ad506f1f2dc73b5100e4f3c"
        )

        with pytest.raises(ValueError, match="missing relay"):
            NWCConfig.from_uri(uri)

    def test_parse_missing_secret(self):
        """Test parsing URI without secret raises error."""
        uri = "nostr+walletconnect://abc123?relay=wss://relay.example.com"

        with pytest.raises(ValueError, match="missing secret"):
            NWCConfig.from_uri(uri)


class TestNWCWallet:
    """Tests for NWCWallet."""

    @patch("lightning_enable_mcp.nwc_wallet._get_pubkey", return_value="aa" * 32)
    def test_init_parses_uri(self, mock_pubkey):
        """Test wallet initializes from URI."""
        uri = (
            "nostr+walletconnect://b889ff5b1513b641e2a139f661a661364979c5beee91842f8f0ef42ab558e9d4"
            "?relay=wss://relay.getalby.com/v1"
            "&secret=71a8c14c1407c113601079c4302dab36460f0ccd0ad506f1f2dc73b5100e4f3c"
        )

        wallet = NWCWallet(uri)

        assert wallet.config.wallet_pubkey == (
            "b889ff5b1513b641e2a139f661a661364979c5beee91842f8f0ef42ab558e9d4"
        )
        assert wallet.config.relay_url == "wss://relay.getalby.com/v1"

    def test_init_invalid_uri(self):
        """Test wallet raises on invalid URI."""
        with pytest.raises(ValueError):
            NWCWallet("invalid-uri")


class TestNIP04Decryption:
    """Tests for NIP-04 decryption (legacy format)."""

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_roundtrip_encrypt_decrypt(self, mock_shared_x):
        """Test that NIP-04 encrypt then decrypt returns original plaintext."""
        plaintext = '{"method":"pay_invoice","params":{"invoice":"lnbc1..."}}'

        encrypted = _nip04_encrypt_with_shared_x(plaintext, _FIXED_SHARED_X)
        assert "?iv=" in encrypted

        decrypted = _decrypt_nip04(encrypted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        assert decrypted == plaintext

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_decrypt_content_dispatches_nip04(self, mock_shared_x):
        """Test that _decrypt_content detects NIP-04 format and decrypts."""
        plaintext = "hello NWC"

        encrypted = _nip04_encrypt_with_shared_x(plaintext, _FIXED_SHARED_X)
        assert "?iv=" in encrypted

        decrypted = _decrypt_content(encrypted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        assert decrypted == plaintext

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_nip04_unicode(self, mock_shared_x):
        """Test NIP-04 roundtrip with unicode content."""
        plaintext = '{"result":{"balance":100000},"emoji":"\\u26a1"}'

        encrypted = _nip04_encrypt_with_shared_x(plaintext, _FIXED_SHARED_X)
        decrypted = _decrypt_nip04(encrypted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        assert decrypted == plaintext

    def test_nip04_invalid_format_raises(self):
        """Test that NIP-04 decrypt raises on missing ?iv= separator."""
        with pytest.raises(ValueError, match="Invalid NIP-04"):
            _decrypt_nip04("justbase64withnoiv", _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)


class TestNIP44Decryption:
    """Tests for NIP-44 v2 decryption (Alby Hub format)."""

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_roundtrip_encrypt_decrypt(self, mock_shared_x):
        """Test that NIP-44 encrypt then decrypt returns original plaintext."""
        plaintext = '{"result_type":"pay_invoice","result":{"preimage":"abc123"}}'

        encrypted = _nip44_encrypt_with_shared_x(plaintext, _FIXED_SHARED_X)
        assert "?iv=" not in encrypted

        decrypted = _decrypt_nip44(encrypted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        assert decrypted == plaintext

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_decrypt_content_dispatches_nip44(self, mock_shared_x):
        """Test that _decrypt_content detects NIP-44 format and decrypts."""
        plaintext = '{"result_type":"get_balance","result":{"balance":50000}}'

        encrypted = _nip44_encrypt_with_shared_x(plaintext, _FIXED_SHARED_X)
        assert "?iv=" not in encrypted

        decrypted = _decrypt_content(encrypted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        assert decrypted == plaintext

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_nip44_unicode(self, mock_shared_x):
        """Test NIP-44 roundtrip with unicode and longer content."""
        plaintext = '{"description":"Pay for API access \\u26a1","amount":1000}'

        encrypted = _nip44_encrypt_with_shared_x(plaintext, _FIXED_SHARED_X)
        decrypted = _decrypt_nip44(encrypted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        assert decrypted == plaintext

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_nip44_invalid_version_byte_raises(self, mock_shared_x):
        """Test that NIP-44 decrypt raises on wrong version byte (0x01)."""
        encrypted = _nip44_encrypt_with_shared_x("test", _FIXED_SHARED_X)
        payload = bytearray(base64.b64decode(encrypted))

        payload[0] = 0x01
        corrupted = base64.b64encode(bytes(payload)).decode()

        with pytest.raises(ValueError, match="Unsupported NIP-44 version"):
            _decrypt_nip44(corrupted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_nip44_invalid_version_byte_v3(self, mock_shared_x):
        """Test that a hypothetical v3 version byte is rejected."""
        encrypted = _nip44_encrypt_with_shared_x("test", _FIXED_SHARED_X)
        payload = bytearray(base64.b64decode(encrypted))
        payload[0] = 0x03
        corrupted = base64.b64encode(bytes(payload)).decode()

        with pytest.raises(ValueError, match="Unsupported NIP-44 version.*0x03"):
            _decrypt_nip44(corrupted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_nip44_tampered_ciphertext_fails_hmac(self, mock_shared_x):
        """Test that tampered ciphertext fails HMAC verification."""
        encrypted = _nip44_encrypt_with_shared_x("secret data", _FIXED_SHARED_X)
        payload = bytearray(base64.b64decode(encrypted))

        # Tamper with a ciphertext byte (after version[1] + nonce[32])
        if len(payload) > 40:
            payload[35] ^= 0xFF
        corrupted = base64.b64encode(bytes(payload)).decode()

        with pytest.raises(ValueError, match="HMAC verification failed"):
            _decrypt_nip44(corrupted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_nip44_tampered_mac_fails_hmac(self, mock_shared_x):
        """Test that tampered MAC fails HMAC verification."""
        encrypted = _nip44_encrypt_with_shared_x("secret data", _FIXED_SHARED_X)
        payload = bytearray(base64.b64decode(encrypted))

        payload[-1] ^= 0xFF
        corrupted = base64.b64encode(bytes(payload)).decode()

        with pytest.raises(ValueError, match="HMAC verification failed"):
            _decrypt_nip44(corrupted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_nip44_long_plaintext(self, mock_shared_x):
        """Test NIP-44 with a longer message to exercise the length-prefix logic."""
        plaintext = "A" * 500
        encrypted = _nip44_encrypt_with_shared_x(plaintext, _FIXED_SHARED_X)
        decrypted = _decrypt_nip44(encrypted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        assert decrypted == plaintext

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_nip44_empty_plaintext(self, mock_shared_x):
        """Test NIP-44 with an empty string."""
        plaintext = ""
        encrypted = _nip44_encrypt_with_shared_x(plaintext, _FIXED_SHARED_X)
        decrypted = _decrypt_nip44(encrypted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        assert decrypted == plaintext


class TestDecryptContentAutoDetection:
    """Tests for the auto-detection logic in _decrypt_content."""

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_nip04_detected_by_iv_marker(self, mock_shared_x):
        """Test that content with ?iv= is routed to NIP-04."""
        plaintext = "nip04 test"
        encrypted = _nip04_encrypt_with_shared_x(plaintext, _FIXED_SHARED_X)
        assert "?iv=" in encrypted
        assert _decrypt_content(encrypted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX) == plaintext

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_nip44_detected_by_absence_of_iv_marker(self, mock_shared_x):
        """Test that content without ?iv= is routed to NIP-44."""
        plaintext = "nip44 test"
        encrypted = _nip44_encrypt_with_shared_x(plaintext, _FIXED_SHARED_X)
        assert "?iv=" not in encrypted
        assert _decrypt_content(encrypted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX) == plaintext

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_both_formats_same_plaintext(self, mock_shared_x):
        """Test that both NIP-04 and NIP-44 decrypt to the same plaintext."""
        plaintext = '{"method":"get_balance","params":{}}'

        nip04_enc = _nip04_encrypt_with_shared_x(plaintext, _FIXED_SHARED_X)
        nip44_enc = _nip44_encrypt_with_shared_x(plaintext, _FIXED_SHARED_X)

        assert _decrypt_content(nip04_enc, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX) == plaintext
        assert _decrypt_content(nip44_enc, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX) == plaintext


class TestNIP44Encryption:
    """Tests for NIP-44 v2 encryption (outgoing NWC requests)."""

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_encrypt_decrypt_roundtrip(self, mock_shared_x):
        """Test that NIP-44 encrypt then decrypt returns original plaintext."""
        plaintext = '{"method":"pay_invoice","params":{"invoice":"lnbc1..."}}'

        encrypted = _encrypt_nip44(plaintext, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        assert "?iv=" not in encrypted  # NIP-44, not NIP-04

        decrypted = _decrypt_nip44(encrypted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        assert decrypted == plaintext

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_encrypt_produces_version_02(self, mock_shared_x):
        """Test that encrypted payload starts with version byte 0x02."""
        encrypted = _encrypt_nip44("test", _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        payload = base64.b64decode(encrypted)
        assert payload[0] == 0x02

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_encrypt_different_nonce_each_time(self, mock_shared_x):
        """Test that each encryption produces different output (random nonce)."""
        enc1 = _encrypt_nip44("same message", _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        enc2 = _encrypt_nip44("same message", _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        assert enc1 != enc2

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_encrypt_decrypt_content_autodetect(self, mock_shared_x):
        """Test that _decrypt_content auto-detects NIP-44 from _encrypt_nip44."""
        plaintext = '{"method":"get_balance","params":{}}'
        encrypted = _encrypt_nip44(plaintext, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        decrypted = _decrypt_content(encrypted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        assert decrypted == plaintext

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_encrypt_large_payload(self, mock_shared_x):
        """Test NIP-44 encryption with a large message."""
        plaintext = "A" * 5000
        encrypted = _encrypt_nip44(plaintext, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        decrypted = _decrypt_nip44(encrypted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        assert decrypted == plaintext

    @patch(
        "lightning_enable_mcp.nwc_wallet._compute_shared_x",
        return_value=_FIXED_SHARED_X,
    )
    def test_encrypt_unicode(self, mock_shared_x):
        """Test NIP-44 encryption with unicode content."""
        plaintext = '{"description":"Pay for API access ⚡","amount":1000}'
        encrypted = _encrypt_nip44(plaintext, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        decrypted = _decrypt_nip44(encrypted, _DUMMY_SECRET_KEY, _DUMMY_PUBKEY_HEX)
        assert decrypted == plaintext


class TestCalcPaddedLen:
    """Tests for NIP-44 padding length calculation."""

    @pytest.mark.parametrize(
        "input_len,expected",
        [
            (1, 32),
            (16, 32),
            (32, 32),
            (33, 64),
            (64, 64),
            (65, 96),
            (100, 128),
            (256, 256),
            (300, 320),
        ],
    )
    def test_padded_len_values(self, input_len, expected):
        assert _calc_padded_len(input_len) == expected

    def test_padded_len_zero_raises(self):
        with pytest.raises(ValueError):
            _calc_padded_len(0)

    def test_padded_len_negative_raises(self):
        with pytest.raises(ValueError):
            _calc_padded_len(-1)


class TestHKDFExpand:
    """Tests for the HKDF-Expand implementation."""

    def test_output_length(self):
        """Test that HKDF-Expand produces correct output length."""
        prk = os.urandom(32)
        info = os.urandom(16)

        assert len(_hkdf_expand(prk, info, 32)) == 32
        assert len(_hkdf_expand(prk, info, 76)) == 76
        assert len(_hkdf_expand(prk, info, 64)) == 64
        assert len(_hkdf_expand(prk, info, 1)) == 1

    def test_deterministic(self):
        """Test that HKDF-Expand is deterministic with same inputs."""
        prk = b"\x01" * 32
        info = b"\x02" * 16

        result1 = _hkdf_expand(prk, info, 76)
        result2 = _hkdf_expand(prk, info, 76)
        assert result1 == result2

    def test_different_info_gives_different_output(self):
        """Test that different info values produce different keys."""
        prk = b"\x01" * 32

        result1 = _hkdf_expand(prk, b"\x02" * 16, 76)
        result2 = _hkdf_expand(prk, b"\x03" * 16, 76)
        assert result1 != result2

    def test_matches_rfc5869_structure(self):
        """Test HKDF-Expand follows RFC 5869 structure: T(1) = HMAC(PRK, info || 0x01)."""
        prk = b"\x0b" * 32
        info = b"\xf0\xf1\xf2\xf3"

        # Manual computation of first 32 bytes (T1)
        t1 = hmac.new(prk, info + b"\x01", hashlib.sha256).digest()
        result = _hkdf_expand(prk, info, 32)
        assert result == t1


# ---------- Encryption-default + NWC_ENCRYPTION env-var override (PR fix) ----------

_TEST_NWC_URI = (
    "nostr+walletconnect://"
    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    "?relay=wss://relay.example.com"
    "&secret=fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"
)


class TestNWCEncryptionDefault:
    """
    Default outbound encryption mode is ``auto`` — fetches the wallet's NIP-47
    INFO event (kind 13194) on first request, picks the strongest advertised
    scheme, caches the choice for the wallet instance's lifetime. Falls back
    to ``nip04`` when no INFO event is available within the timeout — older
    wallets that don't publish 13194 still work because NIP-04 is the original
    NIP-47 default.

    Class name is preserved from v1.12.5 (when the default was the literal
    ``nip04``) for git-history continuity; the contract has shifted to
    ``auto``-with-``nip04``-fallback per v1.12.6.
    """

    def test_nwcconfig_default_encryption_is_auto(self):
        # Default outbound mode is "auto" — fetches NIP-47 INFO event and picks
        # the strongest advertised scheme. v1.12.5 used "nip04"; v1.12.6 promotes
        # to "auto" so the wallet works zero-config across all spec-compliant
        # implementations.
        from lightning_enable_mcp.nwc_wallet import NWC_ENCRYPTION_DEFAULT, NWCConfig

        config = NWCConfig.from_uri(_TEST_NWC_URI)
        assert config.encryption == "auto"
        assert NWC_ENCRYPTION_DEFAULT == "auto"

    @patch("lightning_enable_mcp.nwc_wallet._get_pubkey", return_value="aa" * 32)
    def test_nwcwallet_constructed_without_env_var_uses_default(
        self, _mock_pubkey, monkeypatch
    ):
        monkeypatch.delenv("NWC_ENCRYPTION", raising=False)
        wallet = NWCWallet(_TEST_NWC_URI)
        assert wallet.config.encryption == "auto"

    @patch("lightning_enable_mcp.nwc_wallet._get_pubkey", return_value="aa" * 32)
    def test_nwcwallet_constructed_with_nip44_env_var_honors_override(
        self, _mock_pubkey, monkeypatch
    ):
        monkeypatch.setenv("NWC_ENCRYPTION", "nip44_v2")
        wallet = NWCWallet(_TEST_NWC_URI)
        assert wallet.config.encryption == "nip44_v2"

    @patch("lightning_enable_mcp.nwc_wallet._get_pubkey", return_value="aa" * 32)
    def test_nwcwallet_constructed_with_uppercase_nip44_env_var_normalized(
        self, _mock_pubkey, monkeypatch
    ):
        # Normalization defends against env-var copy-paste with stray casing.
        monkeypatch.setenv("NWC_ENCRYPTION", "NIP44_V2")
        wallet = NWCWallet(_TEST_NWC_URI)
        assert wallet.config.encryption == "nip44_v2"

    @patch("lightning_enable_mcp.nwc_wallet._get_pubkey", return_value="aa" * 32)
    def test_nwcwallet_constructed_with_invalid_encryption_falls_back_to_default(
        self, _mock_pubkey, monkeypatch, caplog
    ):
        # A typo must not silently disable the wallet — fall back to the documented
        # default and log a warning. Regression guard for "user fat-fingers env var,
        # wallet stops working with no clear cause".
        monkeypatch.setenv("NWC_ENCRYPTION", "nip-something-bogus")
        with caplog.at_level("WARNING"):
            wallet = NWCWallet(_TEST_NWC_URI)
        assert wallet.config.encryption == "auto"
        # Warning must mention the rejected value AND the allowed set.
        assert any(
            "nip-something-bogus" in rec.getMessage() for rec in caplog.records
        )

    @pytest.mark.parametrize(
        "tag_value, expected",
        [
            ("nip04 nip44_v2", "nip44_v2"),
            ("nip44_v2 nip04", "nip44_v2"),
            ("nip04", "nip04"),
            ("nip44_v2", "nip44_v2"),
            ("nip04,nip44_v2", "nip44_v2"),  # tolerate comma separator
            ("NIP04 NIP44_V2", "nip44_v2"),  # case-insensitive
            ("nip04  nip44_v2", "nip44_v2"),  # double spaces
            ("", "nip04"),  # empty → fallback
            (None, "nip04"),  # null → fallback
            ("nip99_alpha", "nip04"),  # unknown → fallback
        ],
    )
    def test_pick_encryption_from_info_tag(self, tag_value, expected):
        # Pure parsing test for the picker. Mirrors the .NET theory test —
        # both ports must agree on the contract since they consume the same
        # NIP-47 INFO event format.
        from lightning_enable_mcp.nwc_wallet import _pick_encryption_from_info_tag

        assert _pick_encryption_from_info_tag(tag_value) == expected

    @pytest.mark.asyncio
    @patch("lightning_enable_mcp.nwc_wallet._get_pubkey", return_value="aa" * 32)
    async def test_resolve_auto_encryption_unreachable_relay_falls_back_to_nip04(
        self, _mock_pubkey, monkeypatch
    ):
        # INFO-event fetch must NEVER throw on operational failures — a missing
        # or unreachable relay falls back to nip04 so a real request can still
        # go out. Without this guarantee, every call would fail with "auto" mode
        # whenever the relay was flaky.
        # Force "auto" mode regardless of what the dev/CI env has pinned —
        # an env-var-pinned NWC_ENCRYPTION would skip the resolver entirely
        # and break the test's intent.
        monkeypatch.delenv("NWC_ENCRYPTION", raising=False)
        unreachable_uri = (
            "nostr+walletconnect://"
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            "?relay=ws://127.0.0.1:1"
            "&secret=fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"
        )
        wallet = NWCWallet(unreachable_uri)
        resolved = await wallet._resolve_auto_encryption()
        assert resolved == "nip04", (
            "INFO-event fetch must fall back to nip04 on operational failure, not throw"
        )

    @pytest.mark.asyncio
    @patch("lightning_enable_mcp.nwc_wallet._get_pubkey", return_value="aa" * 32)
    async def test_resolve_auto_encryption_caches_result(
        self, _mock_pubkey, monkeypatch
    ):
        # Second call must be served from the cache without reaching the relay.
        # Asserted by patching the fetcher and counting invocations rather than
        # using wall-clock timing — busy CI agents can violate timing thresholds
        # even when the cache is working correctly.
        monkeypatch.delenv("NWC_ENCRYPTION", raising=False)
        wallet = NWCWallet(_TEST_NWC_URI)

        # Patch the fetcher on this wallet instance to a counting stub. We don't
        # want to actually open a WebSocket — the resolver's contract is "fetch
        # once, cache, return cached on subsequent calls".
        call_count = 0

        async def _stub_fetch():
            nonlocal call_count
            call_count += 1
            return "nip04"

        monkeypatch.setattr(wallet, "_fetch_encryption_from_info_event", _stub_fetch)

        first = await wallet._resolve_auto_encryption()
        assert call_count == 1, "first call must invoke the fetcher exactly once"

        second = await wallet._resolve_auto_encryption()
        assert call_count == 1, (
            "second call must hit the cache — fetcher count must NOT increment"
        )
        assert second == first, "cached result must equal first resolution"

    @patch("lightning_enable_mcp.nwc_wallet._get_pubkey", return_value="aa" * 32)
    def test_explicit_env_var_pin_does_not_resolve_to_auto(
        self, _mock_pubkey, monkeypatch
    ):
        # Explicit env-var pinning skips auto-detect entirely. The config holds
        # the literal scheme, not "auto", so _send_request's fast path bypasses
        # the INFO-event fetch.
        monkeypatch.setenv("NWC_ENCRYPTION", "nip04")
        wallet = NWCWallet(_TEST_NWC_URI)
        assert wallet.config.encryption == "nip04", (
            "explicit env-var pin must persist literally, not get rewritten to auto"
        )

    # ---- _verify_nostr_event_signature tests (mirror the .NET coverage) ----

    @staticmethod
    def _build_signed_info_event(privkey_bytes, pubkey_hex, encryption_tag_value):
        """Build a kind 13194 event with a real BIP340 signature."""
        import time as time_mod
        from lightning_enable_mcp.nwc_wallet import _compute_event_id, _sign_event

        event = {
            "kind": 13194,
            "pubkey": pubkey_hex,
            "created_at": int(time_mod.time()),
            "tags": [["encryption", encryption_tag_value]],
            "content": "Wallet capabilities: pay_invoice get_balance",
        }
        event["id"] = _compute_event_id(event)
        event["sig"] = _sign_event(event, privkey_bytes)
        return event

    @staticmethod
    def _new_keypair():
        pytest.importorskip("secp256k1")
        privkey_bytes = b"\x01" + b"\x42" * 31  # deterministic but valid scalar
        pk = secp.PrivateKey(privkey_bytes)
        # x-only pubkey (drop the leading 02/03 byte from compressed form)
        pubkey_hex = pk.pubkey.serialize()[1:33].hex()
        return privkey_bytes, pubkey_hex

    def test_verify_nostr_event_signature_valid_event_returns_true(self):
        # Sign-then-verify baseline. Establishes that genuine events pass.
        pytest.importorskip("secp256k1")
        from lightning_enable_mcp.nwc_wallet import _verify_nostr_event_signature

        privkey, pubkey_hex = self._new_keypair()
        event = self._build_signed_info_event(privkey, pubkey_hex, "nip04 nip44_v2")
        assert _verify_nostr_event_signature(event) is True, (
            "a correctly signed kind 13194 event must verify"
        )

    def test_verify_nostr_event_signature_tampered_encryption_tag_returns_false(
        self,
    ):
        # The core security guarantee: a relay-injected event with a forged
        # encryption tag (but otherwise looking like the wallet's INFO event)
        # must fail verification. Tamper after signing — the recomputed event
        # id won't match the claimed id.
        pytest.importorskip("secp256k1")
        from lightning_enable_mcp.nwc_wallet import _verify_nostr_event_signature

        privkey, pubkey_hex = self._new_keypair()
        event = self._build_signed_info_event(privkey, pubkey_hex, "nip04 nip44_v2")
        # Mutate the encryption tag to force a downgrade
        for tag in event["tags"]:
            if tag[0] == "encryption":
                tag[1] = "nip04"
                break
        assert _verify_nostr_event_signature(event) is False, (
            "tampering with the encryption tag must invalidate the signature"
        )

    def test_verify_nostr_event_signature_wrong_signature_returns_false(self):
        # Substitute a signature from a different keypair — pubkey unchanged
        # but sig signed by attacker's key. Must fail.
        pytest.importorskip("secp256k1")
        from lightning_enable_mcp.nwc_wallet import (
            _sign_event,
            _verify_nostr_event_signature,
        )

        alice_priv, alice_pubkey_hex = self._new_keypair()
        # Different keypair for Bob
        bob_priv = b"\x02" + b"\x37" * 31
        secp.PrivateKey(bob_priv)  # validate

        event = self._build_signed_info_event(
            alice_priv, alice_pubkey_hex, "nip04 nip44_v2"
        )
        # Replace sig with Bob's signature over the same event id
        event["sig"] = _sign_event(event, bob_priv)
        assert _verify_nostr_event_signature(event) is False, (
            "a signature from the wrong key must not verify"
        )

    def test_verify_nostr_event_signature_malformed_fields_returns_false(self):
        # Defensive checks — malformed/missing fields must not throw.
        from lightning_enable_mcp.nwc_wallet import _verify_nostr_event_signature

        assert _verify_nostr_event_signature({}) is False, "empty event"
        assert (
            _verify_nostr_event_signature(
                {
                    "id": "not-hex",
                    "pubkey": "also-not-hex",
                    "sig": "neither",
                    "created_at": 1,
                    "kind": 13194,
                    "tags": [],
                    "content": "",
                }
            )
            is False
        ), "malformed hex must not throw"

    def test_encrypt_content_returns_string_not_none(self):
        # Regression test for the dead-try fall-through bug: ``_encrypt_content``
        # used to return None when pycryptodome (``Crypto``) was importable, because
        # the only ``return`` lived inside the ``except ImportError`` fallback.
        # The post-fix invariant: the function always returns a NIP-04-shaped
        # string. Skipped when ``secp256k1`` (a C lib) isn't installed locally;
        # CI has it.
        pytest.importorskip("secp256k1")
        from lightning_enable_mcp.nwc_wallet import _encrypt_content

        secret = bytes.fromhex(
            "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"
        )
        # Bob's pubkey: secp256k1 generator point x-coordinate (a known-valid
        # x-only pubkey we can hardcode without re-deriving in the test).
        recipient_pubkey = (
            "79be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"
        )

        result = _encrypt_content("hello", secret, recipient_pubkey)
        assert isinstance(result, str), (
            "must always return a string — None means the dead-try fall-through "
            "bug regressed"
        )
        assert "?iv=" in result, "NIP-04 ciphertext must use the ?iv= separator"
        ciphertext_b64, iv_b64 = result.split("?iv=", 1)
        assert ciphertext_b64
        assert iv_b64

    @pytest.mark.asyncio
    @patch("lightning_enable_mcp.nwc_wallet._get_pubkey", return_value="aa" * 32)
    async def test_connect_propagates_asyncio_cancelled_error(
        self, _mock_pubkey, monkeypatch
    ):
        # Regression test for the broad-except wrapping CancelledError. Caller-driven
        # cancellation (e.g. MCP request cancellation, server shutdown) must
        # propagate as ``asyncio.CancelledError`` — not get wrapped as a
        # ``connect_failed`` ``NWCConnectionError``, which would break standard
        # task-cancellation semantics.
        from lightning_enable_mcp.nwc_wallet import NWCWallet

        wallet = NWCWallet(_TEST_NWC_URI)

        async def _cancelling_connect(*_args, **_kwargs):
            raise asyncio.CancelledError()

        # Patch websockets.connect to raise CancelledError mid-connect. If the
        # ``except asyncio.CancelledError: raise`` re-raise is missing, the broad
        # except will wrap it and we'll see NWCConnectionError instead.
        monkeypatch.setattr(
            "lightning_enable_mcp.nwc_wallet.websockets.connect", _cancelling_connect
        )
        with pytest.raises(asyncio.CancelledError):
            await wallet.connect()

    @pytest.mark.asyncio
    @patch("lightning_enable_mcp.nwc_wallet._get_pubkey", return_value="aa" * 32)
    async def test_send_request_unreachable_relay_raises_with_specific_kind(
        self, _mock_pubkey, monkeypatch
    ):
        # Regression test for the old "Failed to connect to relay" string that
        # collapsed connect failure with no-response/encryption-mismatch into one
        # opaque message. The new contract: connect failure includes the kind
        # ``connect_failed`` so users (and the .NET parity tests) can distinguish
        # it from the no-response timeout.
        monkeypatch.delenv("NWC_ENCRYPTION", raising=False)
        from lightning_enable_mcp.nwc_wallet import (
            NWC_FAIL_CONNECT,
            NWCConnectionError,
            NWCWallet,
        )

        # Port 1 is reserved/unbindable, so connect MUST fail quickly.
        unreachable_uri = (
            "nostr+walletconnect://"
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            "?relay=ws://127.0.0.1:1"
            "&secret=fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"
        )
        wallet = NWCWallet(unreachable_uri)
        with pytest.raises(NWCConnectionError) as excinfo:
            await wallet.connect()
        assert NWC_FAIL_CONNECT in str(excinfo.value)
        assert "127.0.0.1:1" in str(excinfo.value)
