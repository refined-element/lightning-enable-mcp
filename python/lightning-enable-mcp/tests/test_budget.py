"""
Tests for PaymentHistoryService.

The legacy BudgetManager (which combined limits + history + session tracking) was
removed in favor of the .NET-style split: BudgetService owns spending limits and
approval, PaymentHistoryService owns the session audit trail. The still-relevant
history cases from the old BudgetManager tests live here now.
"""

from datetime import datetime, timezone, timedelta

from lightning_enable_mcp.payment_history_service import (
    PaymentHistoryService,
    PaymentRecord,
    MAX_PAYMENT_RECORDS,
)


class TestPaymentHistoryService:
    """Tests for PaymentHistoryService."""

    def test_record_payment(self):
        svc = PaymentHistoryService()

        record = svc.record_payment(
            url="https://api.example.com/data",
            amount_sats=100,
        )

        assert record.url == "https://api.example.com/data"
        assert record.amount_sats == 100
        assert record.status == "success"
        assert svc.total_payments == 1
        assert svc.total_sats_spent == 100

    def test_record_multiple_payments(self):
        svc = PaymentHistoryService()

        svc.record_payment(url="https://api1.example.com", amount_sats=100)
        svc.record_payment(url="https://api2.example.com", amount_sats=200)

        assert svc.total_payments == 2
        assert svc.total_sats_spent == 300

    def test_failed_payment_not_counted_in_spent(self):
        svc = PaymentHistoryService()

        svc.record_payment(url="https://api.example.com", amount_sats=100, status="failed")

        # The record is kept (for the audit trail), but failed payments don't
        # count toward total spent.
        assert svc.total_payments == 1
        assert svc.total_sats_spent == 0

    def test_get_history_limit(self):
        svc = PaymentHistoryService()

        for i in range(5):
            svc.record_payment(url=f"https://api{i}.example.com", amount_sats=10)

        history = svc.get_history(limit=3)
        assert len(history) == 3

    def test_get_history_most_recent_first(self):
        svc = PaymentHistoryService()

        svc.record_payment(url="https://first.example.com", amount_sats=10)
        svc.record_payment(url="https://second.example.com", amount_sats=10)

        history = svc.get_history()
        assert history[0].url == "https://second.example.com"

    def test_get_history_since_filter(self):
        svc = PaymentHistoryService()

        # Inject an old payment directly.
        svc._payments.append(
            PaymentRecord(
                url="https://old.example.com",
                amount_sats=10,
                timestamp=datetime(2024, 1, 1, tzinfo=timezone.utc),
            )
        )
        svc.record_payment(url="https://new.example.com", amount_sats=10)

        since = datetime.now(timezone.utc) - timedelta(hours=1)
        history = svc.get_history(since=since)

        assert len(history) == 1
        assert history[0].url == "https://new.example.com"

    def test_clear(self):
        svc = PaymentHistoryService()
        svc.record_payment(url="https://api.example.com", amount_sats=10)
        svc.clear()
        assert svc.total_payments == 0

    def test_history_is_bounded(self):
        """The in-memory list is capped so a long session can't grow unbounded."""
        svc = PaymentHistoryService()

        for i in range(MAX_PAYMENT_RECORDS + 50):
            svc.record_payment(url=f"https://api{i}.example.com", amount_sats=1)

        assert svc.total_payments == MAX_PAYMENT_RECORDS
        # Oldest were dropped; the most recent are retained.
        urls = {r.url for r in svc.get_history(limit=MAX_PAYMENT_RECORDS)}
        assert f"https://api{MAX_PAYMENT_RECORDS + 49}.example.com" in urls
        assert "https://api0.example.com" not in urls


