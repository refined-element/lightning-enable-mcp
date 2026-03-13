"""
Tests for exchange_currency tool
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock
from decimal import Decimal

from lightning_enable_mcp.tools.exchange_currency import exchange_currency
from lightning_enable_mcp.strike_wallet import StrikeWallet


def _make_strike_wallet_mock(**kwargs):
    """Create a mock that passes isinstance(mock, StrikeWallet) checks."""
    mock = AsyncMock(spec=StrikeWallet, **kwargs)
    return mock


class TestExchangeCurrency:
    """Tests for exchange_currency tool."""

    @pytest.mark.asyncio
    async def test_missing_source_currency_returns_error(self):
        """Test that empty source currency returns an error."""
        result = await exchange_currency(
            source_currency="", target_currency="BTC", amount=100.0
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Source currency is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_missing_target_currency_returns_error(self):
        """Test that empty target currency returns an error."""
        result = await exchange_currency(
            source_currency="USD", target_currency="", amount=100.0
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Target currency is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_zero_amount_returns_error(self):
        """Test that zero amount returns an error."""
        result = await exchange_currency(
            source_currency="USD", target_currency="BTC", amount=0
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Amount must be greater than 0" in parsed["error"]

    @pytest.mark.asyncio
    async def test_negative_amount_returns_error(self):
        """Test that negative amount returns an error."""
        result = await exchange_currency(
            source_currency="USD", target_currency="BTC", amount=-50
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Amount must be greater than 0" in parsed["error"]

    @pytest.mark.asyncio
    async def test_no_wallet_returns_error(self):
        """Test that missing wallet returns an error."""
        result = await exchange_currency(
            source_currency="USD", target_currency="BTC", amount=100.0, wallet=None
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Strike wallet" in parsed["error"]

    @pytest.mark.asyncio
    async def test_non_strike_wallet_returns_error(self):
        """Test that non-Strike wallet returns unsupported error."""
        wallet = AsyncMock()  # Not spec'd as StrikeWallet

        result = await exchange_currency(
            source_currency="USD", target_currency="BTC", amount=100.0,
            wallet=wallet,
        )
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "does not support currency exchange" in parsed["error"]

    @pytest.mark.asyncio
    async def test_successful_usd_to_btc_exchange(self):
        """Test successful USD to BTC exchange."""
        wallet = _make_strike_wallet_mock()

        exchange_result = MagicMock()
        exchange_result.success = True
        exchange_result.exchange_id = "exch-001"
        exchange_result.source_currency = "USD"
        exchange_result.target_currency = "BTC"
        exchange_result.source_amount = Decimal("100.00")
        exchange_result.target_amount = Decimal("0.00100000")
        exchange_result.rate = Decimal("100000.00")
        exchange_result.fee = Decimal("0.50")
        exchange_result.state = "COMPLETED"

        wallet.exchange_currency = AsyncMock(return_value=exchange_result)

        result = await exchange_currency(
            source_currency="USD", target_currency="BTC", amount=100.0,
            wallet=wallet,
        )
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["provider"] == "Strike"
        assert parsed["exchange"]["id"] == "exch-001"
        assert parsed["exchange"]["sourceAmount"] == 100.0
        assert parsed["exchange"]["targetAmount"] == 0.001
        assert "Exchanged" in parsed["message"]

    @pytest.mark.asyncio
    async def test_failed_exchange(self):
        """Test failed exchange from wallet."""
        wallet = _make_strike_wallet_mock()

        exchange_result = MagicMock()
        exchange_result.success = False
        exchange_result.error_message = "Insufficient balance"
        exchange_result.error_code = "INSUFFICIENT_FUNDS"

        wallet.exchange_currency = AsyncMock(return_value=exchange_result)

        result = await exchange_currency(
            source_currency="USD", target_currency="BTC", amount=100.0,
            wallet=wallet,
        )
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "Insufficient balance" in parsed["error"]

    @pytest.mark.asyncio
    async def test_exception_handling(self):
        """Test that exceptions are caught and returned as errors."""
        wallet = _make_strike_wallet_mock()
        wallet.exchange_currency = AsyncMock(
            side_effect=Exception("Network error")
        )

        result = await exchange_currency(
            source_currency="USD", target_currency="BTC", amount=100.0,
            wallet=wallet,
        )
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "Network error" in parsed["error"]
