"""
Tests for create_invoice tool
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.tools.create_invoice import create_invoice
from lightning_enable_mcp.strike_wallet import StrikeWallet


def _make_strike_wallet_mock(**kwargs):
    """Create a mock that passes isinstance(mock, StrikeWallet) checks."""
    mock = AsyncMock(spec=StrikeWallet, **kwargs)
    return mock


class TestCreateInvoice:
    """Tests for create_invoice tool."""

    @pytest.mark.asyncio
    async def test_zero_amount_returns_error(self):
        """Test that zero amount returns an error."""
        result = await create_invoice(amount_sats=0, wallet=MagicMock())
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Amount must be greater than 0" in parsed["error"]

    @pytest.mark.asyncio
    async def test_negative_amount_returns_error(self):
        """Test that negative amount returns an error."""
        result = await create_invoice(amount_sats=-100, wallet=MagicMock())
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Amount must be greater than 0" in parsed["error"]

    @pytest.mark.asyncio
    async def test_no_wallet_returns_error(self):
        """Test that missing wallet returns an error."""
        result = await create_invoice(amount_sats=1000, wallet=None)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Wallet not configured" in parsed["error"]

    @pytest.mark.asyncio
    async def test_strike_invoice_creation(self):
        """Test successful invoice creation via Strike wallet."""
        wallet = _make_strike_wallet_mock()
        wallet._request = AsyncMock(return_value={
            "invoiceId": "inv-strike-001",
            "quote": "lnbc1000n1ptest...",
            "expiresAt": "2026-03-13T13:00:00Z",
        })

        result = await create_invoice(
            amount_sats=1000, memo="Test payment", wallet=wallet
        )
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["provider"] == "Strike"
        assert parsed["invoice"]["id"] == "inv-strike-001"
        assert parsed["invoice"]["bolt11"] == "lnbc1000n1ptest..."
        assert parsed["invoice"]["amountSats"] == 1000

    @pytest.mark.asyncio
    async def test_non_strike_non_opennode_wallet(self):
        """Test invoice creation with NWC wallet (fallback path)."""
        wallet = AsyncMock()  # Not spec'd as Strike or OpenNode
        wallet._send_request = AsyncMock(return_value={
            "result": {
                "payment_hash": "hash123",
                "invoice": "lnbc500n1pnwc...",
            }
        })

        result = await create_invoice(amount_sats=500, wallet=wallet)
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["provider"] == "NWC"
        assert parsed["invoice"]["bolt11"] == "lnbc500n1pnwc..."

    @pytest.mark.asyncio
    async def test_exception_handling(self):
        """Test that exceptions are caught and returned as errors."""
        wallet = _make_strike_wallet_mock()
        wallet._request = AsyncMock(side_effect=Exception("API down"))

        result = await create_invoice(amount_sats=1000, wallet=wallet)
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "API down" in parsed["error"]
