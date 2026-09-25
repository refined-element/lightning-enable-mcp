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
import re
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
    UNKNOWN = "unknown"          # submitted, outcome ambiguous (timeout/cancel after execute) — blocks a re-pay
    FAILED_NO_FUNDS = "failed_no_funds"  # proven no funds moved — does NOT block a retry
    # Contract name shared with the .NET port. Alias of FAILED_NO_FUNDS (same persisted
    # value), so existing ledgers keep loading.
    FAILED = "failed_no_funds"



def parse_state(raw) -> "OperationState | None":
    """Parse a persisted state from EITHER port. This file is shared with the .NET port,
    which writes enum names (``Submitted``, ``FailedNoFunds``) where Python writes
    ``submitted`` / ``failed_no_funds``. A line the other flavor wrote must never be
    dropped, or a submitted send becomes re-sendable."""
    if not isinstance(raw, str) or not raw.strip():
        return None
    try:
        return OperationState(raw)
    except ValueError:
        pass
    # CamelCase -> snake_case, then lower-case; also accept the bare "failed" alias.
    snake = re.sub(r"(?<=[a-z0-9])(?=[A-Z])", "_", raw.strip()).lower()
    if snake == "failed":
        snake = "failed_no_funds"
    try:
        return OperationState(snake)
    except ValueError:
        return None

# The states in which re-submitting an operation could cause a double-payment.
MONEY_MOVING_STATES = frozenset(
    {
        OperationState.SUBMITTED,
        OperationState.PENDING,
        OperationState.SETTLED,
        OperationState.UNKNOWN,
    }
)


@dataclass(frozen=True)
class OperationRecord:
    operation_id: str
    state: OperationState
    amount_sats: int
    payment_hash: Optional[str] = None
    # On-chain operations: provider identifiers (public, non-secret) so a retry can look
    # up status instead of re-sending. Never credentials.
    kind: Optional[str] = None
    provider: Optional[str] = None
    payment_id: Optional[str] = None
    quote_id: Optional[str] = None
    tx_id: Optional[str] = None


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

    def record_submitted(
        self, operation_id: str, amount_sats: int, provider: str, kind: Optional[str] = None
    ) -> None:
        """Record submission BEFORE the wallet call, so a crash right after submission
        still leaves a durable record that blocks a blind re-pay on restart. A fresh
        submission starts with no provider ids (a prior FAILED attempt's ids are dropped)."""
        self._write(
            OperationRecord(operation_id, OperationState.SUBMITTED, amount_sats, None,
                            kind=kind, provider=provider)
        )

    def record_outcome(
        self,
        operation_id: str,
        state: OperationState,
        payment_hash: Optional[str],
        *,
        payment_id: Optional[str] = None,
        quote_id: Optional[str] = None,
        tx_id: Optional[str] = None,
    ) -> None:
        """Record the resolved outcome of an operation. Ids not supplied keep their
        previously recorded values (a status refresh must not erase the payment id)."""
        existing = self.lookup(operation_id)
        self._write(
            OperationRecord(
                operation_id,
                state,
                existing.amount_sats if existing else 0,
                payment_hash or (existing.payment_hash if existing else None),
                kind=existing.kind if existing else None,
                provider=existing.provider if existing else None,
                payment_id=payment_id or (existing.payment_id if existing else None),
                quote_id=quote_id or (existing.quote_id if existing else None),
                tx_id=tx_id or (existing.tx_id if existing else None),
            )
        )

    # ---- internals ----

    def _write(self, record: OperationRecord) -> None:
        if not record.operation_id:
            return
        self._ensure_loaded()
        # Update the in-memory index first so idempotency holds for THIS process even if
        # the durable write below fails.
        self._index[record.operation_id] = record  # type: ignore[index]

        line = {
            "type": "operation",
            "operationId": record.operation_id,
            "state": record.state.value,
            "amountSats": record.amount_sats,
            "timestamp": _utc_now_iso(),
        }
        # Public identifiers only (payment hash, provider payment/quote ids, txid) — they
        # link the operation to its receipt / provider record. Secrets are never written.
        for key, value in (
            ("kind", record.kind),
            ("provider", record.provider),
            ("paymentHash", record.payment_hash),
            ("paymentId", record.payment_id),
            ("quoteId", record.quote_id),
            ("txId", record.tx_id),
        ):
            if value:
                line[key] = value

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
                        state = parse_state(state_str)
                        if state is None:
                            continue
                        # Last line wins — append-only file is in chronological order.
                        self._index[op_id] = OperationRecord(
                            op_id,
                            state,
                            int(obj.get("amountSats", 0) or 0),
                            obj.get("paymentHash"),
                            kind=obj.get("kind"),
                            provider=obj.get("provider"),
                            payment_id=obj.get("paymentId"),
                            quote_id=obj.get("quoteId"),
                            tx_id=obj.get("txId"),
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
