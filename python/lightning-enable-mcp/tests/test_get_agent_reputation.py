"""
Tests for get_agent_reputation tool
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.tools.get_agent_reputation import get_agent_reputation


class TestGetAgentReputation:
    """Tests for get_agent_reputation tool."""

    @pytest.mark.asyncio
    async def test_missing_pubkey_returns_error(self):
        result = await get_agent_reputation(pubkey="", api_client=MagicMock())
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "pubkey is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_no_api_client_returns_error(self):
        result = await get_agent_reputation(pubkey="pk", api_client=None)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "not available" in parsed["error"]

    @pytest.mark.asyncio
    async def test_average_rating_computed(self):
        client = AsyncMock()
        client.get_attestations.return_value = {
            "success": True,
            "attestations": [
                {"eventId": "1", "rating": 5, "content": "great", "proof": "abc"},
                {"eventId": "2", "rating": 3, "content": "ok", "proof": None},
                {"eventId": "3", "rating": 0, "content": "unrated", "proof": None},
            ],
        }

        result = await get_agent_reputation(pubkey="pk", api_client=client)
        parsed = json.loads(result)

        assert parsed["success"] is True
        # Only ratings 1-5 count: (5 + 3) / 2 = 4.0
        assert parsed["averageRating"] == 4.0
        assert parsed["totalReviews"] == 3
        assert parsed["ratedReviews"] == 2
        assert parsed["verifiedReviews"] == 1
        client.get_attestations.assert_called_once_with("pk", 20)

    @pytest.mark.asyncio
    async def test_no_reviews(self):
        client = AsyncMock()
        client.get_attestations.return_value = {
            "success": True,
            "attestations": [],
        }

        result = await get_agent_reputation(pubkey="pk", api_client=client)
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["averageRating"] is None
        assert parsed["totalReviews"] == 0
        assert "No rated reviews" in parsed["hint"]

    @pytest.mark.asyncio
    async def test_api_error_response(self):
        client = AsyncMock()
        client.get_attestations.return_value = {
            "success": False,
            "error": "API returned 503",
        }

        result = await get_agent_reputation(pubkey="pk", api_client=client)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert parsed["error"] == "API returned 503"
