"""
Payment History Service

In-memory, session-scoped record of payments made by the MCP server. Ports the
.NET ``PaymentHistoryService`` — a SEPARATE concern from spending limits, which
live in ``BudgetService``. This split mirrors the .NET architecture (BudgetService
+ PaymentHistoryService) rather than the legacy monolithic BudgetManager.

FUNDS-SAFETY: the payment preimage is NEVER stored here (engineering standard #5
— never log/store preimages). Records keep only safe identifiers: the resource
URL, amount, status, and timestamp. A truncated invoice prefix may be kept for
display, but the preimage is deliberately absent from the record entirely.
"""

import logging
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Optional

logger = logging.getLogger("lightning-enable-mcp.payment-history")

# Bound the in-memory list so a long-lived session can't grow it without limit.
# Matches the .NET PaymentHistoryService MaxPaymentRecords cap.
MAX_PAYMENT_RECORDS = 1000


@dataclass
class PaymentRecord:
    """Record of a single payment.

    NOTE: there is intentionally NO ``preimage`` field. The preimage is proof of
    payment and is treated like a secret — it is never persisted in history.
    """

    url: str
    amount_sats: int
    timestamp: datetime
    status: str = "success"  # "success", "failed", "pending"
    invoice: Optional[str] = None

    def to_dict(self) -> dict:
        """Convert to a JSON-serializable dictionary (preimage-free)."""
        result: dict = {
            "url": self.url,
            "amount_sats": self.amount_sats,
            "timestamp": self.timestamp.isoformat(),
            "status": self.status,
        }
        if self.invoice:
            result["invoice"] = (
                self.invoice[:20] + "..." if len(self.invoice) > 20 else self.invoice
            )
        return result


class PaymentHistoryService:
    """
    Tracks payment history for the current session (in-memory, bounded).

    Separate from BudgetService: BudgetService owns limits/approval/session-spend,
    this service owns the audit trail of what was paid. Mirrors the .NET
    PaymentHistoryService.
    """

    def __init__(self) -> None:
        # Kept in insertion order; the list index is used as a stable tiebreaker
        # when two records share the same timestamp (datetime.now() can collide
        # within a microsecond), so "most recent first" is deterministic.
        self._payments: list[PaymentRecord] = []

    def record_payment(
        self,
        url: str,
        amount_sats: int,
        status: str = "success",
        invoice: Optional[str] = None,
    ) -> PaymentRecord:
        """
        Record a payment.

        Args:
            url: Resource URL / identifier the payment was for.
            amount_sats: Amount paid in satoshis.
            status: "success", "failed", or "pending".
            invoice: Optional BOLT11 invoice (truncated for display in ``to_dict``).

        Returns:
            The created PaymentRecord.

        NOTE: the preimage is deliberately NOT a parameter — history never stores it.
        """
        # Bound the list — drop oldest entries to make room. Mirrors the .NET cap.
        if len(self._payments) >= MAX_PAYMENT_RECORDS:
            del self._payments[: len(self._payments) - MAX_PAYMENT_RECORDS + 1]

        record = PaymentRecord(
            url=url,
            amount_sats=amount_sats,
            timestamp=datetime.now(timezone.utc),
            status=status,
            invoice=invoice,
        )
        self._payments.append(record)

        if status == "success":
            logger.info(f"Recorded payment: {amount_sats} sats to {url}")

        return record

    def get_history(
        self,
        limit: int = 10,
        since: Optional[datetime] = None,
    ) -> list[PaymentRecord]:
        """
        Get payment history, most recent first.

        Args:
            limit: Maximum number of records to return.
            since: Only return payments at or after this timestamp.

        Returns:
            List of PaymentRecords, most recent first.
        """
        # Pair each record with its insertion index so equal timestamps fall back
        # to insertion order (most-recently-inserted first under reverse sort).
        indexed = list(enumerate(self._payments))
        if since is not None:
            indexed = [(i, r) for i, r in indexed if r.timestamp >= since]
        indexed.sort(key=lambda pair: (pair[1].timestamp, pair[0]), reverse=True)
        records = [r for _, r in indexed]
        if limit is not None and limit >= 0:
            records = records[:limit]
        return records

    @property
    def total_payments(self) -> int:
        """Total number of payment records in this session."""
        return len(self._payments)

    @property
    def total_sats_spent(self) -> int:
        """Total satoshis across successful payments."""
        return sum(r.amount_sats for r in self._payments if r.status == "success")

    def clear(self) -> None:
        """Clear all payment history (e.g. for a new session or testing)."""
        self._payments.clear()


# =============================================================================
# Module-level singleton and factory
# =============================================================================

_default_payment_history_service: Optional[PaymentHistoryService] = None


def get_payment_history_service() -> PaymentHistoryService:
    """Get the default PaymentHistoryService singleton."""
    global _default_payment_history_service
    if _default_payment_history_service is None:
        _default_payment_history_service = PaymentHistoryService()
    return _default_payment_history_service


def create_payment_history_service() -> PaymentHistoryService:
    """Create a fresh PaymentHistoryService instance (own session state)."""
    return PaymentHistoryService()
