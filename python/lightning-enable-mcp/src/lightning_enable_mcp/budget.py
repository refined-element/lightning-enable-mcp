"""
Budget Manager

Tracks spending limits and payment history for L402 sessions.
"""

import logging
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Optional

logger = logging.getLogger("lightning-enable-mcp.budget")


class BudgetExceededError(Exception):
    """Exception when a payment would exceed budget limits."""

    pass


@dataclass
class PaymentRecord:
    """Record of a single L402 payment."""

    url: str
    amount_sats: int
    timestamp: datetime
    invoice: str
    preimage: str
    status: str = "success"  # "success", "failed", "pending"

    def to_dict(self) -> dict:
        """Convert to dictionary for serialization."""
        return {
            "url": self.url,
            "amount_sats": self.amount_sats,
            "timestamp": self.timestamp.isoformat(),
            "invoice": self.invoice[:20] + "..." if len(self.invoice) > 20 else self.invoice,
            "status": self.status,
        }


@dataclass
class BudgetLimits:
    """Budget limit configuration."""

    per_request: int = 10000  # Max sats per request (~$10 at 100k sats/$)
    per_session: int = 100000  # Max sats for entire session (~$100)
    # Approval thresholds (in sats) - payments above auto_approve require confirmation
    auto_approve: int = 1000  # ~$1 - auto-approve without prompt
    log_and_approve: int = 5000  # ~$5 - approve but log
    require_confirm: int = 25000  # ~$25 - require explicit confirmation


@dataclass
class BudgetManager:
    """
    Manages spending limits and tracks payment history.

    NOTE: Most MCP clients (including Claude Code) don't support elicitation yet.
    When confirmation is required and elicitation isn't available, tools will
    ask users to re-invoke with confirmed=True.

    Attributes:
        max_per_request: Maximum satoshis allowed per single request
        max_per_session: Maximum satoshis allowed for entire session
        auto_approve_sats: Payments at or below this are auto-approved
        session_spent: Total satoshis spent in this session
        payments: List of payment records
    """

    max_per_request: int = 10000
    max_per_session: int = 100000
    auto_approve_sats: int = 1000  # ~$1 - payments above this need confirmation
    session_spent: int = 0
    payments: list[PaymentRecord] = field(default_factory=list)

    def check_payment(self, amount_sats: int, max_override: int | None = None) -> None:
        """
        Check if a payment is within budget limits.

        Args:
            amount_sats: Amount to check in satoshis
            max_override: Optional per-request limit override

        Raises:
            BudgetExceededError: If payment would exceed limits
        """
        effective_max = min(
            max_override if max_override is not None else self.max_per_request,
            self.max_per_request,
        )

        # Check per-request limit
        if amount_sats > effective_max:
            raise BudgetExceededError(
                f"Payment of {amount_sats} sats exceeds per-request limit of {effective_max} sats"
            )

        # Check session limit
        remaining = self.max_per_session - self.session_spent
        if amount_sats > remaining:
            raise BudgetExceededError(
                f"Payment of {amount_sats} sats would exceed session limit. "
                f"Remaining budget: {remaining} sats"
            )

    def record_payment(
        self,
        url: str,
        amount_sats: int,
        invoice: str,
        preimage: str,
        status: str = "success",
    ) -> PaymentRecord:
        """
        Record a completed payment.

        Args:
            url: URL that was accessed
            amount_sats: Amount paid in satoshis
            invoice: BOLT11 invoice that was paid
            preimage: Payment preimage
            status: Payment status

        Returns:
            The created PaymentRecord
        """
        record = PaymentRecord(
            url=url,
            amount_sats=amount_sats,
            timestamp=datetime.now(timezone.utc),
            invoice=invoice,
            preimage=preimage,
            status=status,
        )

        self.payments.append(record)

        if status == "success":
            self.session_spent += amount_sats
            logger.info(
                f"Recorded payment: {amount_sats} sats to {url}. "
                f"Session total: {self.session_spent}/{self.max_per_session} sats"
            )

        return record

    def get_history(
        self,
        limit: int = 10,
        since: datetime | None = None,
    ) -> list[PaymentRecord]:
        """
        Get payment history.

        Args:
            limit: Maximum number of records to return
            since: Only return payments after this timestamp

        Returns:
            List of PaymentRecords, most recent first
        """
        records = self.payments

        if since:
            records = [r for r in records if r.timestamp >= since]

        # Sort by timestamp descending
        records = sorted(records, key=lambda r: r.timestamp, reverse=True)

        return records[:limit]

    def configure(
        self,
        per_request: int | None = None,
        per_session: int | None = None,
    ) -> BudgetLimits:
        """
        Update budget limits.

        Args:
            per_request: New per-request limit
            per_session: New session limit

        Returns:
            Updated BudgetLimits
        """
        if per_request is not None:
            self.max_per_request = per_request
            logger.info(f"Updated per-request limit to {per_request} sats")

        if per_session is not None:
            self.max_per_session = per_session
            logger.info(f"Updated per-session limit to {per_session} sats")

        return BudgetLimits(
            per_request=self.max_per_request,
            per_session=self.max_per_session,
        )

    def get_status(self) -> dict:
        """
        Get current budget status.

        Returns:
            Dict with budget status information
        """
        return {
            "limits": {
                "per_request": self.max_per_request,
                "per_session": self.max_per_session,
            },
            "spent": self.session_spent,
            "remaining": self.max_per_session - self.session_spent,
            "payment_count": len(self.payments),
        }
