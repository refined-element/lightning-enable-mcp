"""Ordered MCP tool schemas. Order matters: it is the order clients see in list_tools."""

from mcp.types import Tool

from .access_resource import ACCESS_L402_RESOURCE_TOOL
from .budget import CONFIGURE_BUDGET_TOOL, GET_PAYMENT_HISTORY_TOOL
from .budget_status import GET_BUDGET_STATUS_TOOL
from .check_invoice_status import CHECK_INVOICE_STATUS_TOOL
from .create_account import CREATE_LIGHTNING_ENABLE_ACCOUNT_TOOL
from .create_invoice import CREATE_INVOICE_TOOL
from .create_l402_challenge import CREATE_L402_CHALLENGE_TOOL
from .discover_agent_services import DISCOVER_AGENT_SERVICES_TOOL
from .discover_api import DISCOVER_API_TOOL
from .exchange_currency import EXCHANGE_CURRENCY_TOOL
from .get_agent_reputation import GET_AGENT_REPUTATION_TOOL
from .get_balance import GET_BALANCE_TOOL
from .get_btc_price import GET_BTC_PRICE_TOOL
from .get_receipts import GET_RECEIPTS_TOOL
from .pay_challenge import PAY_L402_CHALLENGE_TOOL
from .pay_invoice import PAY_INVOICE_TOOL
from .publish_agent_attestation import PUBLISH_AGENT_ATTESTATION_TOOL
from .publish_agent_capability import PUBLISH_AGENT_CAPABILITY_TOOL
from .request_agent_service import REQUEST_AGENT_SERVICE_TOOL
from .send_onchain import SEND_ONCHAIN_TOOL
from .settle_agent_service import SETTLE_AGENT_SERVICE_TOOL
from .test_l402_payment import TEST_L402_PAYMENT_TOOL
from .unpublish_agent_capability import UNPUBLISH_AGENT_CAPABILITY_TOOL
from .verify_confirmation_code import VERIFY_CONFIRMATION_CODE_TOOL
from .verify_l402_payment import VERIFY_L402_PAYMENT_TOOL

ALL_TOOLS: list[Tool] = [
    ACCESS_L402_RESOURCE_TOOL,
    TEST_L402_PAYMENT_TOOL,
    PAY_L402_CHALLENGE_TOOL,
    CREATE_LIGHTNING_ENABLE_ACCOUNT_TOOL,
    GET_BALANCE_TOOL,
    GET_PAYMENT_HISTORY_TOOL,
    GET_RECEIPTS_TOOL,
    CONFIGURE_BUDGET_TOOL,
    PAY_INVOICE_TOOL,
    CREATE_INVOICE_TOOL,
    CHECK_INVOICE_STATUS_TOOL,
    GET_BTC_PRICE_TOOL,
    EXCHANGE_CURRENCY_TOOL,
    SEND_ONCHAIN_TOOL,
    GET_BUDGET_STATUS_TOOL,
    CREATE_L402_CHALLENGE_TOOL,
    VERIFY_L402_PAYMENT_TOOL,
    VERIFY_CONFIRMATION_CODE_TOOL,
    DISCOVER_API_TOOL,
    DISCOVER_AGENT_SERVICES_TOOL,
    PUBLISH_AGENT_CAPABILITY_TOOL,
    UNPUBLISH_AGENT_CAPABILITY_TOOL,
    REQUEST_AGENT_SERVICE_TOOL,
    PUBLISH_AGENT_ATTESTATION_TOOL,
    GET_AGENT_REPUTATION_TOOL,
    SETTLE_AGENT_SERVICE_TOOL,
]
