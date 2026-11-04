"""
Budget Service with Multi-Tier Approval Logic

Implements USD-based spending limits with approval tiers matching the .NET implementation.
Configuration is READ-ONLY - loaded from user config file at startup.
AI agents CANNOT modify budget configuration.

This module provides the BudgetService class that combines:
- ConfigurationService (from config.py) for user configuration
- PriceService (from price_service.py) for BTC/USD conversion
- Session tracking for spending limits and cooldowns
"""

import asyncio
import logging
import os
import secrets
import sys
import threading
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone, timedelta
from decimal import Decimal
from typing import Optional

from .config import (
    ApprovalLevel,
    ApprovalCheckResult,
    ConfigurationService,
    UserBudgetConfiguration,
    get_config_service,
)
from .confirmation_channel import (
    ConfirmationChannel,
    ConfirmationChannelKind,
    ConfirmationDispatchResult,
    ConfirmationRequest,
    DEFAULT_REFUSAL,
    StderrConfirmationChannel,
    create_confirmation_channel,
    TTL_ENV_VAR,
    resolve_confirmation_ttl_seconds,
)
from .price_service import PriceService, PriceUnavailableError, get_price_service

logger = logging.getLogger("lightning-enable-mcp.budget-service")


@dataclass
class ConfigureBudgetResult:
    """Result of a tighten-only configure_budget call.

    Mirrors the .NET ``ConfigureBudgetResult``. ``success`` is False with an
    ``error`` message when the request would RAISE caps above the current
    effective limit (rejected), and True with the new effective sats caps
    otherwise.
    """

    success: bool
    error: Optional[str] = None
    effective_per_request_sats: Optional[int] = None
    effective_per_session_sats: Optional[int] = None

    @classmethod
    def fail(cls, error: str) -> "ConfigureBudgetResult":
        return cls(success=False, error=error)

    @classmethod
    def ok(cls, per_request_sats: int, per_session_sats: int) -> "ConfigureBudgetResult":
        return cls(
            success=True,
            effective_per_request_sats=per_request_sats,
            effective_per_session_sats=per_session_sats,
        )


def _min_cap(*caps: "int | None") -> "int | None":
    """Most-restrictive of any number of optional sats caps. None means 'no cap'
    (unlimited), so it is ignored; the result is None only when EVERY input is None.
    Mirrors the .NET ``Math.Min(x, y ?? long.MaxValue)`` with a 0/None sentinel for 'no
    config limit'."""
    present = [c for c in caps if c is not None]
    return min(present) if present else None


#: Where an effective cap came from, for ``budget action=status``. Ordered from the
#: operator's file to the agent's own tightening.
CAP_SOURCE_USD = "config USD limit (maxPerPayment / maxPerSession)"
CAP_SOURCE_SATS = "config sats limit (maxPerPaymentSats / maxPerSessionSats)"
CAP_SOURCE_RUNTIME = "runtime cap (budget action=tighten)"
CAP_SOURCE_NONE = "no limit configured"

#: ``bindingDenomination`` values reported by ``get_status``.
_SOURCE_TO_DENOMINATION = {
    CAP_SOURCE_USD: "usd",
    CAP_SOURCE_SATS: "sats",
    CAP_SOURCE_RUNTIME: "runtime",
    CAP_SOURCE_NONE: "none",
}


@dataclass(frozen=True)
class EffectiveCap:
    """A resolved spending cap in satoshis, and which configured limit produced it."""

    sats: "int | None"
    source: str

    @property
    def denomination(self) -> str:
        return _SOURCE_TO_DENOMINATION[self.source]


@dataclass
class SpendReservationResult:
    """Result of an atomic spend reservation (:meth:`BudgetService.try_reserve`).

    A reservation is the funds-safety primitive that closes the check-then-pay race: the
    amount that could become committed is held against the effective session cap BEFORE the
    wallet is called, so two concurrent payments can never both pass against the same
    pre-payment balance. On success the caller MUST later either
    :meth:`BudgetService.commit_reservation` (funds moved) or
    :meth:`BudgetService.release_reservation` (proven no funds moved). Mirrors the .NET
    ``SpendReservationResult`` record.
    """

    success: bool
    reservation_id: Optional[str] = None
    reserved_sats: int = 0
    denial_reason: Optional[str] = None

    @classmethod
    def reserved(cls, reservation_id: str, reserved_sats: int) -> "SpendReservationResult":
        return cls(success=True, reservation_id=reservation_id, reserved_sats=reserved_sats)

    @classmethod
    def denied(cls, reason: str) -> "SpendReservationResult":
        return cls(success=False, denial_reason=reason)


@dataclass
class PendingConfirmation:
    """A pending out-of-band payment confirmation, bound to a specific amount + tool.

    The code is emitted ONLY to the server console/stderr by the calling tool — never
    returned in a tool result — so a prompt-injected model cannot read it and self-approve.
    The human operator reads it from the console and relays it back. One-time use, 2-minute
    expiry. Mirrors the .NET PendingConfirmation.
    """

    nonce: str
    amount_sats: int
    amount_usd: Decimal
    tool_name: str
    description: str
    # The exact payment target the code authorizes — the BOLT11 invoice (pay_invoice /
    # pay_l402_challenge), the resource URL (access_l402_resource / settle_agent_service),
    # or the on-chain address (send_onchain). Bound and checked on consume so a code can
    # never be redirected to a different destination (#21). Distinct from `description`,
    # which is a display string and may be redacted/truncated.
    destination: str
    created_at: datetime
    expires_at: datetime

    @property
    def is_expired(self) -> bool:
        return datetime.now(timezone.utc) >= self.expires_at


