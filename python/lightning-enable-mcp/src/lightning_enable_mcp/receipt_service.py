"""
Receipt Service

Append-only, human-readable spend receipts written to
``~/.lightning-enable/receipts.jsonl`` — one JSON object per line.

This is the DURABLE audit + revocation record that the in-memory
``PaymentHistoryService`` is not: it survives the session and lives off the
agent's context path, so rapid machine-to-machine / agent-to-agent flows don't
pay a per-payment token cost for it. Tool results stay lean; the human/operator
reads the file (or the ``get_receipts`` tool) for the full record.

Never contains secrets — no preimage, macaroon, or wallet connection string
(engineering standard #5). ``revokePath`` is *instructions*, not credentials.
"""

import hashlib
import hmac
import json
import logging
import os
import secrets
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional

logger = logging.getLogger("lightning-enable-mcp.receipts")


def _utc_now_iso() -> str:
    """UTC timestamp, canonical millisecond-precision Z form (matches the .NET side)."""
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


RECEIPTS_FILENAME = "receipts.jsonl"

#: Line type carrying the salted request/response digests of a paid L402 exchange.
EXCHANGE_DIGEST_TYPE = "exchange_digest"

#: The only digest construction this version writes or publishes (see request_digest).
DIGEST_SCHEME = "hmac-sha256-v1"

#: Bytes of the per-exchange nonce (256 bits).
DIGEST_NONCE_BYTES = 32


def request_digest(nonce: bytes, method: str, url: str, body: bytes | None) -> str:
    """Salted digest of a paid request (scheme ``hmac-sha256-v1``).

    ``HMAC-SHA256(key = nonce, message = UTF-8("<METHOD> <url>\\n") || body)``, lowercase
    hex. METHOD is upper-cased; the URL is exactly as requested (query included); the
    body is the bytes sent (none for GET/HEAD). Without the nonce, which stays in the
    local receipt log, a published digest cannot be tested against guessed requests;
    disclosing the nonce lets anyone check it. Same in the .NET port; pinned by the
    shared golden vector.
    """
    msg = f"{(method or 'GET').strip().upper()} {url}\n".encode() + (body or b"")
    return hmac.new(nonce, msg, hashlib.sha256).hexdigest()


def response_digest(nonce: bytes, body: bytes | None) -> str:
    """``HMAC-SHA256(key = nonce, message = response body)``, lowercase hex. The body is
    the entity body after any content-encoding is removed (what the HTTP client hands
    back); see the README for the parity note."""
    return hmac.new(nonce, body or b"", hashlib.sha256).hexdigest()

#: What a redacted value is replaced with. Matches the tools' error scrubber.
REDACTED = "[REDACTED]"

#: Substrings that mark a field as credential-shaped, matched case-insensitively anywhere
#: in the property name. ``preimage`` is the one that matters most: under L402 it is not a
#: receipt number, it IS the proof of payment.
_SENSITIVE_NAME_FRAGMENTS = (
    "preimage",
    "secret",
    "macaroon",
    "connectionstring",
    "apikey",
    "privatekey",
    "password",
    "token",
)

#: How deep to walk a receipt. Real receipts are flat; this bounds a pathological one.
_MAX_REDACT_DEPTH = 8


def is_sensitive_field(name: str) -> bool:
    """Whether a property name looks like it carries a credential."""
    lowered = name.lower()
    return any(fragment in lowered for fragment in _SENSITIVE_NAME_FRAGMENTS)


def redact_receipt(value, _depth: int = 0):
    """Strip anything credential-shaped out of a receipt before it leaves the process.

    Receipts never contain a preimage, macaroon, or wallet connection string by
    construction. This runs anyway, at the READ boundary, because the log is a plain file
    on the operator's disk: a hand-edit, an interleaved append from another tool, or a
    future writer that forgets could put one there, and by then the receipt is one read
    away from a model's context.

    Applied inside :meth:`ReceiptService.read_recent`, so the ``receipts`` tool and the
    ``lightning-enable://receipts`` resource are covered by the same pass — there is no
    second surface to keep in sync.

    The value is REPLACED rather than the key dropped: a reader can see the field was
    present and withheld, instead of silently getting a receipt that looks clean.

    Mirrors the .NET ``ReceiptRedaction``.
    """
    if _depth > _MAX_REDACT_DEPTH:
        return value
    if isinstance(value, dict):
        return {
            key: REDACTED if is_sensitive_field(str(key)) else redact_receipt(item, _depth + 1)
            for key, item in value.items()
        }
    if isinstance(value, list):
        return [redact_receipt(item, _depth + 1) for item in value]
    return value

# Bound disk use: rotate to a single ``.1`` backup once the live file passes this
# size, so the log is self-limiting (~2x this cap total) without per-write trims.
MAX_RECEIPTS_BYTES = 5 * 1024 * 1024  # 5 MB

