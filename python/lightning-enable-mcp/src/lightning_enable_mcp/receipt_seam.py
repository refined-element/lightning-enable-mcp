"""
Wallet-seam receipt recording (parity with .NET's ReceiptRecordingWalletService).

``ReceiptRecordingWallet`` decorates the resolved wallet so EVERY payment that
moves value — from any tool, current or future — leaves exactly one durable line
in ``~/.lightning-enable/receipts.jsonl``. Before this seam existed only
``access_l402_resource`` / ``create_lightning_enable_account`` wrote receipts;
a ``pay_invoice`` / ``pay_l402_challenge`` / ``settle_agent_service`` / on-chain
payment left no durable record.

``PaymentReceiptScope`` is the ambient "payment intent" a tool may open before
paying (kind, redacted endpoint/purpose, budget policy), read here to enrich the
receipt — and it carries back the honest outcome: ``receipt_written`` tells the
tool whether the line landed on disk, so a failed write surfaces as
``receipt_written: false`` in the tool result instead of disappearing.

Receipt writing is best-effort and must never break a payment.
"""

import contextvars
import hashlib
import logging
from typing import Optional

from bolt11 import decode as decode_bolt11

from .receipt_service import ReceiptService, unwrap_wallet, wallet_label_from
from .wallet_errors import (
    PaymentPendingError,
    PaymentProofUnavailableError,
    is_valid_preimage,
)

__all__ = [
    "POLICY_HUMAN_CONFIRMED",
    "PaymentReceiptScope",
    "ReceiptRecordingWallet",
    "policy_label",
    "unwrap_wallet",
]

logger = logging.getLogger("lightning-enable-mcp.receipt_seam")

# The label written for a payment that only proceeds after the human-supplied
# confirmation code was validated (e.g. every on-chain send). Parity with the
# .NET PaymentPolicy.HumanConfirmed constant.
POLICY_HUMAN_CONFIRMED = "confirm"

_current_scope: contextvars.ContextVar["PaymentReceiptScope | None"] = contextvars.ContextVar(
    "payment_receipt_scope", default=None
)


def policy_label(level) -> str:
    """Snake_case policy label for receipts.jsonl from a budget ApprovalLevel.

    One shared implementation (parity with .NET's PaymentPolicy.Label) so the
    file carries one consistent policy vocabulary regardless of which tool —
    or which server port — wrote the line.
    """
    return getattr(level, "value", str(level))


class PaymentReceiptScope:
    """Ambient payment intent + honest receipt-written signal (contextvars-backed).

    Use as a context manager at the top of a payment tool::

        with PaymentReceiptScope("l402", context=redacted_url) as scope:
            ... pay via the wallet seam ...
        result["receipt_written"] = scope.receipt_written

    The contextvar flows into everything the tool awaits (including the wallet
    call buried inside L402Client), and parallel tool calls cannot see each
    other's scopes. Reading ``receipt_written`` after exit is valid — only the
    contextvar is reset, the recorded outcome stays on the object.
    """

    def __init__(self, kind: str, context: Optional[str] = None, policy: Optional[str] = None):
        #: Receipt kind for Lightning payments under this scope: "invoice" | "l402".
        self.kind = kind
        #: Redacted endpoint / purpose / destination. Must never contain a secret.
        self.context = context
        #: Budget policy label (set after the budget check, before paying).
        self.policy = policy
        self._attempted = 0
        self._any_failed = False
        self._token: Optional[contextvars.Token] = None

    def __enter__(self) -> "PaymentReceiptScope":
        self._token = _current_scope.set(self)
        return self

    def __exit__(self, exc_type, exc, tb) -> bool:
        if self._token is not None:
            _current_scope.reset(self._token)
            self._token = None
        return False

    @property
    def receipt_written(self) -> Optional[bool]:
        """None — no payment observed under this scope (nothing to receipt);
        True — every observed payment produced a durable receipt line;
        False — at least one write failed (report it, never hide it)."""
        if self._attempted == 0:
            return None
        return not self._any_failed

    def record_write(self, written: bool) -> None:
        """Called by the receipt seam after each write attempt."""
        self._attempted += 1
        if not written:
            self._any_failed = True

    @staticmethod
    def current() -> "PaymentReceiptScope | None":
        return _current_scope.get()


