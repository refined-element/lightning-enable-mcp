"""
Nostr Wallet Connect (NWC) Client

Implements NIP-47 for Lightning wallet operations via Nostr.
"""

import asyncio
import hashlib
import json
import logging
import os
import secrets
import time
from dataclasses import dataclass, replace
from typing import Any
from urllib.parse import parse_qs, urlparse

import websockets
from websockets.client import WebSocketClientProtocol

logger = logging.getLogger("lightning-enable-mcp.nwc")


class NWCError(Exception):
    """Exception for NWC-related errors."""

    pass


class NWCConnectionError(NWCError):
    """Exception for connection failures."""

    pass


class NWCPaymentError(NWCError):
    """Exception for payment failures."""

    pass


# Outbound NIP-47 encryption schemes. Default is ``auto`` — the wallet's NIP-47
# INFO event (kind 13194) is fetched on first request and the strongest advertised
# scheme is picked; the choice is cached for the wallet instance's lifetime. Falls
# back to NIP-04 when no INFO event is available, since NIP-04 is the original
# NIP-47 default and what every spec-pre-13194 wallet expects. Operators can pin
# to a specific scheme via the ``NWC_ENCRYPTION`` env var.
NWC_ENCRYPTION_NIP04 = "nip04"
NWC_ENCRYPTION_NIP44_V2 = "nip44_v2"
NWC_ENCRYPTION_AUTO = "auto"
NWC_ENCRYPTION_DEFAULT = NWC_ENCRYPTION_AUTO
_VALID_NWC_ENCRYPTIONS = {
    NWC_ENCRYPTION_NIP04,
    NWC_ENCRYPTION_NIP44_V2,
    NWC_ENCRYPTION_AUTO,
}

# How long to wait for the NIP-47 INFO event before falling back to NIP-04.
# Kept short so a missing or stale relay never delays a real request by more
# than a few seconds. Module-level so tests can monkeypatch it.
NWC_AUTO_RESOLVE_TIMEOUT_SECONDS = 3.0


def _pick_encryption_from_info_tag(encryption_tag_value: str | None) -> str:
    """
    Pick the strongest scheme from a NIP-47 INFO event's ``encryption`` tag value.

    The spec defines the tag value as a space-separated list of supported
    schemes (e.g. ``"nip04 nip44_v2"``). Prefers ``nip44_v2`` when listed
    (more secure); otherwise picks ``nip04``; falls back to ``nip04`` when
    the tag is empty/missing/unknown so spec-pre-13194 wallets still work.

    Pulled out as a module-level function so it can be unit-tested without
    spinning up a relay.
    """
    if not encryption_tag_value:
        return NWC_ENCRYPTION_NIP04

    schemes = {
        s.strip().lower()
        for s in encryption_tag_value.replace(",", " ").replace("\t", " ").split(" ")
        if s.strip()
    }

    if NWC_ENCRYPTION_NIP44_V2 in schemes:
        return NWC_ENCRYPTION_NIP44_V2
    return NWC_ENCRYPTION_NIP04


# Failure-kind constants for NWC request outcomes. Mirrored from the .NET
# implementation; tests assert these strings so the user-facing error contract
# stays aligned across language ports.
NWC_FAIL_CONNECT = "connect_failed"
NWC_FAIL_NO_RESPONSE = "no_response"
NWC_FAIL_CANCELLED = "cancelled"
NWC_FAIL_PROTOCOL = "protocol_error"
NWC_FAIL_UNKNOWN = "unknown"


@dataclass
class NWCConfig:
    """Parsed NWC connection configuration."""

    wallet_pubkey: str
    relay_url: str
    secret: str
    encryption: str = NWC_ENCRYPTION_DEFAULT

    @classmethod
    def from_uri(cls, uri: str) -> "NWCConfig":
        """
        Parse a nostr+walletconnect:// URI.

        Format: nostr+walletconnect://<pubkey>?relay=<relay_url>&secret=<secret>
        """
        if not uri.startswith("nostr+walletconnect://"):
            raise ValueError("Invalid NWC URI: must start with nostr+walletconnect://")

        # Parse the URI
        parsed = urlparse(uri)

        # Extract pubkey from netloc
        wallet_pubkey = parsed.netloc
        if not wallet_pubkey:
            raise ValueError("Invalid NWC URI: missing wallet pubkey")

        # Parse query parameters
        params = parse_qs(parsed.query)

        relay_url = params.get("relay", [None])[0]
        if not relay_url:
            raise ValueError("Invalid NWC URI: missing relay parameter")

        secret = params.get("secret", [None])[0]
        if not secret:
            raise ValueError("Invalid NWC URI: missing secret parameter")

        return cls(wallet_pubkey=wallet_pubkey, relay_url=relay_url, secret=secret)


