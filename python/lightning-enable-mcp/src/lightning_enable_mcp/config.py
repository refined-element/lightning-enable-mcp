"""
Configuration Service

Loads user budget configuration from ~/.lightning-enable/config.json.
Configuration is READ-ONLY at runtime - AI agents CANNOT modify it.
"""

import json
import logging
import os
import sys
from dataclasses import dataclass, field
from decimal import Decimal
from enum import Enum
from pathlib import Path
from typing import Optional

logger = logging.getLogger("lightning-enable-mcp.config")


class ApprovalLevel(Enum):
    """Approval level required for a payment."""

    AUTO_APPROVE = "auto_approve"
    """Payment is auto-approved, no user interaction needed."""

    LOG_AND_APPROVE = "log_and_approve"
    """Payment is approved but logged for user awareness."""

    FORM_CONFIRM = "form_confirm"
    """
    Payment requires user confirmation via MCP form elicitation.
    User sees prompt in AI interface.
    """

    URL_CONFIRM = "url_confirm"
    """
    Payment requires out-of-band confirmation via URL.
    User must open link in browser (AI cannot intercept).
    """

    DENY = "deny"
    """Payment is denied (exceeds limits)."""


@dataclass(frozen=True)
class WalletSettings:
    """
    Wallet connection settings.
    Values here are used if environment variables are not set.

    Note: This dataclass is frozen (immutable) - AI cannot modify at runtime.
    """

    nwc_connection_string: Optional[str] = None
    """
    NWC (Nostr Wallet Connect) connection string.
    Format: nostr+walletconnect://pubkey?relay=...&secret=...
    Recommended for L402 - always returns preimage.
    """

    strike_api_key: Optional[str] = None
    """
    Strike API key from https://dashboard.strike.me/
    Strike returns preimage via lightning.preImage - L402 supported.
    """

    opennode_api_key: Optional[str] = None
    """
    OpenNode API key.
    WARNING: OpenNode does NOT return preimage - L402 will not work.
    """

    opennode_environment: Optional[str] = None
    """
    OpenNode environment: "production" (mainnet) or "dev" (testnet).
    Default: production
    """

    lnd_rest_host: Optional[str] = None
    """
    LND REST API host (e.g., "localhost:8080" or "mynode.local:8080").
    Recommended for L402 - always returns preimage.
    """

    lnd_macaroon_hex: Optional[str] = None
    """
    LND macaroon in hex format (admin.macaroon).
    Required for LND wallet to make payments.
    Get with: xxd -ps -c 1000 ~/.lnd/data/chain/bitcoin/mainnet/admin.macaroon
    """

    priority: Optional[str] = None
    """
    Preferred wallet priority: "lnd", "nwc", "strike", or "opennode".
    Default priority: LND > NWC > Strike > OpenNode
    For L402: Use "lnd" or "nwc" - these return preimage.
    """

    @classmethod
    def from_dict(cls, data: dict) -> "WalletSettings":
        """Create WalletSettings from a dictionary."""
        return cls(
            nwc_connection_string=data.get("nwcConnectionString"),
            strike_api_key=data.get("strikeApiKey"),
            opennode_api_key=data.get("openNodeApiKey"),
            opennode_environment=data.get("openNodeEnvironment"),
            lnd_rest_host=data.get("lndRestHost"),
            lnd_macaroon_hex=data.get("lndMacaroonHex"),
            priority=data.get("priority"),
        )

    def to_dict(self) -> dict:
        """Convert to dictionary for JSON serialization."""
        result = {}
        if self.nwc_connection_string:
            result["nwcConnectionString"] = self.nwc_connection_string
        if self.strike_api_key:
            result["strikeApiKey"] = self.strike_api_key
        if self.opennode_api_key:
            result["openNodeApiKey"] = self.opennode_api_key
        if self.opennode_environment:
            result["openNodeEnvironment"] = self.opennode_environment
        if self.lnd_rest_host:
            result["lndRestHost"] = self.lnd_rest_host
        if self.lnd_macaroon_hex:
            result["lndMacaroonHex"] = self.lnd_macaroon_hex
        if self.priority:
            result["priority"] = self.priority
        return result


