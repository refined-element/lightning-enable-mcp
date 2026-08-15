"""
Idempotent Wallet

Wallet-seam decorator that makes Lightning invoice payment IDEMPOTENT via the durable
``OperationLedger``. Before paying, it derives a stable operation id from the invoice and
refuses to submit one already in a money-moving state (submitted/pending/settled) — even
across a process restart or an agent retry — so a crash or retry can never cause a blind
duplicate payment. A refused duplicate raises ``DuplicateSubmissionError``, which the
paying tools handle on their existing ``except`` path by releasing the budget reservation
(no double count) and reporting "already submitted — check status, do not retry".

Sits OUTSIDE the receipt seam in the decorator chain, so a refused duplicate never reaches
the wallet and never writes a receipt. On-chain sends pass straight through (via
``__getattr__``): they carry no payment hash and each one already requires a fresh
out-of-band human confirmation, so a blind restart re-send cannot happen silently.

The operation id is ``SHA256(normalized bolt11)`` — a stable, non-secret key. The raw
invoice, preimage, macaroon, and connection string are never persisted. Mirrors the .NET
``IdempotentWalletService``.
"""

import hashlib
import logging
from typing import Optional

from .operation_ledger import MONEY_MOVING_STATES, OperationLedger, OperationState
from .receipt_service import wallet_label_from
from .wallet_errors import PaymentPendingError, PaymentProofUnavailableError

logger = logging.getLogger("lightning-enable-mcp.idempotent-wallet")


class DuplicateSubmissionError(Exception):
    """Raised when an invoice already in a money-moving state is re-submitted. The paying
    tools catch it, release the reservation, and tell the agent not to retry."""


class IdempotentWallet:
    """Decorates a wallet to make ``pay_invoice`` idempotent via the operation ledger."""

    def __init__(self, inner, ledger: OperationLedger):
        self._inner = inner
        self._ledger = ledger

    def __getattr__(self, name):
        # Everything except pay_invoice passes straight through (send_onchain,
        # get_balance, is_configured, provider labels, …).
        return getattr(self._inner, name)

    async def pay_invoice(self, bolt11: str, *args, **kwargs) -> str:
        operation_id = _derive_operation_id(bolt11)

        existing = self._ledger.lookup(operation_id)
        if existing is not None and existing.state in MONEY_MOVING_STATES:
            raise DuplicateSubmissionError(
                "This invoice was already submitted in a prior attempt (state: "
                f"{existing.state.value}). Refusing to pay it again to avoid a "
                "double-payment. Check its status with your wallet — do NOT retry."
            )

        # Durably record submission BEFORE the wallet call so a crash immediately after
        # submission still leaves a record that blocks a blind re-pay on restart.
        self._ledger.record_submitted(operation_id, _amount_sats(bolt11), wallet_label_from(self._inner))

        try:
            preimage = await self._inner.pay_invoice(bolt11, *args, **kwargs)
        except PaymentPendingError:
            # Accepted, not settled — funds may still move, so keep it blocking a re-pay.
            self._ledger.record_outcome(operation_id, OperationState.PENDING, None)
            raise
        except PaymentProofUnavailableError:
            # Settled without a usable proof — money moved; blocks a re-pay.
            self._ledger.record_outcome(operation_id, OperationState.SETTLED, None)
            raise
        except Exception:
            # Any other error is treated as a hard failure (no funds moved) — a genuine
            # retry is allowed. If funds actually moved despite the throw, the wallet's
            # own invoice single-use protection still prevents an on-network double-spend.
            self._ledger.record_outcome(operation_id, OperationState.FAILED_NO_FUNDS, None)
            raise

        if preimage:
            self._ledger.record_outcome(operation_id, OperationState.SETTLED, _payment_hash(preimage))
        else:
            # A falsy return is a failed payment to every caller — allow a retry.
            self._ledger.record_outcome(operation_id, OperationState.FAILED_NO_FUNDS, None)
        return preimage


def _derive_operation_id(bolt11: str) -> str:
    """SHA256 of the normalized invoice — a stable, non-secret idempotency key."""
    normalized = (bolt11 or "").strip().lower()
    return "ln:" + hashlib.sha256(normalized.encode("utf-8")).hexdigest()


def _amount_sats(bolt11: str) -> int:
    """Best-effort invoice amount for the ledger record (informational; the id is the key)."""
    try:
        from bolt11 import decode as _decode
        decoded = _decode((bolt11 or "").strip().lower())
        msat = getattr(decoded, "amount_msat", None)
        if msat:
            return -(-int(msat) // 1000)  # ceil; sub-sat rounds up to 1
        amt = getattr(decoded, "amount", None)
        return int(amt) if amt else 0
    except Exception:
        return 0


def _payment_hash(preimage: Optional[str]) -> Optional[str]:
    """SHA256(preimage) — the public Lightning payment hash. The preimage itself is never
    persisted. Returns None if the value is not a 32-byte hex preimage."""
    try:
        if not preimage or len(preimage) != 64:
            return None
        return hashlib.sha256(bytes.fromhex(preimage)).hexdigest()
    except Exception:
        return None
