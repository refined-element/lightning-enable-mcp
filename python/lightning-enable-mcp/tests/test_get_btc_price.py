"""
Tests for get_btc_price tool
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock
from decimal import Decimal

from lightning_enable_mcp.tools.get_btc_price import get_btc_price
from lightning_enable_mcp.strike_wallet import StrikeWallet


def _make_strike_wallet_mock(**kwargs):
    """Create a mock that passes isinstance(mock, StrikeWallet) checks."""
    mock = AsyncMock(spec=StrikeWallet, **kwargs)
    return mock


class TestGetBtcPrice:
    """Tests for get_btc_price tool."""

    @pytest.mark.asyncio
    async def test_no_wallet_returns_error(self):
        """Test that missing wallet returns an error."""
        result = await get_btc_price(wallet=None)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Wallet not configured" in parsed["error"]

    @pytest.mark.asyncio
    async def test_non_strike_wallet_returns_error(self):
        """Test that non-Strike wallet returns unsupported error."""
        wallet = AsyncMock()  # Not spec'd as StrikeWallet

        result = await get_btc_price(wallet=wallet)
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "only available with Strike" in parsed["error"]

    @pytest.mark.asyncio
    async def test_successful_price_fetch(self):
        """Test successful BTC price retrieval."""
        wallet = _make_strike_wallet_mock()

        ticker_result = MagicMock()
        ticker_result.success = True
        ticker_result.btc_usd_price = Decimal("100000.50")

        wallet.get_btc_price = AsyncMock(return_value=ticker_result)

        result = await get_btc_price(wallet=wallet)
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["provider"] == "Strike"
        assert parsed["ticker"]["btcUsd"] == 100000.50
        assert "$100,000.50" in parsed["message"]

    @pytest.mark.asyncio
    async def test_failed_price_fetch(self):
        """Test failed BTC price retrieval from Strike."""
        wallet = _make_strike_wallet_mock()

        ticker_result = MagicMock()
        ticker_result.success = False
        ticker_result.error_message = "Rate limit exceeded"
        ticker_result.error_code = "RATE_LIMIT"

        wallet.get_btc_price = AsyncMock(return_value=ticker_result)

        result = await get_btc_price(wallet=wallet)
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "Rate limit exceeded" in parsed["error"]

    @pytest.mark.asyncio
    async def test_exception_handling(self):
        """Test that exceptions are caught and returned as errors."""
        wallet = _make_strike_wallet_mock()
        wallet.get_btc_price = AsyncMock(
            side_effect=Exception("API unreachable")
        )

        result = await get_btc_price(wallet=wallet)
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "API unreachable" in parsed["error"]