def _sha256(data: bytes) -> bytes:
    """Compute SHA256 hash."""
    return hashlib.sha256(data).digest()


def _compute_event_id(event: dict[str, Any]) -> str:
    """Compute Nostr event ID."""
    serialized = json.dumps(
        [
            0,
            event["pubkey"],
            event["created_at"],
            event["kind"],
            event["tags"],
            event["content"],
        ],
        separators=(",", ":"),
        ensure_ascii=False,
    )
    return _sha256(serialized.encode()).hex()


def _verify_nostr_event_signature(event: dict[str, Any]) -> bool:
    """
    Verify a Nostr event's BIP340 Schnorr signature against its claimed pubkey.

    Returns True only if the recomputed event id matches the event's ``id`` field
    AND the ``sig`` field is a valid BIP340 signature of that id under the claimed
    ``pubkey``. Used by the INFO-event auto-detect path so a malicious relay can't
    forge an INFO event attributed to the wallet pubkey and force an encryption
    downgrade. Returns False on any malformed input (defensive).
    """
    try:
        id_hex = event.get("id")
        pubkey_hex = event.get("pubkey")
        sig_hex = event.get("sig")
        if (
            not id_hex
            or not pubkey_hex
            or not sig_hex
            or len(id_hex) != 64
            or len(pubkey_hex) != 64
            or len(sig_hex) != 128
        ):
            return False

        # Recompute the event id from the canonical serialisation. Tampering
        # with any field (including the encryption tag we're about to read)
        # produces a different id.
        recomputed_id = _compute_event_id(event)
        if recomputed_id.lower() != id_hex.lower():
            return False

        from secp256k1 import PublicKey

        pubkey_bytes = bytes.fromhex(pubkey_hex)
        sig_bytes = bytes.fromhex(sig_hex)
        id_bytes = bytes.fromhex(id_hex)

        # secp256k1.PublicKey takes a 33-byte compressed pubkey; x-only pubkey is
        # 32 bytes so we prefix 0x02 (assume even y, NIP-340 convention).
        compressed = b"\x02" + pubkey_bytes
        pubkey = PublicKey(compressed, raw=True)
        # schnorr_verify takes (msg, sig, raw=True). Returns True iff valid.
        return bool(pubkey.schnorr_verify(id_bytes, sig_bytes, None, raw=True))
    except Exception:
        # Defensive: any parsing/crypto exception → treat as unverified.
        return False


def _sign_event(event: dict[str, Any], secret_key: bytes) -> str:
    """
    Sign a Nostr event using secp256k1.

    Args:
        event: Event dict with id field set
        secret_key: 32-byte secret key

    Returns:
        Hex-encoded signature
    """
    try:
        from secp256k1 import PrivateKey

        privkey = PrivateKey(secret_key)
        event_id_bytes = bytes.fromhex(event["id"])
        sig = privkey.schnorr_sign(event_id_bytes, None, raw=True)
        return sig.hex()
    except ImportError:
        raise ImportError("secp256k1 library required for signing")


def _get_pubkey(secret_key: bytes) -> str:
    """
    Get public key from secret key.

    Args:
        secret_key: 32-byte secret key

    Returns:
        Hex-encoded public key (x-only, 32 bytes)
    """
    try:
        from secp256k1 import PrivateKey

        privkey = PrivateKey(secret_key)
        pubkey = privkey.pubkey.serialize()
        # Return x-only pubkey (skip the prefix byte)
        return pubkey[1:33].hex() if len(pubkey) == 33 else pubkey[:32].hex()
    except ImportError:
        raise ImportError("secp256k1 library required")


