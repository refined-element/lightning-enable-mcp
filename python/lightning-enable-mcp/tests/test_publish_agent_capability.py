"""
Tests for publish_agent_capability tool
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.tools.publish_agent_capability import publish_agent_capability


class TestPublishAgentCapability:
    """Tests for publish_agent_capability tool."""

    @pytest.mark.asyncio
    async def test_missing_service_id_returns_error(self):
        result = await publish_agent_capability(
            service_id="", categories=["ai"], content="desc", price_sats=10,
            api_client=MagicMock(is_configured=True),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Service ID is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_missing_categories_returns_error(self):
        result = await publish_agent_capability(
            service_id="svc", categories=[], content="desc", price_sats=10,
            api_client=MagicMock(is_configured=True),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "category is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_missing_content_returns_error(self):
        result = await publish_agent_capability(
            service_id="svc", categories=["ai"], content="", price_sats=10,
            api_client=MagicMock(is_configured=True),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "description (content) is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_zero_price_returns_error(self):
        result = await publish_agent_capability(
            service_id="svc", categories=["ai"], content="desc", price_sats=0,
            api_client=MagicMock(is_configured=True),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "greater than 0" in parsed["error"]

    @pytest.mark.asyncio
    async def test_no_api_client_returns_error(self):
        result = await publish_agent_capability(
            service_id="svc", categories=["ai"], content="desc", price_sats=10,
            api_client=None,
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "not available" in parsed["error"]

    @pytest.mark.asyncio
    async def test_unconfigured_api_client_returns_error(self):
        client = MagicMock()
        client.is_configured = False
        result = await publish_agent_capability(
            service_id="svc", categories=["ai"], content="desc", price_sats=10,
            api_client=client,
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "LIGHTNING_ENABLE_API_KEY" in parsed["error"]
        # GTM upsell: 30-day trial link + in-MCP signup tool hint
        assert "https://api.lightningenable.com/Checkout?plan=individual&utm_source=mcp&utm_medium=tool-hint&utm_campaign=gtm-aug-2026" in parsed["error"]
        assert "create_lightning_enable_account" in parsed["error"]

    @pytest.mark.asyncio
    async def test_successful_publish(self):
        client = AsyncMock()
        client.is_configured = True
        client.publish_capability.return_value = {
            "success": True,
            "eventId": "evt-abc",
            "l402Endpoint": "https://le.example/l402/svc",
        }

        result = await publish_agent_capability(
            service_id="svc", categories=["ai", "translation"], content="desc",
            price_sats=100, l402_endpoint="https://le.example/l402/svc",
            api_client=client,
        )
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["eventId"] == "evt-abc"
        assert parsed["serviceId"] == "svc"
        assert parsed["l402Endpoint"] == "https://le.example/l402/svc"
        assert "nextSteps" in parsed
        client.publish_capability.assert_called_once()

    @pytest.mark.asyncio
    async def test_api_error_response(self):
        client = AsyncMock()
        client.is_configured = True
        client.publish_capability.return_value = {
            "success": False,
            "error": "Subscription required",
        }

        result = await publish_agent_capability(
            service_id="svc", categories=["ai"], content="desc", price_sats=10,
            api_client=client,
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert parsed["error"] == "Subscription required"

    @pytest.mark.asyncio
    async def test_exception_handling(self):
        client = AsyncMock()
        client.is_configured = True
        client.publish_capability.side_effect = Exception("Connection refused")

        result = await publish_agent_capability(
            service_id="svc", categories=["ai"], content="desc", price_sats=10,
            api_client=client,
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Connection refused" in parsed["error"]
