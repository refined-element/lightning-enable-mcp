"""
Budget Status Tool

Get current budget status from the BudgetService (read-only).
Uses the new multi-tier approval system with USD-based limits.
"""

import json
import logging
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..budget_service import BudgetService

logger = logging.getLogger("lightning-enable-mcp.tools.budget_status")


async def get_budget_status(
    budget_service: "BudgetService | None" = None,
) -> str:
    """
    View current budget status and spending limits (read-only).

    Returns the complete budget configuration, session state, and BTC price info.
    Configuration is READ-ONLY - edit ~/.lightning-enable/config.json to change limits.

    Args:
        budget_service: BudgetService instance for budget tracking

    Returns:
        JSON with complete budget status including:
        - configuration: All config settings (tiers, limits, session)
        - session: Current session state (spent, remaining, request count)
        - price: Current BTC/USD price info
        - note: Reminder that config is read-only
    """
    if not budget_service:
        # Try to get the global singleton
        try:
            from ..budget_service import get_budget_service
            budget_service = get_budget_service()
        except Exception as e:
            return json.dumps({
                "success": False,
                "error": f"Budget service not initialized: {e}",
                "hint": "Budget service is initialized on first use. Try making a payment first."
            })

    try:
        status = budget_service.get_status()
        return json.dumps({
            "success": True,
            **status,
        }, indent=2)
    except Exception as e:
        logger.exception("Error getting budget status")
        return json.dumps({
            "success": False,
            "error": str(e)
        })
