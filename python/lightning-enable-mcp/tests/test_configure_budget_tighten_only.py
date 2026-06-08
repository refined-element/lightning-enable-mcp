"""
TIGHTEN-ONLY: the configure_budget tool must let an agent only LOWER its spending
caps, never RAISE them — otherwise a prompt-injected agent could loosen its own
limits and then drain the wallet. The logic now lives in
BudgetService.configure_budget (ported from the .NET ConfigureBudgetAsync); the
legacy BudgetManager was removed.
"""

import json
from decimal import Decimal
from unittest.mock import AsyncMock, MagicMock

import pytest

from lightning_enable_mcp.tools.budget import configure_budget
from lightning_enable_mcp.budget_service import BudgetService
from lightning_enable_mcp.config import (
    UserBudgetConfiguration,
    PaymentLimits,
    TierThresholds,
    SessionSettings,
)


# A fixed conversion so config USD limits map to predictable sats caps:
# 1 USD -> 1000 sats (i.e. BTC = $100,000). So maxPerPayment=$500 -> 500,000 sats,
# maxPerSession=$100 -> 100,000 sats.
def _fake_price_service():
    price = MagicMock()

    async def _usd_to_sats(usd):
        return int(Decimal(str(usd)) * 1000)

    async def _sats_to_usd(sats):
        return Decimal(sats) / 1000

    async def _get_btc_price():
        return Decimal("100000")

    price.usd_to_sats = AsyncMock(side_effect=_usd_to_sats)
    price.sats_to_usd = AsyncMock(side_effect=_sats_to_usd)
    price.get_btc_price = AsyncMock(side_effect=_get_btc_price)
    price.get_cached_btc_price = MagicMock(return_value=Decimal("100000"))
    price.get_last_snapshot = MagicMock(return_value=None)
    return price


def _config_service(max_payment="500.00", max_session="100.00"):
    cfg = UserBudgetConfiguration(
        tiers=TierThresholds(),
        limits=PaymentLimits(
            max_per_payment=Decimal(max_payment) if max_payment is not None else None,
            max_per_session=Decimal(max_session) if max_session is not None else None,
        ),
        session=SessionSettings(),
    )
    svc = MagicMock()
    svc.configuration = cfg
    return svc


def _service(max_payment="500.00", max_session="100.00") -> BudgetService:
    return BudgetService(
        config_service=_config_service(max_payment, max_session),
        price_service=_fake_price_service(),
    )


@pytest.mark.asyncio
async def test_can_lower_limits():
    svc = _service()
    # Well under the config caps (500k/request, 100k/session).
    result = await configure_budget(per_request=500, per_session=5000, budget_service=svc)
    parsed = json.loads(result)
    assert parsed["success"] is True
    assert svc.runtime_max_per_request_sats == 500
    assert svc.runtime_max_per_session_sats == 5000


@pytest.mark.asyncio
async def test_cannot_raise_per_request_above_config():
    svc = _service()
    # 600,000 sats/request > config cap of 500,000 sats -> rejected.
    result = await configure_budget(per_request=600_000, per_session=600_000, budget_service=svc)
    parsed = json.loads(result)
    assert parsed["success"] is False
    assert "only LOWER" in parsed["error"]
    assert svc.runtime_max_per_request_sats is None  # unchanged


@pytest.mark.asyncio
async def test_cannot_raise_per_session_above_config():
    svc = _service()
    # 200,000 sats/session > config cap of 100,000 sats -> rejected.
    result = await configure_budget(per_request=1000, per_session=200_000, budget_service=svc)
    parsed = json.loads(result)
    assert parsed["success"] is False
    assert "only LOWER" in parsed["error"]
    assert svc.runtime_max_per_session_sats is None  # unchanged


@pytest.mark.asyncio
async def test_cannot_raise_above_existing_runtime_cap():
    """Once tightened, a later call cannot loosen back toward the config cap."""
    svc = _service()
    first = json.loads(await configure_budget(per_request=1000, per_session=5000, budget_service=svc))
    assert first["success"] is True

    # 2000 > existing runtime cap of 1000 (even though well under config) -> rejected.
    second = json.loads(await configure_budget(per_request=2000, per_session=5000, budget_service=svc))
    assert second["success"] is False
    assert "only LOWER" in second["error"]
    assert svc.runtime_max_per_request_sats == 1000  # unchanged


@pytest.mark.asyncio
async def test_rejects_non_positive():
    svc = _service()
    r1 = json.loads(await configure_budget(per_request=0, per_session=5000, budget_service=svc))
    assert r1["success"] is False
    r2 = json.loads(await configure_budget(per_request=100, per_session=0, budget_service=svc))
    assert r2["success"] is False


@pytest.mark.asyncio
async def test_rejects_per_request_exceeding_per_session():
    svc = _service()
    r = json.loads(await configure_budget(per_request=5000, per_session=1000, budget_service=svc))
    assert r["success"] is False
    assert "cannot exceed" in r["error"]


@pytest.mark.asyncio
async def test_no_config_limit_means_unlimited_config_cap():
    """With no config max set, only validity + existing runtime cap constrain."""
    svc = _service(max_payment=None, max_session=None)
    # Large values pass because there is no config cap to compare against.
    r = json.loads(await configure_budget(per_request=10_000_000, per_session=10_000_000, budget_service=svc))
    assert r["success"] is True
    assert svc.runtime_max_per_request_sats == 10_000_000


@pytest.mark.asyncio
async def test_no_budget_service_returns_error():
    r = json.loads(await configure_budget(per_request=500, per_session=5000, budget_service=None))
    assert r["success"] is False
    assert "not initialized" in r["error"]
