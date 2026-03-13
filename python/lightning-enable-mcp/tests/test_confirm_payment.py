"""
Tests for confirm_payment tool
"""

import json
import pytest
from unittest.mock import MagicMock

from lightning_enable_mcp.tools.confirm_payment import confirm_payment


class TestConfirmPayment:
    """Tests for confirm_payment tool."""

    @pytest.mark.asyncio
    async def test_missing_nonce_returns_error(self):
        """Test that empty nonce returns an error."""
        result = await confirm_payment(nonce="", budget_service=MagicMock())
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Nonce is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_whitespace_nonce_returns_error(self):
        """Test that whitespace-only nonce returns an error."""
        result = await confirm_payment(nonce="   ", budget_service=MagicMock())
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Nonce is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_no_budget_service_returns_error(self):
        """Test that missing budget service returns an error."""
        result = await confirm_payment(nonce="ABC123", budget_service=None)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Budget service not available" in parsed["error"]

    @pytest.mark.asyncio
    async def test_successful_confirmation(self):
        """Test successful payment confirmation."""
        budget_service = MagicMock()
        budget_service.validate_confirmation.return_value = {
            "nonce": "ABC123",
            "amount_sats": 5000,
            "amount_usd": 5.00,
            "tool_name": "pay_invoice",
            "description": "Invoice payment",
        }

        result = await confirm_payment(nonce="abc123", budget_service=budget_service)
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["confirmed"] is True
        assert parsed["confirmation"]["nonce"] == "ABC123"
        assert parsed["confirmation"]["amountSats"] == 5000
        assert parsed["confirmation"]["amountUsd"] == 5.0
        # Nonce should be uppercased before validation
        budget_service.validate_confirmation.assert_called_once_with("ABC123")

    @pytest.mark.asyncio
    async def test_invalid_nonce_returns_error(self):
        """Test that invalid/expired nonce returns an error."""
        budget_service = MagicMock()
        budget_service.validate_confirmation.return_value = None

        result = await confirm_payment(nonce="BADNON", budget_service=budget_service)
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "Invalid, expired" in parsed["error"]

    @pytest.mark.asyncio
    async def test_attribute_error_handling(self):
        """Test that AttributeError from old budget service is handled."""
        budget_service = MagicMock()
        budget_service.validate_confirmation.side_effect = AttributeError(
            "no attribute validate_confirmation"
        )

        result = await confirm_payment(nonce="ABC123", budget_service=budget_service)
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "not supported" in parsed["error"].lower()

    @pytest.mark.asyncio
    async def test_exception_handling(self):
        """Test that general exceptions are caught and returned as errors."""
        budget_service = MagicMock()
        budget_service.validate_confirmation.side_effect = Exception("DB error")

        result = await confirm_payment(nonce="ABC123", budget_service=budget_service)
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "DB error" in parsed["error"]
