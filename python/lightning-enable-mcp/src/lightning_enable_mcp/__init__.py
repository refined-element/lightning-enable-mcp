"""
Lightning Enable MCP Server

An MCP server for L402 Lightning payments that enables AI agents
to access paid APIs with automatic payment handling.

Available tools:
- pay_invoice - Pay any Lightning invoice
- check_wallet_balance - Check wallet balance
- get_payment_history - View payment history
- get_budget_status - View current budget limits
- access_l402_resource - Auto-pay L402 challenges
- pay_l402_challenge - Manual L402 payment
"""

__version__ = "1.6.0"

from .budget import BudgetManager, BudgetExceededError, PaymentRecord
from .budget_service import (
    BudgetService,
    create_budget_service,
    get_budget_service,
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
    PriceServiceError,
    PriceResult,
    get_price_service,
    get_btc_price,
    sats_to_usd,
    usd_to_sats,
)
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
    # Budget (legacy)
    "BudgetManager",
    "BudgetExceededError",
    "PaymentRecord",
    # Budget Service (new, matching .NET implementation)
    "BudgetService",
    "create_budget_service",
    "get_budget_service",
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
    "PriceServiceError",
    "PriceResult",
    "get_price_service",
    "get_btc_price",
    "sats_to_usd",
    "usd_to_sats",
    # Version
    "__version__",
]
