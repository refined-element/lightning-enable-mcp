"""
Verify L402 Payment Tool

Verifies an L402 token (macaroon + preimage) to confirm payment was made.
This is the "producer" side — verify that the payer has actually paid before granting access.
Requires LIGHTNING_ENABLE_API_KEY with an Agentic Commerce subscription.
"""

import json
import logging
from . import sanitize_error
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..lightning_enable_api import LightningEnableApiClient

logger = logging.getLogger("lightning-enable-mcp.tools.verify_l402_payment")


async def verify_l402_payment(
    macaroon: str,
    preimage: str,
    api_client: "LightningEnableApiClient | None" = None,
) -> str:
    """
    Verify an L402 token (macaroon + preimage) to confirm payment was made.

    Use this after receiving an L402 token from a payer to validate they paid
    before granting access to the resource.

    Args:
        macaroon: Base64-encoded macaroon from the L402 token
        preimage: Hex-encoded preimage (proof of payment)
        api_client: Lightning Enable API client instance

    Returns:
        JSON with verification result or error message
    """
    # Input validation
    if not macaroon or not macaroon.strip():
        return json.dumps({
            "success": False,
            "error": "Macaroon is required. This is the base64-encoded macaroon from the L402 token."
        })

    if not preimage or not preimage.strip():
        return json.dumps({
            "success": False,
            "error": "Preimage is required. This is the hex-encoded proof of payment from the L402 token."
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
        result = await api_client.verify_token(macaroon.strip(), preimage.strip())

        if not result.get("success"):
            return json.dumps({
                "success": False,
                "error": result.get("error", "Unknown error verifying token")
            })

        if result.get("valid"):
            return json.dumps({
                "success": True,
                "valid": True,
                "resource": result.get("resource"),
                "message": "Payment verified. The payer has paid — you can now grant access to the resource."
            })
        else:
            return json.dumps({
                "success": True,
                "valid": False,
                "message": "Payment verification failed. The token is invalid or the invoice has not been paid. Do NOT grant access."
            })

    except Exception as e:
        logger.exception("Error verifying L402 payment")
        return json.dumps({
            "success": False,
            "error": sanitize_error(str(e))
        })