@dataclass(frozen=True)
class TierThresholds:
    """
    Tier thresholds that determine what level of approval is required.
    All values are in USD.

    NOTE: FormConfirm and UrlConfirm ideally use MCP elicitation for confirmation.
    Most clients (including Claude Code as of Jan 2025) don't support elicitation yet.
    When elicitation is unavailable, payments requiring confirmation will ask for
    explicit re-invocation with confirmed=true parameter.

    Note: This dataclass is frozen (immutable) - AI cannot modify at runtime.
    """

    auto_approve: Decimal = Decimal("1.00")
    """
    Payments at or below this amount are auto-approved without any prompt.
    Default: $1.00 (raised from $0.10 for better UX since most clients lack elicitation)
    """

    log_and_approve: Decimal = Decimal("5.00")
    """
    Payments above auto_approve but at or below this amount are logged but approved.
    Default: $5.00 (raised from $1.00)
    """

    form_confirm: Decimal = Decimal("25.00")
    """
    Payments above log_and_approve but at or below this amount require confirmation.
    With elicitation: User sees a prompt in the AI interface.
    Without elicitation: User must re-invoke tool with confirmed=true.
    Default: $25.00 (raised from $10.00)
    """

    url_confirm: Decimal = Decimal("100.00")
    """
    Payments above form_confirm but at or below this amount require strong confirmation.
    With elicitation: User must type the exact amount to confirm.
    Without elicitation: User must re-invoke tool with confirmed=true.
    Default: $100.00
    """

    @classmethod
    def from_dict(cls, data: dict) -> "TierThresholds":
        """Create TierThresholds from a dictionary."""
        return cls(
            auto_approve=Decimal(str(data.get("autoApprove", "1.00"))),
            log_and_approve=Decimal(str(data.get("logAndApprove", "5.00"))),
            form_confirm=Decimal(str(data.get("formConfirm", "25.00"))),
            url_confirm=Decimal(str(data.get("urlConfirm", "100.00"))),
        )

    def to_dict(self) -> dict:
        """Convert to dictionary for JSON serialization."""
        return {
            "autoApprove": float(self.auto_approve),
            "logAndApprove": float(self.log_and_approve),
            "formConfirm": float(self.form_confirm),
            "urlConfirm": float(self.url_confirm),
        }


@dataclass(frozen=True)
class PaymentLimits:
    """
    Maximum payment limits.

    Note: This dataclass is frozen (immutable) - AI cannot modify at runtime.
    """

    max_per_payment: Optional[Decimal] = Decimal("500.00")
    """
    Maximum amount per single payment, even with URL confirmation.
    Payments above this are always denied.
    Default: $500.00 (set to None for no limit with URL confirmation)
    """

    max_per_session: Optional[Decimal] = Decimal("100.00")
    """
    Maximum total spending per session.
    Default: $100.00
    """

    @classmethod
    def from_dict(cls, data: dict) -> "PaymentLimits":
        """Create PaymentLimits from a dictionary."""
        max_per_payment = data.get("maxPerPayment")
        max_per_session = data.get("maxPerSession")

        return cls(
            max_per_payment=Decimal(str(max_per_payment)) if max_per_payment is not None else None,
            max_per_session=Decimal(str(max_per_session)) if max_per_session is not None else None,
        )

    def to_dict(self) -> dict:
        """Convert to dictionary for JSON serialization."""
        return {
            "maxPerPayment": float(self.max_per_payment) if self.max_per_payment is not None else None,
            "maxPerSession": float(self.max_per_session) if self.max_per_session is not None else None,
        }


@dataclass(frozen=True)
class SessionSettings:
    """
    Session-level settings.

    Note: This dataclass is frozen (immutable) - AI cannot modify at runtime.
    """

    require_approval_for_first_payment: bool = False
    """
    If true, require confirmation for the very first payment of the session.
    Default: false (disabled because most MCP clients don't support elicitation yet)
    """

    cooldown_seconds: int = 2
    """
    Minimum seconds between payments (prevents rapid-fire attacks).
    Default: 2 seconds
    """

    @classmethod
    def from_dict(cls, data: dict) -> "SessionSettings":
        """Create SessionSettings from a dictionary."""
        return cls(
            require_approval_for_first_payment=data.get("requireApprovalForFirstPayment", False),
            cooldown_seconds=data.get("cooldownSeconds", 2),
        )

    def to_dict(self) -> dict:
        """Convert to dictionary for JSON serialization."""
        return {
            "requireApprovalForFirstPayment": self.require_approval_for_first_payment,
            "cooldownSeconds": self.cooldown_seconds,
        }


