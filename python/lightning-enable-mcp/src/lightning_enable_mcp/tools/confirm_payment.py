"""
Confirm Payment Tool

Confirm a pending payment using the nonce code from a previous payment request.
This is a separate tool call that appears as a distinct action in Claude Code,
ensuring the user sees and can approve/deny the confirmation.
"""

import json
import logging
from . import sanitize_error
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..budget_service import BudgetService

logger = logging.getLogger("lightning-enable-mcp.tools.confirm_payment")


async def confirm_payment(
    nonce: str,
    budget_service: "BudgetService | None" = None,
) -> str:
    """
    Verify a confirmation code that the HUMAN operator read from the server console.

    For payments above the auto-approve threshold, the server prints a code to its
    console/stderr (never in a tool result). The human reads it and gives it to you. This
    tool only VERIFIES the code (it does not consume it or pay) — to actually pay, call the
    original payment tool again with confirmation_nonce set to the code. Codes expire after
    2 minutes and are one-time use (consumed by the payment tool, bound to its amount+tool).

    Args:
        nonce: The confirmation code the human read from the server console
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
            "message": (
                f"Confirmation code is valid for a payment of ${float(confirmation.amount_usd):.2f} "
                f"({confirmation.amount_sats:,} sats) via {confirmation.tool_name}. To actually pay, call "
                "that tool again with confirmation_nonce set to this code (it is consumed then, one-time)."
            ),
            "confirmation": {
                "nonce": confirmation.nonce,
                "amountSats": confirmation.amount_sats,
                "amountUsd": round(float(confirmation.amount_usd), 2),
                "toolName": confirmation.tool_name,
                "description": confirmation.description,
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
            "error": sanitize_error(str(e))
        })
