"""
Confirm Payment Tool

Confirm a pending payment using the nonce code from a previous payment request.
This is a separate tool call that appears as a distinct action in Claude Code,
ensuring the user sees and can approve/deny the confirmation.
"""

import json
import logging
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..budget_service import BudgetService

logger = logging.getLogger("lightning-enable-mcp.tools.confirm_payment")


async def confirm_payment(
    nonce: str,
    budget_service: "BudgetService | None" = None,
) -> str:
    """
    Confirm a pending payment using the 6-character nonce code.

    Call this after a payment tool returns requiresConfirmation=true with a nonce.
    The nonce expires after 2 minutes and can only be used once.

    Args:
        nonce: The 6-character confirmation code from the payment request
        budget_service: BudgetService for confirmation validation

    Returns:
        JSON with confirmation result or error message
    """
    if not nonce or not nonce.strip():
        return json.dumps({
            "success": False,
            "error": "Nonce is required"
        })

    if not budget_service:
        return json.dumps({
            "success": False,
            "error": "Budget service not available"
        })

    try:
        confirmation = budget_service.validate_confirmation(nonce.strip().upper())

        if confirmation is None:
            return json.dumps({
                "success": False,
                "error": "Invalid, expired, or already-used confirmation nonce",
                "message": "The nonce may have expired (2 minute limit) or was already used. "
                           "Request a new confirmation by calling the original payment tool again."
            })

        return json.dumps({
            "success": True,
            "confirmed": True,
            "message": f"Payment of ${confirmation.get('amount_usd', 0):.2f} "
                       f"({confirmation.get('amount_sats', 0):,} sats) confirmed",
            "confirmation": {
                "nonce": confirmation.get("nonce"),
                "amountSats": confirmation.get("amount_sats"),
                "amountUsd": round(confirmation.get("amount_usd", 0), 2),
                "toolName": confirmation.get("tool_name"),
                "description": confirmation.get("description"),
            }
        }, indent=2)

    except AttributeError:
        # validate_confirmation may not exist on all BudgetService versions
        return json.dumps({
            "success": False,
            "error": "Confirmation validation not supported by current budget service version",
            "hint": "Upgrade the MCP server to support payment confirmations."
        })
    except Exception as e:
        logger.exception("Error confirming payment")
        return json.dumps({
            "success": False,
            "error": str(e)
        })
