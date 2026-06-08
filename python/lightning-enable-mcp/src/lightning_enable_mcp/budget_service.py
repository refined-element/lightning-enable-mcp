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
import secrets
import threading
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
from .price_service import PriceService, get_price_service

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
    ) -> None:
        """
        Initialize the BudgetService.

        Args:
            config_service: Optional ConfigurationService instance. If not provided,
                          uses the global singleton from get_config_service().
            price_service: Optional PriceService instance. If not provided,
                          uses the global singleton from get_price_service().
        """
        self._config_service = config_service or get_config_service()
        self._price_service = price_service or get_price_service()

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

        # Lock for thread safety
        self._lock = asyncio.Lock()

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
        await self._update_thresholds_if_needed()

        config = self._config_service.configuration
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

            # Runtime tighten-only caps (set via configure_budget). Sats-based, enforced
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
                        f"{self._runtime_max_per_request_sats:,} sats set via configure_budget."
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
                        f"{self._session_spent_sats:,}) set via configure_budget."
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

        # Make sure the config-derived sats caps are current before we compare.
        await self._update_thresholds_if_needed()

        async with self._lock:
            # Effective cap = most restrictive of the operator's config-file limit
            # (USD→sats) and any existing runtime cap. A 0 config cap means "no config
            # limit set" → treat as unlimited for this comparison.
            config_req = self._max_per_payment_sats if self._max_per_payment_sats > 0 else None
            config_sess = self._max_per_session_sats if self._max_per_session_sats > 0 else None

            def _effective(config_cap: Optional[int], runtime_cap: Optional[int]) -> Optional[int]:
                caps = [c for c in (config_cap, runtime_cap) if c is not None]
                return min(caps) if caps else None

            eff_req = _effective(config_req, self._runtime_max_per_request_sats)
            eff_sess = _effective(config_sess, self._runtime_max_per_session_sats)

            # TIGHTEN-ONLY. Refusing to raise caps above the current effective limit is
            # the whole point. None = unlimited (no effective cap), so any request passes.
            if (eff_req is not None and per_request_sats > eff_req) or (
                eff_sess is not None and per_session_sats > eff_sess
            ):
                def _fmt(v: Optional[int]) -> str:
                    return "unlimited" if v is None else f"{v:,} sats"

                return ConfigureBudgetResult.fail(
                    "configure_budget can only LOWER spending limits, not raise them. "
                    f"Current effective caps: {_fmt(eff_req)}/request, {_fmt(eff_sess)}/session. "
                    "To increase limits, the operator must edit ~/.lightning-enable/config.json — "
                    "an agent cannot raise its own spending authority."
                )

            self._runtime_max_per_request_sats = per_request_sats
            self._runtime_max_per_session_sats = per_session_sats
            return ConfigureBudgetResult.ok(per_request_sats, per_session_sats)

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
                expires_at=now + timedelta(minutes=2),
            )
            self._pending_confirmations[code] = pc
            return pc

    def validate_confirmation(self, nonce: str) -> Optional[PendingConfirmation]:
        """Peek at a confirmation by code WITHOUT consuming it (used by confirm_payment).

        Returns None if the code is unknown or expired (expired codes are purged).
        """
        if not nonce:
            return None
        with self._confirmation_lock:
            pc = self._pending_confirmations.get(nonce)
            if pc is None:
                return None
            if pc.is_expired:
                self._pending_confirmations.pop(nonce, None)
                return None
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
                return None
            if pc.is_expired:
                self._pending_confirmations.pop(nonce, None)
                return None
            # C-3: bind to the EXACT amount AND tool the code was approved for.
            if pc.amount_sats != expected_amount_sats:
                return None
            if pc.tool_name != expected_tool_name:
                return None
            # #21 anti-redirect: bind to the EXACT destination too. A code approved to pay
            # invoice/URL/address X must never authorize paying a different one.
            if pc.destination != (expected_destination or "").strip():
                return None
            # Amount + tool + destination match -> consume (one-time use).
            self._pending_confirmations.pop(nonce, None)
            return pc

    def clean_expired_confirmations(self) -> None:
        """Purge expired pending confirmations."""
        self._clean_expired_confirmations()

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

    def get_status(self) -> dict:
        """
        Get current budget status as a dictionary.

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
                    "runtimeMaxPerRequestSats": self._runtime_max_per_request_sats,
                    "runtimeMaxPerSessionSats": self._runtime_max_per_session_sats,
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

    def reset_session(self) -> None:
        """
        Resets the session spending to zero.

        This is useful for:
        - Starting a new conversation/task
        - After the user acknowledges they want to continue spending
        - Testing

        After reset:
        - session_spent_sats = 0
        - session_spent_usd = 0
        - request_count = 0
        - is_first_payment = True
        """
        self._session_spent_sats = 0
        self._session_spent_usd = Decimal("0")
        self._request_count = 0
        self._session_started = datetime.now(timezone.utc)
        self._is_first_payment = True
        logger.info("Session reset")

    def is_cooldown_elapsed(self) -> bool:
        """
        Public check if cooldown period has elapsed since last payment.

        Returns:
            True if enough time has passed since the last payment,
            or if no payment has been made yet.
        """
        return self._is_cooldown_elapsed()

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
        _default_budget_service = BudgetService()
    return _default_budget_service


def create_budget_service(
    config_service: Optional[ConfigurationService] = None,
    price_service: Optional[PriceService] = None,
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
    return BudgetService(config_service=config_service, price_service=price_service)
