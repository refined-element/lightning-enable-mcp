"""
Mainnet Bitcoin address validation for on-chain sends (PY-C2).

On-chain sends are irreversible, so a typo'd, garbage, or wrong-network address
must be rejected BEFORE broadcasting. Uses the ``bech32`` library for segwit
(BIP173/350) and an inline base58check verifier for legacy P2PKH/P2SH so an
invalid address can never reach the wallet.
"""

import hashlib

try:
    from bech32 import decode as _segwit_decode
except Exception:  # pragma: no cover - bech32 is a declared dependency
    _segwit_decode = None

_B58_ALPHABET = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"


def _base58check_ok(address: str, valid_version_bytes: tuple) -> bool:
    """Verify a legacy base58check address (version byte + 20-byte hash + 4-byte checksum)."""
    num = 0
    for ch in address:
        idx = _B58_ALPHABET.find(ch)
        if idx == -1:
            return False
        num = num * 58 + idx

    raw = num.to_bytes((num.bit_length() + 7) // 8, "big") if num else b""
    # Leading '1' characters encode leading zero bytes.
    pad = len(address) - len(address.lstrip("1"))
    raw = (b"\x00" * pad) + raw

    if len(raw) != 25:  # 1 version + 20 hash + 4 checksum
        return False

    payload, checksum = raw[:-4], raw[-4:]
    if payload[0] not in valid_version_bytes:
        return False

    digest = hashlib.sha256(hashlib.sha256(payload).digest()).digest()
    return digest[:4] == checksum


def is_valid_mainnet(address) -> bool:
    """
    Return True only if ``address`` is a valid MAINNET Bitcoin address
    (P2PKH / P2SH / bech32 / bech32m). Testnet, regtest, and garbage all
    return False — on-chain sends move real funds irreversibly.
    """
    if not isinstance(address, str):
        return False
    addr = address.strip()
    if not addr:
        return False

    low = addr.lower()
    if low.startswith("bc1"):
        if _segwit_decode is None:
            return False
        witver, witprog = _segwit_decode("bc", addr)
        return witver is not None and witprog is not None

    if addr[0] in ("1", "3"):
        # P2PKH version byte 0x00, P2SH version byte 0x05.
        return _base58check_ok(addr, (0x00, 0x05))

    return False
