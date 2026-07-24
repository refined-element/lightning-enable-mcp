"""
Tests for verify_confirmation_code tool
"""

import json
import pytest
from datetime import datetime, timezone, timedelta
from decimal import Decimal
from unittest.mock import MagicMock

from lightning_enable_mcp.tools.verify_confirmation_code import verify_confirmation_code
from lightning_enable_mcp.budget_service import PendingConfirmation


class TestVerifyConfirmationCode:
    """Tests for verify_confirmation_code tool."""

    @pytest.mark.asyncio
    async def test_missing_nonce_returns_error(self):
        """Test that empty nonce returns an error."""
        result = await verify_confirmation_code(nonce="", budget_service=MagicMock())
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Nonce is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_whitespace_nonce_returns_error(self):
        """Test that whitespace-only nonce returns an error."""
        result = await verify_confirmation_code(nonce="   ", budget_service=MagicMock())
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Nonce is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_no_budget_service_returns_error(self):
        """Test that missing budget service returns an error."""
        result = await verify_confirmation_code(nonce="ABC123", budget_service=None)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Budget service not available" in parsed["error"]

    @pytest.mark.asyncio
    async def test_successful_verification(self):
        """Test successful confirmation-code verification.

        The message must make clear NOTHING was paid and how to actually execute — this
        kills the old "Payment ... confirmed" trap that read as "money moved".
        """
        now = datetime.now(timezone.utc)
        budget_service = MagicMock()
        budget_service.validate_confirmation.return_value = PendingConfirmation(
            nonce="ABC123",
            amount_sats=5000,
            amount_usd=Decimal("5.00"),
            tool_name="pay_invoice",
            description="Invoice payment",
            destination="lnbc-test-invoice",
            created_at=now,
            expires_at=now + timedelta(minutes=2),
        )

        result = await verify_confirmation_code(nonce="abc123", budget_service=budget_service)
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["valid"] is True
        assert parsed["amount_sats"] == 5000
        assert parsed["tool"] == "pay_invoice"
        assert parsed["confirmation"]["nonce"] == "ABC123"
        assert parsed["confirmation"]["amountSats"] == 5000
        assert parsed["confirmation"]["amountUsd"] == 5.0
        # The verdict must say nothing was paid and how to execute.
        assert "NOTHING HAS BEEN PAID" in parsed["message"]
        assert "confirmation_nonce" in parsed["message"]
        assert "pay_invoice" in parsed["message"]
        # Nonce should be uppercased before validation
        budget_service.validate_confirmation.assert_called_once_with("ABC123")

    @pytest.mark.asyncio
    async def test_invalid_nonce_returns_error(self):
        """Test that invalid/expired nonce returns an error."""
        budget_service = MagicMock()
        budget_service.validate_confirmation.return_value = None

        result = await verify_confirmation_code(nonce="BADNON", budget_service=budget_service)
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

        result = await verify_confirmation_code(nonce="ABC123", budget_service=budget_service)
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "not supported" in parsed["error"].lower()

    @pytest.mark.asyncio
    async def test_exception_handling(self):
        """Test that general exceptions are caught and returned as errors."""
        budget_service = MagicMock()
        budget_service.validate_confirmation.side_effect = Exception("DB error")

        result = await verify_confirmation_code(nonce="ABC123", budget_service=budget_service)
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "DB error" in parsed["error"]
