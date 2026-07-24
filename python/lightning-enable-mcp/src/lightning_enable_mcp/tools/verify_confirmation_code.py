"""
Verify Confirmation Code Tool

Verify a payment confirmation code (relayed by the human from the server console).
VERIFICATION ONLY — this never executes a payment. It appears as a distinct action in
Claude Code so the user sees and can approve/deny the check. To actually pay, the agent
re-calls the original payment tool with confirmation_nonce set to the code.
"""

import json
import logging
from . import sanitize_error
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..budget_service import BudgetService

logger = logging.getLogger("lightning-enable-mcp.tools.verify_confirmation_code")


async def verify_confirmation_code(
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
        JSON with verification result or error message
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
            "valid": True,
            # Retained for backward compatibility with the old confirm_payment shape.
            "confirmed": True,
            "amount_sats": confirmation.amount_sats,
            "tool": confirmation.tool_name,
            "message": (
                f"Code verified — NOTHING HAS BEEN PAID. To execute, call "
                f"{confirmation.tool_name} again with confirmation_nonce={confirmation.nonce}."
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
        logger.exception("Error verifying confirmation code")
        return json.dumps({
            "success": False,
            "error": sanitize_error(str(e))
        })