def _encrypt_content(plaintext: str, secret_key: bytes, recipient_pubkey: str) -> str:
    """
    Encrypt content using NIP-04 (ECDH + AES-256-CBC) with raw shared-X as key.

    Args:
        plaintext: Content to encrypt
        secret_key: Sender's 32-byte secret key
        recipient_pubkey: Recipient's hex-encoded public key

    Returns:
        Encrypted content in NIP-04 format: base64(ciphertext)?iv=base64(iv)

    Note on the NIP-04 key derivation: the spec text is genuinely ambiguous —
    some readings call for sha256(shared_x), others for raw shared_x. This
    implementation uses raw shared_x to match l402-ts/src/wallets/nwc.ts (the
    working JS NWC client confirmed against CoinOS in production) and the
    sister .NET port. An earlier version of this file derived the key as
    sha256(shared_x), which broke compatibility with CoinOS — symptom was a
    silent 30s NWC timeout (request ciphertext unreadable by the wallet).
    Don't flip back to sha256 without empirically verifying the wallets we
    care about.
    """
    # The previous implementation had a try/except ImportError block that
    # only returned a value inside the except branch — when pycryptodome
    # (``Crypto``) WAS installed, the function fell through and returned
    # None, producing an invalid Nostr event content. ``cryptography`` is
    # already a hard dependency of this package (see pyproject.toml) so
    # there is no reason to branch; collapsed to a single code path.
    import base64
    import os

    from cryptography.hazmat.backends import default_backend
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
    from secp256k1 import PrivateKey, PublicKey  # noqa: F401  (PrivateKey not used here, kept for parity)

    # Compute ECDH shared point; AES key is the raw 32-byte X coordinate.
    recipient_bytes = bytes.fromhex(recipient_pubkey)
    if len(recipient_bytes) == 32:
        # Add prefix for compressed pubkey (assume even y-coordinate)
        recipient_bytes = b"\x02" + recipient_bytes
    pubkey = PublicKey(recipient_bytes, raw=True)
    shared_point = pubkey.tweak_mul(secret_key)
    shared_secret = shared_point.serialize()[1:33]  # raw shared-X — matches l402-ts + CoinOS

    # Generate IV and encrypt with AES-256-CBC + PKCS7 padding
    iv = os.urandom(16)
    cipher = Cipher(algorithms.AES(shared_secret), modes.CBC(iv), backend=default_backend())
    encryptor = cipher.encryptor()

    plaintext_bytes = plaintext.encode("utf-8")
    padding_len = 16 - (len(plaintext_bytes) % 16)
    padded = plaintext_bytes + bytes([padding_len] * padding_len)

    ciphertext = encryptor.update(padded) + encryptor.finalize()
    return f"{base64.b64encode(ciphertext).decode()}?iv={base64.b64encode(iv).decode()}"


