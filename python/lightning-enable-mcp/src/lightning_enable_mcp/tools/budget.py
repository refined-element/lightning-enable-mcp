"""
Budget Tools

Tighten (lower) session spending limits and view payment history.

These repoint onto the single source of truth:
- configure_budget  -> BudgetService.configure_budget (tighten-only runtime caps)
- get_payment_history -> PaymentHistoryService (separate audit trail)

This mirrors the .NET split (BudgetService + PaymentHistoryService); the legacy
BudgetManager has been removed.
"""

import json
import logging
from . import sanitize_error
from datetime import datetime, timezone
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..budget_service import BudgetService
    from ..payment_history_service import PaymentHistoryService

logger = logging.getLogger("lightning-enable-mcp.tools.budget")


async def configure_budget(
    per_request: int = 1000,
    per_session: int = 10000,
    budget_service: "BudgetService | None" = None,
) -> str:
    """
    TIGHTEN (lower) the session spending limits, in sats.

    An agent can only LOWER its caps — never raise them above the operator's
    config-file limits (or an existing tighter runtime cap). To raise limits,
    the operator edits ~/.lightning-enable/config.json.

    Args:
        per_request: Maximum satoshis per individual request.
        per_session: Maximum total satoshis for the entire session.
        budget_service: BudgetService (single source of truth for limits).

    Returns:
        JSON with the new effective caps, or an error.
    """
    if not budget_service:
        return json.dumps(
            {"success": False, "error": "Budget service not initialized"}
        )

    try:
        result = await budget_service.configure_budget(
            per_request_sats=per_request,
            per_session_sats=per_session,
        )

        if not result.success:
            return json.dumps({"success": False, "error": result.error})

        return json.dumps(
            {
                "success": True,
                "limits": {
                    "per_request": result.effective_per_request_sats,
                    "per_session": result.effective_per_session_sats,
                },
                "message": (
                    f"Budget tightened to {result.effective_per_request_sats:,} sats/request and "
                    f"{result.effective_per_session_sats:,} sats/session. These runtime caps can "
                    "only be lowered further; raising them requires editing "
                    "~/.lightning-enable/config.json."
                ),
            },
            indent=2,
        )

    except Exception as e:
        logger.exception("Error configuring budget")
        return json.dumps({"success": False, "error": sanitize_error(str(e))})


async def get_payment_history(
    limit: int = 10,
    since: str | None = None,
    payment_history_service: "PaymentHistoryService | None" = None,
) -> str:
    """
    List recent payments made during this session.

    Args:
        limit: Maximum number of payments to return.
        since: ISO timestamp to filter payments from.
        payment_history_service: PaymentHistoryService (audit trail).

    Returns:
        JSON with the list of payments.
    """
    if not payment_history_service:
        return json.dumps(
            {"success": False, "error": "Payment history service not initialized"}
        )

    try:
        # Parse since timestamp if provided
        since_dt = None
        if since:
            try:
                since_dt = datetime.fromisoformat(since.replace("Z", "+00:00"))
            except ValueError:
                return json.dumps(
                    {
                        "success": False,
                        "error": f"Invalid timestamp format: {since}. Use ISO format.",
                    }
                )
            # An ISO string without an offset parses as naive; assume UTC so it can be
            # compared to the UTC-aware record timestamps (otherwise get_history raises
            # TypeError on naive-vs-aware comparison).
            if since_dt.tzinfo is None:
                since_dt = since_dt.replace(tzinfo=timezone.utc)

        payments = payment_history_service.get_history(limit=limit, since=since_dt)
        total_payments = payment_history_service.total_payments
        total_spent = payment_history_service.total_sats_spent

        result = {
            "success": True,
            "payments": [p.to_dict() for p in payments],
            "count": len(payments),
            "total_payments": total_payments,
            "session_summary": {
                "total_spent": total_spent,
            },
        }

        if payments:
            result["message"] = (
                f"Showing {len(payments)} of {total_payments} payments. "
                f"Total spent: {total_spent} sats."
            )
        else:
            result["message"] = "No payments recorded in this session."

        return json.dumps(result, indent=2)

    except Exception as e:
        logger.exception("Error getting payment history")
        return json.dumps({"success": False, "error": sanitize_error(str(e))})
