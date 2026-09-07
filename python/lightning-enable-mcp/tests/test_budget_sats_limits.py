"""Sats-denominated budget limits — an alternative to the USD limits.

``limits.maxPerPaymentSats`` / ``limits.maxPerSessionSats`` let an operator bound an
agent in the unit the agent actually spends. The point is that a sats budget has NO
price-feed dependency: three price sources being down must not stop a sats-budgeted
agent from paying, the way it (correctly) stops a USD-budgeted one that cannot be
evaluated.

When both denominations are set the STRICTER cap wins per check.

Mirrors ``dotnet/tests/LightningEnable.Mcp.Tests/Services/BudgetSatsLimitsTests.cs``.
"""

from decimal import Decimal
from unittest.mock import AsyncMock, MagicMock

import pytest

from lightning_enable_mcp.budget_service import BudgetService
from lightning_enable_mcp.config import (
    ApprovalLevel,
    ConfigurationService,
    PaymentLimits,
    SessionSettings,
    TierThresholds,
    UserBudgetConfiguration,
)
from lightning_enable_mcp.price_service import PriceUnavailableError

SATS_ENV_VARS = (
    "LIGHTNING_ENABLE_MAX_PER_PAYMENT_SATS",
    "LIGHTNING_ENABLE_MAX_PER_SESSION_SATS",
    "LIGHTNING_ENABLE_AUTO_APPROVE_SATS",
)


@pytest.fixture(autouse=True)
def _no_ambient_sats_env(monkeypatch):
    for name in SATS_ENV_VARS:
        monkeypatch.delenv(name, raising=False)


def _price_service(available: bool = True):
    """1 USD <-> 1000 sats (BTC = $100,000), or a service where every source is down."""
    price = MagicMock()
    if available:
        price.usd_to_sats = AsyncMock(side_effect=lambda usd: int(Decimal(str(usd)) * 1000))
        price.sats_to_usd = AsyncMock(side_effect=lambda sats: Decimal(sats) / 1000)
        price.get_btc_price = AsyncMock(return_value=Decimal("100000"))
        price.get_cached_btc_price = MagicMock(return_value=Decimal("100000"))
    else:
        down = PriceUnavailableError("all price sources failed")
        price.usd_to_sats = AsyncMock(side_effect=down)
        price.sats_to_usd = AsyncMock(side_effect=down)
        price.get_btc_price = AsyncMock(side_effect=down)
        price.get_cached_btc_price = MagicMock(return_value=Decimal("0"))
    price.get_last_snapshot = MagicMock(return_value=None)
    return price


def _config_service(limits: PaymentLimits, session: SessionSettings | None = None):
    cfg = UserBudgetConfiguration(
        tiers=TierThresholds(),
        limits=limits,
        session=session
        or SessionSettings(require_approval_for_first_payment=False, cooldown_seconds=0),
    )
    svc = MagicMock()
    svc.configuration = cfg
    svc.config_file_path = "/tmp/config.json"
    svc.config_file_exists = True
    return svc


def _service(limits: PaymentLimits, price_available: bool = True) -> BudgetService:
    return BudgetService(
        config_service=_config_service(limits),
        price_service=_price_service(price_available),
    )


#: Sats only — no USD limits at all.
SATS_ONLY = PaymentLimits(
    max_per_payment=None,
    max_per_session=None,
    max_per_payment_sats=1_000,
    max_per_session_sats=5_000,
)


# ── Sats-only budget, price feed down ─────────────────────────────────────────


