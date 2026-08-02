"""
Tests for unpublish_agent_capability tool
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.tools.unpublish_agent_capability import (
    unpublish_agent_capability,
)


class TestUnpublishAgentCapability:
    """Tests for unpublish_agent_capability tool."""

    @pytest.mark.asyncio
    async def test_missing_service_id_returns_error(self):
        result = await unpublish_agent_capability(
            service_id="", api_client=MagicMock(is_configured=True),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Service ID is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_no_api_client_returns_error(self):
        result = await unpublish_agent_capability(service_id="svc", api_client=None)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "not available" in parsed["error"]

    @pytest.mark.asyncio
    async def test_unconfigured_api_key_returns_error(self):
        result = await unpublish_agent_capability(
            service_id="svc", api_client=MagicMock(is_configured=False),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "API key not configured" in parsed["error"]
        # GTM upsell: 30-day trial link + in-MCP signup tool hint
        assert "https://api.lightningenable.com/Checkout?plan=individual&utm_source=mcp&utm_medium=tool-hint&utm_campaign=gtm-aug-2026" in parsed["error"]
        assert "create_lightning_enable_account" in parsed["error"]

    @pytest.mark.asyncio
    async def test_remove_success_calls_client_with_proxy_id(self):
        api_client = MagicMock(is_configured=True)
        api_client.unpublish_capability = AsyncMock(return_value={
            "success": True,
            "proxyId": "shopify-lightningenable",
            "retired": True,
            "alreadyRetired": None,
        })

        result = await unpublish_agent_capability(
            service_id="shopify-lightningenable", reason="dead", api_client=api_client,
        )
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["retired"] is True
        assert parsed["proxyId"] == "shopify-lightningenable"
        # service_id is passed through as the proxy id; no pubkey/mode any more.
        api_client.unpublish_capability.assert_awaited_once_with(
            "shopify-lightningenable", "dead",
        )

    @pytest.mark.asyncio
    async def test_already_retired_message(self):
        api_client = MagicMock(is_configured=True)
        api_client.unpublish_capability = AsyncMock(return_value={
            "success": True, "proxyId": "svc", "retired": True, "alreadyRetired": True,
        })
        result = await unpublish_agent_capability(service_id="svc", api_client=api_client)
        parsed = json.loads(result)
        assert parsed["alreadyRetired"] is True
        assert "already retired" in parsed["message"].lower()

    @pytest.mark.asyncio
    async def test_client_error_is_surfaced(self):
        api_client = MagicMock(is_configured=True)
        api_client.unpublish_capability = AsyncMock(return_value={
            "success": False, "error": "Proxy not found",
        })
        result = await unpublish_agent_capability(service_id="svc", api_client=api_client)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "not found" in parsed["error"]