# Per-wallet revocation instructions. Instructions only — never the connection
# string / key itself.
_REVOKE_PATHS = {
    "NWC": "Your NWC wallet app (CoinOS / Alby Hub / CLINK) → Connections / Nostr Wallet Connect → delete this connection.",
    "Strike": "Strike dashboard (dashboard.strike.me) → API keys → revoke the key this agent uses.",
    "LND": "Your LND node → bake a new macaroon and revoke/rotate the one this client uses.",
    "OpenNode": "OpenNode dashboard → Integrations / API keys → revoke the key this agent uses.",
}
_REVOKE_DEFAULT = "Revoke this wallet's connection or API key in its own app/dashboard."


def unwrap_wallet(wallet):
    """The raw wallet behind any decorator chain (for isinstance checks and provider
    labels). Lives here — the one home for the seam's ``_inner`` contract — and is
    re-exported by receipt_seam. Walks ``_inner`` so it sees the real wallet through BOTH
    the idempotency guard and the receipt seam.

    BOUNDED depth: real decorator chains are only 2-3 deep. The bound is essential because
    an unbounded walk infinite-loops on an object whose attribute access always yields a new
    truthy ``_inner`` — notably a MagicMock in tests (``mock._inner._inner...`` is endless and
    each level is a distinct object, so an id-based cycle guard never trips)."""
    for _ in range(8):
        inner = getattr(wallet, "_inner", None)
        if inner is None or inner is wallet:
            break
        wallet = inner
    return wallet


def wallet_label_from(wallet) -> str:
    """Map a wallet instance to a short provider label (NWC / Strike / LND / OpenNode)."""
    if wallet is None:
        return "unknown"
    # Look through the receipt seam (ReceiptRecordingWallet) to the real provider.
    wallet = unwrap_wallet(wallet)
    name = type(wallet).__name__
    return {
        "NWCWallet": "NWC",
        "StrikeWallet": "Strike",
        "LndWallet": "LND",
        "OpenNodeWallet": "OpenNode",
    }.get(name, name.replace("Wallet", "") or "unknown")


