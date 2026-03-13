"""
Tests for get_all_balances tool
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock
from decimal import Decimal

from lightning_enable_mcp.tools.get_all_balances import get_all_balances
from lightning_enable_mcp.strike_wallet import StrikeWallet


def _make_strike_wallet_mock(**kwargs):
    """Create a mock that passes isinstance(mock, StrikeWallet) checks."""
    mock = AsyncMock(spec=StrikeWallet, **kwargs)
    return mock


class TestGetAllBalances:
    """Tests for get_all_balances tool."""

    @pytest.mark.asyncio
    async def test_no_wallet_returns_error(self):
        """Test that missing wallet returns an error."""
        result = await get_all_balances(wallet=None)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Wallet not configured" in parsed["error"]

    @pytest.mark.asyncio
    async def test_strike_balances_success(self):
        """Test successful multi-currency balance retrieval from Strike."""
        wallet = _make_strike_wallet_mock()

        btc_balance = MagicMock()
        btc_balance.currency = "BTC"
        btc_balance.available = Decimal("0.00100000")
        btc_balance.total = Decimal("0.00100000")
        btc_balance.pending = Decimal("0")

        usd_balance = MagicMock()
        usd_balance.currency = "USD"
        usd_balance.available = Decimal("50.00")
        usd_balance.total = Decimal("50.00")
        usd_balance.pending = Decimal("0")

        balance_result = MagicMock()
        balance_result.success = True
        balance_result.balances = [btc_balance, usd_balance]

        wallet.get_all_balances = AsyncMock(return_value=balance_result)

        result = await get_all_balances(wallet=wallet)
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["provider"] == "Strike"
        assert len(parsed["balances"]) == 2
        btc = next(b for b in parsed["balances"] if b["currency"] == "BTC")
        assert btc["available"] == 0.001
        assert "sats" in btc["formatted"]

    @pytest.mark.asyncio
    async def test_strike_balances_failure(self):
        """Test failed balance retrieval from Strike."""
        wallet = _make_strike_wallet_mock()

        balance_result = MagicMock()
        balance_result.success = False
        balance_result.error_message = "Unauthorized"
        balance_result.error_code = "AUTH_ERROR"

        wallet.get_all_balances = AsyncMock(return_value=balance_result)

        result = await get_all_balances(wallet=wallet)
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "Unauthorized" in parsed["error"]

    @pytest.mark.asyncio
    async def test_non_strike_wallet_fallback(self):
        """Test fallback to simple balance for non-Strike wallets."""
        wallet = AsyncMock()  # Not spec'd as StrikeWallet
        wallet.get_balance = AsyncMock(return_value=50000)

        result = await get_all_balances(wallet=wallet)
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert len(parsed["balances"]) == 1
        assert parsed["balances"][0]["currency"] == "BTC"

    @pytest.mark.asyncio
    async def test_strike_wallet_param(self):
        """Test using separate strike_wallet parameter."""
        strike_wallet = _make_strike_wallet_mock()

        btc_balance = MagicMock()
        btc_balance.currency = "BTC"
        btc_balance.available = Decimal("0.005")
        btc_balance.total = Decimal("0.005")
        btc_balance.pending = Decimal("0")

        balance_result = MagicMock()
        balance_result.success = True
        balance_result.balances = [btc_balance]

        strike_wallet.get_all_balances = AsyncMock(return_value=balance_result)

        result = await get_all_balances(wallet=None, strike_wallet=strike_wallet)
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["provider"] == "Strike"

    @pytest.mark.asyncio
    async def test_exception_handling(self):
        """Test that exceptions are caught and returned as errors."""
        wallet = _make_strike_wallet_mock()
        wallet.get_all_balances = AsyncMock(
            side_effect=Exception("Connection refused")
        )

        result = await get_all_balances(wallet=wallet)
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "Connection refused" in parsed["error"]
