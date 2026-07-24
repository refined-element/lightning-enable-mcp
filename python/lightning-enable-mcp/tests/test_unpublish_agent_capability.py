"""
Tests for unpublish_agent_capability tool
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.tools.unpublish_agent_capability import (
    unpublish_agent_capability,
)

VALID_PUBKEY = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2"


class TestUnpublishAgentCapability:
    """Tests for unpublish_agent_capability tool."""

    @pytest.mark.asyncio
    async def test_invalid_pubkey_returns_error(self):
        result = await unpublish_agent_capability(
            pubkey="not-hex", service_id="svc",
            api_client=MagicMock(is_configured=True),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "pubkey" in parsed["error"].lower()

    @pytest.mark.asyncio
    async def test_missing_service_id_returns_error(self):
        result = await unpublish_agent_capability(
            pubkey=VALID_PUBKEY, service_id="",
            api_client=MagicMock(is_configured=True),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Service ID is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_invalid_mode_returns_error(self):
        result = await unpublish_agent_capability(
            pubkey=VALID_PUBKEY, service_id="svc", mode="delete",
            api_client=MagicMock(is_configured=True),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Mode must be" in parsed["error"]

    @pytest.mark.asyncio
    async def test_no_api_client_returns_error(self):
        result = await unpublish_agent_capability(
            pubkey=VALID_PUBKEY, service_id="svc", api_client=None,
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "not available" in parsed["error"]

    @pytest.mark.asyncio
    async def test_unconfigured_api_key_returns_error(self):
        result = await unpublish_agent_capability(
            pubkey=VALID_PUBKEY, service_id="svc",
            api_client=MagicMock(is_configured=False),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "API key not configured" in parsed["error"]

    @pytest.mark.asyncio
    async def test_remove_success_calls_client_and_returns_result(self):
        api_client = MagicMock(is_configured=True)
        api_client.unpublish_capability = AsyncMock(return_value={
            "success": True,
            "serviceId": "svc",
            "proxyId": "agent-svc-ab12",
            "mode": "remove",
            "retired": True,
        })

        result = await unpublish_agent_capability(
            pubkey=VALID_PUBKEY, service_id="svc", reason="done",
            api_client=api_client,
        )
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["retired"] is True
        assert parsed["proxyId"] == "agent-svc-ab12"
        # Mode defaults to remove; pubkey/service passed through normalized.
        api_client.unpublish_capability.assert_awaited_once_with(
            VALID_PUBKEY, "svc", "remove", "done",
        )

    @pytest.mark.asyncio
    async def test_client_error_is_surfaced(self):
        api_client = MagicMock(is_configured=True)
        api_client.unpublish_capability = AsyncMock(return_value={
            "success": False, "error": "Capability not found for this agent",
        })

        result = await unpublish_agent_capability(
            pubkey=VALID_PUBKEY, service_id="svc", api_client=api_client,
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "not found" in parsed["error"]
