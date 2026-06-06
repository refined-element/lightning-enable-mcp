"""
Budget Tools

Configure spending limits and view payment history.
"""

import json
import logging
from . import sanitize_error
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

        # SECURITY (PY-CONFIGURE): an agent must NOT be able to RAISE its own
        # spending caps — that would let a prompt-injected agent loosen the limits
        # and then drain the wallet. configure_budget may only TIGHTEN (lower)
        # limits, never increase them above the operator-set values (from
        # ~/.lightning-enable/config.json / env). To raise limits, the operator
        # edits the config file.
        current = budget_manager.get_status().get("limits", {})
        current_per_request = current.get("per_request")
        current_per_session = current.get("per_session")
        if (current_per_request is not None and per_request > current_per_request) or (
            current_per_session is not None and per_session > current_per_session
        ):
            return json.dumps(
                {
                    "success": False,
                    "error": (
                        "configure_budget can only LOWER spending limits, not raise them. "
                        f"Current caps: {current_per_request} sats/request, "
                        f"{current_per_session} sats/session. To increase limits, the operator "
                        "must edit ~/.lightning-enable/config.json — an agent cannot raise its "
                        "own spending authority."
                    ),
                }
            )

        # Update limits (tighten-only, validated above)
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
        return json.dumps({"success": False, "error": sanitize_error(str(e))})


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
        return json.dumps({"success": False, "error": sanitize_error(str(e))})