class BudgetService:
    """
    Service for managing spending budget limits with multi-tier approval.
    Configuration is READ-ONLY - loaded from user config file at startup.
    AI agents CANNOT modify budget configuration.

    This service combines:
    - ConfigurationService for user budget configuration
    - PriceService for BTC/USD price conversion
    - Session tracking for spending limits and cooldowns

    The approval flow:
    1. Check if payment exceeds session limit -> DENY
    2. Check if payment exceeds per-payment limit -> DENY
    3. Check if cooldown is active -> DENY
    4. Check first payment flag if configured -> FORM_CONFIRM or URL_CONFIRM
    5. Compare USD amount against tier thresholds -> appropriate level

    Usage:
        budget_service = create_budget_service()

        # Check approval level before making a payment
        result = await budget_service.check_approval_level(1000)  # 1000 sats
        if result.can_proceed:
            if not result.requires_confirmation:
                # Auto-approve or log-and-approve
                await make_payment(1000)
                budget_service.record_spend(1000)
                budget_service.record_payment_time()
            else:
                # Needs user confirmation first
                print(result.confirmation_message)
    """

    def __init__(
        self,
        config_service: Optional[ConfigurationService] = None,
        price_service: Optional[PriceService] = None,
        confirmation_channel: ConfirmationChannel | None = None,
    ) -> None:
        """
        Initialize the BudgetService.

        Args:
            config_service: Optional ConfigurationService instance. If not provided,
                          uses the global singleton from get_config_service().
            price_service: Optional PriceService instance. If not provided,
                          uses the global singleton from get_price_service().
            confirmation_channel: Where out-of-band confirmation codes are delivered.
                          None means the historical local behaviour (print to stderr);
                          get_budget_service() supplies the configured channel.
        """
        self._config_service = config_service or get_config_service()
        self._price_service = price_service or get_price_service()
        self._confirmation_channel: ConfirmationChannel = (
            confirmation_channel or StderrConfirmationChannel()
        )

        # Session tracking
        self._session_spent_sats: int = 0
        self._session_spent_usd: Decimal = Decimal("0")
        self._request_count: int = 0
        self._session_started: datetime = datetime.now(timezone.utc)
        self._last_payment_time: datetime = datetime.min.replace(tzinfo=timezone.utc)
        self._is_first_payment: bool = True

        # Out-of-band confirmation store (code -> PendingConfirmation). The code is
        # printed to stderr by the pay tools and never returned in a tool result.
        self._pending_confirmations: dict[str, PendingConfirmation] = {}
        # The confirmation methods are SYNC (called from async tools), so the async
        # self._lock can't guard them. Use a re-entrant threading lock so create/validate/
        # consume are atomic even under a thread pool / multi-worker (hosted) server — and
        # so create_pending_confirmation can call _clean_expired_confirmations while held.
        self._confirmation_lock = threading.RLock()
        self._failed_confirmation_attempts = 0

        # Cached sats thresholds (updated when price changes significantly)
        self._auto_approve_sats: int = 0
        self._log_and_approve_sats: int = 0
        self._form_confirm_sats: int = 0
        self._url_confirm_sats: int = 0
        self._max_per_payment_sats: int = 0
        self._max_per_session_sats: int = 0
        self._thresholds_cache_expiry: datetime = datetime.min.replace(tzinfo=timezone.utc)

        # Tighten-only runtime caps (sats) set by the agent via configure_budget.
        # None = no runtime cap. Enforced in addition to the USD config limits
        # (most-restrictive-wins). An agent can only ever LOWER these. Mirrors the
        # .NET BudgetService _runtimeMaxPerRequestSats / _runtimeMaxPerSessionSats.
        self._runtime_max_per_request_sats: Optional[int] = None
        self._runtime_max_per_session_sats: Optional[int] = None

        # Active spend reservations (id -> reserved sats). The sum is mirrored in
        # _reserved_sats so the reservation gate is O(1). Evaluating (settled + reserved)
        # and inserting a reservation happen in ONE critical section under _lock, which is
        # what makes the session cap race-safe. Mirrors the .NET BudgetService
        # _reservations / _reservedSats.
        self._reservations: dict[str, int] = {}
        self._reserved_sats: int = 0

        # Whether the last cap evaluation had a BTC price. False means the USD limits
        # could not be converted and the sats limits carried the budget on their own —
        # surfaced by get_status so the operator can see it.
        self._price_available: bool = True

        # Lock for thread safety
        self._lock = asyncio.Lock()

    # =========================================================================
    # Effective caps
    #
    # Every gate resolves the same way: take the most restrictive of the USD config
    # limit (converted), the sats config limit (used as-is), and the tighten-only
    # runtime cap. `usd_available` is what makes a sats budget price-independent — with
    # no price the USD leg is simply absent from the comparison.
    # =========================================================================

    def _effective_request_cap(self, usd_available: bool) -> EffectiveCap:
        limits = self._config_service.configuration.limits
        usd_cap = (
            self._max_per_payment_sats
            if usd_available and limits.max_per_payment is not None and self._max_per_payment_sats > 0
            else None
        )
        return self._resolve_cap(usd_cap, limits.max_per_payment_sats, self._runtime_max_per_request_sats)

    def _effective_session_cap(self, usd_available: bool) -> EffectiveCap:
        limits = self._config_service.configuration.limits
        usd_cap = (
            self._max_per_session_sats
            if usd_available and limits.max_per_session is not None and self._max_per_session_sats > 0
            else None
        )
        return self._resolve_cap(usd_cap, limits.max_per_session_sats, self._runtime_max_per_session_sats)

    @staticmethod
    def _resolve_cap(
        usd_derived: "int | None", sats_config: "int | None", runtime: "int | None"
    ) -> EffectiveCap:
        """Most-restrictive-wins, remembering WHICH limit won.

        Ties resolve to the config limits before the runtime cap, and to USD before sats,
        so the reported source names the operator's own setting rather than an equal
        agent-set one.
        """
        winner = _min_cap(usd_derived, sats_config, runtime)
        if winner is None:
            return EffectiveCap(None, CAP_SOURCE_NONE)
        if usd_derived == winner:
            return EffectiveCap(winner, CAP_SOURCE_USD)
        if sats_config == winner:
            return EffectiveCap(winner, CAP_SOURCE_SATS)
        return EffectiveCap(winner, CAP_SOURCE_RUNTIME)

    async def _refresh_price_and_thresholds(self) -> bool:
        """Prime the BTC price and the USD->sats cap cache. Returns whether USD limits
        can be evaluated on this pass.

        A price outage is only survivable when sats limits are configured — that is the
        whole point of setting them. Without them there is nothing left to enforce, so
        the caller must fail closed exactly as it always has.
        """
        try:
            await self._price_service.get_btc_price()
            await self._update_thresholds_if_needed()
            self._price_available = True
            return True
        except PriceUnavailableError:
            self._price_available = False
            if not self._config_service.configuration.limits.has_sats_limits:
                raise
            logger.warning(
                "BTC price unavailable; enforcing the satoshi limits only. The USD limits "
                "cannot be evaluated until a price source recovers."
            )
            return False

    async def check_approval_level(self, amount_sats: int) -> ApprovalCheckResult:
        """
        Checks what approval level is required for a payment.
        Uses USD-based tier thresholds converted to sats.

        This is the main entry point for budget validation. It:
        1. Updates cached sats thresholds if price has changed
        2. Converts the sats amount to USD
        3. Checks against all limits (session, per-payment, cooldown)
        4. Determines the approval level based on tier thresholds

        Args:
            amount_sats: Amount to spend in satoshis.

        Returns:
            ApprovalCheckResult with:
            - level: The approval level (AUTO_APPROVE, LOG_AND_APPROVE, FORM_CONFIRM, URL_CONFIRM, or DENY)
            - amount_sats: The input amount
            - amount_usd: The USD equivalent
            - can_proceed: True if level is not DENY
            - requires_confirmation: True if level is FORM_CONFIRM or URL_CONFIRM
            - denial_reason: Explanation if denied
            - confirmation_message: Message to show user if confirmation needed
            - remaining_session_budget_usd: How much USD is left in session budget
        """
        config = self._config_service.configuration

        # A sats budget survives a price outage; a USD-only one cannot be evaluated and
        # must still fail closed (the PriceUnavailableError propagates, as before).
        try:
            usd_available = await self._refresh_price_and_thresholds()
        except PriceUnavailableError:
            return ApprovalCheckResult(
                level=ApprovalLevel.DENY,
                amount_sats=amount_sats,
                amount_usd=Decimal("0"),
                denial_reason=(
                    "BTC price is currently unavailable (all price sources failed), so this "
                    "payment cannot be checked against your budget and was refused. Please "
                    "retry shortly, or set limits.maxPerPaymentSats / maxPerSessionSats in "
                    "~/.lightning-enable/config.json for a budget that needs no price feed."
                ),
                remaining_session_budget_usd=Decimal("0"),
            )

        if not usd_available:
            return await self._check_sats_only(amount_sats)

        amount_usd = await self._price_service.sats_to_usd(amount_sats)

        async with self._lock:
            session_spent_usd = await self._price_service.sats_to_usd(self._session_spent_sats)
            session_limit_usd = config.limits.max_per_session or Decimal("999999999")
            remaining_session_usd = session_limit_usd - session_spent_usd

            # Check session limit first
            if config.limits.max_per_session is not None:
                if session_spent_usd + amount_usd > config.limits.max_per_session:
                    return ApprovalCheckResult(
                        level=ApprovalLevel.DENY,
                        amount_sats=amount_sats,
                        amount_usd=amount_usd,
                        denial_reason=(
                            f"Payment of ${amount_usd:.2f} would exceed session limit. "
                            f"Spent: ${session_spent_usd:.2f}, "
                            f"Limit: ${session_limit_usd:.2f}, "
                            f"Remaining: ${remaining_session_usd:.2f}"
                        ),
                        remaining_session_budget_usd=max(Decimal("0"), remaining_session_usd),
                    )

            # Check per-payment limit
            if config.limits.max_per_payment is not None:
                if amount_usd > config.limits.max_per_payment:
                    return ApprovalCheckResult(
                        level=ApprovalLevel.DENY,
                        amount_sats=amount_sats,
                        amount_usd=amount_usd,
                        denial_reason=(
                            f"Payment of ${amount_usd:.2f} exceeds maximum per-payment limit "
                            f"of ${config.limits.max_per_payment:.2f}. "
                            "Edit ~/.lightning-enable/config.json to change limits."
                        ),
                        remaining_session_budget_usd=max(Decimal("0"), remaining_session_usd),
                    )

            # Sats config limits, enforced on top of the USD ones — most-restrictive-wins.
            # Whichever denomination is tighter denies first, so an operator can set both
            # and get the stricter of the two without ordering mattering.
            sats_denial = self._sats_config_denial(amount_sats)
            if sats_denial is not None:
                return ApprovalCheckResult(
                    level=ApprovalLevel.DENY,
                    amount_sats=amount_sats,
                    amount_usd=amount_usd,
                    denial_reason=sats_denial,
                    remaining_session_budget_usd=max(Decimal("0"), remaining_session_usd),
                )

            # Runtime tighten-only caps (set via budget action=tighten). Sats-based, enforced
            # on top of the USD config limits above — most-restrictive-wins. Mirrors the
            # .NET BudgetService runtime-cap enforcement.
            if (
                self._runtime_max_per_request_sats is not None
                and amount_sats > self._runtime_max_per_request_sats
            ):
                return ApprovalCheckResult(
                    level=ApprovalLevel.DENY,
                    amount_sats=amount_sats,
                    amount_usd=amount_usd,
                    denial_reason=(
                        f"Payment of {amount_sats:,} sats exceeds the runtime per-request cap of "
                        f"{self._runtime_max_per_request_sats:,} sats set via budget action=tighten."
                    ),
                    remaining_session_budget_usd=max(Decimal("0"), remaining_session_usd),
                )
            if (
                self._runtime_max_per_session_sats is not None
                and self._session_spent_sats + amount_sats > self._runtime_max_per_session_sats
            ):
                return ApprovalCheckResult(
                    level=ApprovalLevel.DENY,
                    amount_sats=amount_sats,
                    amount_usd=amount_usd,
                    denial_reason=(
                        f"Payment of {amount_sats:,} sats would exceed the runtime per-session cap of "
                        f"{self._runtime_max_per_session_sats:,} sats (already spent "
                        f"{self._session_spent_sats:,}) set via budget action=tighten."
                    ),
                    remaining_session_budget_usd=max(Decimal("0"), remaining_session_usd),
                )

            # Check cooldown
            if not self._is_cooldown_elapsed():
                cooldown_remaining = (
                    config.session.cooldown_seconds
                    - (datetime.now(timezone.utc) - self._last_payment_time).total_seconds()
                )
                return ApprovalCheckResult(
                    level=ApprovalLevel.DENY,
                    amount_sats=amount_sats,
                    amount_usd=amount_usd,
                    denial_reason=f"Cooldown active. Please wait {cooldown_remaining:.1f} seconds before next payment.",
                    remaining_session_budget_usd=max(Decimal("0"), remaining_session_usd),
                )

            # Determine approval level based on tiers
            level: ApprovalLevel
            confirm_message: Optional[str] = None

            # First payment of session always requires at least form confirmation
            if self._is_first_payment and config.session.require_approval_for_first_payment:
                level = (
                    ApprovalLevel.URL_CONFIRM
                    if amount_usd > config.tiers.form_confirm
                    else ApprovalLevel.FORM_CONFIRM
                )
                confirm_message = f"First payment of session: ${amount_usd:.2f} ({amount_sats:,} sats)"
            elif amount_usd <= config.tiers.auto_approve:
                level = ApprovalLevel.AUTO_APPROVE
            elif amount_usd <= config.tiers.log_and_approve:
                level = ApprovalLevel.LOG_AND_APPROVE
            elif amount_usd <= config.tiers.form_confirm:
                level = ApprovalLevel.FORM_CONFIRM
                confirm_message = f"Approve payment of ${amount_usd:.2f} ({amount_sats:,} sats)?"
            elif amount_usd <= config.tiers.url_confirm:
                level = ApprovalLevel.URL_CONFIRM
                confirm_message = f"Large payment of ${amount_usd:.2f} requires browser confirmation."
            else:
                # Above all tiers - need URL confirmation for any amount with limit
                level = ApprovalLevel.URL_CONFIRM
                confirm_message = f"Payment of ${amount_usd:.2f} requires secure browser confirmation."

            return ApprovalCheckResult(
                level=level,
                amount_sats=amount_sats,
                amount_usd=amount_usd,
                confirmation_message=confirm_message,
                remaining_session_budget_usd=max(Decimal("0"), remaining_session_usd),
            )

    def _sats_config_denial(self, amount_sats: int) -> "str | None":
        """Why the config's sats limits refuse ``amount_sats``, or None if they allow it.

        Caller must hold ``self._lock`` (it reads ``_session_spent_sats``).
        """
        limits = self._config_service.configuration.limits

        cap = limits.max_per_payment_sats
        if cap is not None and amount_sats > cap:
            return (
                f"Payment of {amount_sats:,} sats exceeds the per-payment limit of "
                f"{cap:,} sats (limits.maxPerPaymentSats). Edit "
                "~/.lightning-enable/config.json to change limits."
            )

        session_cap = limits.max_per_session_sats
        if session_cap is not None and self._session_spent_sats + amount_sats > session_cap:
            remaining = max(0, session_cap - self._session_spent_sats)
            return (
                f"Payment of {amount_sats:,} sats would exceed the session limit of "
                f"{session_cap:,} sats (limits.maxPerSessionSats). Already spent "
                f"{self._session_spent_sats:,}; {remaining:,} sats remain."
            )

        return None

    async def _check_sats_only(self, amount_sats: int) -> ApprovalCheckResult:
        """Approval decided by the satoshi limits alone, because no BTC price is available.

        Only reachable when the operator configured a sats CEILING — that is what makes
        the budget enforceable with no conversion. But a ceiling says "never more than
        this"; it does not say "this much is fine unattended". Those are different
        statements, and the USD tier ladder that normally makes the second one cannot be
        evaluated here.

        So this path FAILS CLOSED on approval:

        * ``limits.autoApproveSats`` at or below → AUTO_APPROVE. This is the operator
          saying, explicitly and in satoshis, how much may be spent without a human.
        * above it, or when it is unset → FORM_CONFIRM, i.e. the normal confirmation
          flow. The paying tools request a code exactly as they always do; the auto-pay
          paths (which refuse anything needing confirmation) refuse. Neither is touched
          here — this only decides the level.

        LOG_AND_APPROVE is never returned: it proceeds unattended, which is precisely
        what must not happen on an unevaluable tier.

        FORM_CONFIRM rather than URL_CONFIRM because URL_CONFIRM's stronger check asks
        the human to retype the payment's USD amount — the one number that does not
        exist during a price outage.

        The ceilings and the cooldown are checked BEFORE any of this, so confirmation can
        never buy past a cap.
        """
        async with self._lock:
            denial = self._sats_config_denial(amount_sats)
            if denial is None:
                if (
                    self._runtime_max_per_request_sats is not None
                    and amount_sats > self._runtime_max_per_request_sats
                ):
                    denial = (
                        f"Payment of {amount_sats:,} sats exceeds the runtime per-request cap "
                        f"of {self._runtime_max_per_request_sats:,} sats set via "
                        "budget action=tighten."
                    )
                elif (
                    self._runtime_max_per_session_sats is not None
                    and self._session_spent_sats + amount_sats
                    > self._runtime_max_per_session_sats
                ):
                    denial = (
                        f"Payment of {amount_sats:,} sats would exceed the runtime per-session "
                        f"cap of {self._runtime_max_per_session_sats:,} sats (already spent "
                        f"{self._session_spent_sats:,}) set via budget action=tighten."
                    )

            if denial is not None:
                return ApprovalCheckResult(
                    level=ApprovalLevel.DENY,
                    amount_sats=amount_sats,
                    amount_usd=Decimal("0"),
                    denial_reason=denial,
                    remaining_session_budget_usd=Decimal("0"),
                )

            if not self._is_cooldown_elapsed():
                config = self._config_service.configuration
                cooldown_remaining = (
                    config.session.cooldown_seconds
                    - (datetime.now(timezone.utc) - self._last_payment_time).total_seconds()
                )
                return ApprovalCheckResult(
                    level=ApprovalLevel.DENY,
                    amount_sats=amount_sats,
                    amount_usd=Decimal("0"),
                    denial_reason=(
                        f"Cooldown active. Please wait {cooldown_remaining:.1f} seconds "
                        "before next payment."
                    ),
                    remaining_session_budget_usd=Decimal("0"),
                )

            # Inside the ceilings. Now: may it proceed WITHOUT a human?
            auto_approve_sats = self._config_service.configuration.limits.auto_approve_sats
            first_payment_needs_approval = (
                self._is_first_payment
                and self._config_service.configuration.session.require_approval_for_first_payment
            )

            if (
                auto_approve_sats is not None
                and amount_sats <= auto_approve_sats
                and not first_payment_needs_approval
            ):
                return ApprovalCheckResult(
                    level=ApprovalLevel.AUTO_APPROVE,
                    amount_sats=amount_sats,
                    amount_usd=Decimal("0"),
                    remaining_session_budget_usd=Decimal("0"),
                )

            if first_payment_needs_approval:
                why = "the first payment of the session always requires confirmation"
            elif auto_approve_sats is None:
                why = (
                    "no limits.autoApproveSats is configured, so nothing may be spent "
                    "unattended while the price is down"
                )
            else:
                why = f"the unattended limit is {auto_approve_sats:,} sats"

            return ApprovalCheckResult(
                level=ApprovalLevel.FORM_CONFIRM,
                amount_sats=amount_sats,
                amount_usd=Decimal("0"),
                confirmation_message=(
                    f"Approve {amount_sats:,} sats? The BTC price is unavailable, so this "
                    f"payment was checked against your satoshi limits only and {why}."
                ),
                remaining_session_budget_usd=Decimal("0"),
            )

    def record_spend(self, amount_sats: int) -> None:
        """
        Records that an amount was spent.

        Call this AFTER a successful payment to update session tracking.
        This uses the cached BTC price for the USD conversion to avoid
        making an async call.

        Args:
            amount_sats: Amount spent in satoshis.

        Raises:
            ValueError: If amount is negative.

        Example:
            result = await budget_service.check_approval_level(1000)
            if result.can_proceed and not result.requires_confirmation:
                await wallet.pay_invoice(invoice)
                budget_service.record_spend(1000)
                budget_service.record_payment_time()
        """
        if amount_sats < 0:
            raise ValueError("Amount cannot be negative")

        # Use last-known cached price for synchronous USD tracking. If no
        # successful fetch has happened yet (cached_btc_price == 0), USD
        # tracking starts later — never substitute a fake number.
        btc_price = self._price_service.get_cached_btc_price()
        btc = Decimal(amount_sats) / Decimal("100000000")
        amount_usd = round(btc * btc_price, 2) if btc_price > 0 else Decimal("0")

        self._session_spent_sats += amount_sats
        self._session_spent_usd += amount_usd
        self._request_count += 1
        self._is_first_payment = False

        logger.info(
            f"Recorded spend: {amount_sats} sats (${amount_usd:.2f}). "
            f"Session total: {self._session_spent_sats} sats (${self._session_spent_usd:.2f})"
        )

    def record_payment_time(self) -> None:
        """
        Records that a payment was just made (for cooldown tracking).

        Call this AFTER a successful payment to start the cooldown timer.
        The cooldown prevents rapid-fire payments that could drain the wallet.

        Example:
            await wallet.pay_invoice(invoice)
            budget_service.record_spend(amount_sats)
            budget_service.record_payment_time()  # Start cooldown
        """
        self._last_payment_time = datetime.now(timezone.utc)

    # =========================================================================
    # Atomic spend reservations (mirrors the .NET TryReserveAsync / CommitReservation /
    # ReleaseReservation). This is what closes the check-then-pay-then-record race:
    # the amount is reserved against the effective session cap BEFORE the wallet is
    # called, so a second concurrent payment is denied instead of passing its own check
    # against the same pre-payment balance.
    # =========================================================================

    async def try_reserve(self, amount_sats: int) -> SpendReservationResult:
        """Atomically reserve ``amount_sats`` against the effective session cap BEFORE the
        wallet is called. Mirrors the .NET ``TryReserveAsync``.

        Fails CLOSED if the BTC price is unavailable (the caps are USD-derived). Refreshes
        the cached USD->sats caps OUTSIDE the lock (it awaits the price service), then under
        ONE ``async with self._lock`` evaluates the effective caps against
        (settled + all reservations + this amount) and inserts the reservation — so a second
        concurrent payment sees this reservation and is denied instead of passing its own
        check against the same balance. The lock covers BOTH the evaluation and the insert
        and is NEVER held across a wallet call.
        """
        if amount_sats <= 0:
            return SpendReservationResult.denied("Reservation amount must be greater than zero.")

        # FAIL CLOSED on a price outage — UNLESS the operator configured satoshi limits,
        # which are enforceable with no conversion. That is exactly what they are for:
        # three price sources being down must not stop a sats-budgeted agent. The USD
        # limits are simply absent from the comparison until a source recovers. Refreshing
        # the cached USD->sats caps happens here too, OUTSIDE the lock (it awaits); the
        # gate below then runs fully synchronously, so the lock is never held across an await.
        try:
            usd_available = await self._refresh_price_and_thresholds()
        except PriceUnavailableError:
            return SpendReservationResult.denied(
                "BTC price is currently unavailable (all price sources failed), so this "
                "payment cannot be checked against your budget and was refused. Please retry "
                "shortly, or set limits.maxPerPaymentSats / maxPerSessionSats in "
                "~/.lightning-enable/config.json for a budget that needs no price feed."
            )

        async with self._lock:
            # Effective caps (sats) = most restrictive of the config USD limit (converted,
            # when a price is available), the config sats limit, and any tighten-only
            # runtime cap.
            eff_session_cap = self._effective_session_cap(usd_available).sats
            eff_request_cap = self._effective_request_cap(usd_available).sats

            if eff_request_cap is not None and amount_sats > eff_request_cap:
                return SpendReservationResult.denied(
                    f"Payment of {amount_sats:,} sats exceeds the per-payment cap of "
                    f"{eff_request_cap:,} sats."
                )

            # The whole point: settled + ALL active reservations + this amount must fit.
            committed_plus_reserved = self._session_spent_sats + self._reserved_sats
            if (
                eff_session_cap is not None
                and committed_plus_reserved + amount_sats > eff_session_cap
            ):
                remaining = max(0, eff_session_cap - committed_plus_reserved)
                return SpendReservationResult.denied(
                    f"Payment of {amount_sats:,} sats would exceed the session cap of "
                    f"{eff_session_cap:,} sats (already spent {self._session_spent_sats:,}, "
                    f"reserved {self._reserved_sats:,} by in-flight payments; "
                    f"{remaining:,} sats available)."
                )

            reservation_id = uuid.uuid4().hex
            self._reservations[reservation_id] = amount_sats
            self._reserved_sats += amount_sats
            return SpendReservationResult.reserved(reservation_id, amount_sats)

    def commit_reservation(self, reservation_id: str, actual_debit_sats: int) -> None:
        """Convert a reservation into settled spend. Mirrors the .NET ``CommitReservation``.

        Pops the reservation (idempotent no-op if absent — so a retried / double commit can
        never double-count), returns its held amount to the pool, then records
        ``actual_debit_sats`` as spend exactly as the old ``record_spend`` did (sats + USD +
        count + first-payment-cleared). Committing LESS than the reserved amount (e.g. an
        on-chain principal+fee that came in under the principal+headroom reserve) automatically
        frees the unused headroom. Use for SETTLED and PENDING outcomes (funds committed).

        Sync with no ``await`` => atomic under the single asyncio event loop, same as the
        record_spend it replaces.
        """
        if actual_debit_sats < 0:
            raise ValueError("Amount cannot be negative")
        if not reservation_id:
            return

        reserved = self._reservations.pop(reservation_id, None)
        if reserved is None:
            # Unknown / already-resolved reservation — idempotent no-op.
            return
        self._reserved_sats -= reserved

        # Record the ACTUAL debit as settled spend, mirroring record_spend's USD tracking so
        # get_status / get_remaining_session_sats stay accurate. Uses the last-known cached
        # price; if none has been fetched yet (0) USD tracking starts later — never a fake number.
        btc_price = self._price_service.get_cached_btc_price()
        btc = Decimal(actual_debit_sats) / Decimal("100000000")
        amount_usd = round(btc * btc_price, 2) if btc_price > 0 else Decimal("0")

        self._session_spent_sats += actual_debit_sats
        self._session_spent_usd += amount_usd
        self._request_count += 1
        self._is_first_payment = False

        logger.info(
            f"Committed reservation: {actual_debit_sats} sats (${amount_usd:.2f}). "
            f"Session total: {self._session_spent_sats} sats (${self._session_spent_usd:.2f})"
        )

    def release_reservation(self, reservation_id: str) -> None:
        """Release a reservation WITHOUT recording spend. Mirrors the .NET ``ReleaseReservation``.

        Use ONLY when no funds provably moved: a hard payment failure, or an exception raised
        before the wallet was ever called. Pops the reservation (idempotent no-op if absent)
        and returns its held amount to the available budget.
        """
        if not reservation_id:
            return
        reserved = self._reservations.pop(reservation_id, None)
        if reserved is not None:
            self._reserved_sats -= reserved

    async def configure_budget(
        self, per_request_sats: int, per_session_sats: int
    ) -> ConfigureBudgetResult:
        """TIGHTEN-ONLY runtime spending caps (sats). Ports the .NET ConfigureBudgetAsync.

        An agent may only LOWER its per-request / per-session caps at runtime — it can
        never RAISE them above the operator's config-file limit (USD→sats) or an already
        tighter runtime cap. This is the whole point: a prompt-injected agent must not be
        able to loosen its own spending authority and then drain the wallet. To raise
        limits, the operator edits ~/.lightning-enable/config.json.

        Returns a ConfigureBudgetResult: ``fail`` (rejected) when the request is invalid
        or would raise a cap above the current effective limit; ``ok`` with the new
        effective caps otherwise.
        """
        if per_request_sats <= 0:
            return ConfigureBudgetResult.fail("per_request must be a positive number of sats.")
        if per_session_sats <= 0:
            return ConfigureBudgetResult.fail("per_session must be a positive number of sats.")
        if per_request_sats > per_session_sats:
            return ConfigureBudgetResult.fail("per_request cannot exceed per_session.")

        # Make sure the config-derived sats caps are current before we compare. A price
        # outage does not block tightening: with sats limits configured the comparison is
        # made against those alone, which can only ever be MORE restrictive than including
        # a USD cap would be.
        try:
            usd_available = await self._refresh_price_and_thresholds()
        except PriceUnavailableError:
            return ConfigureBudgetResult.fail(
                "BTC price is currently unavailable, so the operator's USD limits cannot be "
                "converted to compare against. Please retry shortly."
            )

        async with self._lock:
            # Effective cap = most restrictive of the operator's config limits (USD→sats
            # and/or sats) and any existing runtime cap.
            eff_req = self._effective_request_cap(usd_available).sats
            eff_sess = self._effective_session_cap(usd_available).sats

            # TIGHTEN-ONLY. Refusing to raise caps above the current effective limit is
            # the whole point. None = unlimited (no effective cap), so any request passes.
            if (eff_req is not None and per_request_sats > eff_req) or (
                eff_sess is not None and per_session_sats > eff_sess
            ):
                def _fmt(v: Optional[int]) -> str:
                    return "unlimited" if v is None else f"{v:,} sats"

                return ConfigureBudgetResult.fail(
                    "budget action=tighten can only LOWER spending limits, not raise them. "
                    f"Current effective caps: {_fmt(eff_req)}/request, {_fmt(eff_sess)}/session. "
                    "To increase limits, the operator must edit ~/.lightning-enable/config.json — "
                    "an agent cannot raise its own spending authority."
                )

            self._runtime_max_per_request_sats = per_request_sats
            self._runtime_max_per_session_sats = per_session_sats
            return ConfigureBudgetResult.ok(per_request_sats, per_session_sats)

    async def get_remaining_session_sats(self) -> Optional[int]:
        """Remaining session budget in SATS, or None if it cannot be determined.

        Read-only. Note the port asymmetry: .NET's budget is sats-native, so it can
        just read RemainingSessionBudget. Python's is USD-native (config file limits)
        with optional tighten-only runtime sats caps, so remaining sats must be
        DERIVED as the most restrictive of:
          - the USD session limit minus USD spent, converted at the live BTC price
          - the runtime per-session sats cap minus sats spent (price-independent)

        Returns None — meaning UNKNOWN — when:
          - neither bound is set (unbounded: the wallet balance, not this service,
            is what limits spending), or
          - a USD bound exists but the BTC price is unavailable. A known runtime cap
            does NOT rescue this: the true remaining is min(known, unknown), which
            could be either, so returning the known one would OVERSTATE headroom.

        Callers must render None as "unknown" — never as 0, and never by falling back
        to a hardcoded BTC rate. A wrong number here misstates spending headroom to an
        agent, which is worse than no number at all.
        """
        config = self._config_service.configuration
        bounds: list[int] = []

        # Runtime sats cap: exact, needs no price.
        if self._runtime_max_per_session_sats is not None:
            bounds.append(max(0, self._runtime_max_per_session_sats - self._session_spent_sats))

        # USD config limit: needs the live BTC price to be expressed in sats.
        if config.limits.max_per_session is not None:
            remaining_usd = config.limits.max_per_session - self._session_spent_usd
            if remaining_usd <= 0:
                # Already exhausted — 0 by arithmetic on USD alone. No conversion,
                # so this 0 is KNOWN, not a guessed default.
                bounds.append(0)
            else:
                try:
                    bounds.append(await self._price_service.usd_to_sats(remaining_usd))
                except PriceUnavailableError:
                    # Fail closed: this bound is unknown, so the answer is unknown.
                    logger.debug(
                        "Remaining session sats undeterminable: BTC price unavailable"
                    )
                    return None

        if not bounds:
            return None
        return min(bounds)

    @property
    def runtime_max_per_request_sats(self) -> Optional[int]:
        """The tighten-only runtime per-request cap (sats), or None if unset."""
        return self._runtime_max_per_request_sats

    @property
    def runtime_max_per_session_sats(self) -> Optional[int]:
        """The tighten-only runtime per-session cap (sats), or None if unset."""
        return self._runtime_max_per_session_sats

    # =========================================================================
    # Out-of-band confirmation (mirrors the .NET BudgetService)
    # =========================================================================

    _CONFIRMATION_CODE_CHARS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
    # Most codes outstanding at once. Beyond this a payment tool is refused instead of
    # minting another, so a looping agent cannot spam the operator channel or grow memory.
    MAX_PENDING_CONFIRMATIONS = 3
    # Consecutive invalid attempts (verify or consume) after which EVERY pending code is
    # revoked. A 6-char code over 36 symbols has ~31 bits; without a limit it can be guessed
    # at tool-call speed. With it, each minted code (which notifies the operator) buys at
    # most this many guesses.
    MAX_FAILED_CONFIRMATION_ATTEMPTS = 5

    class PendingConfirmationCapError(RuntimeError):
        """Raised by create_pending_confirmation when the outstanding-code cap is reached."""

    def create_pending_confirmation(
        self,
        amount_sats: int,
        amount_usd: Decimal,
        tool_name: str,
        description: str,
        destination: str,
    ) -> PendingConfirmation:
        """Create a pending confirmation with a crypto-random code, bound to amount + tool
        + destination.

        ``destination`` is the exact payment target (invoice / URL / on-chain address); it is
        checked on consume so an approved code can never be redirected elsewhere (#21).

        The caller (a pay tool) MUST print the returned ``nonce`` to STDERR ONLY and MUST
        NOT include it in the tool result — that is what stops a prompt-injected agent from
        reading the code and self-approving.
        """
        with self._confirmation_lock:
            self._clean_expired_confirmations()
            if len(self._pending_confirmations) >= self.MAX_PENDING_CONFIRMATIONS:
                raise BudgetService.PendingConfirmationCapError(
                    f"{self.MAX_PENDING_CONFIRMATIONS} confirmation codes are already outstanding, "
                    "so no new code was issued. Use a code the operator already has, or wait for "
                    "the outstanding codes to expire, then retry."
                )
            # Regenerate on the (astronomically unlikely) chance of a collision with a
            # still-live confirmation, so a new code can never overwrite — and thereby
            # silently re-bind — an outstanding human-approved one.
            code = "".join(secrets.choice(self._CONFIRMATION_CODE_CHARS) for _ in range(6))
            while code in self._pending_confirmations:
                code = "".join(secrets.choice(self._CONFIRMATION_CODE_CHARS) for _ in range(6))
            now = datetime.now(timezone.utc)
            pc = PendingConfirmation(
                nonce=code,
                amount_sats=amount_sats,
                amount_usd=amount_usd,
                tool_name=tool_name,
                description=description,
                destination=(destination or "").strip(),
                created_at=now,
                expires_at=now + timedelta(seconds=self._confirmation_ttl_seconds()),
            )
            self._pending_confirmations[code] = pc
            return pc

    async def request_confirmation(
        self, request: ConfirmationRequest
    ) -> ConfirmationDispatchResult:
        """Ask the configured approval channel for a human confirmation of an over-threshold
        payment.

        This is the ONLY entry point a payment tool should use: it decides whether a code may
        exist at all, mints it, delivers it out of band, and — if delivery fails — cancels it
        again so no orphan code is left behind.

        On the ``refuse`` channel NO pending confirmation is created; the result carries a
        descriptive, operator-actionable reason. A delivery failure on any other channel is
        also a refusal: a payment is never approved because its notification could not be sent.

        The returned code is for the HUMAN. It must never appear in a tool result.
        """
        channel = self._confirmation_channel

        # REFUSE: short-circuit BEFORE minting. There is no code, so there is nothing an agent
        # (or anyone reading a log) could replay, and no expiring nonce to reason about.
        if channel.kind is ConfirmationChannelKind.REFUSE:
            return ConfirmationDispatchResult.refused(
                channel.kind, channel.refusal_reason or DEFAULT_REFUSAL
            )

        try:
            pending = self.create_pending_confirmation(
                request.amount_sats,
                request.amount_usd,
                request.tool_name,
                request.description,
                destination=request.destination,
            )
        except BudgetService.PendingConfirmationCapError as ex:
            # Pending cap reached: refuse WITHOUT notifying the operator again.
            return ConfirmationDispatchResult.refused(
                channel.kind,
                f"This payment needs human approval, but {ex} The payment was REFUSED, not approved.",
            )

        try:
            delivery = await channel.deliver(pending, request)
        except Exception as ex:  # noqa: BLE001 — a broken channel must refuse, not explode
            delivery = None
            delivery_error = str(ex)
        else:
            delivery_error = delivery.error

        if delivery is None or not delivery.success:
            # FAIL CLOSED. An undelivered code is not an approval, so drop it rather than
            # leaving a live nonce nobody was told about.
            self.cancel_pending_confirmation(pending.nonce)
            return ConfirmationDispatchResult.refused(
                channel.kind,
                "This payment needs human approval and the approval channel "
                f"({channel.kind.value}) could not deliver the request: {delivery_error}. The "
                "payment was REFUSED, not approved. The operator must fix the approval channel "
                "and the agent must then retry the payment from the start.",
            )

        return ConfirmationDispatchResult.delivered_to(
            channel.kind, pending, channel.operator_hint
        )

    @property
    def confirmation_channel_name(self) -> str:
        """Which approval channel this service delivers confirmation codes on."""
        return self._confirmation_channel.kind.value

    def cancel_pending_confirmation(self, nonce: str) -> None:
        """Drop a pending confirmation without consuming it.

        Used when its out-of-band delivery failed, so an undeliverable code can never be
        guessed or replayed later.
        """
        if not nonce:
            return
        with self._confirmation_lock:
            self._pending_confirmations.pop(nonce, None)

    def validate_confirmation(self, nonce: str) -> Optional[PendingConfirmation]:
        """Peek at a confirmation by code WITHOUT consuming it (used by verify_confirmation_code).

        Returns None if the code is unknown or expired (expired codes are purged).
        """
        if not nonce:
            return None
        with self._confirmation_lock:
            pc = self._pending_confirmations.get(nonce)
            if pc is None:
                return self._record_failed_attempt()
            if pc.is_expired:
                self._pending_confirmations.pop(nonce, None)
                return self._record_failed_attempt()
            return pc

    def validate_and_consume_confirmation(
        self,
        nonce: str,
        expected_amount_sats: int,
        expected_tool_name: str,
        expected_destination: str,
    ) -> Optional[PendingConfirmation]:
        """Validate a code, check expiry, verify it matches the amount AND tool AND
        destination about to be paid, then consume it (one-time use).

        Returns None if invalid, expired, already used, or the amount/tool/destination does
        not match. On a MISMATCH the code is NOT consumed (a correct retry still works), so a
        code approved for one (amount, tool, destination) can never authorize a different one.
        """
        if not nonce:
            return None
        with self._confirmation_lock:
            pc = self._pending_confirmations.get(nonce)
            if pc is None:
                return self._record_failed_attempt()
            if pc.is_expired:
                self._pending_confirmations.pop(nonce, None)
                return self._record_failed_attempt()
            # C-3: bind to the EXACT amount AND tool the code was approved for.
            if pc.amount_sats != expected_amount_sats:
                return self._record_failed_attempt()
            if pc.tool_name != expected_tool_name:
                return self._record_failed_attempt()
            # #21 anti-redirect: bind to the EXACT destination too. A code approved to pay
            # invoice/URL/address X must never authorize paying a different one.
            if pc.destination != (expected_destination or "").strip():
                return self._record_failed_attempt()
            # Amount + tool + destination match -> consume (one-time use).
            self._pending_confirmations.pop(nonce, None)
            self._failed_confirmation_attempts = 0
            return pc

    def _record_failed_attempt(self) -> None:
        """Count an invalid code attempt; at the limit revoke EVERY pending code (fail
        closed) so guessing cannot continue against them. Caller holds the lock."""
        with self._confirmation_lock:
            self._failed_confirmation_attempts += 1
            if self._failed_confirmation_attempts >= self.MAX_FAILED_CONFIRMATION_ATTEMPTS:
                self._pending_confirmations.clear()
                self._failed_confirmation_attempts = 0
                print(
                    f"[Lightning Enable] {self.MAX_FAILED_CONFIRMATION_ATTEMPTS} invalid confirmation "
                    "code attempts: all pending confirmation codes were revoked. The agent must "
                    "request a fresh confirmation.",
                    file=sys.stderr,
                    flush=True,
                )
        return None

    def _confirmation_ttl_seconds(self) -> int:
        """Env LIGHTNING_ENABLE_CONFIRMATION_TTL_SECONDS > confirmation.ttlSeconds > 120,
        clamped to 30..900."""
        try:
            config_ttl = self._config_service.configuration.confirmation.ttl_seconds
        except Exception:  # noqa: BLE001 — a missing section means "use the default"
            config_ttl = None
        return resolve_confirmation_ttl_seconds(os.environ.get(TTL_ENV_VAR), config_ttl)

    def _clean_expired_confirmations(self) -> None:
        # Re-entrant: create_pending_confirmation calls this while holding the lock.
        with self._confirmation_lock:
            expired = [code for code, pc in self._pending_confirmations.items() if pc.is_expired]
            for code in expired:
                self._pending_confirmations.pop(code, None)

    def get_user_configuration(self) -> UserBudgetConfiguration:
        """
        Gets the user's budget configuration from config file.

        This configuration is READ-ONLY. To change limits, edit:
        ~/.lightning-enable/config.json

        Returns:
            The frozen UserBudgetConfiguration instance.
        """
        return self._config_service.configuration

    def get_status(self, usd_available: "bool | None" = None) -> dict:
        """
        Get current budget status as a dictionary.

        ``usd_available`` says whether a BTC price could be fetched just now — the caller
        knows, because ``budget action=status`` refreshes the price itself. Omit it to
        report the state the last budget gate observed.

        This is useful for displaying the current state to users or for
        debugging. The returned dict contains:
        - configuration: All config settings from the config file
        - session: Current session state (spent, remaining, etc.)
        - price: Current cached BTC price info
        - note: Reminder that config is read-only

        Returns:
            Dict with complete budget status information.
        """
        config = self._config_service.configuration
        snapshot = self._price_service.get_last_snapshot()
        btc_price = snapshot.btc_usd if snapshot else Decimal("0")

        # Calculate remaining budget
        session_limit_usd = config.limits.max_per_session or Decimal("999999999")
        remaining_usd = max(Decimal("0"), session_limit_usd - self._session_spent_usd)

        # get_status is synchronous, so it never fetches a price: the caller passes what
        # it just observed, or we report the state the last budget gate saw.
        if usd_available is None:
            usd_available = self._price_available
        request_cap = self._effective_request_cap(usd_available)
        session_cap = self._effective_session_cap(usd_available)

        # The tighter of the two caps decides the headline denomination; with no caps at
        # all it is "none".
        if request_cap.sats is None and session_cap.sats is None:
            binding = "none"
        elif session_cap.sats is None:
            binding = request_cap.denomination
        elif request_cap.sats is None:
            binding = session_cap.denomination
        else:
            binding = (
                request_cap.denomination
                if request_cap.sats <= session_cap.sats
                else session_cap.denomination
            )

        # "Outage mode" is the sats-only path actually being in force: no price, and sats
        # limits able to carry the check. Without them an outage refuses payments outright,
        # which is the pre-existing fail-closed path rather than a mode.
        outage_mode_active = not usd_available and config.limits.has_sats_limits

        if outage_mode_active:
            auto_approve_sats = config.limits.auto_approve_sats
            unattended = (
                f"payments up to {auto_approve_sats:,} sats proceed unattended "
                "(limits.autoApproveSats); anything above needs confirmation"
                if auto_approve_sats is not None
                else "every payment needs confirmation, because limits.autoApproveSats is not set"
            )
            note = (
                "The BTC price is unavailable, so the USD limits and tiers are not being "
                f"enforced; your satoshi limits are carrying the budget on their own and "
                f"{unattended}. Set both maxPerPaymentSats and maxPerSessionSats to close "
                "every gap during an outage."
            )
        elif not usd_available:
            note = (
                "The BTC price is unavailable and no satoshi limits are configured, so "
                "payments are refused until a price source recovers. Set "
                "limits.maxPerPaymentSats / maxPerSessionSats for a budget that needs no "
                "price feed."
            )
        elif config.limits.has_sats_limits:
            note = (
                "USD and satoshi limits are both in force; the stricter one wins on every "
                "check. limits.autoApproveSats applies only while the BTC price is "
                "unavailable — right now the USD tiers decide what needs confirmation."
            )
        else:
            note = "Limits are USD-denominated and are converted at the current BTC price."

        return {
            "configuration": {
                "configFile": self._config_service.config_file_path,
                "configFileExists": self._config_service.config_file_exists,
                "currency": config.currency,
                "tiers": {
                    "autoApprove": float(config.tiers.auto_approve),
                    "logAndApprove": float(config.tiers.log_and_approve),
                    "formConfirm": float(config.tiers.form_confirm),
                    "urlConfirm": float(config.tiers.url_confirm),
                },
                "limits": {
                    "maxPerPayment": float(config.limits.max_per_payment) if config.limits.max_per_payment else None,
                    "maxPerSession": float(config.limits.max_per_session) if config.limits.max_per_session else None,
                    "maxPerPaymentSats": config.limits.max_per_payment_sats,
                    "maxPerSessionSats": config.limits.max_per_session_sats,
                    # Outage-only tier: what may be spent without a human when the USD
                    # ladder cannot be evaluated. Null means nothing may.
                    "autoApproveSats": config.limits.auto_approve_sats,
                    "outageModeActive": outage_mode_active,
                    "runtimeMaxPerRequestSats": self._runtime_max_per_request_sats,
                    "runtimeMaxPerSessionSats": self._runtime_max_per_session_sats,
                    # What actually binds right now, in sats, and which configured limit
                    # produced it — so "why was this refused?" is answerable from status
                    # alone rather than by re-deriving the USD conversion by hand.
                    "effectivePerPaymentSats": request_cap.sats,
                    "effectivePerPaymentSource": request_cap.source,
                    "effectivePerSessionSats": session_cap.sats,
                    "effectivePerSessionSource": session_cap.source,
                    "bindingDenomination": binding,
                    "priceAvailable": usd_available,
                    "note": note,
                },
                "session": {
                    "requireApprovalForFirstPayment": config.session.require_approval_for_first_payment,
                    "cooldownSeconds": config.session.cooldown_seconds,
                },
            },
            "session": {
                "spentSats": self._session_spent_sats,
                "spentUsd": float(self._session_spent_usd),
                "remainingUsd": float(remaining_usd),
                "requestCount": self._request_count,
                "sessionStarted": self._session_started.isoformat(),
                "isFirstPayment": self._is_first_payment,
                "cooldownActive": not self._is_cooldown_elapsed(),
            },
            "price": {
                "btcUsd": float(btc_price) if btc_price > 0 else None,
                "source": snapshot.source if snapshot else "unavailable",
                "fetchedAt": snapshot.fetched_at.isoformat() if snapshot else None,
            },
            "note": "Configuration is READ-ONLY. Edit ~/.lightning-enable/config.json to change limits.",
        }

    def _is_cooldown_elapsed(self) -> bool:
        """
        Internal check if cooldown period has elapsed since last payment.

        Uses the cooldown_seconds from user configuration.

        Returns:
            True if enough time has passed since the last payment.
        """
        config = self._config_service.configuration
        elapsed = datetime.now(timezone.utc) - self._last_payment_time
        return elapsed.total_seconds() >= config.session.cooldown_seconds

    async def _update_thresholds_if_needed(self) -> None:
        """
        Update cached sats thresholds if cache expired.

        The thresholds are cached for 5 minutes to avoid constantly
        converting USD to sats. This is important because:
        1. Price fetching can fail or be rate-limited
        2. Small price changes don't significantly affect tier decisions
        3. Reduces API calls and improves performance
        """
        now = datetime.now(timezone.utc)
        if now < self._thresholds_cache_expiry:
            return

        config = self._config_service.configuration

        # Convert USD thresholds to sats
        self._auto_approve_sats = await self._price_service.usd_to_sats(config.tiers.auto_approve)
        self._log_and_approve_sats = await self._price_service.usd_to_sats(config.tiers.log_and_approve)
        self._form_confirm_sats = await self._price_service.usd_to_sats(config.tiers.form_confirm)
        self._url_confirm_sats = await self._price_service.usd_to_sats(config.tiers.url_confirm)

        if config.limits.max_per_payment is not None:
            self._max_per_payment_sats = await self._price_service.usd_to_sats(config.limits.max_per_payment)

        if config.limits.max_per_session is not None:
            self._max_per_session_sats = await self._price_service.usd_to_sats(config.limits.max_per_session)

        # Cache for 5 minutes
        self._thresholds_cache_expiry = now + timedelta(minutes=5)

        logger.debug(
            f"Updated sats thresholds: auto={self._auto_approve_sats}, "
            f"log={self._log_and_approve_sats}, form={self._form_confirm_sats}, "
            f"url={self._url_confirm_sats}"
        )

    # Read-only properties for session state
    @property
    def session_spent_sats(self) -> int:
        """Total satoshis spent in this session."""
        return self._session_spent_sats

    @property
    def session_spent_usd(self) -> Decimal:
        """Total USD spent in this session."""
        return self._session_spent_usd

    @property
    def request_count(self) -> int:
        """Number of payments made in this session."""
        return self._request_count

    @property
    def session_started(self) -> datetime:
        """When this session started (UTC)."""
        return self._session_started

    @property
    def is_first_payment(self) -> bool:
        """Whether the next payment will be the first of the session."""
        return self._is_first_payment


