"""
Tests for discover_agent_services tool
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.tools.discover_agent_services import discover_agent_services


class TestDiscoverAgentServices:
    """Tests for discover_agent_services tool."""

    @pytest.mark.asyncio
    async def test_no_filter_returns_error(self):
        """With no filters at all, returns a guidance error."""
        result = await discover_agent_services(api_client=MagicMock())
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "at least one search filter" in parsed["error"]
        assert "examples" in parsed

    @pytest.mark.asyncio
    async def test_no_api_client_returns_error(self):
        """Missing API client returns an error."""
        result = await discover_agent_services(query="weather", api_client=None)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "not available" in parsed["error"]

    @pytest.mark.asyncio
    async def test_successful_discovery(self):
        """Happy path: capabilities are formatted for agent consumption."""
        client = AsyncMock()
        client.discover_capabilities.return_value = {
            "success": True,
            "total": 1,
            "capabilities": [
                {
                    "eventId": "evt123",
                    "serviceId": "translate-svc",
                    "pubkey": "abc",
                    "content": "Translate text",
                    "categories": ["ai", "translation"],
                    "hashtags": ["translate"],
                    "priceSats": 50,
                    "l402Endpoint": "https://example.com/l402",
                    "createdAt": 1700000000,
                }
            ],
        }

        result = await discover_agent_services(query="translation", api_client=client)
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["total"] == 1
        assert len(parsed["results"]) == 1
        entry = parsed["results"][0]
        assert entry["event_id"] == "evt123"
        assert entry["service_id"] == "translate-svc"
        assert entry["description"] == "Translate text"
        assert entry["price_sats"] == 50
        assert entry["l402_endpoint"] == "https://example.com/l402"
        client.discover_capabilities.assert_called_once_with(
            None, None, "translation", 20
        )

    @pytest.mark.asyncio
    async def test_discovery_error_response(self):
        """Discovery failure surfaces the error plus a hint."""
        client = AsyncMock()
        client.discover_capabilities.return_value = {
            "success": False,
            "error": "Registry unavailable",
        }

        result = await discover_agent_services(category="ai", api_client=client)
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert parsed["error"] == "Registry unavailable"
        assert "hint" in parsed

    @pytest.mark.asyncio
    async def test_budget_annotation_affordable_calls(self):
        """When a budget_service is present, affordable_calls is annotated."""
        client = AsyncMock()
        client.discover_capabilities.return_value = {
            "success": True,
            "total": 1,
            "capabilities": [
                {"serviceId": "svc", "priceSats": 100, "categories": [], "hashtags": []}
            ],
        }

        budget = MagicMock()
        budget.get_status.return_value = {
            "session": {"remainingUsd": 1.0, "spentUsd": 0.0},
            "price": {"btcUsd": 100000.0},  # 1 sat = $0.001 -> 1000 sats affordable
        }

        result = await discover_agent_services(
            query="x", api_client=client, budget_service=budget
        )
        parsed = json.loads(result)

        assert parsed["success"] is True
        # remaining_session_sats = (1.0 / 100000) * 1e8 = 1000; // 100 = 10
        assert parsed["results"][0]["affordable_calls"] == 10