def _calc_padded_len(unpadded_len: int) -> int:
    """Calculate NIP-44 padded length for plaintext."""
    if unpadded_len <= 0:
        raise ValueError("Plaintext length must be > 0")
    if unpadded_len <= 32:
        return 32
    next_power = 1 << (unpadded_len - 1).bit_length()
    chunk = max(32, next_power >> 3)
    return chunk * ((unpadded_len + chunk - 1) // chunk)


def _encrypt_nip44(plaintext: str, secret_key: bytes, recipient_pubkey: str) -> str:
    """
    Encrypt content using NIP-44 v2 (ChaCha20 with HKDF-derived keys).

    Args:
        plaintext: Content to encrypt
        secret_key: Sender's 32-byte secret key
        recipient_pubkey: Recipient's hex-encoded public key

    Returns:
        Base64-encoded NIP-44 v2 payload
    """
    import base64
    import hmac as hmac_module
    import os
    import struct

    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms
    from cryptography.hazmat.backends import default_backend

    plaintext_bytes = plaintext.encode("utf-8")
    if len(plaintext_bytes) < 1 or len(plaintext_bytes) > 65535:
        raise ValueError(f"Plaintext length {len(plaintext_bytes)} out of range (1-65535)")

    # Compute shared x-coordinate (raw, NOT hashed — NIP-44 differs from NIP-04)
    shared_x = _compute_shared_x(secret_key, recipient_pubkey)

    # conversation_key = HKDF-extract(salt="nip44-v2", ikm=shared_x)
    conversation_key = hmac_module.new(b"nip44-v2", shared_x, hashlib.sha256).digest()

    # Generate random 32-byte nonce
    nonce = os.urandom(32)

    # Derive message keys via HKDF-expand
    message_keys = _hkdf_expand(conversation_key, nonce, 76)
    chacha_key = message_keys[0:32]
    chacha_nonce = message_keys[32:44]
    hmac_key = message_keys[44:76]

    # Pad plaintext: 2-byte big-endian length + plaintext + zero padding
    padded_len = _calc_padded_len(len(plaintext_bytes))
    padded = struct.pack(">H", len(plaintext_bytes)) + plaintext_bytes + b"\x00" * (padded_len - len(plaintext_bytes))

    # Encrypt with ChaCha20 (stream cipher — encrypt and decrypt are the same XOR operation)
    chacha20_nonce = b"\x00\x00\x00\x00" + chacha_nonce
    cipher = Cipher(
        algorithms.ChaCha20(chacha_key, chacha20_nonce),
        mode=None,
        backend=default_backend(),
    )
    encryptor = cipher.encryptor()
    ciphertext = encryptor.update(padded) + encryptor.finalize()

    # Compute HMAC over nonce + ciphertext
    mac = hmac_module.new(hmac_key, nonce + ciphertext, hashlib.sha256).digest()

    # Assemble: version_byte(0x02) + nonce + ciphertext + mac
    payload = bytes([0x02]) + nonce + ciphertext + mac

    return base64.b64encode(payload).decode()


def _compute_shared_x(secret_key: bytes, pubkey_hex: str) -> bytes:
    """
    Compute the ECDH shared x-coordinate.

    Args:
        secret_key: 32-byte secret key
        pubkey_hex: Hex-encoded public key (32 or 33 bytes)

    Returns:
        32-byte shared x-coordinate
    """
    from secp256k1 import PrivateKey, PublicKey

    pubkey_bytes = bytes.fromhex(pubkey_hex)
    if len(pubkey_bytes) == 32:
        pubkey_bytes = b"\x02" + pubkey_bytes
    pubkey = PublicKey(pubkey_bytes, raw=True)
    shared_point = pubkey.tweak_mul(secret_key)
    return shared_point.serialize()[1:33]


def _decrypt_nip04(encrypted: str, secret_key: bytes, sender_pubkey: str) -> str:
    """
    Decrypt content using NIP-04 (AES-256-CBC with shared secret).

    Args:
        encrypted: Encrypted content in NIP-04 format: base64(ciphertext)?iv=base64(iv)
        secret_key: Recipient's 32-byte secret key
        sender_pubkey: Sender's hex-encoded public key

    Returns:
        Decrypted plaintext
    """
    import base64

    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
    from cryptography.hazmat.backends import default_backend

    # Parse encrypted content
    parts = encrypted.split("?iv=")
    if len(parts) != 2:
        raise ValueError("Invalid NIP-04 encrypted content")

    ciphertext = base64.b64decode(parts[0])
    iv = base64.b64decode(parts[1])

    # AES key is the raw 32-byte shared-X — symmetric with _encrypt_nip04 and
    # matches l402-ts/CoinOS wire format. See _encrypt_nip04 for the empirical
    # rationale behind not using sha256(shared_x).
    shared_x = _compute_shared_x(secret_key, sender_pubkey)
    shared_secret = shared_x  # raw — matches l402-ts + CoinOS

    # Decrypt
    cipher = Cipher(algorithms.AES(shared_secret), modes.CBC(iv), backend=default_backend())
    decryptor = cipher.decryptor()
    padded = decryptor.update(ciphertext) + decryptor.finalize()

    # Remove PKCS7 padding
    padding_len = padded[-1]
    plaintext = padded[:-padding_len]

    return plaintext.decode("utf-8")


def _decrypt_nip44(encrypted: str, secret_key: bytes, sender_pubkey: str) -> str:
    """
    Decrypt content using NIP-44 v2 (ChaCha20 with HKDF-derived keys).

    Args:
        encrypted: Base64-encoded NIP-44 v2 payload
        secret_key: Recipient's 32-byte secret key
        sender_pubkey: Sender's hex-encoded public key

    Returns:
        Decrypted plaintext

    Raises:
        ValueError: If version byte is not 0x02 or HMAC verification fails
    """
    import base64
    import hmac
    import struct

    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms
    from cryptography.hazmat.backends import default_backend

    # Decode the entire payload
    payload = base64.b64decode(encrypted)

    # Check version byte
    version = payload[0]
    if version != 0x02:
        raise ValueError(f"Unsupported NIP-44 version: {version:#04x}, expected 0x02")

    # Extract components
    nonce = payload[1:33]       # 32 bytes
    ciphertext = payload[33:-32]  # variable length
    mac = payload[-32:]         # 32 bytes

    # Compute shared x-coordinate (raw, NOT hashed — NIP-44 differs from NIP-04)
    shared_x = _compute_shared_x(secret_key, sender_pubkey)

    # conversation_key = HKDF-extract(salt="nip44-v2", ikm=shared_x)
    # HKDF-extract is just HMAC-SHA256(key=salt, msg=ikm)
    conversation_key = hmac.new(b"nip44-v2", shared_x, hashlib.sha256).digest()

    # message_keys = HKDF-expand(prk=conversation_key, info=nonce, length=76)
    # HKDF-expand for length <= 32*ceil(76/32) = 96, needs 3 rounds
    message_keys = _hkdf_expand(conversation_key, nonce, 76)

    chacha_key = message_keys[0:32]
    chacha_nonce = message_keys[32:44]   # 12 bytes
    hmac_key = message_keys[44:76]

    # Verify HMAC: HMAC-SHA256(key=hmac_key, msg=nonce + ciphertext)
    expected_mac = hmac.new(hmac_key, nonce + ciphertext, hashlib.sha256).digest()
    if not hmac.compare_digest(mac, expected_mac):
        raise ValueError("NIP-44 HMAC verification failed")

    # Decrypt with ChaCha20 (raw stream cipher, NOT AEAD)
    # cryptography's ChaCha20 expects a 16-byte nonce: 4-byte counter (LE) + 12-byte nonce
    chacha20_nonce = b"\x00\x00\x00\x00" + chacha_nonce
    cipher = Cipher(
        algorithms.ChaCha20(chacha_key, chacha20_nonce),
        mode=None,
        backend=default_backend(),
    )
    decryptor = cipher.decryptor()
    decrypted = decryptor.update(ciphertext) + decryptor.finalize()

    # Extract plaintext: first 2 bytes are big-endian length
    plaintext_len = struct.unpack(">H", decrypted[0:2])[0]
    plaintext = decrypted[2:2 + plaintext_len]

    return plaintext.decode("utf-8")


def _hkdf_expand(prk: bytes, info: bytes, length: int) -> bytes:
    """
    HKDF-Expand (RFC 5869).

    Args:
        prk: Pseudorandom key (from HKDF-Extract)
        info: Context/application-specific info
        length: Output length in bytes

    Returns:
        Output keying material of requested length
    """
    import hmac as hmac_module
    import math

    hash_len = 32  # SHA-256 output length
    n = math.ceil(length / hash_len)
    okm = b""
    t = b""

    for i in range(1, n + 1):
        t = hmac_module.new(prk, t + info + bytes([i]), hashlib.sha256).digest()
        okm += t

    return okm[:length]


def _decrypt_content(encrypted: str, secret_key: bytes, sender_pubkey: str) -> str:
    """
    Decrypt content using NIP-04 or NIP-44 v2 (auto-detected).

    NIP-04 format: base64(ciphertext)?iv=base64(iv)
    NIP-44 format: base64(version_byte + nonce + ciphertext + mac)

    Args:
        encrypted: Encrypted content string
        secret_key: Recipient's 32-byte secret key
        sender_pubkey: Sender's hex-encoded public key

    Returns:
        Decrypted plaintext
    """
    try:
        if "?iv=" in encrypted:
            return _decrypt_nip04(encrypted, secret_key, sender_pubkey)
        else:
            return _decrypt_nip44(encrypted, secret_key, sender_pubkey)
    except ImportError as e:
        raise ImportError(f"Required library not available: {e}")


class NWCWallet:
    """Nostr Wallet Connect client for Lightning payments."""

    def __init__(self, connection_string: str) -> None:
        """
        Initialize NWC wallet.

        Args:
            connection_string: nostr+walletconnect:// URI

        Reads ``NWC_ENCRYPTION`` env var for outbound encryption override.
        Allowed values: ``auto`` (default — fetches the wallet's NIP-47 INFO
        event and picks the strongest advertised scheme; falls back to ``nip04``
        when no INFO event is available), ``nip04`` (force NIP-04), and
        ``nip44_v2`` (force NIP-44 v2; required by some wallets like Alby Hub).
        Invalid values fall back to the documented default with a warning so a
        typo doesn't silently disable a previously-working wallet.
        """
        self.config = NWCConfig.from_uri(connection_string)

        encryption_override = os.environ.get("NWC_ENCRYPTION")
        if encryption_override:
            normalized = encryption_override.strip().lower()
            if normalized in _VALID_NWC_ENCRYPTIONS:
                self.config = replace(self.config, encryption=normalized)
                logger.info(
                    "NWC outbound encryption overridden via NWC_ENCRYPTION: %s", normalized
                )
            else:
                # Allowed list is derived from the source of truth so it can never
                # drift from _VALID_NWC_ENCRYPTIONS / IsValid contract.
                allowed_csv = ", ".join(sorted(_VALID_NWC_ENCRYPTIONS))
                logger.warning(
                    "Ignoring invalid NWC_ENCRYPTION=%r (allowed: %s). "
                    "Falling back to default %r.",
                    encryption_override,
                    allowed_csv,
                    NWC_ENCRYPTION_DEFAULT,
                )

        self._secret_key = bytes.fromhex(self.config.secret)
        self._pubkey = _get_pubkey(self._secret_key)
        self._ws: WebSocketClientProtocol | None = None
        self._connected = False
        self._pending_requests: dict[str, asyncio.Future[dict[str, Any]]] = {}
        self._response_task: asyncio.Task[None] | None = None
        # Auto-detect cache. Populated on the first ``_send_request`` when the
        # configured encryption is "auto" — fetches the wallet's NIP-47 INFO
        # event (kind 13194), reads the ``encryption`` tag, picks the strongest
        # advertised scheme. Subsequent requests use the cached value with no
        # extra round trip. The lock serialises concurrent first-request fetches
        # so we don't open N relay connections at startup.
        self._resolved_auto_encryption: str | None = None
        self._auto_resolve_lock = asyncio.Lock()

    async def connect(self) -> None:
        """Connect to the relay."""
        if self._connected:
            return

        try:
            self._ws = await websockets.connect(self.config.relay_url)
            self._connected = True
            self._response_task = asyncio.create_task(self._handle_responses())
            logger.info(f"Connected to NWC relay: {self.config.relay_url}")
        except asyncio.CancelledError:
            # Cancellation must propagate untouched — wrapping it as a
            # connect_failed NWCConnectionError would break standard task
            # cancellation semantics for callers (e.g. MCP request cancellations,
            # server shutdown). The broad except below would otherwise swallow it.
            raise
        except Exception as e:
            raise NWCConnectionError(
                f"NWC request failed ({NWC_FAIL_CONNECT}): "
                f"WebSocket connection to {self.config.relay_url} failed: {e!s}"
            ) from e

    async def disconnect(self) -> None:
        """Disconnect from the relay."""
        if self._response_task:
            self._response_task.cancel()
            try:
                await self._response_task
            except asyncio.CancelledError:
                pass

        if self._ws:
            await self._ws.close()

        self._connected = False

    async def _handle_responses(self) -> None:
        """Handle incoming messages from the relay."""
        if not self._ws:
            return

        try:
            async for message in self._ws:
                try:
                    data = json.loads(message)
                    await self._process_message(data)
                except json.JSONDecodeError:
                    logger.warning(f"Invalid JSON from relay: {message[:100]}")
                except Exception as e:
                    logger.exception(f"Error processing message: {e}")
        except websockets.ConnectionClosed:
            logger.info("Relay connection closed")
            self._connected = False

    async def _process_message(self, data: list[Any]) -> None:
        """Process a Nostr message."""
        if not data or data[0] != "EVENT":
            return

        if len(data) < 3:
            return

        event = data[2]
        if event.get("kind") != 23195:  # NIP-47 response kind
            return

        # F-11: defence-in-depth on top of the NIP-04/44 encryption layer.
        # The encryption itself authenticates the sender (only the wallet's
        # private key can produce ciphertext we can decrypt), but verifying the
        # claimed pubkey + BIP340 signature catches malformed/garbage events
        # earlier and rejects any future spec extension that decouples sender
        # identity from the encryption ECDH (e.g. relayed delegated responses).
        # Mirror of the .NET handler in NwcWalletService.cs.
        event_pubkey = event.get("pubkey", "").lower()
        wallet_pubkey = self.config.wallet_pubkey.lower()
        if event_pubkey != wallet_pubkey:
            logger.warning(
                "NWC response event pubkey mismatch (event=%s, expected=%s); ignoring",
                event_pubkey[:16] if event_pubkey else "<empty>",
                wallet_pubkey[:16],
            )
            return

        if not _verify_nostr_event_signature(event):
            logger.warning(
                "NWC response event signature verification failed; ignoring"
            )
            return

        # Check this response is addressed to us and references one of our requests
        p_tag = None
        e_tag = None
        for tag in event.get("tags", []):
            if tag[0] == "p" and tag[1] == self._pubkey:
                p_tag = tag[1]
            if tag[0] == "e":
                e_tag = tag[1]

        if not p_tag or not e_tag:
            return

        # Decrypt response
        try:
            content = _decrypt_content(
                event["content"], self._secret_key, self.config.wallet_pubkey
            )
            response = json.loads(content)

            # Resolve pending request
            if e_tag in self._pending_requests:
                self._pending_requests[e_tag].set_result(response)
        except Exception as e:
            logger.exception(f"Error decrypting response: {e}")

    async def _resolve_auto_encryption(self) -> str:
        """
        Resolve outbound encryption when configured as "auto". Fetches the
        wallet's NIP-47 INFO event (kind 13194) once on first request, picks
        the strongest advertised scheme, and caches the result on this wallet
        instance for the rest of its lifetime. On any failure (relay
        unreachable, timeout, malformed event) falls back to NIP-04.

        Concurrent first calls are serialised by ``_auto_resolve_lock`` so we
        don't open N relay connections for N parallel first-requests.
        """
        # Fast-path cache check
        if self._resolved_auto_encryption is not None:
            return self._resolved_auto_encryption

        async with self._auto_resolve_lock:
            # Double-check after acquiring the lock
            if self._resolved_auto_encryption is not None:
                return self._resolved_auto_encryption

            resolved = await self._fetch_encryption_from_info_event()
            self._resolved_auto_encryption = resolved
            logger.info("NWC auto-detect resolved outbound encryption: %s", resolved)
            return resolved

    async def _fetch_encryption_from_info_event(self) -> str:
        """
        One-shot WebSocket REQ for the wallet's kind 13194 (NIP-47 INFO) event.
        Always returns a value — exceptions and timeouts translate to the
        NIP-04 fallback so a flaky relay or older wallet doesn't make every
        future request fail.
        """
        # Wall-clock deadline for the whole fetch. The previous implementation
        # decremented a synthetic ``deadline_remaining`` constant per recv() loop
        # which both overshot the budget when recv was slow and undershot when
        # many small messages arrived quickly. We track real elapsed time via
        # time.monotonic() so the cap is faithfully enforced regardless of
        # message rate or system load.
        deadline = time.monotonic() + NWC_AUTO_RESOLVE_TIMEOUT_SECONDS
        ws = None
        try:
            connect_remaining = max(0.0, deadline - time.monotonic())
            ws = await asyncio.wait_for(
                websockets.connect(self.config.relay_url),
                timeout=connect_remaining,
            )

            sub_id = secrets.token_hex(8)
            req = json.dumps(
                [
                    "REQ",
                    sub_id,
                    {
                        "kinds": [13194],
                        "authors": [self.config.wallet_pubkey],
                        "limit": 1,
                    },
                ]
            )
            await ws.send(req)

            # Drain messages until the deadline. The wallet service publishes
            # 13194 to the relay; relays usually have it stored, so we get
            # EVENT then EOSE quickly. Older wallets that never published
            # one trigger EOSE without an EVENT and we fall back.
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    logger.info(
                        "NWC INFO-event fetch timed out after %ss; falling back to NIP-04",
                        NWC_AUTO_RESOLVE_TIMEOUT_SECONDS,
                    )
                    return NWC_ENCRYPTION_NIP04
                msg_raw = await asyncio.wait_for(ws.recv(), timeout=remaining)
                try:
                    data = json.loads(msg_raw)
                except json.JSONDecodeError:
                    continue
                if not isinstance(data, list) or len(data) < 2:
                    continue
                msg_type = data[0]
                if msg_type == "EVENT" and len(data) >= 3:
                    # Validate subscription id matches the one we just generated.
                    # A relay (or hostile peer) could otherwise inject an
                    # unsolicited EVENT we'd treat as the wallet's INFO event
                    # and silently downgrade/upgrade encryption for real calls.
                    rcv_sub_id = data[1] if len(data) > 1 else None
                    if rcv_sub_id != sub_id:
                        continue

                    event = data[2]
                    if event.get("kind") != 13194:
                        continue

                    # Defence in depth: verify the event was published by the
                    # wallet pubkey we're talking to.
                    pubkey_hex = event.get("pubkey", "")
                    if pubkey_hex.lower() != self.config.wallet_pubkey.lower():
                        continue

                    # Cryptographic verification of the event signature.
                    # Without this, a malicious relay could forge a kind 13194
                    # event attributed to the wallet pubkey and force an
                    # encryption downgrade or DoS. Recomputes the event id
                    # from the canonical serialisation and verifies the BIP340
                    # Schnorr signature against the claimed pubkey — any
                    # tampered tag (including the encryption tag we're about
                    # to read) breaks verification.
                    if not _verify_nostr_event_signature(event):
                        logger.info(
                            "NWC INFO event signature verification failed; ignoring"
                        )
                        continue

                    enc_tag_value: str | None = None
                    for tag in event.get("tags", []):
                        if (
                            isinstance(tag, list)
                            and len(tag) >= 2
                            and tag[0] == "encryption"
                        ):
                            enc_tag_value = tag[1]
                            break
                    return _pick_encryption_from_info_tag(enc_tag_value)
                elif msg_type == "EOSE":
                    rcv_sub_id = data[1] if len(data) > 1 else None
                    if rcv_sub_id != sub_id:
                        # EOSE for a different subscription — ignore.
                        continue
                    logger.info(
                        "NWC INFO event not in relay history; falling back to NIP-04"
                    )
                    return NWC_ENCRYPTION_NIP04
        except asyncio.CancelledError:
            # Caller cancellation must propagate — don't translate to a fallback.
            raise
        except asyncio.TimeoutError:
            logger.info(
                "NWC INFO-event fetch timed out after %ss; falling back to NIP-04",
                NWC_AUTO_RESOLVE_TIMEOUT_SECONDS,
            )
            return NWC_ENCRYPTION_NIP04
        except Exception as e:
            logger.info(
                "NWC INFO-event fetch failed (%s); falling back to NIP-04",
                e,
            )
            return NWC_ENCRYPTION_NIP04
        finally:
            if ws is not None:
                try:
                    await ws.close()
                except Exception:
                    pass

    async def _send_request(self, method: str, params: dict[str, Any]) -> dict[str, Any]:
        """
        Send an NWC request and wait for response.

        Args:
            method: NIP-47 method name
            params: Request parameters

        Returns:
            Response dict
        """
        if not self._connected or not self._ws:
            await self.connect()

        # Resolve outbound encryption. When config is "auto" (the default) we
        # fetch the wallet's NIP-47 INFO event once and cache the choice.
        # Explicit "nip04"/"nip44_v2" skip the fetch entirely. Inbound
        # auto-detects so this only affects outbound.
        if self.config.encryption == NWC_ENCRYPTION_AUTO:
            effective_encryption = await self._resolve_auto_encryption()
        else:
            effective_encryption = self.config.encryption

        request = {"method": method, "params": params}
        if effective_encryption == NWC_ENCRYPTION_NIP44_V2:
            encrypted_content = _encrypt_nip44(
                json.dumps(request), self._secret_key, self.config.wallet_pubkey
            )
            tags = [["p", self.config.wallet_pubkey], ["encryption", "nip44_v2"]]
        else:
            encrypted_content = _encrypt_content(
                json.dumps(request), self._secret_key, self.config.wallet_pubkey
            )
            # No "encryption" tag for NIP-04 — that's the original NIP-47 default.
            tags = [["p", self.config.wallet_pubkey]]

        # Create event
        event = {
            "kind": 23194,  # NIP-47 request kind
            "pubkey": self._pubkey,
            "created_at": int(time.time()),
            "tags": tags,
            "content": encrypted_content,
        }

        # Sign event
        event["id"] = _compute_event_id(event)
        event["sig"] = _sign_event(event, self._secret_key)

        # Create future for response
        future: asyncio.Future[dict[str, Any]] = asyncio.Future()
        self._pending_requests[event["id"]] = future

        # Subscribe to responses
        sub_id = secrets.token_hex(8)
        sub_msg = json.dumps(
            [
                "REQ",
                sub_id,
                {
                    "kinds": [23195],
                    "#e": [event["id"]],
                    "#p": [self._pubkey],
                    "since": int(time.time()) - 10,
                },
            ]
        )
        await self._ws.send(sub_msg)

        # Send request
        await self._ws.send(json.dumps(["EVENT", event]))

        # Wait for response with timeout
        try:
            response = await asyncio.wait_for(future, timeout=60.0)
            return response
        except asyncio.TimeoutError:
            # Improved error: tell the user the most common cause is an outbound
            # encryption mismatch (Primal/CoinOS silently drop nip44_v2; Alby Hub
            # silently drops nip04) and how to opt into the other scheme.
            # We quote effective_encryption (the actually-used scheme, post-
            # auto-resolve) so the hint points at the *real* mismatch direction.
            alt_scheme = (
                NWC_ENCRYPTION_NIP04
                if effective_encryption == NWC_ENCRYPTION_NIP44_V2
                else NWC_ENCRYPTION_NIP44_V2
            )
            raise NWCError(
                f"NWC request failed ({NWC_FAIL_NO_RESPONSE}): wallet did not respond "
                f"to '{method}' within 60s using {effective_encryption} encryption. "
                f"Most common cause: encryption mismatch — try setting "
                f"NWC_ENCRYPTION={alt_scheme} if your wallet "
                f"(e.g. Alby Hub for nip44_v2; Primal/CoinOS for nip04) requires "
                f"the other scheme."
            )
        finally:
            del self._pending_requests[event["id"]]
            # Unsubscribe
            await self._ws.send(json.dumps(["CLOSE", sub_id]))

    async def pay_invoice(self, bolt11: str) -> str:
        """
        Pay a Lightning invoice.

        Args:
            bolt11: BOLT11 invoice string

        Returns:
            Payment preimage as hex string

        Raises:
            NWCPaymentError: If payment fails
        """
        response = await self._send_request("pay_invoice", {"invoice": bolt11})

        if response.get("error"):
            error = response["error"]
            raise NWCPaymentError(f"Payment failed: {error.get('message', error)}")

        result = response.get("result", {})
        preimage = result.get("preimage")

        if not preimage:
            raise NWCPaymentError("No preimage in payment response")

        # Validate preimage format - should be 64-char hex string
        # Some wallets may incorrectly return the invoice or other data
        if preimage.startswith(("lnbc", "lntb", "lnurl")):
            logger.error("NWC wallet returned invoice instead of preimage")
            raise NWCPaymentError(
                "Wallet returned invoice instead of preimage. "
                "This may be a bug in your NWC wallet implementation."
            )

        # Normalize preimage - some wallets may include 0x prefix or spaces
        preimage = preimage.replace("0x", "").replace(" ", "").lower()

        # Validate it looks like hex
        if not all(c in "0123456789abcdef" for c in preimage):
            logger.error("NWC wallet returned invalid preimage format")
            raise NWCPaymentError(
                "Invalid preimage format. Expected hex string."
            )

        return preimage

    async def get_balance(self) -> int:
        """
        Get wallet balance.

        Returns:
            Balance in satoshis
        """
        response = await self._send_request("get_balance", {})

        if response.get("error"):
            error = response["error"]
            raise NWCError(f"Failed to get balance: {error.get('message', error)}")

        result = response.get("result", {})
        # Balance is in millisatoshis
        balance_msat = result.get("balance", 0)
        return balance_msat // 1000

    async def get_info(self) -> dict[str, Any]:
        """
        Get wallet info.

        Returns:
            Wallet info dict
        """
        response = await self._send_request("get_info", {})

        if response.get("error"):
            error = response["error"]
            raise NWCError(f"Failed to get info: {error.get('message', error)}")

        return response.get("result", {})