class TestSatsOnlyBudgetSurvivesAPriceOutage:
    @pytest.mark.asyncio
    async def test_within_cap_is_not_denied(self):
        svc = _service(SATS_ONLY, price_available=False)

        result = await svc.check_approval_level(900)

        assert result.level != ApprovalLevel.DENY, (
            "a sats budget has no price dependency, so a price outage must not block it"
        )
        assert result.can_proceed

    @pytest.mark.asyncio
    async def test_over_the_per_payment_cap_is_denied(self):
        svc = _service(SATS_ONLY, price_available=False)

        result = await svc.check_approval_level(1_001)

        assert result.level == ApprovalLevel.DENY
        assert "1,000" in result.denial_reason
        assert "sats" in result.denial_reason

    @pytest.mark.asyncio
    async def test_over_the_session_cap_is_denied(self):
        svc = _service(SATS_ONLY, price_available=False)

        first = await svc.check_approval_level(900)
        assert first.level != ApprovalLevel.DENY
        svc.record_spend(4_500)

        result = await svc.check_approval_level(900)

        assert result.level == ApprovalLevel.DENY
        assert "5,000" in result.denial_reason

    @pytest.mark.asyncio
    async def test_reservation_is_granted_and_bounded(self):
        svc = _service(SATS_ONLY, price_available=False)

        assert (await svc.try_reserve(1_001)).denial_reason.startswith(
            "Payment of 1,001 sats exceeds the per-payment cap"
        ), "the per-payment cap still applies with no price feed"

        # Five 1,000-sat reservations exactly fill the 5,000-sat session cap.
        for _ in range(5):
            reserved = await svc.try_reserve(1_000)
            assert reserved.success, "try_reserve must not fail closed when a sats budget is set"

        sixth = await svc.try_reserve(1_000)
        assert not sixth.success
        assert "session cap" in sixth.denial_reason

    @pytest.mark.asyncio
    async def test_no_sats_limits_still_fails_closed(self):
        """Unchanged behaviour: a USD-only budget cannot be evaluated without a price."""
        usd_only = PaymentLimits(
            max_per_payment=Decimal("500.00"), max_per_session=Decimal("100.00")
        )
        svc = _service(usd_only, price_available=False)

        result = await svc.check_approval_level(900)
        assert result.level == ApprovalLevel.DENY
        assert "price" in result.denial_reason.lower()

        assert (await svc.try_reserve(900)).success is False


# ── Tiering during a price outage ─────────────────────────────────────────────


#: Sats ceilings plus an explicit sats auto-approve tier.
SATS_WITH_AUTO_APPROVE = PaymentLimits(
    max_per_payment=None,
    max_per_session=None,
    max_per_payment_sats=1_000,
    max_per_session_sats=5_000,
    auto_approve_sats=100,
)


class TestPriceOutageTieringFailsClosed:
    """With no BTC price the USD tier ladder cannot be evaluated.

    The sats CEILINGS still bound the spend, but a ceiling is not an approval tier: it
    says "never more than this", not "this much is fine unattended". So the only thing
    that may auto-approve during an outage is an explicit sats tier the operator set.
    Everything else takes the normal confirmation path.
    """

    @pytest.mark.asyncio
    async def test_at_or_below_auto_approve_sats_is_auto_approved(self):
        svc = _service(SATS_WITH_AUTO_APPROVE, price_available=False)

        for amount in (1, 99, 100):
            result = await svc.check_approval_level(amount)
            assert result.level == ApprovalLevel.AUTO_APPROVE, amount
            assert not result.requires_confirmation, amount

    @pytest.mark.asyncio
    async def test_above_auto_approve_sats_requires_confirmation(self):
        svc = _service(SATS_WITH_AUTO_APPROVE, price_available=False)

        result = await svc.check_approval_level(101)

        assert result.requires_confirmation
        assert result.level == ApprovalLevel.FORM_CONFIRM
        assert result.can_proceed, "confirmation is a gate, not a denial"
        assert "100" in result.confirmation_message
        assert "sats" in result.confirmation_message

    @pytest.mark.asyncio
    async def test_without_auto_approve_sats_every_payment_requires_confirmation(self):
        """No explicit tier means no unattended spending while the price is down."""
        svc = _service(SATS_ONLY, price_available=False)

        for amount in (1, 500, 1_000):
            result = await svc.check_approval_level(amount)
            assert result.requires_confirmation, amount
            assert result.level == ApprovalLevel.FORM_CONFIRM, amount

    @pytest.mark.asyncio
    @pytest.mark.parametrize("limits", [SATS_ONLY, SATS_WITH_AUTO_APPROVE])
    @pytest.mark.parametrize("amount", [1, 100, 101, 900])
    async def test_never_log_and_approve_during_an_outage(self, limits, amount):
        """LOG_AND_APPROVE proceeds unattended — it is not an option without a price."""
        svc = _service(limits, price_available=False)

        result = await svc.check_approval_level(amount)

        assert result.level != ApprovalLevel.LOG_AND_APPROVE

    @pytest.mark.asyncio
    async def test_the_hard_ceiling_still_denies_above_it(self):
        svc = _service(SATS_WITH_AUTO_APPROVE, price_available=False)

        result = await svc.check_approval_level(1_001)

        assert result.level == ApprovalLevel.DENY, (
            "a ceiling is checked before any tier — confirmation cannot buy past it"
        )

    @pytest.mark.asyncio
    async def test_an_auto_approve_tier_above_the_ceiling_cannot_widen_it(self):
        # A misconfigured tier must not become a way around the ceiling.
        limits = PaymentLimits(
            max_per_payment=None,
            max_per_session=None,
            max_per_payment_sats=1_000,
            auto_approve_sats=999_999,
        )
        svc = _service(limits, price_available=False)

        assert (await svc.check_approval_level(1_001)).level == ApprovalLevel.DENY
        assert (await svc.try_reserve(1_001)).success is False

    @pytest.mark.asyncio
    async def test_first_payment_approval_setting_is_still_honoured(self):
        svc = BudgetService(
            config_service=_config_service(
                SATS_WITH_AUTO_APPROVE,
                SessionSettings(require_approval_for_first_payment=True, cooldown_seconds=0),
            ),
            price_service=_price_service(False),
        )

        first = await svc.check_approval_level(50)
        assert first.requires_confirmation, (
            "the operator asked for the first payment of the session to be confirmed"
        )

        svc.record_spend(50)
        assert (await svc.check_approval_level(50)).level == ApprovalLevel.AUTO_APPROVE

    @pytest.mark.asyncio
    async def test_the_cooldown_still_applies(self):
        svc = BudgetService(
            config_service=_config_service(
                SATS_WITH_AUTO_APPROVE,
                SessionSettings(require_approval_for_first_payment=False, cooldown_seconds=60),
            ),
            price_service=_price_service(False),
        )
        svc.record_payment_time()

        result = await svc.check_approval_level(50)

        assert result.level == ApprovalLevel.DENY
        assert "Cooldown" in result.denial_reason

    @pytest.mark.asyncio
    async def test_auto_approve_sats_is_ignored_when_the_price_is_available(self):
        """It is an outage-only tier: with a price, the USD ladder decides as always."""
        limits = PaymentLimits(
            max_per_payment=Decimal("500.00"),
            max_per_session=Decimal("100.00"),
            max_per_payment_sats=1_000_000,
            auto_approve_sats=1,
        )
        svc = _service(limits, price_available=True)

        # 500 sats = $0.50, under the $1.00 autoApprove USD tier -> auto-approved even
        # though it is far above the 1-sat outage tier.
        result = await svc.check_approval_level(500)

        assert result.level == ApprovalLevel.AUTO_APPROVE


