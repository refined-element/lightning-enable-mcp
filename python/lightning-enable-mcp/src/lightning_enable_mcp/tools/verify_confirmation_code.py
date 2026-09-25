"""
Verify Confirmation Code Tool

Verify a payment confirmation code (relayed by the human from the server console).
VERIFICATION ONLY — this never executes a payment. It appears as a distinct action in
Claude Code so the user sees and can approve/deny the check. To actually pay, the agent
re-calls the original payment tool with confirmation_nonce set to the code.
"""

import json
import logging
from typing import TYPE_CHECKING

from mcp.types import Tool

from . import sanitize_error

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
    original payment tool again with confirmation_nonce set to the code. Codes are short-lived
    (120s by default) and one-time use; invalid attempts count toward a guess limit (consumed by the payment tool, bound to its amount+tool).

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
                "message": "The nonce may have expired or was already used. Repeated invalid "
                           "codes revoke every pending confirmation. Request a new confirmation by calling the original payment tool again."
            })

        return json.dumps({
            "success": True,
            "valid": True,
            # Retained for backward compatibility with the old confirm_payment shape.
            "confirmed": True,
            "amount_sats": confirmation.amount_sats,
            "tool": confirmation.tool_name,
            # Neither the code nor the destination is echoed back: a correct guess must not
            # reveal what it unlocks, and the code belongs only on the operator channel.
            "message": (
                f"Code verified — NOTHING HAS BEEN PAID. To execute, call "
                f"{confirmation.tool_name} again with the same code as confirmation_nonce."
            ),
            "confirmation": {
                "amountSats": confirmation.amount_sats,
                "amountUsd": round(float(confirmation.amount_usd), 2),
                "toolName": confirmation.tool_name,
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


# MCP tool schema (lives beside its handler; registered in tools/registry.py).
VERIFY_CONFIRMATION_CODE_TOOL = Tool(
    name="verify_confirmation_code",
    description=(
        "Check whether a payment confirmation code is still valid and what it "
        "authorizes. Verification ONLY - it never pays; to pay, re-call the payment "
        "tool with confirmation_nonce."
    ),
    inputSchema={
        "type": "object",
        "properties": {
            "nonce": {
                "type": "string",
                "description": "The 6-character code from the payment request",
            },
        },
        "required": ["nonce"],
    },
)
