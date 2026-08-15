"""
Operation Ledger

Durable, append-only idempotency/restart-safety record at
``~/.lightning-enable/operations.jsonl`` — one JSON object per line.

It records that a payment intent was submitted / settled / failed so a retry — even
one that spans a process restart — cannot cause a blind duplicate payment: the
idempotency guard (see ``idempotent_wallet``) consults ``lookup`` before paying and
refuses to re-submit an operation already in a money-moving state. Stores NO secrets
(no preimage, macaroon, invoice, or connection string) — only an opaque operation id,
amount, provider, and a public payment hash. Mirrors the .NET ``OperationLedger``.

This is distinct from the receipt log (which proves an observed outcome); the ledger
governs execution/idempotency.
"""

import json
import logging
import os
from dataclasses import dataclass
from datetime import datetime, timezone
from enum import Enum
from pathlib import Path
from typing import Optional

logger = logging.getLogger("lightning-enable-mcp.operation-ledger")

OPERATIONS_FILENAME = "operations.jsonl"
MAX_OPERATIONS_BYTES = 5 * 1024 * 1024  # 5 MB, rotate to ".1"


def _utc_now_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


class OperationState(str, Enum):
    """Lifecycle state of a payment operation."""
    SUBMITTED = "submitted"      # sent to the wallet, outcome unknown — blocks a re-pay
    PENDING = "pending"          # accepted, not settled — blocks a re-pay (funds may move)
    SETTLED = "settled"          # money moved — blocks a re-pay
    FAILED_NO_FUNDS = "failed_no_funds"  # proven no funds moved — does NOT block a retry


# The states in which re-submitting an operation could cause a double-payment.
MONEY_MOVING_STATES = frozenset(
    {OperationState.SUBMITTED, OperationState.PENDING, OperationState.SETTLED}
)


@dataclass(frozen=True)
class OperationRecord:
    operation_id: str
    state: OperationState
    amount_sats: int
    payment_hash: Optional[str] = None


class OperationLedger:
    """Durable, append-only idempotency ledger. Reads through to disk so idempotency
    holds across a process restart."""

    def __init__(self, path: Optional[Path] = None):
        self._path = path or (Path.home() / ".lightning-enable" / OPERATIONS_FILENAME)
        # Latest state per operation id, rebuilt from disk on first use (covers restart).
        self._index: Optional[dict[str, OperationRecord]] = None

    @property
    def path(self) -> Path:
        return self._path

    def lookup(self, operation_id: str) -> Optional[OperationRecord]:
        """Latest known state for the operation id, or None if never seen."""
        if not operation_id:
            return None
        self._ensure_loaded()
        return self._index.get(operation_id)  # type: ignore[union-attr]

    def record_submitted(self, operation_id: str, amount_sats: int, provider: str) -> None:
        """Record submission BEFORE the wallet call, so a crash right after submission
        still leaves a durable record that blocks a blind re-pay on restart."""
        self._write(operation_id, OperationState.SUBMITTED, amount_sats, payment_hash=None, provider=provider)

    def record_outcome(
        self, operation_id: str, state: OperationState, payment_hash: Optional[str]
    ) -> None:
        """Record the resolved outcome of an operation."""
        # Preserve the amount already recorded for this operation.
        existing = self.lookup(operation_id)
        amount = existing.amount_sats if existing else 0
        self._write(operation_id, state, amount, payment_hash, provider=None)

    # ---- internals ----

    def _write(
        self,
        operation_id: str,
        state: OperationState,
        amount_sats: int,
        payment_hash: Optional[str],
        provider: Optional[str],
    ) -> None:
        if not operation_id:
            return
        self._ensure_loaded()
        # Update the in-memory index first so idempotency holds for THIS process even if
        # the durable write below fails.
        self._index[operation_id] = OperationRecord(operation_id, state, amount_sats, payment_hash)  # type: ignore[union-attr]

        line = {
            "type": "operation",
            "operationId": operation_id,
            "state": state.value,
            "amountSats": amount_sats,
            "timestamp": _utc_now_iso(),
        }
        if provider:
            line["provider"] = provider
        # Payment hash is public routing data (safe to persist); it links the operation
        # to its receipt. Secrets are never written.
        if payment_hash:
            line["paymentHash"] = payment_hash

        try:
            self._append(json.dumps(line))
        except Exception as e:
            logger.warning("Failed to write operation ledger: %s", e)

    def _ensure_loaded(self) -> None:
        if self._index is not None:
            return
        self._index = {}
        # Read the rotated ".1" backup first (older) then the live file (newer).
        for p in (self._path.with_name(self._path.name + ".1"), self._path):
            try:
                if not p.exists():
                    continue
                with open(p, "r", encoding="utf-8") as f:
                    for raw in f:
                        line = raw.strip()
                        if not line:
                            continue
                        try:
                            obj = json.loads(line)
                        except Exception:
                            continue  # skip a torn/partial line
                        if not isinstance(obj, dict):
                            continue
                        op_id = obj.get("operationId")
                        state_str = obj.get("state")
                        if not op_id or state_str is None:
                            continue
                        try:
                            state = OperationState(state_str)
                        except ValueError:
                            continue
                        # Last line wins — append-only file is in chronological order.
                        self._index[op_id] = OperationRecord(
                            op_id, state, int(obj.get("amountSats", 0) or 0), obj.get("paymentHash")
                        )
            except Exception as e:  # pragma: no cover - defensive
                logger.warning("Failed to load operation ledger from %s: %s", p, e)

    def _append(self, line: str) -> None:
        self._path.parent.mkdir(parents=True, exist_ok=True)
        self._rotate_if_needed()
        is_new = not self._path.exists()
        with open(self._path, "a", encoding="utf-8") as f:
            f.write(line + "\n")
        if is_new:
            self._restrict_perms()

    def _rotate_if_needed(self) -> None:
        try:
            if self._path.exists() and self._path.stat().st_size > MAX_OPERATIONS_BYTES:
                backup = self._path.with_name(self._path.name + ".1")
                os.replace(self._path, backup)
        except Exception as e:  # pragma: no cover - defensive
            logger.warning("Operation ledger rotation failed: %s", e)

    def _restrict_perms(self) -> None:
        try:
            from .config import _restrict_file_permissions
            _restrict_file_permissions(self._path)
        except Exception:  # pragma: no cover - best effort, mirrors receipts.jsonl
            pass