class ReceiptService:
    """Writes and reads append-only payment receipts."""

    def __init__(self, wallet_label: str = "unknown", receipts_path: Optional[Path] = None):
        self._wallet_label = wallet_label or "unknown"
        self._path = receipts_path or (Path.home() / ".lightning-enable" / RECEIPTS_FILENAME)

    @property
    def path(self) -> Path:
        return self._path

    def log_payment(
        self,
        *,
        kind: str,
        amount_sats: int,
        status: Optional[str] = None,
        payment_hash: Optional[str] = None,
        context: Optional[str] = None,
        policy: Optional[str] = None,
        session_spent_sats: Optional[int] = None,
        fee_sats: Optional[int] = None,
        tx_id: Optional[str] = None,
        wallet_label: Optional[str] = None,
    ) -> bool:
        """Append one generic ``payment_receipt`` line (kind: invoice | l402 | onchain).

        Returns True only when the line was durably written — a False return is
        the caller's honest ``receipt_written: false`` signal. Never raises into
        the payment path. ``context`` MUST already be redacted by the caller.
        Pre-generalization ``l402_payment_receipt`` lines remain readable
        (``read_recent`` is schema-agnostic).
        """
        label = wallet_label or self._wallet_label or "unknown"
        receipt = {
            "type": "payment_receipt",
            "kind": kind,
            "timestamp": _utc_now_iso(),
            "wallet": label,
            "amountSats": amount_sats,
        }
        # Optional fields are omitted, not null — keeps every line lean and lets
        # readers treat presence as meaning.
        if status:
            receipt["status"] = status
        if payment_hash:
            receipt["paymentHash"] = payment_hash
        if context:
            receipt["context"] = context
        if policy:
            receipt["policy"] = policy
        # Post-payment session total (session_remaining is intentionally omitted:
        # deriving it consistently across runtimes was error-prone — spentSats is
        # the accurate, unambiguous figure).
        if session_spent_sats is not None:
            receipt["sessionSpentSats"] = session_spent_sats
        if fee_sats is not None:
            receipt["feeSats"] = fee_sats
        if tx_id:
            receipt["txId"] = tx_id
        receipt["revokePath"] = _REVOKE_PATHS.get(label, _REVOKE_DEFAULT)
        return self._append(receipt)

    def log_exchange_digest(
        self,
        payment_hash: str,
        method: str,
        url: str,
        request_body: bytes | None,
        response_body: bytes | None,
        nonce: bytes | None = None,
    ) -> bool:
        """Append the salted digests of a paid L402 exchange (``exchange_digest`` line).

        Written after the paid retry returns, because the payment receipt itself is
        written by the wallet seam before the response exists. A fresh 256-bit nonce is
        drawn per exchange and kept only in this line; the bodies are never stored.
        ``read_recent`` folds the digests (not the nonce) into the matching payment
        receipt. ``nonce`` is for tests only. Never raises.
        """
        try:
            if not payment_hash:
                return False
            key = nonce if nonce is not None else secrets.token_bytes(DIGEST_NONCE_BYTES)
            return self._append(
                {
                    "type": EXCHANGE_DIGEST_TYPE,
                    "timestamp": _utc_now_iso(),
                    "paymentHash": payment_hash.lower(),
                    "digestScheme": DIGEST_SCHEME,
                    "digestNonce": key.hex(),
                    "requestDigest": request_digest(key, method, url, request_body),
                    "responseDigest": response_digest(key, response_body),
                }
            )
        except Exception as e:  # pragma: no cover - defensive
            logger.warning("Failed to write exchange digest: %s", type(e).__name__)
            return False

    def lookup_payment(self, payment_hash: str, window: int = 2_000) -> dict | None:
        """What the local log knows about ``payment_hash``: None when it is not there,
        else ``{"paymentHash", "requestDigest"?, "responseDigest"?}``. Digests are given
        only for a salted (``hmac-sha256-v1``) record; an older record without a nonce
        yields the payment hash alone, so an unsalted digest is never published."""
        wanted = (payment_hash or "").strip().lower()
        if not wanted:
            return None
        found: dict | None = None
        for obj in self._read_objects(window):
            if str(obj.get("paymentHash", "")).lower() != wanted:
                continue
            found = found or {"paymentHash": wanted}
            if _is_salted_digest(obj):
                found["requestDigest"] = obj["requestDigest"]
                found["responseDigest"] = obj["responseDigest"]
        return found

    def read_recent(self, limit: int = 20) -> list[dict]:
        """Return the most recent receipts (newest last). Tolerant of a missing/partial file.

        ``exchange_digest`` lines are not returned on their own: their request/response
        hashes are folded into the payment receipt with the same payment hash.
        """
        if limit <= 0:
            return []

        # Each paid L402 call can add one digest line after its receipt, so read twice
        # the window to still return ``limit`` receipts.
        objects = self._read_objects(limit * 2)
        digests = {
            str(o.get("paymentHash", "")).lower(): o
            for o in objects
            if _is_salted_digest(o) and o.get("paymentHash")
        }
        out: list[dict] = []
        for obj in objects:
            if obj.get("type") == EXCHANGE_DIGEST_TYPE:
                continue
            digest = digests.get(str(obj.get("paymentHash", "")).lower()) if obj.get("paymentHash") else None
            if digest is not None:
                obj = {
                    **obj,
                    "requestDigest": digest.get("requestDigest"),
                    "responseDigest": digest.get("responseDigest"),
                }
            # Redact at the READ boundary so every surface — the `receipts` tool and
            # the lightning-enable://receipts resource alike — is covered by one pass.
            out.append(redact_receipt(obj))
        return out[-limit:]

    def _read_objects(self, max_lines: int) -> list[dict]:
        # Read the rotated ".1" backup first (older) then the live file (newer), so a
        # read right after a rotation still returns recent history rather than the
        # near-empty fresh file.
        lines: list[str] = []
        for p in (self._path.with_name(self._path.name + ".1"), self._path):
            try:
                if p.exists():
                    with open(p, encoding="utf-8") as f:
                        lines.extend(f.readlines())
            except Exception as e:  # pragma: no cover - defensive
                logger.warning("Failed to read receipts from %s: %s", p, e)

        out: list[dict] = []
        for line in lines[-max_lines:]:
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except Exception:
                continue  # skip a torn/partial line rather than fail the whole read
            if isinstance(obj, dict):  # skip non-object lines (hand-edits / interleaved appends)
                out.append(obj)
        return out

    # ---- internals ----

    def _append(self, receipt: dict) -> bool:
        try:
            self._path.parent.mkdir(parents=True, exist_ok=True)
            self._rotate_if_needed()
            is_new = not self._path.exists()
            with open(self._path, "a", encoding="utf-8") as f:
                f.write(json.dumps(receipt) + "\n")
            if is_new:
                self._restrict_perms()
            return True
        except Exception as e:
            # A receipt is an audit convenience — it must NEVER break a payment.
            # The False return keeps the failure VISIBLE (receipt_written: false).
            logger.warning("Failed to write payment receipt: %s", e)
            return False

    def _rotate_if_needed(self) -> None:
        try:
            if self._path.exists() and self._path.stat().st_size > MAX_RECEIPTS_BYTES:
                backup = self._path.with_name(self._path.name + ".1")
                os.replace(self._path, backup)  # atomically replaces any old backup
        except Exception as e:  # pragma: no cover - defensive
            logger.warning("Receipt log rotation failed: %s", e)

    def _restrict_perms(self) -> None:
        try:
            from .config import _restrict_file_permissions
            _restrict_file_permissions(self._path)
        except Exception:  # pragma: no cover - best effort, mirrors config.json
            pass


def _is_salted_digest(obj: dict) -> bool:
    return (
        obj.get("type") == EXCHANGE_DIGEST_TYPE
        and obj.get("digestScheme") == DIGEST_SCHEME
        and isinstance(obj.get("digestNonce"), str)
        and isinstance(obj.get("requestDigest"), str)
        and isinstance(obj.get("responseDigest"), str)
    )