class ReceiptRecordingWallet:
    """The single receipt seam: wraps the resolved wallet and writes exactly one
    durable receipt per payment that moved (or committed) funds.

    Everything except ``pay_invoice`` / ``send_onchain`` passes straight through,
    so this wraps any wallet type without knowing its full surface.
    """

    def __init__(self, inner, receipt_service: ReceiptService, budget_service=None):
        self._inner = inner
        self._receipts = receipt_service
        self._budget = budget_service

    def __getattr__(self, name):
        return getattr(self._inner, name)

    async def pay_invoice(self, bolt11: str, *args, **kwargs) -> str:
        # Receipt on settled AND pending: pending funds are committed (the budget
        # records them), so the durable log must not under-report the budget.
        # Hard failures move no money and get no receipt.
        try:
            preimage = await self._inner.pay_invoice(bolt11, *args, **kwargs)
        except PaymentPendingError:
            self._write_invoice_receipt(bolt11, status="pending", preimage=None)
            raise
        except PaymentProofUnavailableError:
            # The documented BASE class — covers PreimageUnavailableError AND any
            # wallet that raises the base directly ("funds left, no proof"). The
            # L402 client records the spend for this whole family, so the durable
            # log must cover it too or receipts under-report the budget.
            self._write_invoice_receipt(bolt11, status="settled", preimage=None)
            raise

        # A falsy return is treated by every caller as a failed payment.
        if preimage:
            self._write_invoice_receipt(bolt11, status="settled", preimage=preimage)
        return preimage

    async def send_onchain(self, address: str, amount_sats: int, *args, **kwargs):
        result = await self._inner.send_onchain(address, amount_sats, *args, **kwargs)

        if getattr(result, "success", False):
            written = False
            try:
                sent_sats = getattr(result, "amount_sats", 0) or amount_sats
                fee_sats = getattr(result, "fee_sats", 0) or 0
                state = getattr(result, "state", None)
                scope = PaymentReceiptScope.current()
                written = self._receipts.log_payment(
                    kind="onchain",
                    amount_sats=sent_sats,
                    # A broadcast-but-unconfirmed send has still left the wallet; only
                    # a provider-confirmed COMPLETED state reads as settled.
                    status="settled" if str(state).upper() == "COMPLETED" else "pending",
                    context=scope.context if scope else None,
                    policy=scope.policy if scope else None,
                    # Project from the REQUESTED amount + fee: the tool records budget
                    # spend from those same figures, and the projection must match
                    # what the budget will actually hold.
                    session_spent_sats=self._project_session_spent(amount_sats + fee_sats),
                    fee_sats=fee_sats,
                    tx_id=getattr(result, "txid", None),
                    wallet_label=wallet_label_from(self._inner),
                )
            except Exception as e:
                logger.warning("Failed to write on-chain payment receipt: %s", e)
                written = False
            scope = PaymentReceiptScope.current()
            if scope is not None:
                scope.record_write(written)

        return result

    # ---- internals ----

    def _write_invoice_receipt(self, bolt11_str: str, *, status: str, preimage: Optional[str]) -> None:
        written = False
        try:
            amount_sats, decoded_hash = _decode_amount_and_hash(bolt11_str)
            # SHA256(preimage) IS the Lightning payment hash — safe to persist,
            # useless to spend. When no preimage exists (pending / provider
            # withholds it), fall back to the hash decoded from the invoice.
            payment_hash = None
            if is_valid_preimage(preimage):
                payment_hash = hashlib.sha256(bytes.fromhex(preimage.strip())).hexdigest()
            elif decoded_hash:
                payment_hash = decoded_hash

            scope = PaymentReceiptScope.current()
            written = self._receipts.log_payment(
                kind=(scope.kind if scope else None) or "invoice",
                amount_sats=amount_sats,
                status=status,
                payment_hash=payment_hash,
                context=scope.context if scope else None,
                policy=scope.policy if scope else None,
                session_spent_sats=self._project_session_spent(amount_sats),
                wallet_label=wallet_label_from(self._inner),
            )
        except Exception as e:
            # A receipt must NEVER break a payment; the failure stays VISIBLE
            # through the scope's receipt_written signal.
            logger.warning("Failed to write payment receipt: %s", e)
            written = False

        scope = PaymentReceiptScope.current()
        if scope is not None:
            scope.record_write(written)

    def _project_session_spent(self, amount_sats: int) -> Optional[int]:
        """Projected post-payment session total. The seam writes BEFORE the calling
        tool/client records the spend, so "current + this payment" is what the
        budget will read immediately after the tool returns.

        Known limitation (parity with .NET): two payments truly in flight at once
        would both project from the same base, so the earlier receipt's total can
        read low. Accepted for the lean scope — the budget cooldown paces payments
        sequentially in practice, and the per-receipt amountSats is always exact.
        """
        try:
            if self._budget is None:
                return None
            spent = self._budget.get_status()["session"]["spentSats"]
            return int(spent) + amount_sats
        except Exception:
            return None


def _decode_amount_and_hash(bolt11_str: str) -> tuple[int, Optional[str]]:
    """(amount in sats, payment hash) from a BOLT11 invoice; (0, None) if undecodable."""
    try:
        decoded = decode_bolt11(bolt11_str.strip())
    except Exception:
        return 0, None

    amount_sats = 0
    if getattr(decoded, "amount_msat", None):
        amount_sats = -(-decoded.amount_msat // 1000)  # ceil; sub-sat rounds up to 1
    elif getattr(decoded, "amount", None):
        amount_sats = decoded.amount

    payment_hash = getattr(decoded, "payment_hash", None)
    if not isinstance(payment_hash, str) or not payment_hash:
        payment_hash = None
    return int(amount_sats or 0), payment_hash
