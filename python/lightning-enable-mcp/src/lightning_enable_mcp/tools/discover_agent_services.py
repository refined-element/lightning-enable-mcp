"""
Discover Agent Services Tool

Discovers agent services on the Nostr network (kind 38400 capability events).
Search by category, hashtag, or keyword. Works without an API key via the
public registry.
"""

import json
import logging
from typing import TYPE_CHECKING

from . import sanitize_error

if TYPE_CHECKING:
    from ..budget_service import BudgetService
    from ..lightning_enable_api import LightningEnableApiClient

logger = logging.getLogger("lightning-enable-mcp.tools.discover_agent_services")


async def discover_agent_services(
    category: str | None = None,
    hashtags: list[str] | None = None,
    query: str | None = None,
    limit: int = 20,
    api_client: "LightningEnableApiClient | None" = None,
    budget_service: "BudgetService | None" = None,
) -> str:
    """
    Discover agent services by category, hashtag, or keyword search.

    Returns capabilities published as kind 38400 events. Use this to find
    agents that offer services you can pay for via L402.

    Args:
        category: Filter by service category (e.g., 'ai', 'data', 'translation')
        hashtags: Filter by hashtags
        query: Search query
        limit: Maximum results to return (default 20)
        api_client: Lightning Enable API client instance
        budget_service: Optional BudgetService for affordability annotations

    Returns:
        JSON with discovered capabilities or an error message.
    """
    try:
        # Validate that at least one filter is provided
        if (
            (not category or not category.strip())
            and not hashtags
            and (not query or not query.strip())
        ):
            return json.dumps({
                "success": False,
                "error": "Please provide at least one search filter: 'category', 'hashtags', or 'query'.",
                "examples": [
                    {"description": "Find AI services", "call": 'discover_agent_services(category="ai")'},
                    {"description": "Search for translation", "call": 'discover_agent_services(query="translation")'},
                    {"description": "Browse by hashtag", "call": 'discover_agent_services(hashtags=["weather", "forecast"])'},
                ],
            }, indent=2)

        if api_client is None:
            return json.dumps({
                "success": False,
                "error": "Agent service not available. The MCP server may not be configured correctly.",
            })

        result = await api_client.discover_capabilities(category, hashtags, query, limit)

        if not result.get("success"):
            return json.dumps({
                "success": False,
                "error": result.get("error", "Discovery failed"),
                "hint": "The agent capability registry may be temporarily unavailable. Try again later.",
            })

        capabilities = result.get("capabilities", [])

        # Budget info (best-effort)
        budget_info = None
        remaining_session_sats: int | None = None
        if budget_service is not None:
            try:
                status = budget_service.get_status()
                remaining_usd = status["session"]["remainingUsd"]
                btc_price = status["price"]["btcUsd"]
                if btc_price:
                    remaining_session_sats = int(
                        (remaining_usd / btc_price) * 100_000_000
                    )
                budget_info = {
                    "remaining_usd": remaining_usd,
                    "session_spent_usd": status["session"]["spentUsd"],
                }
            except Exception:
                budget_info = None

        # Format capabilities for agent consumption
        formatted = []
        for cap in capabilities:
            entry: dict = {}
            if cap.get("eventId"):
                entry["event_id"] = cap["eventId"]
            if cap.get("serviceId"):
                entry["service_id"] = cap["serviceId"]
            if cap.get("pubkey"):
                entry["pubkey"] = cap["pubkey"]
            if cap.get("content"):
                entry["description"] = cap["content"]
            if cap.get("categories"):
                entry["categories"] = cap["categories"]
            if cap.get("hashtags"):
                entry["hashtags"] = cap["hashtags"]
            price_sats = cap.get("priceSats", 0) or 0
            entry["price_sats"] = price_sats
            if cap.get("l402Endpoint"):
                entry["l402_endpoint"] = cap["l402Endpoint"]
            if cap.get("createdAt") is not None:
                entry["created_at"] = cap["createdAt"]

            # Budget annotation — guard against division by zero
            if remaining_session_sats is not None and price_sats > 0:
                entry["affordable_calls"] = remaining_session_sats // price_sats

            formatted.append(entry)

        return json.dumps({
            "success": True,
            "query": query,
            "category": category,
            "hashtags": hashtags,
            "results": formatted,
            "total": result.get("total", len(formatted)),
            "budget": budget_info,
            "hint": (
                'Use request_agent_service(capability_event_id="<event_id>") to request a service, '
                'or settle_agent_service(l402_endpoint="<url>") to pay and access it directly via L402.'
                if formatted
                else "No agent services found. Try different keywords or categories."
            ),
        }, indent=2)

    except Exception as e:
        logger.exception("Error discovering agent services")
        return json.dumps({
            "success": False,
            "error": f"Error discovering agent services: {sanitize_error(str(e))}",
        })
