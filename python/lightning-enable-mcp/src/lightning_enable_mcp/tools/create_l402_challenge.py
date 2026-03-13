"""
Create L402 Challenge Tool

Creates an L402 payment challenge (Lightning invoice + macaroon) for a resource.
This is the "producer" side of L402 — merchants create challenges, payers pay them.
Requires LIGHTNING_ENABLE_API_KEY with an Agentic Commerce subscription.
"""

import json
import logging
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..lightning_enable_api import LightningEnableApiClient

logger = logging.getLogger("lightning-enable-mcp.tools.create_l402_challenge")


async def create_l402_challenge(
    resource: str,
    price_sats: int,
    description: str | None = None,
    api_client: "LightningEnableApiClient | None" = None,
) -> str:
    """
    Create an L402 payment challenge to charge another agent or user for accessing a resource.

    Returns a Lightning invoice and macaroon. The payer must pay the invoice and present
    the L402 token (macaroon:preimage) back to you for verification.

    Args:
        resource: Resource identifier - URL, service name, or description of what you're charging for
        price_sats: Price in satoshis to charge
        description: Description shown on the Lightning invoice
        api_client: Lightning Enable API client instance

    Returns:
        JSON with challenge details (invoice, macaroon, paymentHash) or error message
    """
    # Input validation
    if not resource or not resource.strip():
        return json.dumps({
            "success": False,
            "error": "Resource identifier is required. Provide a URL, service name, or description of what you're charging for."
        })

    if price_sats <= 0:
        return json.dumps({
            "success": False,
            "error": "Price must be greater than 0 sats"
        })

    if api_client is None:
        return json.dumps({
            "success": False,
            "error": "Lightning Enable API service not available"
        })

    if not api_client.is_configured:
        return json.dumps({
            "success": False,
            "error": "Lightning Enable API key not configured. "
                     "Set LIGHTNING_ENABLE_API_KEY environment variable or add 'lightningEnableApiKey' to ~/.lightning-enable/config.json. "
                     "Requires an Agentic Commerce subscription at https://lightningenable.com."
        })

    try:
        result = await api_client.create_challenge(resource, price_sats, description)

        if not result.get("success"):
            return json.dumps({
                "success": False,
                "error": result.get("error", "Unknown error creating challenge")
            })

        challenge = result.get("challenge", {})
        macaroon = challenge.get("macaroon", "")

        return json.dumps({
            "success": True,
            "challenge": {
                "invoice": challenge.get("invoice"),
                "macaroon": macaroon,
                "paymentHash": challenge.get("paymentHash"),
                "expiresAt": challenge.get("expiresAt"),
            },
            "resource": resource,
            "priceSats": price_sats,
            "instructions": {
                "forPayer": f"Pay the Lightning invoice, then present the L402 token: 'L402 {macaroon}:<preimage>' "
                           "where <preimage> is the proof of payment received after paying the invoice.",
                "tokenFormat": "L402 {macaroon}:{preimage}",
                "verifyWith": "After receiving the L402 token from the payer, use verify_l402_payment to confirm payment before granting access."
            },
            "message": f"L402 challenge created for {price_sats} sats. Share the invoice with the payer."
        }, indent=2)

    except Exception as e:
        logger.exception("Error creating L402 challenge")
        return json.dumps({
            "success": False,
            "error": str(e)
        })
