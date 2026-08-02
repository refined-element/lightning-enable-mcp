"""
Tests for request_agent_service tool
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.config import ApprovalLevel, ApprovalCheckResult
from lightning_enable_mcp.tools.request_agent_service import request_agent_service


def _approval(level, **kwargs):
    from decimal import Decimal
    return ApprovalCheckResult(
        level=level,
        amount_sats=kwargs.get("amount_sats", 100),
        amount_usd=kwargs.get("amount_usd", Decimal("0.10")),
        denial_reason=kwargs.get("denial_reason"),
        remaining_session_budget_usd=kwargs.get(
            "remaining_session_budget_usd", Decimal("5.00")
        ),
    )


class TestRequestAgentService:
    """Tests for request_agent_service tool."""

    @pytest.mark.asyncio
    async def test_missing_capability_id_returns_error(self):
        result = await request_agent_service(
            capability_event_id="", budget_sats=100, api_client=MagicMock(),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Capability event ID is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_zero_budget_returns_error(self):
        result = await request_agent_service(
            capability_event_id="evt", budget_sats=0, api_client=MagicMock(),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "greater than 0" in parsed["error"]

    @pytest.mark.asyncio
    async def test_invalid_parameters_json_returns_error(self):
        result = await request_agent_service(
            capability_event_id="evt", budget_sats=100, parameters="{not json",
            api_client=MagicMock(),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "valid JSON" in parsed["error"]

    @pytest.mark.asyncio
    async def test_unconfigured_api_client_returns_error(self):
        client = MagicMock()
        client.is_configured = False
        result = await request_agent_service(
            capability_event_id="evt", budget_sats=100, api_client=client,
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "LIGHTNING_ENABLE_API_KEY" in parsed["error"]
        # GTM upsell: 30-day trial link + in-MCP signup tool hint
        assert "https://api.lightningenable.com/Checkout?plan=individual&utm_source=mcp&utm_medium=tool-hint&utm_campaign=gtm-aug-2026" in parsed["error"]
        assert "create_lightning_enable_account" in parsed["error"]

    @pytest.mark.asyncio
    async def test_no_api_client_returns_error(self):
        result = await request_agent_service(
            capability_event_id="evt", budget_sats=100, api_client=None,
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "not available" in parsed["error"]

    @pytest.mark.asyncio
    async def test_budget_deny_blocks_request(self):
        client = AsyncMock()
        budget = MagicMock()
        budget.check_approval_level = AsyncMock(
            return_value=_approval(ApprovalLevel.DENY, denial_reason="over limit")
        )

        result = await request_agent_service(
            capability_event_id="evt", budget_sats=999999,
            api_client=client, budget_service=budget,
        )
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert parsed["error"] == "Budget limit exceeded"
        assert parsed["details"]["reason"] == "over limit"
        # Request must NOT be sent when budget denies.
        client.request_service.assert_not_called()

    @pytest.mark.asyncio
    async def test_successful_request_with_l402_endpoint(self):
        client = AsyncMock()
        client.request_service.return_value = {
            "success": True,
            "requestEventId": "req-1",
            "l402Endpoint": "https://provider.example/l402",
        }
        budget = MagicMock()
        budget.check_approval_level = AsyncMock(
            return_value=_approval(ApprovalLevel.AUTO_APPROVE)
        )

        result = await request_agent_service(
            capability_event_id="evt", budget_sats=100,
            api_client=client, budget_service=budget,
        )
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["requestEventId"] == "req-1"
        assert parsed["l402Endpoint"] == "https://provider.example/l402"
        assert "settle_agent_service" in parsed["nextStep"]
        # No spend recorded at request time.
        budget.record_spend.assert_not_called()

    @pytest.mark.asyncio
    async def test_successful_request_without_l402_endpoint(self):
        client = AsyncMock()
        client.request_service.return_value = {
            "success": True,
            "requestEventId": "req-2",
            "l402Endpoint": None,
        }

        result = await request_agent_service(
            capability_event_id="evt", budget_sats=100, api_client=client,
        )
        parsed = json.loads(result)
        assert parsed["success"] is True
        assert "kind 38402" in parsed["nextStep"]

    @pytest.mark.asyncio
    async def test_api_error_response(self):
        client = AsyncMock()
        client.request_service.return_value = {
            "success": False,
            "error": "API key not configured",
        }

        result = await request_agent_service(
            capability_event_id="evt", budget_sats=100, api_client=client,
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert parsed["error"] == "API key not configured"