# ── Both denominations set: the stricter one wins ─────────────────────────────


class TestStricterCapWins:
    @pytest.mark.asyncio
    async def test_sats_cap_stricter_than_usd(self):
        # $5.00 = 5,000 sats, but the sats cap is 1,000.
        limits = PaymentLimits(
            max_per_payment=Decimal("5.00"),
            max_per_session=Decimal("100.00"),
            max_per_payment_sats=1_000,
        )
        svc = _service(limits)

        assert (await svc.check_approval_level(1_500)).level == ApprovalLevel.DENY
        assert (await svc.check_approval_level(900)).level != ApprovalLevel.DENY
        assert (await svc.try_reserve(1_500)).success is False

    @pytest.mark.asyncio
    async def test_usd_cap_stricter_than_sats(self):
        # $1.00 = 1,000 sats, and the sats cap is a looser 50,000.
        limits = PaymentLimits(
            max_per_payment=Decimal("1.00"),
            max_per_session=Decimal("100.00"),
            max_per_payment_sats=50_000,
        )
        svc = _service(limits)

        assert (await svc.check_approval_level(1_500)).level == ApprovalLevel.DENY
        assert (await svc.try_reserve(1_500)).success is False
        assert (await svc.try_reserve(900)).success is True

    @pytest.mark.asyncio
    async def test_session_cap_stricter_of_the_two(self):
        # $10.00 = 10,000 sats session; the sats session cap is 2,000.
        limits = PaymentLimits(
            max_per_payment=Decimal("500.00"),
            max_per_session=Decimal("10.00"),
            max_per_session_sats=2_000,
        )
        svc = _service(limits)

        assert (await svc.try_reserve(1_500)).success is True
        assert (await svc.try_reserve(1_000)).success is False, "2,000-sat session cap binds"

    @pytest.mark.asyncio
    async def test_runtime_tighten_still_wins_when_it_is_the_strictest(self):
        limits = PaymentLimits(
            max_per_payment=Decimal("500.00"),
            max_per_session=Decimal("100.00"),
            max_per_payment_sats=10_000,
            max_per_session_sats=50_000,
        )
        svc = _service(limits)

        tightened = await svc.configure_budget(per_request_sats=500, per_session_sats=1_000)
        assert tightened.success

        assert (await svc.check_approval_level(600)).level == ApprovalLevel.DENY
        assert (await svc.try_reserve(600)).success is False
        assert (await svc.try_reserve(500)).success is True

    @pytest.mark.asyncio
    async def test_tighten_cannot_raise_above_a_sats_config_limit(self):
        """Tighten-only semantics extend to the sats config caps."""
        svc = _service(SATS_ONLY)

        result = await svc.configure_budget(per_request_sats=999_999, per_session_sats=999_999)

        assert result.success is False
        assert "LOWER" in result.error or "lower" in result.error


