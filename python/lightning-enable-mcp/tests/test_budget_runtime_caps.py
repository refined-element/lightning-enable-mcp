"""
Tests for runtime tighten-only cap ENFORCEMENT inside BudgetService.check_approval_level.

After an agent lowers its caps via configure_budget, those sats caps must be enforced
on every subsequent approval check (most-restrictive-wins, on top of the USD config
limits). Mirrors the .NET BudgetService runtime-cap enforcement.
"""

from decimal import Decimal
from unittest.mock import AsyncMock, MagicMock

import pytest

from lightning_enable_mcp.budget_service import BudgetService
from lightning_enable_mcp.config import (
    ApprovalLevel,
    UserBudgetConfiguration,
    PaymentLimits,
    TierThresholds,
    SessionSettings,
)


def _fake_price_service():
    """1 USD <-> 1000 sats (BTC = $100,000)."""
    price = MagicMock()
    price.usd_to_sats = AsyncMock(side_effect=lambda usd: int(Decimal(str(usd)) * 1000))
    price.sats_to_usd = AsyncMock(side_effect=lambda sats: Decimal(sats) / 1000)
    price.get_btc_price = AsyncMock(return_value=Decimal("100000"))
    price.get_cached_btc_price = MagicMock(return_value=Decimal("100000"))
    price.get_last_snapshot = MagicMock(return_value=None)
    return price


def _config_service():
    # Generous config limits ($500/payment, $100,000/session) so the runtime caps,
    # not the config limits, are what bite in these tests. cooldown 0 to avoid timing.
    cfg = UserBudgetConfiguration(
        tiers=TierThresholds(),
        limits=PaymentLimits(
            max_per_payment=Decimal("500.00"),
            max_per_session=Decimal("100000.00"),
        ),
        session=SessionSettings(require_approval_for_first_payment=False, cooldown_seconds=0),
    )
    svc = MagicMock()
    svc.configuration = cfg
    return svc


def _service() -> BudgetService:
    return BudgetService(config_service=_config_service(), price_service=_fake_price_service())


@pytest.mark.asyncio
async def test_no_runtime_cap_allows_payment():
    svc = _service()
    result = await svc.check_approval_level(5000)
    assert result.level != ApprovalLevel.DENY


@pytest.mark.asyncio
async def test_runtime_per_request_cap_denies_over():
    svc = _service()
    ok = await svc.configure_budget(per_request_sats=1000, per_session_sats=50000)
    assert ok.success

    # 1500 > runtime per-request cap of 1000 -> DENY.
    result = await svc.check_approval_level(1500)
    assert result.level == ApprovalLevel.DENY
    assert "runtime per-request cap" in result.denial_reason

    # 1000 is exactly at the cap -> not denied by the runtime cap.
    at_cap = await svc.check_approval_level(1000)
    assert at_cap.level != ApprovalLevel.DENY


@pytest.mark.asyncio
async def test_runtime_per_session_cap_denies_when_cumulative_exceeds():
    svc = _service()
    ok = await svc.configure_budget(per_request_sats=5000, per_session_sats=5000)
    assert ok.success

    # First 4000 is fine.
    first = await svc.check_approval_level(4000)
    assert first.level != ApprovalLevel.DENY
    svc.record_spend(4000)

    # Already spent 4000; another 2000 -> 6000 > 5000 session cap -> DENY.
    second = await svc.check_approval_level(2000)
    assert second.level == ApprovalLevel.DENY
    assert "runtime per-session cap" in second.denial_reason

    # A 1000 top-up reaches exactly 5000 -> allowed.
    third = await svc.check_approval_level(1000)
    assert third.level != ApprovalLevel.DENY