class TestPaymentRecord:
    """Tests for PaymentRecord — including the funds-safety property that the
    preimage is never stored."""

    def test_no_preimage_field(self):
        """FUNDS-SAFETY: PaymentRecord must not carry a preimage at all."""
        record = PaymentRecord(
            url="https://api.example.com",
            amount_sats=100,
            timestamp=datetime.now(timezone.utc),
        )
        assert not hasattr(record, "preimage")
        # record_payment also has no preimage parameter.
        import inspect

        sig = inspect.signature(PaymentHistoryService.record_payment)
        assert "preimage" not in sig.parameters

    def test_to_dict_has_no_preimage(self):
        record = PaymentRecord(
            url="https://api.example.com",
            amount_sats=100,
            timestamp=datetime(2024, 6, 15, 12, 0, 0, tzinfo=timezone.utc),
            invoice="lnbc100n1...",
        )
        data = record.to_dict()

        assert data["url"] == "https://api.example.com"
        assert data["amount_sats"] == 100
        assert data["status"] == "success"
        assert "timestamp" in data
        assert "preimage" not in data

    def test_to_dict_truncates_long_invoice(self):
        long_invoice = "lnbc100n1p" + "x" * 100
        record = PaymentRecord(
            url="https://api.example.com",
            amount_sats=100,
            timestamp=datetime.now(timezone.utc),
            invoice=long_invoice,
        )
        data = record.to_dict()

        assert len(data["invoice"]) == 23  # 20 chars + "..."
        assert data["invoice"].endswith("...")

    def test_to_dict_omits_invoice_when_absent(self):
        record = PaymentRecord(
            url="https://api.example.com",
            amount_sats=100,
            timestamp=datetime.now(timezone.utc),
        )
        data = record.to_dict()
        assert "invoice" not in data


# =============================================================================
# BudgetService.get_remaining_session_sats
#
# Python's budget is USD-native (config limits) with optional tighten-only
# runtime sats caps, so "remaining sats" must be DERIVED. When it cannot be
# derived the answer is None ("unknown") — never 0, and never via a hardcoded
# BTC rate. A wrong number here overstates spending headroom to an agent.
# =============================================================================

from decimal import Decimal
from unittest.mock import AsyncMock, MagicMock

import pytest

from lightning_enable_mcp.budget_service import BudgetService
from lightning_enable_mcp.config import (
    UserBudgetConfiguration,
    PaymentLimits,
    TierThresholds,
    SessionSettings,
)
from lightning_enable_mcp.price_service import PriceUnavailableError


def _price_service(*, available: bool = True):
    """1 USD <-> 1000 sats (BTC = $100,000). `available=False` models a total outage."""
    price = MagicMock()
    if available:
        price.usd_to_sats = AsyncMock(side_effect=lambda usd: int(Decimal(str(usd)) * 1000))
        price.sats_to_usd = AsyncMock(side_effect=lambda sats: Decimal(sats) / 1000)
        price.get_btc_price = AsyncMock(return_value=Decimal("100000"))
        price.get_cached_btc_price = MagicMock(return_value=Decimal("100000"))
    else:
        err = PriceUnavailableError("CoinGecko, Coinbase, and Kraken all failed")
        price.usd_to_sats = AsyncMock(side_effect=err)
        price.sats_to_usd = AsyncMock(side_effect=err)
        price.get_btc_price = AsyncMock(side_effect=err)
        price.get_cached_btc_price = MagicMock(return_value=Decimal("0"))
    price.get_last_snapshot = MagicMock(return_value=None)
    return price


def _config_service(max_per_session):
    cfg = UserBudgetConfiguration(
        tiers=TierThresholds(),
        limits=PaymentLimits(max_per_payment=Decimal("500.00"), max_per_session=max_per_session),
        session=SessionSettings(require_approval_for_first_payment=False, cooldown_seconds=0),
    )
    svc = MagicMock()
    svc.configuration = cfg
    return svc


def _budget(max_per_session=Decimal("10.00"), *, price_available=True) -> BudgetService:
    return BudgetService(
        config_service=_config_service(max_per_session),
        price_service=_price_service(available=price_available),
    )


