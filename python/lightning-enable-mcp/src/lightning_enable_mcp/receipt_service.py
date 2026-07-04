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

import json
import logging
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional

logger = logging.getLogger("lightning-enable-mcp.receipts")


def _utc_now_iso() -> str:
    """UTC timestamp, canonical millisecond-precision Z form (matches the .NET side)."""
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


RECEIPTS_FILENAME = "receipts.jsonl"

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


def wallet_label_from(wallet) -> str:
    """Map a wallet instance to a short provider label (NWC / Strike / LND / OpenNode)."""
    if wallet is None:
        return "unknown"
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
        endpoint: str,
        amount_sats: int,
        policy: str,
        session_spent_sats: Optional[int] = None,
        kind: str = "l402_payment_receipt",
    ) -> None:
        """Append one payment receipt. Best-effort — never raises into the payment path.

        ``endpoint`` MUST already be redacted by the caller (no query/secrets).
        """
        receipt = {
            "type": kind,
            "timestamp": _utc_now_iso(),
            "endpoint": endpoint,
            "amountSats": amount_sats,
            "wallet": self._wallet_label,
            "policy": policy,
            # Post-payment session total (session_remaining is intentionally omitted:
            # deriving it consistently across runtimes was error-prone — spentSats is
            # the accurate, unambiguous figure).
            "sessionSpentSats": session_spent_sats,
            "revokePath": _REVOKE_PATHS.get(self._wallet_label, _REVOKE_DEFAULT),
        }
        self._append(receipt)

    def read_recent(self, limit: int = 20) -> list[dict]:
        """Return the most recent receipts (newest last). Tolerant of a missing/partial file."""
        if limit <= 0:
            return []

        # Read the rotated ".1" backup first (older) then the live file (newer), so a
        # read right after a rotation still returns recent history rather than the
        # near-empty fresh file.
        lines: list[str] = []
        for p in (self._path.with_name(self._path.name + ".1"), self._path):
            try:
                if p.exists():
                    with open(p, "r", encoding="utf-8") as f:
                        lines.extend(f.readlines())
            except Exception as e:  # pragma: no cover - defensive
                logger.warning("Failed to read receipts from %s: %s", p, e)

        out: list[dict] = []
        for line in lines[-limit:]:
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

    def _append(self, receipt: dict) -> None:
        try:
            self._path.parent.mkdir(parents=True, exist_ok=True)
            self._rotate_if_needed()
            is_new = not self._path.exists()
            with open(self._path, "a", encoding="utf-8") as f:
                f.write(json.dumps(receipt) + "\n")
            if is_new:
                self._restrict_perms()
        except Exception as e:
            # A receipt is an audit convenience — it must NEVER break a payment.
            logger.warning("Failed to write payment receipt: %s", e)

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
