"""
Budget Status Tool

Get current budget status from the BudgetService (read-only).
Uses the new multi-tier approval system with USD-based limits.
"""

import json
import logging
from . import sanitize_error
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

    # Trigger a fresh BTC price fetch so the displayed price isn't stale.
    # If the price service raises, get_status() falls back to "unavailable"
    # and the response surfaces the error rather than guessing.
    price_error: str | None = None
    try:
        from ..price_service import get_price_service, PriceUnavailableError
        try:
            await get_price_service().get_btc_price()
        except PriceUnavailableError as ex:
            price_error = str(ex)
            logger.warning("Could not refresh BTC price for budget status: %s", ex)
    except Exception as ex:  # pragma: no cover - defensive
        logger.exception("Unexpected error refreshing BTC price")
        price_error = sanitize_error(str(ex))

    try:
        status = budget_service.get_status()
        if price_error and isinstance(status.get("price"), dict):
            status["price"]["error"] = price_error
        return json.dumps({
            "success": True,
            **status,
        }, indent=2)
    except Exception as e:
        logger.exception("Error getting budget status")
        return json.dumps({
            "success": False,
            "error": sanitize_error(str(e))
        })
