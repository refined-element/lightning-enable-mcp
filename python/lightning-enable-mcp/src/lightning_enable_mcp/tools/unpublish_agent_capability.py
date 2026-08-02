"""
Unpublish Agent Capability Tool

Takes a previously published listing down. Targets the L402 proxy management API
(the *ungated* pipeline the live marketplace listings use): it soft-retires the
proxy and emits the on-Nostr removal — a NIP-09 kind 5 deletion plus a
status=removed 38400 replacement — so agents watching the marketplace stop
seeing a dead endpoint. Requires LIGHTNING_ENABLE_API_KEY.
"""

import json
import logging
from typing import TYPE_CHECKING

from . import sanitize_error

if TYPE_CHECKING:
    from ..lightning_enable_api import LightningEnableApiClient

logger = logging.getLogger("lightning-enable-mcp.tools.unpublish_agent_capability")


async def unpublish_agent_capability(
    service_id: str,
    reason: str | None = None,
    api_client: "LightningEnableApiClient | None" = None,
) -> str:
    """
    Take a published listing down.

    Args:
        service_id: The listing's identifier — its Nostr d-tag / proxy id
            (e.g. the value after the last ':' in the card's nw: footer).
        reason: Optional free-text reason recorded on the removal event.
        api_client: Lightning Enable API client instance.

    Returns:
        JSON with the unpublish result or an error message.
    """
    try:
        if not service_id or not service_id.strip():
            return json.dumps({
                "success": False,
                "error": "Service ID is required (the listing's d-tag / proxy id to take down).",
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
                         "Required for unpublishing listings. "
                         "Get an API key: 30-day free trial at "
                         "https://api.lightningenable.com/Checkout?plan=individual&utm_source=mcp&utm_medium=tool-hint&utm_campaign=gtm-aug-2026 "
                         "— or call the `create_lightning_enable_account` tool to sign up right here.",
            })

        result = await api_client.unpublish_capability(service_id.strip(), reason)

        if not result.get("success"):
            return json.dumps({
                "success": False,
                "error": result.get("error", "Unknown error unpublishing listing"),
            })

        already = result.get("alreadyRetired")
        return json.dumps({
            "success": True,
            "serviceId": service_id,
            "proxyId": result.get("proxyId", service_id),
            "retired": result.get("retired"),
            "alreadyRetired": already,
            "message": (
                f"Listing '{service_id}' was already retired." if already else
                f"Listing '{service_id}' removed: the L402 proxy is retired and a "
                "NIP-09 deletion (+ status=removed replacement) was published to Nostr."
            ),
        }, indent=2)

    except Exception as e:
        logger.exception("Error unpublishing listing")
        return json.dumps({
            "success": False,
            "error": f"Error unpublishing listing: {sanitize_error(str(e))}",
        })