# ── Status reporting ──────────────────────────────────────────────────────────


class TestBudgetStatusReportsTheBindingDenomination:
    @pytest.mark.asyncio
    async def test_sats_only_reports_sats_as_binding(self):
        svc = _service(SATS_ONLY, price_available=False)
        await svc.check_approval_level(100)

        limits = svc.get_status()["configuration"]["limits"]

        assert limits["maxPerPaymentSats"] == 1_000
        assert limits["maxPerSessionSats"] == 5_000
        assert limits["bindingDenomination"] == "sats"
        assert limits["effectivePerPaymentSats"] == 1_000
        assert limits["effectivePerSessionSats"] == 5_000
        assert "sats" in limits["effectivePerPaymentSource"]

    @pytest.mark.asyncio
    async def test_usd_only_reports_usd_as_binding(self):
        svc = _service(
            PaymentLimits(
                max_per_payment=Decimal("5.00"), max_per_session=Decimal("100.00")
            )
        )
        # Prime the USD -> sats cache so the status has converted caps to report.
        await svc.check_approval_level(100)

        limits = svc.get_status()["configuration"]["limits"]

        assert limits["bindingDenomination"] == "usd"
        assert limits["maxPerPaymentSats"] is None
        assert limits["effectivePerPaymentSats"] == 5_000

    @pytest.mark.asyncio
    async def test_mixed_reports_whichever_binds(self):
        svc = _service(
            PaymentLimits(
                max_per_payment=Decimal("5.00"),
                max_per_session=Decimal("100.00"),
                max_per_payment_sats=1_000,
            )
        )
        await svc.check_approval_level(100)

        limits = svc.get_status()["configuration"]["limits"]

        assert limits["bindingDenomination"] == "sats", "the 1,000-sat cap beats $5.00"
        assert limits["effectivePerPaymentSats"] == 1_000

    @pytest.mark.asyncio
    async def test_price_outage_is_noted_when_sats_carry_the_budget(self):
        svc = _service(SATS_ONLY, price_available=False)
        # The last gate is what get_status reports when the caller does not say.
        await svc.check_approval_level(100)

        limits = svc.get_status()["configuration"]["limits"]

        assert limits["priceAvailable"] is False
        assert "price" in limits["note"].lower()

    def test_caller_can_state_the_price_it_just_observed(self):
        """`budget action=status` refreshes the price itself, so it passes what it found."""
        svc = _service(SATS_ONLY)

        limits = svc.get_status(usd_available=False)["configuration"]["limits"]

        assert limits["priceAvailable"] is False
        assert limits["bindingDenomination"] == "sats"

    @pytest.mark.asyncio
    async def test_outage_mode_and_the_sats_auto_approve_tier_are_reported(self):
        svc = _service(SATS_WITH_AUTO_APPROVE, price_available=False)
        await svc.check_approval_level(50)

        limits = svc.get_status()["configuration"]["limits"]

        assert limits["autoApproveSats"] == 100
        assert limits["outageModeActive"] is True
        assert "confirmation" in limits["note"].lower()

    @pytest.mark.asyncio
    async def test_outage_mode_is_off_while_the_price_is_available(self):
        svc = _service(SATS_WITH_AUTO_APPROVE)
        await svc.check_approval_level(50)

        limits = svc.get_status()["configuration"]["limits"]

        assert limits["outageModeActive"] is False
        assert limits["autoApproveSats"] == 100, "still reported, just not in force"

    def test_outage_mode_is_off_when_no_sats_limits_can_carry_the_budget(self):
        # Without sats limits an outage refuses payments outright — that is not
        # "outage mode", it is the pre-existing fail-closed path.
        svc = _service(
            PaymentLimits(max_per_payment=Decimal("5.00"), max_per_session=Decimal("100.00"))
        )

        assert svc.get_status(usd_available=False)["configuration"]["limits"][
            "outageModeActive"
        ] is False

    @pytest.mark.asyncio
    async def test_runtime_tighten_is_reported_as_binding(self):
        svc = _service(SATS_ONLY)
        await svc.configure_budget(per_request_sats=100, per_session_sats=200)

        limits = svc.get_status()["configuration"]["limits"]

        assert limits["bindingDenomination"] == "runtime"
        assert limits["effectivePerPaymentSats"] == 100


