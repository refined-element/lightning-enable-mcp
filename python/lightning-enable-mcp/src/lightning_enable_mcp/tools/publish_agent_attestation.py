"""
Publish Agent Attestation Tool

Publishes an attestation (review) for an agent after a completed agreement
(kind 38403 event), building on-protocol reputation. Requires
LIGHTNING_ENABLE_API_KEY.
"""

import json
import logging
from typing import TYPE_CHECKING

from . import sanitize_error

if TYPE_CHECKING:
    from ..lightning_enable_api import LightningEnableApiClient

logger = logging.getLogger("lightning-enable-mcp.tools.publish_agent_attestation")


async def publish_agent_attestation(
    subject_pubkey: str,
    agreement_id: str,
    rating: int,
    content: str,
    proof: str | None = None,
    api_client: "LightningEnableApiClient | None" = None,
) -> str:
    """
    Publish an attestation (review) for an agent after a completed agreement.

    Creates a kind 38403 event that builds the agent's on-protocol reputation.
    Requires LIGHTNING_ENABLE_API_KEY.

    Args:
        subject_pubkey: Pubkey of the agent being reviewed
        agreement_id: Event ID of the agreement this review is for
        rating: Rating from 1-5
        content: Free-text review content
        proof: Optional hash of L402 payment preimage as proof of real transaction
        api_client: Lightning Enable API client instance

    Returns:
        JSON with attestation result or an error message.
    """
    try:
        # Input validation
        if not subject_pubkey or not subject_pubkey.strip():
            return json.dumps({
                "success": False,
                "error": "Subject pubkey is required. This is the pubkey of the agent you are reviewing.",
            })

        if not agreement_id or not agreement_id.strip():
            return json.dumps({
                "success": False,
                "error": "Agreement ID is required. This is the event ID of the agreement this review is for.",
            })

        if rating < 1 or rating > 5:
            return json.dumps({
                "success": False,
                "error": "Rating must be between 1 and 5.",
            })

        if not content or not content.strip():
            return json.dumps({
                "success": False,
                "error": "Review content is required.",
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
                         "Set LIGHTNING_ENABLE_API_KEY environment variable or add 'lightningEnableApiKey' to ~/.lightning-enable/config.json.",
            })

        result = await api_client.publish_attestation(
            subject_pubkey, agreement_id, rating, content, proof,
        )

        if not result.get("success"):
            return json.dumps({
                "success": False,
                "error": result.get("error", "Unknown error publishing attestation"),
            })

        return json.dumps({
            "success": True,
            "eventId": result.get("eventId"),
            "attestationId": result.get("attestationId"),
            "subjectPubkey": subject_pubkey,
            "agreementId": agreement_id,
            "rating": rating,
            "proof": "included" if proof else "none",
            "message": f"Attestation published successfully as kind 38403 event. Rating: {rating}/5.",
            "nextSteps": {
                "viewReputation": f'Use get_agent_reputation(pubkey="{subject_pubkey}") to see the agent\'s full reputation.',
                "discover": "Other agents will see this attestation when evaluating the reviewed agent.",
            },
        }, indent=2)

    except Exception as e:
        logger.exception("Error publishing attestation")
        return json.dumps({
            "success": False,
            "error": f"Error publishing attestation: {sanitize_error(str(e))}",
        })
