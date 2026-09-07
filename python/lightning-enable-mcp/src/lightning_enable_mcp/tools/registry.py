"""Ordered MCP tool schemas, and which of them each profile advertises.

Order matters: it is the order clients see in ``list_tools``.

``STANDARD_TOOLS`` is the consolidated verb set. ``LEGACY_TOOLS`` are the
pre-consolidation names, folded into the ``budget`` / ``receipts`` / ``wallet_ops``
/ ``l402_producer`` / ``agent_services`` verbs; they stay callable in every profile
as deprecated aliases and are only *advertised* under the ``full`` profile.
"""

from mcp.types import Tool

from .access_resource import ACCESS_L402_RESOURCE_TOOL
from .annotations import TOOL_ANNOTATIONS
from .budget import CONFIGURE_BUDGET_TOOL, GET_PAYMENT_HISTORY_TOOL
from .budget_status import GET_BUDGET_STATUS_TOOL
from .check_invoice_status import CHECK_INVOICE_STATUS_TOOL
from .consolidated import (
    AGENT_SERVICES_TOOL,
    BUDGET_TOOL,
    L402_PRODUCER_TOOL,
    RECEIPTS_TOOL,
    WALLET_OPS_TOOL,
)
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
from .profiles import FULL, LITE, LITE_TOOL_NAMES, resolve_profile
from .publish_agent_attestation import PUBLISH_AGENT_ATTESTATION_TOOL
from .publish_agent_capability import PUBLISH_AGENT_CAPABILITY_TOOL
from .request_agent_service import REQUEST_AGENT_SERVICE_TOOL
from .send_onchain import SEND_ONCHAIN_TOOL
from .settle_agent_service import SETTLE_AGENT_SERVICE_TOOL
from .test_l402_payment import TEST_L402_PAYMENT_TOOL
from .unpublish_agent_capability import UNPUBLISH_AGENT_CAPABILITY_TOOL
from .verify_confirmation_code import VERIFY_CONFIRMATION_CODE_TOOL
from .verify_l402_payment import VERIFY_L402_PAYMENT_TOOL

#: The consolidated verb set — advertised by the default ``standard`` profile.
STANDARD_TOOLS: list[Tool] = [
    ACCESS_L402_RESOURCE_TOOL,
    PAY_INVOICE_TOOL,
    PAY_L402_CHALLENGE_TOOL,
    TEST_L402_PAYMENT_TOOL,
    GET_BALANCE_TOOL,
    BUDGET_TOOL,
    RECEIPTS_TOOL,
    CREATE_INVOICE_TOOL,
    CHECK_INVOICE_STATUS_TOOL,
    VERIFY_CONFIRMATION_CODE_TOOL,
    DISCOVER_API_TOOL,
    CREATE_LIGHTNING_ENABLE_ACCOUNT_TOOL,
    WALLET_OPS_TOOL,
    L402_PRODUCER_TOOL,
    AGENT_SERVICES_TOOL,
]

#: Pre-consolidation names, advertised only by the ``full`` profile. Still callable
#: (with a ``deprecated`` marker on the result) under every profile.
LEGACY_TOOLS: list[Tool] = [
    GET_BUDGET_STATUS_TOOL,
    CONFIGURE_BUDGET_TOOL,
    GET_RECEIPTS_TOOL,
    GET_PAYMENT_HISTORY_TOOL,
    GET_BTC_PRICE_TOOL,
    EXCHANGE_CURRENCY_TOOL,
    SEND_ONCHAIN_TOOL,
    CREATE_L402_CHALLENGE_TOOL,
    VERIFY_L402_PAYMENT_TOOL,
    DISCOVER_AGENT_SERVICES_TOOL,
    PUBLISH_AGENT_CAPABILITY_TOOL,
    UNPUBLISH_AGENT_CAPABILITY_TOOL,
    REQUEST_AGENT_SERVICE_TOOL,
    PUBLISH_AGENT_ATTESTATION_TOOL,
    GET_AGENT_REPUTATION_TOOL,
    SETTLE_AGENT_SERVICE_TOOL,
]

#: The minimal spend-and-stay-in-budget surface, in ``STANDARD_TOOLS`` order.
LITE_TOOLS: list[Tool] = [t for t in STANDARD_TOOLS if t.name in set(LITE_TOOL_NAMES)]

# Stamp the annotation table onto every advertised tool. Done here, once, so the
# read-only / money-moving classification lives in one reviewable table
# (``annotations.py``) rather than being repeated beside each schema. A tool with no
# entry is a bug — MCP directory review requires a title and an explicit
# readOnlyHint on every listed tool — so fail loudly at import instead of shipping
# an unannotated tool.
for _tool in (*STANDARD_TOOLS, *LEGACY_TOOLS):
    try:
        _tool.annotations = TOOL_ANNOTATIONS[_tool.name]
    except KeyError:  # pragma: no cover - guarded by test_tool_profiles.py
        raise RuntimeError(
            f"Tool '{_tool.name}' has no entry in TOOL_ANNOTATIONS "
            "(tools/annotations.py). Every advertised tool needs a title and an "
            "explicit readOnlyHint."
        ) from None
del _tool


def tools_for_profile(profile: str) -> list[Tool]:
    """Tools ``list_tools`` advertises under ``profile`` (see ``profiles.py``)."""
    resolved = resolve_profile(profile)
    if resolved == LITE:
        return list(LITE_TOOLS)
    if resolved == FULL:
        return [*STANDARD_TOOLS, *LEGACY_TOOLS]
    return list(STANDARD_TOOLS)
