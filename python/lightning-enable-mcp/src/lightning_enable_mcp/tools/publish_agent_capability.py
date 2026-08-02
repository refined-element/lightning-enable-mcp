"""
Publish Agent Capability Tool

Publishes an agent capability advertisement to the Nostr network (kind 38400 event),
making the agent discoverable by other agents. Requires LIGHTNING_ENABLE_API_KEY.
"""

import json
import logging
from typing import TYPE_CHECKING

from . import sanitize_error

if TYPE_CHECKING:
    from ..lightning_enable_api import LightningEnableApiClient

logger = logging.getLogger("lightning-enable-mcp.tools.publish_agent_capability")


async def publish_agent_capability(
    service_id: str,
    categories: list[str],
    content: str,
    price_sats: int,
    l402_endpoint: str | None = None,
    target_url: str | None = None,
    hashtags: list[str] | None = None,
    api_client: "LightningEnableApiClient | None" = None,
) -> str:
    """
    Publish an agent capability advertisement to the Nostr network.

    Makes your agent discoverable by other agents. Creates a kind 38400 event.
    Optionally creates an L402 proxy for payment settlement.
    Requires LIGHTNING_ENABLE_API_KEY.

    Args:
        service_id: Unique service identifier (used as d-tag)
        categories: Service categories (e.g., ['ai', 'translation'])
        content: Description of the service
        price_sats: Price per request in satoshis
        l402_endpoint: L402 endpoint URL for payment settlement
        target_url: Target API URL (if auto-creating an L402 proxy)
        hashtags: Hashtags for discoverability
        api_client: Lightning Enable API client instance

    Returns:
        JSON with publish result or an error message.
    """
    try:
        # Input validation
        if not service_id or not service_id.strip():
            return json.dumps({
                "success": False,
                "error": "Service ID is required. Provide a unique identifier for your service (e.g., 'my-translation-service').",
            })

        if not categories or len(categories) == 0:
            return json.dumps({
                "success": False,
                "error": "At least one category is required (e.g., ['ai', 'translation']).",
            })

        if not content or not content.strip():
            return json.dumps({
                "success": False,
                "error": "Service description (content) is required.",
            })

        if price_sats <= 0:
            return json.dumps({
                "success": False,
                "error": "Price must be greater than 0 sats.",
            })

        if api_client is None:
            return json.dumps({
                "success": False,
                "error": "Agent service not available. The MCP server may not be configured correctly.",
            })

        if not api_client.is_configured:
            return json.dumps({
                "success": False,
                "error": "Lightning Enable API key not configured. "
                         "Set LIGHTNING_ENABLE_API_KEY environment variable or add 'lightningEnableApiKey' to ~/.lightning-enable/config.json. "
                         "Required for publishing agent capabilities. "
                         "Get an API key: 30-day free trial at "
                         "https://api.lightningenable.com/Checkout?plan=individual&utm_source=mcp&utm_medium=tool-hint&utm_campaign=gtm-aug-2026 "
                         "— or call the `create_lightning_enable_account` tool to sign up right here.",
            })

        result = await api_client.publish_capability(
            service_id, categories, content, price_sats,
            l402_endpoint, target_url, hashtags,
        )

        if not result.get("success"):
            return json.dumps({
                "success": False,
                "error": result.get("error", "Unknown error publishing capability"),
            })

        endpoint = result.get("l402Endpoint")
        return json.dumps({
            "success": True,
            "eventId": result.get("eventId"),
            "serviceId": service_id,
            "categories": categories,
            "priceSats": price_sats,
            "l402Endpoint": endpoint,
            "message": f"Agent capability '{service_id}' published successfully as kind 38400 event.",
            "nextSteps": {
                "discovery": f'Other agents can find this via: discover_agent_services(category="{categories[0]}")',
                "settlement": (
                    f"Payments will be settled via L402 at: {endpoint}"
                    if endpoint
                    else "No L402 endpoint configured. Add one for automatic payment settlement."
                ),
                "update": f"Republish with the same serviceId ('{service_id}') to update the capability.",
            },
        }, indent=2)

    except Exception as e:
        logger.exception("Error publishing capability")
        return json.dumps({
            "success": False,
            "error": f"Error publishing capability: {sanitize_error(str(e))}",
        })
