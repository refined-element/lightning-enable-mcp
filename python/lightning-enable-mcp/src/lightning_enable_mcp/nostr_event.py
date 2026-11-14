"""
Nostr event helpers shared by every Nostr-speaking component (NIP-01).

One home for the canonical event serialisation, the event id, BIP-340 signing,
signature verification and the bech32 key encodings, so the NWC wallet and the
signed-attestation code cannot drift apart. Mirrors the .NET ``NostrEvents``
class; the two ports are pinned to the same bytes by
``shared/attestation-golden-vector.json``.

Canonical serialisation (NIP-01): ``[0, pubkey, created_at, kind, tags, content]``
as compact JSON in UTF-8, with ``"``, ``\\``, ``\\b``, ``\\f``, ``\\n``, ``\\r`` and
``\\t`` escaped by their short forms, any other control character below U+0020
as a lowercase ``\\u00xx`` escape, and every other character written raw. This is
what ``json.dumps(..., ensure_ascii=False, separators=(",", ":"))`` produces, and
what JavaScript's ``JSON.stringify`` produces, so ids agree with the common Nostr
libraries.
"""

from __future__ import annotations

import hashlib
import json
from typing import Any

_COINCURVE_HINT = (
    "coincurve is required for Nostr signing. It ships with this package — reinstall "
    "via:  pip install --upgrade lightning-enable-mcp "
    "(or install it directly:  pip install 'coincurve>=20.0.0')."
)

_HEX = frozenset("0123456789abcdefABCDEF")


def is_hex(value: Any, length: int) -> bool:
    """Whether ``value`` is a string of exactly ``length`` hex characters."""
    return isinstance(value, str) and len(value) == length and all(c in _HEX for c in value)


def serialize_event(
    pubkey: str, created_at: int, kind: int, tags: list[Any], content: str
) -> str:
    """The canonical NIP-01 serialisation that the event id is the SHA-256 of."""
    return json.dumps(
        [0, pubkey, created_at, kind, tags, content],
        separators=(",", ":"),
        ensure_ascii=False,
    )


def compute_event_id(event: dict[str, Any]) -> str:
    """Lowercase hex event id: SHA-256 of the canonical serialisation."""
    serialized = serialize_event(
        event["pubkey"], event["created_at"], event["kind"], event["tags"], event["content"]
    )
    return hashlib.sha256(serialized.encode("utf-8")).hexdigest()


def sign_event_id(event_id_hex: str, secret_key: bytes, aux_rand: bytes | None = b"") -> str:
    """BIP-340 Schnorr signature over the raw 32-byte event id, as lowercase hex.

    ``aux_rand`` follows coincurve: ``b""`` (the default) draws 32 fresh random bytes,
    which is what BIP-340 recommends for production signing; 32 explicit bytes make the
    signature deterministic (golden vectors use this).
    """
    try:
        from coincurve import PrivateKey
    except ImportError as e:  # pragma: no cover - coincurve is a base dependency
        raise ImportError(_COINCURVE_HINT) from e

    return PrivateKey(secret_key).sign_schnorr(bytes.fromhex(event_id_hex), aux_rand).hex()


def sign_event(event: dict[str, Any], secret_key: bytes) -> str:
    """Sign an event whose ``id`` is already set (fresh auxiliary randomness)."""
    return sign_event_id(event["id"], secret_key)


def xonly_pubkey_hex(secret_key: bytes) -> str:
    """The 32-byte x-only public key for ``secret_key``, as lowercase hex."""
    try:
        from coincurve import PrivateKey
    except ImportError as e:  # pragma: no cover - coincurve is a base dependency
        raise ImportError(_COINCURVE_HINT) from e

    # 33-byte compressed pubkey (prefix || X); the x-only key drops the prefix.
    return PrivateKey(secret_key).public_key.format(compressed=True)[1:33].hex()


def verify_event(event: dict[str, Any]) -> bool:
    """Whether ``event`` has a correct id AND a valid BIP-340 signature under its pubkey.

    Recomputes the id from the canonical serialisation, so tampering with any signed
    field (tags, content, created_at, kind, pubkey) fails. Returns False on any
    malformed input instead of raising.
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

        if compute_event_id(event).lower() != id_hex.lower():
            return False

        from coincurve import PublicKeyXOnly

        # verify(signature, message): the message is the raw 32-byte id, not re-hashed.
        return bool(
            PublicKeyXOnly(bytes.fromhex(pubkey_hex)).verify(
                bytes.fromhex(sig_hex), bytes.fromhex(id_hex)
            )
        )
    except Exception:
        return False


# ── bech32 (NIP-19) ──────────────────────────────────────────────────────────


def _bech32_encode(hrp: str, data: bytes) -> str:
    from bech32 import bech32_encode, convertbits

    words = convertbits(data, 8, 5)
    if words is None:  # pragma: no cover - 32 bytes always converts
        raise ValueError("bech32 conversion failed")
    return bech32_encode(hrp, words)


def _bech32_decode(expected_hrp: str, text: str) -> bytes:
    from bech32 import bech32_decode, convertbits

    hrp, words = bech32_decode(text)
    if hrp != expected_hrp or words is None:
        raise ValueError(f"not a valid {expected_hrp} string")
    data = convertbits(words, 5, 8, False)
    if data is None or len(data) != 32:
        raise ValueError(f"{expected_hrp} must encode 32 bytes")
    return bytes(data)


def npub_encode(pubkey_hex: str) -> str:
    """NIP-19 ``npub1...`` form of a 32-byte x-only pubkey."""
    return _bech32_encode("npub", bytes.fromhex(pubkey_hex))


def parse_secret_key(text: str) -> bytes:
    """A 32-byte secret key from 64 hex characters or a NIP-19 ``nsec1...`` string.

    Raises ValueError (without echoing the value) when it is neither, or when it is
    not a valid secp256k1 scalar.
    """
    value = (text or "").strip()
    if value.lower().startswith("nsec1"):
        raw = _bech32_decode("nsec", value.lower())
    elif is_hex(value, 64):
        raw = bytes.fromhex(value)
    else:
        raise ValueError("expected 64 hex characters or an nsec1... string")

    scalar = int.from_bytes(raw, "big")
    # secp256k1 group order n: a valid secret key is 1 <= k < n.
    if not 0 < scalar < 0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141:
        raise ValueError("not a valid secp256k1 secret key")
    return raw
