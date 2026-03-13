"""
Tests for check_invoice_status tool
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock, patch

from lightning_enable_mcp.tools.check_invoice_status import check_invoice_status
from lightning_enable_mcp.strike_wallet import StrikeWallet


def _make_strike_wallet_mock(**kwargs):
    """Create a mock that passes isinstance(mock, StrikeWallet) checks."""
    mock = AsyncMock(spec=StrikeWallet, **kwargs)
    return mock


class TestCheckInvoiceStatus:
    """Tests for check_invoice_status tool."""

    @pytest.mark.asyncio
    async def test_missing_invoice_id_returns_error(self):
        """Test that empty invoice ID returns an error."""
        result = await check_invoice_status(invoice_id="", wallet=MagicMock())
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Invoice ID is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_whitespace_invoice_id_returns_error(self):
        """Test that whitespace-only invoice ID returns an error."""
        result = await check_invoice_status(invoice_id="   ", wallet=MagicMock())
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Invoice ID is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_no_wallet_returns_error(self):
        """Test that missing wallet returns an error."""
        result = await check_invoice_status(invoice_id="inv-123", wallet=None)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Wallet not configured" in parsed["error"]

    @pytest.mark.asyncio
    async def test_paid_invoice_strike(self):
        """Test checking a paid invoice via Strike wallet."""
        wallet = _make_strike_wallet_mock()
        wallet._request = AsyncMock(return_value={
            "state": "PAID",
            "amount": {"amount": "0.001", "currency": "BTC"},
            "paidAt": "2026-03-13T12:00:00Z",
        })

        result = await check_invoice_status(invoice_id="inv-123", wallet=wallet)
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["provider"] == "Strike"
        assert parsed["invoice"]["isPaid"] is True
        assert parsed["invoice"]["isPending"] is False
        assert parsed["invoice"]["state"] == "PAID"
        assert "PAID" in parsed["message"]

    @pytest.mark.asyncio
    async def test_pending_invoice_strike(self):
        """Test checking a pending invoice via Strike wallet."""
        wallet = _make_strike_wallet_mock()
        wallet._request = AsyncMock(return_value={
            "state": "UNPAID",
            "amount": {"amount": "0.001", "currency": "BTC"},
        })

        result = await check_invoice_status(invoice_id="inv-456", wallet=wallet)
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["invoice"]["isPaid"] is False
        assert parsed["invoice"]["isPending"] is True
        assert "pending" in parsed["message"].lower()

    @pytest.mark.asyncio
    async def test_non_strike_wallet_returns_error(self):
        """Test that non-Strike wallet returns unsupported error."""
        wallet = AsyncMock()  # Not spec'd as StrikeWallet

        result = await check_invoice_status(invoice_id="inv-123", wallet=wallet)
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "not supported" in parsed["error"].lower()

    @pytest.mark.asyncio
    async def test_exception_handling(self):
        """Test that exceptions are caught and returned as errors."""
        wallet = _make_strike_wallet_mock()
        wallet._request = AsyncMock(side_effect=Exception("Connection timeout"))

        result = await check_invoice_status(invoice_id="inv-123", wallet=wallet)
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "Connection timeout" in parsed["error"]
