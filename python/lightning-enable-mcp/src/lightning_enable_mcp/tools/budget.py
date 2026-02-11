"""
Budget Tools

Configure spending limits and view payment history.
"""

import json
import logging
from datetime import datetime, timezone
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..budget import BudgetManager

logger = logging.getLogger("lightning-enable-mcp.tools.budget")


async def configure_budget(
    per_request: int = 1000,
    per_session: int = 10000,
    budget_manager: "BudgetManager | None" = None,
) -> str:
    """
    Set spending limits for the session.

    Args:
        per_request: Maximum satoshis per individual request
        per_session: Maximum total satoshis for the entire session
        budget_manager: Budget manager instance

    Returns:
        JSON with confirmation of limits set
    """
    if not budget_manager:
        return json.dumps(
            {"success": False, "error": "Budget manager not initialized"}
        )

    try:
        # Validate inputs
        if per_request <= 0:
            return json.dumps(
                {"success": False, "error": "per_request must be positive"}
            )

        if per_session <= 0:
            return json.dumps(
                {"success": False, "error": "per_session must be positive"}
            )

        if per_request > per_session:
            return json.dumps(
                {
                    "success": False,
                    "error": "per_request cannot exceed per_session",
                }
            )

        # Update limits
        limits = budget_manager.configure(
            per_request=per_request,
            per_session=per_session,
        )

        # Get current status
        status = budget_manager.get_status()

        result = {
            "success": True,
            "limits": {
                "per_request": limits.per_request,
                "per_session": limits.per_session,
            },
            "current_status": {
                "spent": status["spent"],
                "remaining": status["remaining"],
                "payment_count": status["payment_count"],
            },
            "message": (
                f"Budget configured: {limits.per_request} sats per request, "
                f"{limits.per_session} sats per session. "
                f"Remaining: {status['remaining']} sats."
            ),
        }

        return json.dumps(result, indent=2)

    except Exception as e:
        logger.exception("Error configuring budget")
        return json.dumps({"success": False, "error": str(e)})


async def get_payment_history(
    limit: int = 10,
    since: str | None = None,
    budget_manager: "BudgetManager | None" = None,
) -> str:
    """
    List recent L402 payments made during this session.

    Args:
        limit: Maximum number of payments to return
        since: ISO timestamp to filter payments from
        budget_manager: Budget manager instance

    Returns:
        JSON with list of payments
    """
    if not budget_manager:
        return json.dumps(
            {"success": False, "error": "Budget manager not initialized"}
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

        # Get payment history
        payments = budget_manager.get_history(limit=limit, since=since_dt)

        # Get budget status
        status = budget_manager.get_status()

        result = {
            "success": True,
            "payments": [p.to_dict() for p in payments],
            "count": len(payments),
            "total_payments": status["payment_count"],
            "session_summary": {
                "total_spent": status["spent"],
                "remaining_budget": status["remaining"],
                "per_request_limit": status["limits"]["per_request"],
                "per_session_limit": status["limits"]["per_session"],
            },
        }

        if payments:
            result["message"] = (
                f"Showing {len(payments)} of {status['payment_count']} payments. "
                f"Total spent: {status['spent']} sats."
            )
        else:
            result["message"] = "No payments recorded in this session."

        return json.dumps(result, indent=2)

    except Exception as e:
        logger.exception("Error getting payment history")
        return json.dumps({"success": False, "error": str(e)})
