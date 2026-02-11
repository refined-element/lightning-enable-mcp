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

        # Cached sats thresholds (updated when price changes significantly)
        self._auto_approve_sats: int = 0
        self._log_and_approve_sats: int = 0
        self._form_confirm_sats: int = 0
        self._url_confirm_sats: int = 0
        self._max_per_payment_sats: int = 0
        self._max_per_session_sats: int = 0
        self._thresholds_cache_expiry: datetime = datetime.min.replace(tzinfo=timezone.utc)

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

        # Use cached price for synchronous update
        btc_price = self._price_service.get_cached_btc_price()
        btc = Decimal(amount_sats) / Decimal("100000000")
        amount_usd = round(btc * btc_price, 2)

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
        btc_price = self._price_service.get_cached_btc_price()

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
                "btcUsd": float(btc_price),
                "source": self._price_service.get_cache_source() or "cached",
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
