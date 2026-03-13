"""
Tests for create_l402_challenge tool
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock, patch

from lightning_enable_mcp.tools.create_l402_challenge import create_l402_challenge


class TestCreateL402Challenge:
    """Tests for create_l402_challenge tool."""

    @pytest.mark.asyncio
    async def test_missing_resource_returns_error(self):
        """Test that empty resource returns an error."""
        result = await create_l402_challenge(
            resource="",
            price_sats=100,
            api_client=MagicMock(is_configured=True),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Resource identifier is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_zero_price_returns_error(self):
        """Test that zero price returns an error."""
        result = await create_l402_challenge(
            resource="my-api",
            price_sats=0,
            api_client=MagicMock(is_configured=True),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Price must be greater than 0" in parsed["error"]

    @pytest.mark.asyncio
    async def test_negative_price_returns_error(self):
        """Test that negative price returns an error."""
        result = await create_l402_challenge(
            resource="my-api",
            price_sats=-10,
            api_client=MagicMock(is_configured=True),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Price must be greater than 0" in parsed["error"]

    @pytest.mark.asyncio
    async def test_no_api_client_returns_error(self):
        """Test that missing API client returns an error."""
        result = await create_l402_challenge(
            resource="my-api",
            price_sats=100,
            api_client=None,
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "not available" in parsed["error"]

    @pytest.mark.asyncio
    async def test_unconfigured_api_client_returns_error(self):
        """Test that unconfigured API client returns an error."""
        client = MagicMock()
        client.is_configured = False
        result = await create_l402_challenge(
            resource="my-api",
            price_sats=100,
            api_client=client,
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "LIGHTNING_ENABLE_API_KEY" in parsed["error"]

    @pytest.mark.asyncio
    async def test_successful_challenge_creation(self):
        """Test successful challenge creation."""
        client = AsyncMock()
        client.is_configured = True
        client.create_challenge.return_value = {
            "success": True,
            "challenge": {
                "invoice": "lnbc100n1...",
                "macaroon": "base64macaroon==",
                "paymentHash": "abc123",
                "expiresAt": "2026-03-13T12:00:00Z",
            },
        }

        result = await create_l402_challenge(
            resource="premium-api/v1/data",
            price_sats=100,
            description="Access to premium data",
            api_client=client,
        )
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["challenge"]["invoice"] == "lnbc100n1..."
        assert parsed["challenge"]["macaroon"] == "base64macaroon=="
        assert parsed["challenge"]["paymentHash"] == "abc123"
        assert parsed["resource"] == "premium-api/v1/data"
        assert parsed["priceSats"] == 100
        assert "instructions" in parsed
        assert "forPayer" in parsed["instructions"]
        assert "verifyWith" in parsed["instructions"]

        client.create_challenge.assert_called_once_with(
            "premium-api/v1/data", 100, "Access to premium data"
        )

    @pytest.mark.asyncio
    async def test_api_error_response(self):
        """Test handling of API error response."""
        client = AsyncMock()
        client.is_configured = True
        client.create_challenge.return_value = {
            "success": False,
            "error": "Subscription expired",
        }

        result = await create_l402_challenge(
            resource="my-api",
            price_sats=100,
            api_client=client,
        )
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert parsed["error"] == "Subscription expired"

    @pytest.mark.asyncio
    async def test_exception_handling(self):
        """Test that exceptions are caught and returned as errors."""
        client = AsyncMock()
        client.is_configured = True
        client.create_challenge.side_effect = Exception("Connection refused")

        result = await create_l402_challenge(
            resource="my-api",
            price_sats=100,
            api_client=client,
        )
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "Connection refused" in parsed["error"]
