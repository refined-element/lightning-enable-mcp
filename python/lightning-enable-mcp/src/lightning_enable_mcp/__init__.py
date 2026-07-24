"""
Lightning Enable MCP Server

An MCP server for L402 Lightning payments that enables AI agents
to access paid APIs with automatic payment handling.

Available tools:
- pay_invoice - Pay any Lightning invoice
- get_balance - Check wallet balance (sats, multi-currency, wallet info)
- get_payment_history - View payment history
- get_budget_status - View current budget limits
- access_l402_resource - Auto-pay L402 challenges
- pay_l402_challenge - Manual L402 payment
"""

from importlib.metadata import PackageNotFoundError as _PackageNotFoundError
from importlib.metadata import version as _pkg_version

from .budget_service import (
    BudgetService,
    ConfigureBudgetResult,
    create_budget_service,
    get_budget_service,
)
from .payment_history_service import (
    PaymentHistoryService,
    PaymentRecord,
    create_payment_history_service,
    get_payment_history_service,
)
from .config import (
    ApprovalLevel,
    ApprovalCheckResult,
    ConfigurationService,
    PaymentLimits,
    SessionSettings,
    TierThresholds,
    UserBudgetConfiguration,
    WalletSettings,
    get_config_service,
    get_configuration,
)
from .l402_client import L402Client, L402Error, L402Challenge, L402Token
from .nwc_wallet import NWCWallet, NWCError, NWCConfig
from .price_service import (
    PriceService,
    PriceSnapshot,
    PriceUnavailableError,
    get_price_service,
    get_btc_price,
    sats_to_usd,
    usd_to_sats,
)
# Derive __version__ from installed package metadata so it can never drift from
# pyproject.toml (which is the single source of truth). Fall back to a sentinel when
# running directly from an uninstalled source checkout (e.g., without pip install).
# Defined BEFORE the .server import because server.py reads __version__ at import time
# (to report it in the MCP serverInfo handshake) — reordering avoids a circular import.
try:
    __version__ = _pkg_version("lightning-enable-mcp")
except _PackageNotFoundError:  # pragma: no cover
    __version__ = "0.0.0+unknown"

from .server import LightningEnableServer, main

__all__ = [
    # Server
    "LightningEnableServer",
    "main",
    # L402 Client
    "L402Client",
    "L402Error",
    "L402Challenge",
    "L402Token",
    # NWC Wallet
    "NWCWallet",
    "NWCError",
    "NWCConfig",
    # Budget Service (single source of truth, matching .NET implementation)
    "BudgetService",
    "ConfigureBudgetResult",
    "create_budget_service",
    "get_budget_service",
    # Payment History Service (separate audit trail, matching .NET)
    "PaymentHistoryService",
    "PaymentRecord",
    "create_payment_history_service",
    "get_payment_history_service",
    # Configuration
    "ApprovalLevel",
    "ApprovalCheckResult",
    "ConfigurationService",
    "PaymentLimits",
    "SessionSettings",
    "TierThresholds",
    "UserBudgetConfiguration",
    "WalletSettings",
    "get_config_service",
    "get_configuration",
    # Price Service
    "PriceService",
    "PriceSnapshot",
    "PriceUnavailableError",
    "get_price_service",
    "get_btc_price",
    "sats_to_usd",
    "usd_to_sats",
    # Version
    "__version__",
]
