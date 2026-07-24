"""
Tests for the unified get_balance tool.

get_balance supersedes check_wallet_balance + get_all_balances and must return a
single superset shape that drops nothing either old tool returned:
- balance_sats / balance_btc (scalar) — ALWAYS the PRIMARY wallet's balance, the headline
- wallet_info block (NWC get_info enrichment)
- balances[] (multi-currency ADDED from a configured Strike wallet, single BTC entry otherwise)
- session spend summary

A configured Strike wallet is SUPPLEMENTARY: it adds balances[] but never replaces the
primary wallet's scalar headline.
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


def _make_strike_result(*balances):
    result = MagicMock()
    result.success = True
    result.balances = list(balances)
    return result


def _balance_entry(currency, amount):
    entry = MagicMock()
    entry.currency = currency
    entry.available = Decimal(amount)
    entry.total = Decimal(amount)
    entry.pending = Decimal("0")
    return entry


class _FakeLndWallet:
    """Primary (non-Strike) wallet whose class name carries the provider identity."""

    def __init__(self, sats):
        self.get_balance = AsyncMock(return_value=sats)
        self.get_info = AsyncMock(return_value=None)


class _FakeOpenNodeWallet:
    """Single-currency wallet that returns the -1 'unavailable' sentinel."""

    def __init__(self, sats):
        self.get_balance = AsyncMock(return_value=sats)
        self.get_info = AsyncMock(return_value=None)


class TestGetBalanceNoWallet:
    @pytest.mark.asyncio
    async def test_no_wallet_uses_receiving_message_not_payment_message(self):
        result = await get_balance(wallet=None)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert parsed["configured"] is False
        # Read-only tool: OpenNode is a valid balance backend and must be offered, and the
        # payment-oriented "cannot pay L402" caveat must NOT appear.
        assert "Wallet not configured" in parsed["error"]
        assert "OPENNODE_API_KEY" in parsed["error"]
        assert "cannot pay" not in parsed["error"]


class TestGetBalanceStrike:
    @pytest.mark.asyncio
    async def test_strike_multi_currency_with_scalar_sats(self):
        """Strike returns multi-currency balances[] AND a derived scalar balance_sats."""
        wallet = _make_strike_wallet_mock()
        wallet.get_all_balances = AsyncMock(return_value=_make_strike_result(
            _balance_entry("BTC", "0.00100000"),
            _balance_entry("USD", "45.10"),
        ))

        parsed = json.loads(await get_balance(wallet=wallet))

        assert parsed["success"] is True
        assert parsed["wallet_type"] == "strike"
        assert parsed["provider"] == "Strike"
        # Multi-currency balances[] preserved from get_all_balances.
        assert len(parsed["balances"]) == 2
        btc = next(b for b in parsed["balances"] if b["currency"] == "BTC")
        assert btc["available"] == 0.001
        assert "sats" in btc["formatted"]
        # Scalar headline derived from the BTC entry of the single multi-currency call.
        assert parsed["balance_sats"] == 100_000
        assert parsed["balance_btc"] == 0.001
        # Exactly one round-trip: the scalar comes from get_all_balances, so get_balance
        # (a second /balances call) must NOT be invoked.
        wallet.get_balance.assert_not_called()

    @pytest.mark.asyncio
    async def test_strike_usd_only_still_returns_scalar(self):
        """A USD-only Strike account keeps balance_sats (honest 0), never drops it."""
        wallet = _make_strike_wallet_mock()
        wallet.get_all_balances = AsyncMock(return_value=_make_strike_result(
            _balance_entry("USD", "45.10"),
        ))

        parsed = json.loads(await get_balance(wallet=wallet))

        assert parsed["success"] is True
        assert parsed["balance_sats"] == 0
        assert parsed["balance_btc"] == 0
        assert len(parsed["balances"]) == 1
        assert parsed["balances"][0]["currency"] == "USD"

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
        assert parsed["errorCode"] == "AUTH_ERROR"

    @pytest.mark.asyncio
    async def test_separate_strike_wallet_param(self):
        strike_wallet = _make_strike_wallet_mock()
        strike_wallet.get_all_balances = AsyncMock(return_value=_make_strike_result(
            _balance_entry("BTC", "0.005"),
        ))

        parsed = json.loads(await get_balance(wallet=None, strike_wallet=strike_wallet))
        assert parsed["success"] is True
        assert parsed["provider"] == "Strike"
        assert parsed["balance_sats"] == 500_000

    @pytest.mark.asyncio
    async def test_strike_exception_handling(self):
        wallet = _make_strike_wallet_mock()
        wallet.get_all_balances = AsyncMock(side_effect=Exception("Connection refused"))
        parsed = json.loads(await get_balance(wallet=wallet))
        assert parsed["success"] is False
        assert "Connection refused" in parsed["error"]


class TestGetBalanceDualWallet:
    @pytest.mark.asyncio
    async def test_lnd_primary_plus_strike_reports_primary_headline_no_hijack(self):
        """LND primary + separate Strike: headline is LND's sats, Strike ADDS balances[]."""
        lnd = _FakeLndWallet(250_000)
        strike = _make_strike_wallet_mock()
        strike.get_all_balances = AsyncMock(return_value=_make_strike_result(
            _balance_entry("BTC", "0.00100000"),  # 100k sats — would be the hijacked value
            _balance_entry("USD", "45.10"),
        ))

        parsed = json.loads(await get_balance(wallet=lnd, strike_wallet=strike))

        assert parsed["success"] is True
        # Headline scalar is the PRIMARY (LND) balance, NOT Strike's BTC entry.
        assert parsed["balance_sats"] == 250_000
        assert parsed["provider"] != "Strike"
        assert parsed["wallet_type"] == "lnd"
        # Strike's multi-currency balances[] are ADDED as supplementary detail.
        assert len(parsed["balances"]) == 2
        currencies = {b["currency"] for b in parsed["balances"]}
        assert currencies == {"BTC", "USD"}