@dataclass(frozen=True)
class UserBudgetConfiguration:
    """
    User-configurable budget settings stored in ~/.lightning-enable/config.json.
    These settings are READ-ONLY at runtime - AI cannot modify them.

    Note: This dataclass is frozen (immutable) - AI cannot modify at runtime.
    """

    currency: str = "USD"
    """Currency for threshold values. Currently only "USD" is supported."""

    tiers: TierThresholds = field(default_factory=TierThresholds)
    """Tier thresholds in the configured currency (USD)."""

    limits: PaymentLimits = field(default_factory=PaymentLimits)
    """Maximum payment limits."""

    session: SessionSettings = field(default_factory=SessionSettings)
    """Session-level settings."""

    wallets: WalletSettings = field(default_factory=WalletSettings)
    """
    Wallet connection settings.
    These can be set here instead of environment variables.
    """

    @classmethod
    def from_dict(cls, data: dict) -> "UserBudgetConfiguration":
        """Create UserBudgetConfiguration from a dictionary."""
        return cls(
            currency=data.get("currency", "USD"),
            tiers=TierThresholds.from_dict(data.get("tiers", {})),
            limits=PaymentLimits.from_dict(data.get("limits", {})),
            session=SessionSettings.from_dict(data.get("session", {})),
            wallets=WalletSettings.from_dict(data.get("wallets", {})),
        )

    def to_dict(self) -> dict:
        """Convert to dictionary for JSON serialization."""
        return {
            "currency": self.currency,
            "tiers": self.tiers.to_dict(),
            "limits": self.limits.to_dict(),
            "session": self.session.to_dict(),
            "wallets": self.wallets.to_dict(),
        }


@dataclass
class ApprovalCheckResult:
    """Extended budget check result with approval level."""

    level: ApprovalLevel
    """The approval level required for this payment."""

    amount_sats: int
    """The amount in satoshis."""

    amount_usd: Decimal
    """The amount in USD (for display)."""

    denial_reason: Optional[str] = None
    """Reason if the payment is denied."""

    confirmation_message: Optional[str] = None
    """Message to show user for confirmation prompts."""

    confirmation_url: Optional[str] = None
    """URL for out-of-band confirmation (if level == URL_CONFIRM)."""

    remaining_session_budget_usd: Decimal = Decimal("0")
    """Remaining session budget in USD."""

    @property
    def can_proceed(self) -> bool:
        """Whether this payment can proceed (with or without confirmation)."""
        return self.level != ApprovalLevel.DENY

    @property
    def requires_confirmation(self) -> bool:
        """Whether this payment requires any form of user confirmation."""
        return self.level in (ApprovalLevel.FORM_CONFIRM, ApprovalLevel.URL_CONFIRM)


