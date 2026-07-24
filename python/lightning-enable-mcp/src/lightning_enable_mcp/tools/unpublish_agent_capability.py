"""
Unpublish Agent Capability Tool

Takes a previously published capability down (NIP-A5 listing lifecycle). In
`remove` mode the backend soft-retires the L402 proxy and emits the on-Nostr
removal (a NIP-09 kind 5 deletion plus a status=removed 38400 replacement), so
agents watching the marketplace stop seeing a dead endpoint. Requires
LIGHTNING_ENABLE_API_KEY (same auth/signing path as publish).
"""

import json
import logging
import re
from typing import TYPE_CHECKING

from . import sanitize_error

if TYPE_CHECKING:
    from ..lightning_enable_api import LightningEnableApiClient

logger = logging.getLogger("lightning-enable-mcp.tools.unpublish_agent_capability")

_HEX64 = re.compile(r"^[0-9a-f]{64}$", re.IGNORECASE)
_VALID_MODES = ("remove", "pause")


async def unpublish_agent_capability(
    pubkey: str,
    service_id: str,
    mode: str = "remove",
    reason: str | None = None,
    api_client: "LightningEnableApiClient | None" = None,
) -> str:
    """
    Take a published agent capability down.

    Args:
        pubkey: Nostr public key (64-hex) of the agent that owns the listing.
        service_id: The listing's service identifier (its d-tag / proxy id).
        mode: "remove" (default) permanently withdraws the listing and retires
            its L402 proxy; "pause" is reserved (not yet supported by the backend).
        reason: Optional free-text reason recorded on the removal event.
        api_client: Lightning Enable API client instance.

    Returns:
        JSON with the unpublish result or an error message.
    """
    try:
        if not pubkey or not _HEX64.match(pubkey.strip()):
            return json.dumps({
                "success": False,
                "error": "A valid 64-character hex Nostr pubkey is required.",
            })

        if not service_id or not service_id.strip():
            return json.dumps({
                "success": False,
                "error": "Service ID is required (the d-tag of the listing to take down).",
            })

        normalized_mode = (mode or "remove").strip().lower()
        if normalized_mode not in _VALID_MODES:
            return json.dumps({
                "success": False,
                "error": f"Mode must be one of {list(_VALID_MODES)} (got '{mode}').",
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
                         "Required for unpublishing agent capabilities.",
            })

        result = await api_client.unpublish_capability(
            pubkey.strip(), service_id.strip(), normalized_mode, reason,
        )

        if not result.get("success"):
            return json.dumps({
                "success": False,
                "error": result.get("error", "Unknown error unpublishing capability"),
            })

        return json.dumps({
            "success": True,
            "serviceId": result.get("serviceId", service_id),
            "proxyId": result.get("proxyId"),
            "mode": result.get("mode", normalized_mode),
            "retired": result.get("retired"),
            "message": (
                f"Capability '{service_id}' removed: the L402 proxy is retired and a "
                "NIP-09 deletion (+ status=removed replacement) was published to Nostr."
            ),
        }, indent=2)

    except Exception as e:
        logger.exception("Error unpublishing capability")
        return json.dumps({
            "success": False,
            "error": f"Error unpublishing capability: {sanitize_error(str(e))}",
        })