class TestGetRemainingSessionSats:
    @pytest.mark.asyncio
    async def test_usd_limit_converted_to_sats(self):
        """$10 session limit @ $100k/BTC -> 10,000 sats."""
        svc = _budget(Decimal("10.00"))
        assert await svc.get_remaining_session_sats() == 10_000

    @pytest.mark.asyncio
    async def test_usd_limit_minus_spend(self):
        svc = _budget(Decimal("10.00"))
        svc.record_spend(2_000)  # 2,000 sats == $2
        assert await svc.get_remaining_session_sats() == 8_000

    @pytest.mark.asyncio
    async def test_no_limit_and_no_runtime_cap_is_unknown(self):
        """Unbounded is NOT a number — and must never be rendered as 'unlimited'."""
        svc = _budget(max_per_session=None)
        assert await svc.get_remaining_session_sats() is None

    @pytest.mark.asyncio
    async def test_runtime_cap_only(self):
        svc = _budget(max_per_session=None)
        assert (await svc.configure_budget(per_request_sats=500, per_session_sats=5_000)).success
        assert await svc.get_remaining_session_sats() == 5_000

    @pytest.mark.asyncio
    async def test_runtime_cap_minus_spend(self):
        svc = _budget(max_per_session=None)
        assert (await svc.configure_budget(per_request_sats=500, per_session_sats=5_000)).success
        svc.record_spend(1_500)
        assert await svc.get_remaining_session_sats() == 3_500

    @pytest.mark.asyncio
    async def test_most_restrictive_wins_runtime_tighter(self):
        """$10 (=10,000 sats) config limit vs 3,000 sats runtime cap -> 3,000."""
        svc = _budget(Decimal("10.00"))
        assert (await svc.configure_budget(per_request_sats=500, per_session_sats=3_000)).success
        assert await svc.get_remaining_session_sats() == 3_000

    @pytest.mark.asyncio
    async def test_most_restrictive_wins_usd_tighter(self):
        """
        $1 (=1,000 sats) config limit vs a looser 9,000 sats runtime cap -> 1,000.

        The runtime cap is set directly rather than via configure_budget, which is
        tighten-only and would (correctly) reject 9,000 against a 1,000-sat config
        cap. The state is still reachable in the real world: config.json is read
        live, so the operator can lower max_per_session after the agent set its
        runtime cap — and a rising BTC price shrinks what a USD limit is worth in
        sats.
        """
        svc = _budget(Decimal("1.00"))
        svc._runtime_max_per_session_sats = 9_000
        assert await svc.get_remaining_session_sats() == 1_000

    @pytest.mark.asyncio
    async def test_price_unavailable_is_unknown_not_zero(self):
        """CRITICAL: no BTC price -> the USD bound is UNKNOWN. Never guess."""
        svc = _budget(Decimal("10.00"), price_available=False)
        assert await svc.get_remaining_session_sats() is None

    @pytest.mark.asyncio
    async def test_price_unavailable_is_unknown_even_with_runtime_cap(self):
        """
        A known runtime cap does NOT rescue an unknown USD bound: the true
        remaining is min(known, unknown), which could be either. Reporting the
        known one would overstate headroom. Fail closed.
        """
        svc = _budget(Decimal("10.00"), price_available=False)
        svc._runtime_max_per_session_sats = 3_000
        assert await svc.get_remaining_session_sats() is None

    @pytest.mark.asyncio
    async def test_never_negative(self):
        """Overspend floors at 0 remaining, never a negative headroom."""
        svc = _budget(Decimal("1.00"))
        svc.record_spend(50_000)  # $50 spent against a $1 limit
        assert await svc.get_remaining_session_sats() == 0

    @pytest.mark.asyncio
    async def test_exhausted_runtime_cap_floors_at_zero(self):
        svc = _budget(max_per_session=None)
        assert (await svc.configure_budget(per_request_sats=500, per_session_sats=1_000)).success
        svc.record_spend(1_000)
        assert await svc.get_remaining_session_sats() == 0

    @pytest.mark.asyncio
    async def test_exhausted_usd_limit_needs_no_price(self):
        """
        Already over the USD limit -> remaining is 0 by arithmetic on USD alone.
        No conversion, so no price needed: this 0 is KNOWN, not a guessed default.
        """
        svc = _budget(Decimal("1.00"), price_available=False)
        svc._session_spent_usd = Decimal("5.00")
        assert await svc.get_remaining_session_sats() == 0
