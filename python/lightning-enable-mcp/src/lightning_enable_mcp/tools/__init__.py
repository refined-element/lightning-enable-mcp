"""
Lightning Enable MCP Tools

Tool implementations for L402 operations.
"""

import re

_CREDENTIAL_PATTERNS = [
    re.compile(r"Bearer\s+\S+", re.IGNORECASE),
    re.compile(r"shpat_\S+", re.IGNORECASE),
    re.compile(r"sk_live_\S+", re.IGNORECASE),
    re.compile(r"sk_test_\S+", re.IGNORECASE),
    # Lightning Enable merchant API keys — create_lightning_enable_account can echo a
    # server error body containing one. Mirrors .NET's CreateAccountTool.Scrub().
    re.compile(r"le_(?:live|test)_\S+", re.IGNORECASE),
]

_MAX_ERROR_LEN = 200


def sanitize_error(msg: str, max_len: int = _MAX_ERROR_LEN) -> str:
    """Redact credential-shaped tokens and cap length for model-visible error strings.

    The length cap matters as much as the patterns: an upstream error can embed a full
    untruncated response body, so an unrecognized credential shape could otherwise ride
    along in the tail. Mirrors .NET's CreateAccountTool.Scrub().
    """
    if not msg:
        return ""

    for pattern in _CREDENTIAL_PATTERNS:
        msg = pattern.sub("[REDACTED]", msg)

    if len(msg) > max_len:
        msg = msg[:max_len] + "..."

    return msg

from .access_resource import access_l402_resource
from .create_account import create_lightning_enable_account
from .check_invoice_status import check_invoice_status
from .verify_confirmation_code import verify_confirmation_code
from .create_invoice import create_invoice
from .create_l402_challenge import create_l402_challenge
from .discover_api import discover_api
from .exchange_currency import exchange_currency
from .get_balance import get_balance
from .get_btc_price import get_btc_price
from .pay_challenge import pay_l402_challenge
from .pay_invoice import pay_invoice
from .send_onchain import send_onchain
from .verify_l402_payment import verify_l402_payment
from .budget import configure_budget, get_payment_history
from .budget_status import get_budget_status
from .discover_agent_services import discover_agent_services
from .publish_agent_capability import publish_agent_capability
from .request_agent_service import request_agent_service
from .publish_agent_attestation import publish_agent_attestation
from .get_agent_reputation import get_agent_reputation
from .settle_agent_service import settle_agent_service

__all__ = [
    "access_l402_resource",
    "create_lightning_enable_account",
    "check_invoice_status",
    "verify_confirmation_code",
    "create_invoice",
    "create_l402_challenge",
    "discover_api",
    "exchange_currency",
    "get_balance",
    "get_btc_price",
    "pay_l402_challenge",
    "pay_invoice",
    "send_onchain",
    "verify_l402_payment",
    "configure_budget",
    "get_payment_history",
    "get_budget_status",
    "discover_agent_services",
    "publish_agent_capability",
    "request_agent_service",
    "publish_agent_attestation",
    "get_agent_reputation",
    "settle_agent_service",
]