# ── Configuration plumbing ────────────────────────────────────────────────────


class TestSatsLimitsConfiguration:
    def test_read_from_the_config_file(self, tmp_path, monkeypatch):
        monkeypatch.setattr("pathlib.Path.home", lambda: tmp_path)
        config_dir = tmp_path / ".lightning-enable"
        config_dir.mkdir()
        (config_dir / "config.json").write_text(
            '{"limits": {"maxPerPaymentSats": 2500, "maxPerSessionSats": 40000}}',
            encoding="utf-8",
        )

        limits = ConfigurationService().configuration.limits

        assert limits.max_per_payment_sats == 2_500
        assert limits.max_per_session_sats == 40_000

    def test_env_vars_override_the_config_file(self, tmp_path, monkeypatch):
        monkeypatch.setattr("pathlib.Path.home", lambda: tmp_path)
        config_dir = tmp_path / ".lightning-enable"
        config_dir.mkdir()
        (config_dir / "config.json").write_text(
            '{"limits": {"maxPerPaymentSats": 2500}}', encoding="utf-8"
        )
        monkeypatch.setenv("LIGHTNING_ENABLE_MAX_PER_PAYMENT_SATS", "750")
        monkeypatch.setenv("LIGHTNING_ENABLE_MAX_PER_SESSION_SATS", "9000")

        limits = ConfigurationService().configuration.limits

        assert limits.max_per_payment_sats == 750
        assert limits.max_per_session_sats == 9_000

    @pytest.mark.parametrize("bad", ["not-a-number", "0", "-5", ""])
    def test_a_malformed_env_value_is_ignored_not_treated_as_no_limit(
        self, tmp_path, monkeypatch, bad
    ):
        monkeypatch.setattr("pathlib.Path.home", lambda: tmp_path)
        config_dir = tmp_path / ".lightning-enable"
        config_dir.mkdir()
        (config_dir / "config.json").write_text(
            '{"limits": {"maxPerPaymentSats": 2500}}', encoding="utf-8"
        )
        monkeypatch.setenv("LIGHTNING_ENABLE_MAX_PER_PAYMENT_SATS", bad)

        limits = ConfigurationService().configuration.limits

        assert limits.max_per_payment_sats == 2_500, (
            "an unusable env value must never silently widen the operator's budget"
        )

    def test_absent_by_default(self, tmp_path, monkeypatch):
        monkeypatch.setattr("pathlib.Path.home", lambda: tmp_path)

        limits = ConfigurationService().configuration.limits

        assert limits.max_per_payment_sats is None
        assert limits.max_per_session_sats is None
        assert limits.auto_approve_sats is None

    def test_auto_approve_sats_from_the_config_file(self, tmp_path, monkeypatch):
        monkeypatch.setattr("pathlib.Path.home", lambda: tmp_path)
        config_dir = tmp_path / ".lightning-enable"
        config_dir.mkdir()
        (config_dir / "config.json").write_text(
            '{"limits": {"maxPerPaymentSats": 2500, "autoApproveSats": 250}}',
            encoding="utf-8",
        )

        assert ConfigurationService().configuration.limits.auto_approve_sats == 250

    def test_auto_approve_sats_env_var_overrides_the_config_file(self, tmp_path, monkeypatch):
        monkeypatch.setattr("pathlib.Path.home", lambda: tmp_path)
        config_dir = tmp_path / ".lightning-enable"
        config_dir.mkdir()
        (config_dir / "config.json").write_text(
            '{"limits": {"autoApproveSats": 250}}', encoding="utf-8"
        )
        monkeypatch.setenv("LIGHTNING_ENABLE_AUTO_APPROVE_SATS", "42")

        assert ConfigurationService().configuration.limits.auto_approve_sats == 42

    @pytest.mark.parametrize("bad", ["not-a-number", "0", "-5", ""])
    def test_a_malformed_auto_approve_env_value_is_ignored(self, tmp_path, monkeypatch, bad):
        monkeypatch.setattr("pathlib.Path.home", lambda: tmp_path)
        config_dir = tmp_path / ".lightning-enable"
        config_dir.mkdir()
        (config_dir / "config.json").write_text(
            '{"limits": {"autoApproveSats": 250}}', encoding="utf-8"
        )
        monkeypatch.setenv("LIGHTNING_ENABLE_AUTO_APPROVE_SATS", bad)

        assert ConfigurationService().configuration.limits.auto_approve_sats == 250, (
            "an unusable env value must never silently raise the unattended-spend tier"
        )
