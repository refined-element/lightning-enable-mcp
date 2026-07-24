"""
Tests for the unified get_balance tool.

get_balance supersedes check_wallet_balance + get_all_balances and must return a
single superset shape that drops nothing either old tool returned:
- balance_sats / balance_btc (scalar)
- wallet_info block (NWC get_info enrichment)
- balances[] (multi-currency for Strike, single BTC entry otherwise)
- session spend summary
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock
from decimal import Decimal

from lightning_enable_mcp.tools.get_balance import get_balance
from lightning_enable_mcp.strike_wallet import StrikeWallet


def _make_strike_wallet_mock(**kwargs):
    """Create a mock that passes isinstance(mock, StrikeWallet) checks."""
    return AsyncMock(spec=StrikeWallet, **kwargs)


class TestGetBalanceNoWallet:
    @pytest.mark.asyncio
    async def test_no_wallet_returns_error(self):
        result = await get_balance(wallet=None)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Wallet not configured" in parsed["error"]
        assert parsed["configured"] is False


class TestGetBalanceStrike:
    @pytest.mark.asyncio
    async def test_strike_multi_currency_with_scalar_sats(self):
        """Strike returns multi-currency balances[] AND a derived scalar balance_sats."""
        wallet = _make_strike_wallet_mock()

        btc_balance = MagicMock()
        btc_balance.currency = "BTC"
        btc_balance.available = Decimal("0.00100000")
        btc_balance.total = Decimal("0.00100000")
        btc_balance.pending = Decimal("0")

        usd_balance = MagicMock()
        usd_balance.currency = "USD"
        usd_balance.available = Decimal("45.10")
        usd_balance.total = Decimal("45.10")
        usd_balance.pending = Decimal("0")

        balance_result = MagicMock()
        balance_result.success = True
        balance_result.balances = [btc_balance, usd_balance]
        wallet.get_all_balances = AsyncMock(return_value=balance_result)

        parsed = json.loads(await get_balance(wallet=wallet))

        assert parsed["success"] is True
        assert parsed["wallet_type"] == "strike"
        assert parsed["provider"] == "Strike"
        # Multi-currency balances[] preserved from get_all_balances.
        assert len(parsed["balances"]) == 2
        btc = next(b for b in parsed["balances"] if b["currency"] == "BTC")
        assert btc["available"] == 0.001
        assert "sats" in btc["formatted"]
        # Scalar sats derived from the BTC entry (superset shape).
        assert parsed["balance_sats"] == 100_000
        assert parsed["balance_btc"] == 0.001

    @pytest.mark.asyncio
    async def test_strike_balances_failure(self):
        wallet = _make_strike_wallet_mock()
        balance_result = MagicMock()
        balance_result.success = False
        balance_result.error_message = "Unauthorized"
        balance_result.error_code = "AUTH_ERROR"
        wallet.get_all_balances = AsyncMock(return_value=balance_result)

        parsed = json.loads(await get_balance(wallet=wallet))
        assert parsed["success"] is False
        assert "Unauthorized" in parsed["error"]

    @pytest.mark.asyncio
    async def test_separate_strike_wallet_param(self):
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

        parsed = json.loads(await get_balance(wallet=None, strike_wallet=strike_wallet))
        assert parsed["success"] is True
        assert parsed["provider"] == "Strike"

    @pytest.mark.asyncio
    async def test_strike_exception_handling(self):
        wallet = _make_strike_wallet_mock()
        wallet.get_all_balances = AsyncMock(side_effect=Exception("Connection refused"))
        parsed = json.loads(await get_balance(wallet=wallet))
        assert parsed["success"] is False
        assert "Connection refused" in parsed["error"]


class TestGetBalanceSingleWallet:
    @pytest.mark.asyncio
    async def test_non_strike_single_btc_entry(self):
        """Non-Strike (NWC/LND/OpenNode) returns a single BTC entry + scalar sats."""
        wallet = AsyncMock()  # not spec'd as StrikeWallet
        wallet.get_balance = AsyncMock(return_value=50_000)
        wallet.get_info = AsyncMock(return_value=None)

        parsed = json.loads(await get_balance(wallet=wallet))
        assert parsed["success"] is True
        assert parsed["balance_sats"] == 50_000
        assert len(parsed["balances"]) == 1
        assert parsed["balances"][0]["currency"] == "BTC"

    @pytest.mark.asyncio
    async def test_nwc_wallet_info_preserved(self):
        """The NWC get_info enrichment block from check_wallet_balance is preserved."""
        wallet = AsyncMock()
        wallet.get_balance = AsyncMock(return_value=123_456)
        wallet.get_info = AsyncMock(return_value={
            "alias": "my-node",
            "network": "mainnet",
            "block_height": 899_999,
            "extra": "ignored",
        })

        parsed = json.loads(await get_balance(wallet=wallet))
        assert parsed["success"] is True
        assert parsed["balance_sats"] == 123_456
        assert parsed["wallet_info"]["alias"] == "my-node"
        assert parsed["wallet_info"]["network"] == "mainnet"
        assert parsed["wallet_info"]["block_height"] == 899_999

    @pytest.mark.asyncio
    async def test_get_info_failure_is_non_fatal(self):
        wallet = AsyncMock()
        wallet.get_balance = AsyncMock(return_value=1_000)
        wallet.get_info = AsyncMock(side_effect=Exception("get_info unsupported"))

        parsed = json.loads(await get_balance(wallet=wallet))
        assert parsed["success"] is True
        assert parsed["balance_sats"] == 1_000
        assert "wallet_info" not in parsed

    @pytest.mark.asyncio
    async def test_balance_exception_handling(self):
        wallet = AsyncMock()
        wallet.get_balance = AsyncMock(side_effect=Exception("Connection refused"))
        parsed = json.loads(await get_balance(wallet=wallet))
        assert parsed["success"] is False
        assert "Connection refused" in parsed["error"]


class TestGetBalanceSession:
    @pytest.mark.asyncio
    async def test_remaining_budget_is_not_hardwired_to_zero(self):
        wallet = AsyncMock()
        wallet.get_balance = AsyncMock(return_value=500_000)
        wallet.get_info = AsyncMock(return_value=None)

        budget = MagicMock()
        budget.get_status = MagicMock(return_value={
            "session": {"spentSats": 1_000, "requestCount": 2},
        })
        budget.get_remaining_session_sats = AsyncMock(return_value=99_000)

        data = json.loads(await get_balance(wallet=wallet, budget_service=budget))
        assert data["session"]["remainingBudgetSats"] == 99_000
        assert data["session"]["spentSats"] == 1_000

    @pytest.mark.asyncio
    async def test_unknown_remaining_budget_is_null_not_zero(self):
        wallet = AsyncMock()
        wallet.get_balance = AsyncMock(return_value=500_000)
        wallet.get_info = AsyncMock(return_value=None)

        budget = MagicMock()
        budget.get_status = MagicMock(return_value={"session": {"spentSats": 0, "requestCount": 0}})
        budget.get_remaining_session_sats = AsyncMock(return_value=None)

        data = json.loads(await get_balance(wallet=wallet, budget_service=budget))
        assert data["session"]["remainingBudgetSats"] is None
        assert "remainingBudgetNote" in data["session"]