# =============================================================================
# Module-level singleton and factory
# =============================================================================

_default_budget_service: Optional[BudgetService] = None


def get_budget_service() -> BudgetService:
    """
    Get the default BudgetService singleton.

    Creates a new BudgetService on first call using the global
    ConfigurationService and PriceService singletons.

    Returns:
        The global BudgetService instance.
    """
    global _default_budget_service
    if _default_budget_service is None:
        # Build the approval channel from config + environment + whether a human could be
        # watching this console. Any misconfiguration warning is printed once, here.
        config_service = get_config_service()
        channel = create_confirmation_channel(
            config_service.configuration.confirmation,
            warn=lambda message: print(
                f"[Lightning Enable] WARNING: {message}", file=sys.stderr, flush=True
            ),
        )
        logger.info("Approval channel for over-threshold payments: %s", channel.kind.value)
        _default_budget_service = BudgetService(
            config_service=config_service, confirmation_channel=channel
        )
    return _default_budget_service


def create_budget_service(
    config_service: Optional[ConfigurationService] = None,
    price_service: Optional[PriceService] = None,
    confirmation_channel: ConfirmationChannel | None = None,
) -> BudgetService:
    """
    Create a new BudgetService instance.

    Use this when you need a fresh BudgetService with its own session state,
    or when you want to provide custom configuration or price services.

    Args:
        config_service: Optional ConfigurationService. Uses global singleton if not provided.
        price_service: Optional PriceService. Uses global singleton if not provided.

    Returns:
        A new BudgetService instance.

    Example:
        # Create with defaults (uses global singletons)
        service = create_budget_service()

        # Create with custom services (useful for testing)
        mock_config = MockConfigurationService()
        mock_price = MockPriceService()
        service = create_budget_service(mock_config, mock_price)
    """
    return BudgetService(
        config_service=config_service,
        price_service=price_service,
        confirmation_channel=confirmation_channel,
    )