class ConfigurationService:
    """
    Service for loading and managing user budget configuration.
    Configuration is READ-ONLY at runtime - no tool can modify it.
    """

    def __init__(self) -> None:
        """Initialize the configuration service."""
        # Determine config directory based on platform (cross-platform)
        home_dir = Path.home()
        self._config_directory = home_dir / ".lightning-enable"
        self._config_file_path = self._config_directory / "config.json"
        self._config_file_exists = False
        self._configuration = self._load_configuration()

    @property
    def configuration(self) -> UserBudgetConfiguration:
        """Gets the user's budget configuration (read-only)."""
        return self._configuration

    @property
    def config_file_path(self) -> str:
        """Gets the path to the configuration file."""
        return str(self._config_file_path)

    @property
    def config_file_exists(self) -> bool:
        """Whether the configuration was loaded from an existing file."""
        return self._config_file_exists

    def reload(self) -> None:
        """Reloads configuration from disk."""
        self._configuration = self._load_configuration()

    def _load_configuration(self) -> UserBudgetConfiguration:
        """Load configuration from file or create default."""
        try:
            if self._config_file_path.exists():
                self._config_file_exists = True
                with open(self._config_file_path, "r", encoding="utf-8") as f:
                    data = json.load(f)

                config = UserBudgetConfiguration.from_dict(data)
                self._validate_configuration(config)
                self._log_config_loaded(config)
                return config
            else:
                self._config_file_exists = False
                self._create_default_config_file()
        except Exception as e:
            print(
                f"[Lightning Enable] Warning: Could not load config from {self._config_file_path}: {e}",
                file=sys.stderr,
            )
            print("[Lightning Enable] Using default configuration.", file=sys.stderr)

        return self._create_default_configuration()

    def _create_default_config_file(self) -> None:
        """Create default configuration file with helpful comments."""
        try:
            # Create directory if it doesn't exist
            self._config_directory.mkdir(parents=True, exist_ok=True)

            default_config = self._create_default_configuration()
            json_data = json.dumps(default_config.to_dict(), indent=2)

            with open(self._config_file_path, "w", encoding="utf-8") as f:
                f.write(json_data)

            # Print first-run setup message
            print("", file=sys.stderr)
            print("=" * 70, file=sys.stderr)
            print("          Lightning Enable MCP - First Run Setup", file=sys.stderr)
            print("=" * 70, file=sys.stderr)
            print("  A default configuration file has been created at:", file=sys.stderr)
            print(f"  {self._config_file_path}", file=sys.stderr)
            print("", file=sys.stderr)
            print("  Default spending limits (in USD):", file=sys.stderr)
            print("    - Auto-approve:      <= $1.00", file=sys.stderr)
            print("    - Log & approve:     $1.00 - $5.00", file=sys.stderr)
            print("    - Require confirm:   $5.00 - $25.00", file=sys.stderr)
            print("    - Browser confirm:   $25.00 - $100.00", file=sys.stderr)
            print("    - Max per payment:   $500.00", file=sys.stderr)
            print("    - Max per session:   $100.00", file=sys.stderr)
            print("", file=sys.stderr)
            print("  Edit the config file to customize these limits.", file=sys.stderr)
            print("  AI agents CANNOT modify this file.", file=sys.stderr)
            print("=" * 70, file=sys.stderr)
            print("", file=sys.stderr)

        except Exception as e:
            print(
                f"[Lightning Enable] Could not create default config: {e}",
                file=sys.stderr,
            )

    @staticmethod
    def _create_default_configuration() -> UserBudgetConfiguration:
        """Create default configuration with sensible defaults."""
        return UserBudgetConfiguration(
            currency="USD",
            tiers=TierThresholds(
                auto_approve=Decimal("1.00"),
                log_and_approve=Decimal("5.00"),
                form_confirm=Decimal("25.00"),
                url_confirm=Decimal("100.00"),
            ),
            limits=PaymentLimits(
                max_per_payment=Decimal("500.00"),
                max_per_session=Decimal("100.00"),
            ),
            session=SessionSettings(
                require_approval_for_first_payment=False,
                cooldown_seconds=2,
            ),
            wallets=WalletSettings(),
        )

    def _validate_configuration(self, config: UserBudgetConfiguration) -> None:
        """
        Validate configuration values and log warnings for invalid values.

        Note: Since config is frozen, we cannot modify it here. Instead, we log
        warnings and the calling code should fall back to defaults if needed.
        """
        # Check tiers are in ascending order
        if config.tiers.auto_approve > config.tiers.log_and_approve:
            print(
                "[Lightning Enable] Warning: autoApprove should be <= logAndApprove.",
                file=sys.stderr,
            )

        if config.tiers.log_and_approve > config.tiers.form_confirm:
            print(
                "[Lightning Enable] Warning: logAndApprove should be <= formConfirm.",
                file=sys.stderr,
            )

        if config.tiers.form_confirm > config.tiers.url_confirm:
            print(
                "[Lightning Enable] Warning: formConfirm should be <= urlConfirm.",
                file=sys.stderr,
            )

        # Check limits are positive
        if config.limits.max_per_payment is not None and config.limits.max_per_payment <= 0:
            print(
                "[Lightning Enable] Warning: maxPerPayment must be positive.",
                file=sys.stderr,
            )

        if config.limits.max_per_session is not None and config.limits.max_per_session <= 0:
            print(
                "[Lightning Enable] Warning: maxPerSession must be positive.",
                file=sys.stderr,
            )

        # Check cooldown is reasonable
        if config.session.cooldown_seconds < 0 or config.session.cooldown_seconds > 60:
            print(
                "[Lightning Enable] Warning: cooldownSeconds should be 0-60.",
                file=sys.stderr,
            )

    def _log_config_loaded(self, config: UserBudgetConfiguration) -> None:
        """Log configuration loaded message."""
        print(
            f"[Lightning Enable] Loaded budget config from {self._config_file_path}",
            file=sys.stderr,
        )
        print(
            f"[Lightning Enable] Tiers: auto<=${config.tiers.auto_approve}, "
            f"log<=${config.tiers.log_and_approve}, "
            f"form<=${config.tiers.form_confirm}, "
            f"url<=${config.tiers.url_confirm}",
            file=sys.stderr,
        )

        max_payment = (
            f"${config.limits.max_per_payment}"
            if config.limits.max_per_payment is not None
            else "unlimited"
        )
        max_session = (
            f"${config.limits.max_per_session}"
            if config.limits.max_per_session is not None
            else "unlimited"
        )
        print(
            f"[Lightning Enable] Limits: max/payment={max_payment}, max/session={max_session}",
            file=sys.stderr,
        )


# Singleton instance for easy access
_config_service: Optional[ConfigurationService] = None


def get_config_service() -> ConfigurationService:
    """
    Get the singleton ConfigurationService instance.

    Returns:
        The global ConfigurationService instance.
    """
    global _config_service
    if _config_service is None:
        _config_service = ConfigurationService()
    return _config_service


def get_configuration() -> UserBudgetConfiguration:
    """
    Get the current user budget configuration.

    This is a convenience function that returns the configuration
    from the singleton ConfigurationService.

    Returns:
        The current UserBudgetConfiguration (read-only).
    """
    return get_config_service().configuration
