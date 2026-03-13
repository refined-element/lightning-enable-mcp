"""
Tests for verify_l402_payment tool
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.tools.verify_l402_payment import verify_l402_payment


class TestVerifyL402Payment:
    """Tests for verify_l402_payment tool."""

    @pytest.mark.asyncio
    async def test_missing_macaroon_returns_error(self):
        """Test that empty macaroon returns an error."""
        result = await verify_l402_payment(
            macaroon="",
            preimage="abc123",
            api_client=MagicMock(is_configured=True),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Macaroon is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_missing_preimage_returns_error(self):
        """Test that empty preimage returns an error."""
        result = await verify_l402_payment(
            macaroon="base64macaroon==",
            preimage="",
            api_client=MagicMock(is_configured=True),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Preimage is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_no_api_client_returns_error(self):
        """Test that missing API client returns an error."""
        result = await verify_l402_payment(
            macaroon="base64macaroon==",
            preimage="abc123",
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
        result = await verify_l402_payment(
            macaroon="base64macaroon==",
            preimage="abc123",
            api_client=client,
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "LIGHTNING_ENABLE_API_KEY" in parsed["error"]

    @pytest.mark.asyncio
    async def test_valid_payment_verification(self):
        """Test successful payment verification."""
        client = AsyncMock()
        client.is_configured = True
        client.verify_token.return_value = {
            "success": True,
            "valid": True,
            "resource": "premium-api/v1/data",
        }

        result = await verify_l402_payment(
            macaroon="base64macaroon==",
            preimage="abc123def456",
            api_client=client,
        )
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["valid"] is True
        assert parsed["resource"] == "premium-api/v1/data"
        assert "grant access" in parsed["message"].lower()

        client.verify_token.assert_called_once_with("base64macaroon==", "abc123def456")

    @pytest.mark.asyncio
    async def test_invalid_payment_verification(self):
        """Test failed payment verification (token invalid)."""
        client = AsyncMock()
        client.is_configured = True
        client.verify_token.return_value = {
            "success": True,
            "valid": False,
        }

        result = await verify_l402_payment(
            macaroon="base64macaroon==",
            preimage="wrongpreimage",
            api_client=client,
        )
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["valid"] is False
        assert "do not grant access" in parsed["message"].lower()

    @pytest.mark.asyncio
    async def test_api_error_response(self):
        """Test handling of API error response."""
        client = AsyncMock()
        client.is_configured = True
        client.verify_token.return_value = {
            "success": False,
            "error": "Internal server error",
        }

        result = await verify_l402_payment(
            macaroon="base64macaroon==",
            preimage="abc123",
            api_client=client,
        )
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert parsed["error"] == "Internal server error"

    @pytest.mark.asyncio
    async def test_exception_handling(self):
        """Test that exceptions are caught and returned as errors."""
        client = AsyncMock()
        client.is_configured = True
        client.verify_token.side_effect = Exception("Network error")

        result = await verify_l402_payment(
            macaroon="base64macaroon==",
            preimage="abc123",
            api_client=client,
        )
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "Network error" in parsed["error"]

    @pytest.mark.asyncio
    async def test_whitespace_trimmed(self):
        """Test that macaroon and preimage are trimmed."""
        client = AsyncMock()
        client.is_configured = True
        client.verify_token.return_value = {
            "success": True,
            "valid": True,
            "resource": "test",
        }

        await verify_l402_payment(
            macaroon="  base64macaroon==  ",
            preimage="  abc123  ",
            api_client=client,
        )

        client.verify_token.assert_called_once_with("base64macaroon==", "abc123")
