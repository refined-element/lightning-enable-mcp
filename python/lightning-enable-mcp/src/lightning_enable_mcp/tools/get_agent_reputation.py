"""
Get Agent Reputation Tool

Queries kind 38403 attestation events for a given pubkey and returns the
agent's average rating and individual reviews. Works without an API key.
"""

import json
import logging
from typing import TYPE_CHECKING

from . import sanitize_error

if TYPE_CHECKING:
    from ..lightning_enable_api import LightningEnableApiClient

logger = logging.getLogger("lightning-enable-mcp.tools.get_agent_reputation")


async def get_agent_reputation(
    pubkey: str,
    limit: int = 20,
    api_client: "LightningEnableApiClient | None" = None,
) -> str:
    """
    Get an agent's reputation score and reviews.

    Queries kind 38403 attestation events for the given pubkey. Returns the
    average rating and individual reviews.

    Args:
        pubkey: Pubkey of the agent to query reputation for
        limit: Maximum number of attestations to return (default 20)
        api_client: Lightning Enable API client instance

    Returns:
        JSON with reputation summary or an error message.
    """
    try:
        if not pubkey or not pubkey.strip():
            return json.dumps({
                "success": False,
                "error": "Agent pubkey is required.",
            })

        if api_client is None:
            return json.dumps({
                "success": False,
                "error": "Agent service not available. The MCP server may not be configured correctly.",
            })

        result = await api_client.get_attestations(pubkey, limit)

        if not result.get("success"):
            return json.dumps({
                "success": False,
                "error": result.get("error", "Unknown error querying reputation"),
            })

        attestations = result.get("attestations", [])

        rated = [a for a in attestations if 1 <= (a.get("rating") or 0) <= 5]
        average_rating = (
            round(sum(a["rating"] for a in rated) / len(rated), 2)
            if rated
            else None
        )

        formatted = [{
            "eventId": att.get("eventId"),
            "reviewerPubkey": att.get("reviewerPubkey"),
            "rating": att.get("rating", 0) or 0,
            "content": att.get("content"),
            "agreementId": att.get("agreementId"),
            "hasProof": att.get("proof") is not None,
            "createdAt": att.get("createdAt"),
        } for att in attestations]

        verified_reviews = sum(1 for a in attestations if a.get("proof") is not None)

        return json.dumps({
            "success": True,
            "pubkey": pubkey,
            "averageRating": average_rating,
            "totalReviews": len(attestations),
            "ratedReviews": len(rated),
            "verifiedReviews": verified_reviews,
            "attestations": formatted,
            "hint": (
                f"Agent has a {average_rating:.1f}/5.0 rating from {len(rated)} review(s)."
                if average_rating is not None
                else "No rated reviews found for this agent."
            ),
        }, indent=2)

    except Exception as e:
        logger.exception("Error querying agent reputation")
        return json.dumps({
            "success": False,
            "error": f"Error querying agent reputation: {sanitize_error(str(e))}",
        })
