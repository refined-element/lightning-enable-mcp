"""Shared setup for the payment tools' approval-channel tests.

The channel changes WHERE the confirmation code goes; it must never change WHETHER the code
reaches the model. So every tool's "does not leak the code" assertion runs across all three
delivering channels, and every tool has a refusing-channel case proving no payment happens
when no human can be asked.
"""

from datetime import datetime, timedelta, timezone
from decimal import Decimal
from unittest.mock import AsyncMock

from lightning_enable_mcp.budget_service import PendingConfirmation
from lightning_enable_mcp.confirmation_channel import (
    ConfirmationChannelKind,
    ConfirmationDispatchResult,
)

DELIVERING_CHANNELS = [
    ConfirmationChannelKind.STDERR,
    ConfirmationChannelKind.WEBHOOK,
    ConfirmationChannelKind.FILE,
]

REFUSAL_REASON = (
    "This payment needs human approval, but this server has no approval channel "
    '(confirmation.channel = "refuse"), so it was refused rather than approved.'
)

_HINTS = {
    ConfirmationChannelKind.STDERR: "printed to the server console/logs",
    ConfirmationChannelKind.WEBHOOK: "sent to the operator's approval webhook",
    ConfirmationChannelKind.FILE: "appended to the approval file",
}


def make_pending(
    nonce: str = "ABC123",
    amount_sats: int = 1000,
    tool_name: str = "pay_invoice",
    destination: str = "test-destination",
    amount_usd: Decimal = Decimal("5.00"),
) -> PendingConfirmation:
    """A realistic pending confirmation for a mocked budget service."""
    now = datetime.now(timezone.utc)
    return PendingConfirmation(
        nonce=nonce,
        amount_sats=amount_sats,
        amount_usd=amount_usd,
        tool_name=tool_name,
        description=destination,
        destination=destination,
        created_at=now,
        expires_at=now + timedelta(minutes=2),
    )


def setup_delivered(
    budget,
    channel: ConfirmationChannelKind = ConfirmationChannelKind.STDERR,
    pending: PendingConfirmation = None,
    **pending_kwargs,
) -> PendingConfirmation:
    """Make the budget mock hand back a delivered confirmation on ``channel``."""
    pending = pending if pending is not None else make_pending(**pending_kwargs)
    budget.request_confirmation = AsyncMock(
        return_value=ConfirmationDispatchResult.delivered_to(
            channel, pending, _HINTS[channel]
        )
    )
    return pending


def setup_refused(budget) -> None:
    """Make the budget mock refuse: no code exists, so nothing can be approved."""
    budget.request_confirmation = AsyncMock(
        return_value=ConfirmationDispatchResult.refused(
            ConfirmationChannelKind.REFUSE, REFUSAL_REASON
        )
    )