class TestGetBalanceSingleWallet:
    @pytest.mark.asyncio
    async def test_non_strike_single_btc_entry_one_round_trip(self):
        """Non-Strike (NWC/LND/OpenNode) returns a single BTC entry + scalar sats."""
        wallet = AsyncMock()  # not spec'd as StrikeWallet
        wallet.get_balance = AsyncMock(return_value=50_000)
        wallet.get_info = AsyncMock(return_value=None)

        parsed = json.loads(await get_balance(wallet=wallet))
        assert parsed["success"] is True
        assert parsed["balance_sats"] == 50_000
        assert len(parsed["balances"]) == 1
        assert parsed["balances"][0]["currency"] == "BTC"
        # Exactly one balance round-trip for a single-currency wallet.
        wallet.get_balance.assert_awaited_once()

    @pytest.mark.asyncio
    async def test_opennode_balance_unavailable_returns_honest_error(self):
        """OpenNode's -1 sentinel becomes an honest error, never a phantom/negative balance."""
        wallet = _FakeOpenNodeWallet(-1)

        parsed = json.loads(await get_balance(wallet=wallet))
        assert parsed["success"] is False
        assert parsed["errorCode"] == "BALANCE_UNAVAILABLE"
        assert "not available" in parsed["error"]
        # No fabricated/negative balance leaks out.
        assert "balance_sats" not in parsed
        assert "balances" not in parsed

    @pytest.mark.asyncio
    async def test_zero_balance_is_success_not_unavailable(self):
        """A genuine zero balance is success:true with 0 sats, distinct from unavailable."""
        wallet = AsyncMock()
        wallet.get_balance = AsyncMock(return_value=0)
        wallet.get_info = AsyncMock(return_value=None)

        parsed = json.loads(await get_balance(wallet=wallet))
        assert parsed["success"] is True
        assert parsed["balance_sats"] == 0

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
