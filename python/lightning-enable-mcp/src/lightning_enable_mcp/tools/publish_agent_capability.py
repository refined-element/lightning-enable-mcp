"""
Publish Agent Capability Tool

Publishes an agent capability advertisement to the Nostr network (kind 38400 event),
making the agent discoverable by other agents. Requires LIGHTNING_ENABLE_API_KEY.
"""

import json
import logging
from typing import TYPE_CHECKING, Any

from mcp.types import Tool

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

        if result.get("published") is False:
            return _unpublished(result, service_id, categories, price_sats)

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
                "discovery": (
                    'Other agents can find this via: agent_services(action="discover", '
                    f'category="{categories[0]}")'
                ),
                "settlement": (
                    f"Payments will be settled via L402 at: {endpoint}"
                    if endpoint
                    else "No L402 endpoint configured. Add one for automatic payment settlement."
                ),
                "update": (
                    "Publish once: each publish creates a new listing and a new L402 proxy. "
                    "To change this listing, unpublish it (pass the proxy id at the end of "
                    "l402Endpoint) and publish again, or edit it in the Lightning Enable dashboard."
                ),
            },
        }, indent=2)

    except Exception as e:
        logger.exception("Error publishing capability")
        return json.dumps({
            "success": False,
            "error": f"Error publishing capability: {sanitize_error(str(e))}",
        })


def _proxy_id_from(url: str | None) -> str | None:
    """The proxy id from ``/api/proxy/{id}/verify`` or ``.../l402/proxy/{id}``."""
    if not url:
        return None
    segments = [s for s in url.split("/") if s]
    if "proxy" in segments:
        at = segments.index("proxy")
        if at + 1 < len(segments):
            return segments[at + 1]
    return None


def _unpublished(
    result: dict[str, Any], service_id: str, categories: list[str], price_sats: int
) -> str:
    """The server created the capability's proxy but did not list it.

    Listing a third-party target needs ownership verification first. Says so plainly and
    hands back the one-time verification token (never logged) with what to do next.
    """
    from .producer_verify import methods_from, storage_note, store_token

    proxy_id = (
        result.get("proxyId")
        or _proxy_id_from(result.get("verifyEndpoint"))
        or _proxy_id_from(result.get("l402Endpoint"))
    )
    proxy_arg = proxy_id or "<proxy id>"
    token = result.get("verificationToken")
    instructions = result.get("verificationInstructions")

    verification: dict[str, Any] = {}
    if token:
        # Kept like sell_url's API key: shown once here, stored owner-only beside config.json
        # so l402_producer(action="verify") finds it.
        storage = (
            store_token(None, proxy_id, token, "publish_agent_capability")
            if proxy_id
            else {"written": False, "path": None, "error": "the server sent no proxy id"}
        )
        verification["token"] = token
        verification["tokenNote"] = (
            "This token is shown only this once here, and Lightning Enable keeps only a hash "
            f"of it. {storage_note(storage)} If you lose it, issue a new one with "
            f'l402_producer(action="verification_token", proxy_id="{proxy_arg}").'
        )
        verification["tokenStorage"] = storage
    if isinstance(instructions, dict) and isinstance(instructions.get("tokenPlaceholder"), str):
        verification["tokenPlaceholder"] = instructions["tokenPlaceholder"]
    verification["methods"] = methods_from(instructions)

    first_step = (
        "Publish the token by one of the methods above, replacing <token>."
        if token
        else f'Get a token with l402_producer(action="verification_token", proxy_id="{proxy_arg}").'
    )
    return json.dumps(
        {
            "success": True,
            "published": False,
            "reason": result.get("reason") or "ownership_verification_required",
            "serviceId": service_id,
            "proxyId": proxy_id,
            "categories": categories,
            "priceSats": price_sats,
            "l402Endpoint": result.get("l402Endpoint"),
            "message": (
                f"Capability '{service_id}' was created but NOT published: it is not on Nostr "
                "or in the registry yet, because listing requires proof that you own the "
                "target. Its L402 endpoint already works by its direct URL."
            ),
            "verification": verification,
            "nextSteps": [
                first_step,
                f'Run the check with l402_producer(action="verify", proxy_id="{proxy_arg}").',
                f'Once verified, list it with l402_producer(action="publish", proxy_id="{proxy_arg}").',
            ],
        },
        indent=2,
    )


# MCP tool schema (lives beside its handler; registered in tools/registry.py).
PUBLISH_AGENT_CAPABILITY_TOOL = Tool(
    name="publish_agent_capability",
    description=(
        "Publish an agent capability advertisement to the Nostr network. "
        "Makes your agent discoverable by other agents as a kind 38400 listing, "
        "published via Lightning Enable's L402 proxy pipeline. Provide target_url — "
        "an L402 proxy is created to back the listing and handle payment. The event "
        "is signed by the Lightning Enable platform key. Each call creates a new "
        "listing, so publish once; to change a listing, unpublish it and publish "
        "again. Listing waits for ownership verification: the result says "
        "published=false and returns a one-time token to publish before "
        "l402_producer(action=verify). Requires LIGHTNING_ENABLE_API_KEY."
    ),
    inputSchema={
        "type": "object",
        "properties": {
            "service_id": {
                "type": "string",
                "description": "Unique service identifier (used as d-tag)",
            },
            "categories": {
                "type": "array",
                "items": {"type": "string"},
                "description": "Service categories (e.g., ['ai', 'translation'])",
            },
            "content": {
                "type": "string",
                "description": "Description of the service",
            },
            "price_sats": {
                "type": "integer",
                "description": "Price per request in satoshis",
            },
            "l402_endpoint": {
                "type": "string",
                "description": "L402 endpoint URL for payment settlement",
            },
            "target_url": {
                "type": "string",
                "description": "Target API URL (if auto-creating an L402 proxy via Lightning Enable)",
            },
            "hashtags": {
                "type": "array",
                "items": {"type": "string"},
                "description": "Hashtags for discoverability",
            },
        },
        "required": ["service_id", "categories", "content", "price_sats"],
    },
)
