"""
Lightning Enable MCP Server

Main server module providing L402 payment capabilities to AI agents via MCP.
"""

import asyncio
import json
import logging
import os
import sys
from collections.abc import Mapping
from types import MappingProxyType
from typing import Any, NamedTuple

from mcp.server import Server
from mcp.server.lowlevel.helper_types import ReadResourceContents
from mcp.server.stdio import stdio_server
from mcp.types import (
    Resource,
    ResourceTemplate,
    TextContent,
    Tool,
)
from pydantic import AnyUrl

from . import __version__
from .budget_service import BudgetService, get_budget_service
from .confirmation_channel import CHANNEL_ENV_VAR, VALID_CHANNELS
from .idempotent_wallet import IdempotentWallet
from .l402_client import L402Client
from .lightning_enable_api import LightningEnableApiClient
from .lnd_wallet import LndWallet
from .nwc_wallet import NWCConfig, NWCWallet
from .opennode_wallet import OpenNodeWallet
from .operation_ledger import OperationLedger
from .payment_history_service import (
    PaymentHistoryService,
    get_payment_history_service,
)
from .receipt_seam import ReceiptRecordingWallet
from .receipt_service import ReceiptService, wallet_label_from
from .strike_wallet import StrikeWallet
from .tools.access_resource import access_l402_resource
from .tools.budget import configure_budget, get_payment_history
from .tools.budget_status import get_budget_status
from .tools.check_invoice_status import check_invoice_status
from .tools.consolidated import ACTION_TOOLS
from .tools.create_account import create_lightning_enable_account
from .tools.create_invoice import create_invoice
from .tools.create_l402_challenge import create_l402_challenge
from .tools.discover_agent_services import discover_agent_services
from .tools.discover_api import discover_api
from .tools.exchange_currency import exchange_currency
from .tools.get_agent_reputation import get_agent_reputation
from .tools.get_balance import get_balance
from .tools.get_btc_price import get_btc_price
from .tools.get_receipts import get_receipts
from .tools.pay_challenge import pay_l402_challenge
from .tools.pay_invoice import pay_invoice
from .tools.producer_setup import (
    add_endpoint,
    configure_receive,
    create_proxy,
    producer_status,
)
from .tools.producer_setup import list_challenges as list_l402_challenges
from .tools.producer_setup import publish as publish_l402_service
from .tools.profiles import PROFILE_ENV_VAR, resolve_profile
from .tools.publish_agent_attestation import publish_agent_attestation
from .tools.publish_agent_capability import publish_agent_capability
from .tools.registry import tools_for_profile
from .tools.request_agent_service import request_agent_service
from .tools.send_onchain import send_onchain
from .tools.settle_agent_service import settle_agent_service
from .tools.setup_wallet import setup_wallet
from .tools.test_l402_payment import test_l402_payment
from .tools.unpublish_agent_capability import unpublish_agent_capability
from .tools.verify_confirmation_code import verify_confirmation_code
from .tools.verify_l402_payment import verify_l402_payment

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s - %(name)s - %(levelname)s - %(message)s",
)
logger = logging.getLogger("lightning-enable-mcp")


import re

_CREDENTIAL_PATTERNS = [
    re.compile(r"Bearer\s+\S+", re.IGNORECASE),
    re.compile(r"shpat_\S+", re.IGNORECASE),
    re.compile(r"sk_live_\S+", re.IGNORECASE),
    re.compile(r"sk_test_\S+", re.IGNORECASE),
    re.compile(r"[A-Za-z0-9+/]{40,}={0,2}"),  # Long base64-like tokens
]


def _sanitize_error(msg: str) -> str:
    """Remove potential credentials from error messages."""
    for pattern in _CREDENTIAL_PATTERNS:
        msg = pattern.sub("[REDACTED]", msg)
    return msg


# Renamed/merged tools keep their old names as accepted-but-unadvertised forwarding
# aliases. An alias dispatches to the new implementation — injecting the action that
# selects the old tool's behaviour — and the forwarded JSON gains a `deprecated`
# marker. Aliases are ABSENT from list_tools() under the default profile, so the
# advertised surface is the new names; `LIGHTNING_ENABLE_TOOL_PROFILE=full`
# re-advertises the consolidation's legacy names for prompts written against them.
class AliasTarget(NamedTuple):
    """Where a deprecated tool name forwards to."""

    tool: str
    """Name of the tool that supersedes the alias."""

    args: Mapping[str, Any] = MappingProxyType({})
    """Arguments injected into the call (the action selecting the old behaviour)."""

    @property
    def use(self) -> str:
        """Human-readable call form, e.g. ``budget(action="status")``."""
        if not self.args:
            return self.tool
        inner = ", ".join(f'{k}="{v}"' for k, v in self.args.items())
        return f"{self.tool}({inner})"


DEPRECATED_ALIASES: dict[str, AliasTarget] = {
    # Pre-consolidation renames (v1).
    "confirm_payment": AliasTarget("verify_confirmation_code"),
    "check_wallet_balance": AliasTarget("get_balance"),
    "get_all_balances": AliasTarget("get_balance"),
    # Tool-surface consolidation.
    "get_budget_status": AliasTarget("budget", {"action": "status"}),
    "configure_budget": AliasTarget("budget", {"action": "tighten"}),
    "get_receipts": AliasTarget("receipts", {"source": "durable"}),
    "get_payment_history": AliasTarget("receipts", {"source": "session"}),
    "get_btc_price": AliasTarget("wallet_ops", {"action": "price"}),
    "exchange_currency": AliasTarget("wallet_ops", {"action": "exchange"}),
    "send_onchain": AliasTarget("wallet_ops", {"action": "send_onchain"}),
    "create_l402_challenge": AliasTarget("l402_producer", {"action": "create"}),
    "verify_l402_payment": AliasTarget("l402_producer", {"action": "verify"}),
    "discover_agent_services": AliasTarget("agent_services", {"action": "discover"}),
    "request_agent_service": AliasTarget("agent_services", {"action": "request"}),
    "settle_agent_service": AliasTarget("agent_services", {"action": "settle"}),
    "publish_agent_capability": AliasTarget("agent_services", {"action": "publish"}),
    "unpublish_agent_capability": AliasTarget("agent_services", {"action": "unpublish"}),
    "publish_agent_attestation": AliasTarget("agent_services", {"action": "attest"}),
    "get_agent_reputation": AliasTarget("agent_services", {"action": "reputation"}),
}
_ALIAS_REMOVAL = "v3.0.0"


def _mark_deprecated(result: str, target: AliasTarget) -> str:
    """Annotate a forwarded tool result (JSON string) with a deprecation marker.

    Parses the underlying tool's JSON response and injects
    ``deprecated: {replaced_by, use, removal}``. If the payload is not a JSON object
    (unexpected), the original string is returned unchanged so an alias never breaks
    a caller that the new tool would have served.
    """
    try:
        data = json.loads(result)
    except (ValueError, TypeError):
        return result
    if not isinstance(data, dict):
        return result
    data["deprecated"] = {
        "replaced_by": target.tool,
        "use": target.use,
        "removal": _ALIAS_REMOVAL,
    }
    return json.dumps(data, indent=2)


def _unknown_action(tool: str, key: str, value: Any) -> str:
    """Descriptive error for a missing/unrecognized action on a consolidated tool."""
    _, allowed = ACTION_TOOLS[tool]
    shown = "missing" if value is None else repr(value)
    return json.dumps(
        {
            "success": False,
            "error": (
                f"{tool}: {key} is {shown}. Set {key} to one of: "
                f"{', '.join(allowed)}."
            ),
            "tool": tool,
            "valid_actions": list(allowed),
        },
        indent=2,
    )


#: The whole-log resource URI.
RECEIPTS_RESOURCE_URI = "lightning-enable://receipts"

#: The per-payment-hash resource template.
RECEIPT_BY_HASH_URI_TEMPLATE = "lightning-enable://receipts/{paymentHash}"

#: How many receipts the log resource carries. Matches the ``receipts`` tool's clamp.
RECEIPTS_RESOURCE_MAX_ROWS = 200

#: How far back a single-hash lookup searches. Deeper than the log resource because looking
#: up one known payment is a different question from "what happened lately".
RECEIPT_LOOKUP_WINDOW = 2_000

#: JSONL: newline-delimited JSON, one receipt per line.
JSON_LINES_MIME_TYPE = "application/x-ndjson"


def _to_json_lines(receipts: list[dict]) -> str:
    """Render receipts as JSONL — compact, one per line. It is only JSONL if none wrap."""
    return "".join(
        json.dumps(receipt, separators=(",", ":")) + "\n" for receipt in receipts
    )


def _action_of(tool: str, arguments: Mapping[str, Any]) -> str | None:
    """Return the validated action for a consolidated tool, or None if invalid."""
    key, allowed = ACTION_TOOLS[tool]
    value = arguments.get(key)
    return value if value in allowed else None


class LightningEnableServer:
    """MCP Server for L402 Lightning payments."""

    def __init__(self) -> None:
        self.server = Server("lightning-enable", version=__version__)
        # How much of the tool surface list_tools advertises. Read once, at startup:
        # a client caches the tool list for the session anyway. Profiles are
        # listing-only — every tool stays callable by name in every profile.
        self.tool_profile: str = resolve_profile(os.getenv(PROFILE_ENV_VAR))
        self.wallet: LndWallet | NWCWallet | OpenNodeWallet | StrikeWallet | None = None
        self.strike_wallet: StrikeWallet | None = None  # For Strike-specific features
        # The wallet routed through the receipt seam (ReceiptRecordingWallet): every
        # payment tool pays through THIS so each payment leaves one durable receipt.
        # Non-payment tools (balance, invoicing, price) keep the raw self.wallet.
        self.paying_wallet: ReceiptRecordingWallet | None = None
        self.l402_client: L402Client | None = None
        self.budget_service: BudgetService | None = None  # Single source of truth for limits
        self.payment_history_service: PaymentHistoryService | None = None  # Session audit trail
        # Always available so get_receipts can read the durable log even without a
        # wallet configured (the "pull the plug" audit moment). The wallet label is
        # upgraded from "unknown" once a wallet is initialized (used only on write).
        self.receipt_service: ReceiptService | None = ReceiptService(wallet_label="unknown")
        self._nwc_config: NWCConfig | None = None  # Store NWC config for pubkey access
        self.api_client: LightningEnableApiClient | None = None  # For L402 producer tools

        self._setup_handlers()

    def _setup_handlers(self) -> None:
        """Register MCP tool and resource handlers."""

        @self.server.list_tools()
        async def list_tools() -> list[Tool]:
            """Return the tools advertised under the active profile."""
            return tools_for_profile(self.tool_profile)

        # ── Resources ───────────────────────────────────────────────────────
        #
        # A tool call is the agent deciding to look; a resource is something a client can
        # attach, watch, or show a human without the model spending a turn on it. The
        # durable spend log is exactly that kind of artifact — which is why it lives off
        # the agent's hot path in the first place — so it is worth both shapes.
        #
        # Resources are NOT affected by the tool profile: they cost no schema bytes in the
        # model's context, so there is nothing to trim. Keep in lockstep with the .NET port
        # (Resources/ReceiptResources.cs).

        @self.server.list_resources()
        async def list_resources() -> list[Resource]:
            return [
                Resource(
                    uri=AnyUrl(RECEIPTS_RESOURCE_URI),
                    name="receipts",
                    title="Payment receipts",
                    mimeType=JSON_LINES_MIME_TYPE,
                    description=(
                        "The durable, append-only payment receipt log "
                        "(~/.lightning-enable/receipts.jsonl) — the most recent "
                        f"{RECEIPTS_RESOURCE_MAX_ROWS} receipts as JSONL, oldest first. "
                        "Never contains preimages."
                    ),
                )
            ]

        @self.server.list_resource_templates()
        async def list_resource_templates() -> list[ResourceTemplate]:
            return [
                ResourceTemplate(
                    uriTemplate=RECEIPT_BY_HASH_URI_TEMPLATE,
                    name="receipt",
                    title="Payment receipt by payment hash",
                    mimeType=JSON_LINES_MIME_TYPE,
                    description=(
                        "Every durable receipt recorded for one payment hash, as JSONL. "
                        "Never contains preimages — the payment hash is the safe reference, "
                        "the preimage is the proof of payment and is not a receipt field."
                    ),
                )
            ]

        @self.server.read_resource()
        async def read_resource(uri: AnyUrl) -> list[ReadResourceContents]:
            # A list of ReadResourceContents, not a bare str: the str overload is
            # deprecated in the SDK and cannot carry the JSONL media type.
            return [
                ReadResourceContents(
                    content=self._read_receipts_resource(str(uri)),
                    mime_type=JSON_LINES_MIME_TYPE,
                )
            ]

        @self.server.call_tool()
        async def call_tool(name: str, arguments: dict[str, Any]) -> list[TextContent]:
            """Handle tool invocations."""
            requested = name
            try:
                # Ensure services are initialized
                if self.wallet is None or self.l402_client is None:
                    await self._initialize_services()

                # Resolve a deprecated alias to its replacement FIRST, injecting the
                # action that selects the old tool's behaviour, so everything below
                # (wallet guard included) reasons about one canonical name.
                alias = DEPRECATED_ALIASES.get(name)
                if alias is not None:
                    name = alias.tool
                    arguments = {**arguments, **alias.args}

                if self.wallet is None and self._requires_wallet(name, arguments):
                    return [
                        TextContent(
                            type="text",
                            text="Error: No wallet configured. Set one L402-capable wallet: "
                            "STRIKE_API_KEY, NWC_CONNECTION_STRING, or "
                            "LND_REST_HOST+LND_MACAROON_HEX. "
                            "(OPENNODE_API_KEY is receiving/invoicing only — it cannot pay "
                            "L402 challenges.) "
                            "Call setup_wallet for the guided path, then run "
                            "test_l402_payment to confirm the wallet works end to end.",
                        )
                    ]

                # Route to appropriate handler
                if name == "setup_wallet":
                    result = await setup_wallet(
                        nwc_connection_string=arguments.get("nwc_connection_string"),
                    )

                elif name == "access_l402_resource":
                    result = await access_l402_resource(
                        url=arguments["url"],
                        method=arguments.get("method", "GET"),
                        headers=arguments.get("headers", {}),
                        body=arguments.get("body"),
                        max_sats=arguments.get("max_sats", 1000),
                        confirmation_nonce=arguments.get("confirmation_nonce"),
                        l402_client=self.l402_client,
                        budget_service=self.budget_service,
                        payment_history_service=self.payment_history_service,
                    )

                elif name == "receipts":
                    source = _action_of("receipts", arguments)
                    if source == "durable":
                        result = await get_receipts(
                            limit=arguments.get("limit", 20),
                            receipt_service=self.receipt_service,
                        )
                    elif source == "session":
                        result = await get_payment_history(
                            limit=arguments.get("limit", 10),
                            since=arguments.get("since"),
                            payment_history_service=self.payment_history_service,
                        )
                    else:
                        result = _unknown_action(
                            "receipts", "source", arguments.get("source")
                        )

                elif name == "test_l402_payment":
                    result = await test_l402_payment(
                        confirmation_nonce=arguments.get("confirmation_nonce"),
                        l402_client=self.l402_client,
                        budget_service=self.budget_service,
                        payment_history_service=self.payment_history_service,
                    )

                elif name == "pay_l402_challenge":
                    result = await pay_l402_challenge(
                        invoice=arguments.get("invoice", ""),
                        macaroon=arguments.get("macaroon"),
                        max_sats=arguments.get("max_sats", 1000),
                        confirmation_nonce=arguments.get("confirmation_nonce"),
                        challenge_header=arguments.get("challenge_header"),
                        wallet=self.paying_wallet or self.wallet,
                        budget_service=self.budget_service,
                        payment_history_service=self.payment_history_service,
                    )

                elif name == "create_lightning_enable_account":
                    result = await create_lightning_enable_account(
                        email=arguments.get("email", ""),
                        max_sats=arguments.get("max_sats", 1000),
                        confirmation_nonce=arguments.get("confirmation_nonce"),
                        l402_client=self.l402_client,
                        budget_service=self.budget_service,
                        payment_history_service=self.payment_history_service,
                    )

                elif name == "get_balance":
                    result = await get_balance(
                        wallet=self.wallet,
                        strike_wallet=self.strike_wallet,
                        budget_service=self.budget_service,
                    )

                elif name == "budget":
                    action = _action_of("budget", arguments)
                    if action == "status":
                        result = await get_budget_status(
                            budget_service=self.budget_service,
                            payment_history_service=self.payment_history_service,
                        )
                    elif action == "tighten":
                        result = await configure_budget(
                            per_request=arguments.get("per_request", 1000),
                            per_session=arguments.get("per_session", 10000),
                            budget_service=self.budget_service,
                        )
                    else:
                        result = _unknown_action(
                            "budget", "action", arguments.get("action")
                        )

                elif name == "pay_invoice":
                    result = await pay_invoice(
                        invoice=arguments.get("invoice", ""),
                        max_sats=arguments.get("max_sats", 1000),
                        confirmation_nonce=arguments.get("confirmation_nonce"),
                        wallet=self.paying_wallet or self.wallet,
                        budget_service=self.budget_service,
                        payment_history_service=self.payment_history_service,
                    )

                elif name == "create_invoice":
                    result = await create_invoice(
                        amount_sats=arguments.get("amount_sats", 0),
                        memo=arguments.get("memo"),
                        expiry_secs=arguments.get("expiry_secs", 3600),
                        wallet=self.wallet,
                    )

                elif name == "check_invoice_status":
                    result = await check_invoice_status(
                        invoice_id=arguments.get("invoice_id", ""),
                        wallet=self.wallet,
                    )

                elif name == "wallet_ops":
                    action = _action_of("wallet_ops", arguments)
                    if action == "price":
                        result = await get_btc_price(
                            wallet=self.strike_wallet,
                        )
                    elif action == "exchange":
                        result = await exchange_currency(
                            source_currency=arguments.get("source_currency", ""),
                            target_currency=arguments.get("target_currency", ""),
                            amount=arguments.get("amount", 0),
                            wallet=self.strike_wallet,
                        )
                    elif action == "send_onchain":
                        # send_onchain supports Strike and LND wallets
                        onchain_wallet = self.strike_wallet
                        if onchain_wallet is None and isinstance(self.wallet, LndWallet):
                            onchain_wallet = self.wallet
                        # Route through the receipt seam so the (irreversible) send leaves
                        # a durable receipt. The tool unwraps for its isinstance checks.
                        if onchain_wallet is not None and self.receipt_service is not None:
                            onchain_wallet = ReceiptRecordingWallet(
                                onchain_wallet, self.receipt_service, self.budget_service
                            )
                        result = await send_onchain(
                            address=arguments.get("address", ""),
                            amount_sats=arguments.get("amount_sats", 0),
                            confirmation_nonce=arguments.get("confirmation_nonce"),
                            wallet=onchain_wallet,
                            budget_service=self.budget_service,
                        )
                    else:
                        result = _unknown_action(
                            "wallet_ops", "action", arguments.get("action")
                        )

                elif name == "l402_producer":
                    action = _action_of("l402_producer", arguments)
                    if action == "create":
                        result = await create_l402_challenge(
                            resource=arguments.get("resource", ""),
                            price_sats=arguments.get("price_sats", 0),
                            description=arguments.get("description"),
                            api_client=self.api_client,
                        )
                    elif action == "verify":
                        result = await verify_l402_payment(
                            macaroon=arguments.get("macaroon", ""),
                            preimage=arguments.get("preimage", ""),
                            api_client=self.api_client,
                        )
                    elif action == "configure_receive":
                        result = await configure_receive(
                            nwc_connection_string=arguments.get("nwc_connection_string"),
                            api_client=self.api_client,
                        )
                    elif action == "status":
                        result = await producer_status(
                            limit=arguments.get("limit", 5),
                            api_client=self.api_client,
                        )
                    elif action == "create_proxy":
                        result = await create_proxy(
                            name=arguments.get("name", ""),
                            target_base_url=arguments.get("target_base_url", ""),
                            description=arguments.get("description"),
                            default_price_sats=arguments.get("default_price_sats", 10),
                            api_client=self.api_client,
                        )
                    elif action == "add_endpoint":
                        result = await add_endpoint(
                            proxy_id=arguments.get("proxy_id", ""),
                            endpoint_id=arguments.get("endpoint_id", ""),
                            path=arguments.get("path", ""),
                            http_method=arguments.get("http_method", "GET"),
                            summary=arguments.get("summary"),
                            price_sats=arguments.get("price_sats", 0),
                            api_client=self.api_client,
                        )
                    elif action == "publish":
                        result = await publish_l402_service(
                            proxy_id=arguments.get("proxy_id", ""),
                            service_name=arguments.get("service_name"),
                            service_description=arguments.get("service_description"),
                            categories=arguments.get("categories"),
                            api_client=self.api_client,
                        )
                    elif action == "list_challenges":
                        result = await list_l402_challenges(
                            status=arguments.get("challenge_status"),
                            limit=arguments.get("limit", 20),
                            offset=arguments.get("offset", 0),
                            api_client=self.api_client,
                        )
                    else:
                        result = _unknown_action(
                            "l402_producer", "action", arguments.get("action")
                        )

                elif name == "verify_confirmation_code":
                    result = await verify_confirmation_code(
                        nonce=arguments.get("nonce", ""),
                        budget_service=self.budget_service,
                    )

                elif name == "discover_api":
                    result = await discover_api(
                        url=arguments.get("url"),
                        query=arguments.get("query"),
                        category=arguments.get("category"),
                        budget_aware=arguments.get("budget_aware", True),
                        budget_service=self.budget_service,
                    )

                elif name == "agent_services":
                    action = _action_of("agent_services", arguments)
                    if action == "discover":
                        result = await discover_agent_services(
                            category=arguments.get("category"),
                            hashtags=arguments.get("hashtags"),
                            query=arguments.get("query"),
                            limit=arguments.get("limit", 20),
                            api_client=self.api_client,
                            budget_service=self.budget_service,
                        )
                    elif action == "publish":
                        result = await publish_agent_capability(
                            service_id=arguments.get("service_id", ""),
                            categories=arguments.get("categories", []),
                            content=arguments.get("content", ""),
                            price_sats=arguments.get("price_sats", 0),
                            l402_endpoint=arguments.get("l402_endpoint"),
                            target_url=arguments.get("target_url"),
                            hashtags=arguments.get("hashtags"),
                            api_client=self.api_client,
                        )
                    elif action == "unpublish":
                        result = await unpublish_agent_capability(
                            service_id=arguments.get("service_id", ""),
                            reason=arguments.get("reason"),
                            api_client=self.api_client,
                        )
                    elif action == "request":
                        result = await request_agent_service(
                            capability_event_id=arguments.get("capability_event_id", ""),
                            budget_sats=arguments.get("budget_sats", 0),
                            parameters=arguments.get("parameters"),
                            api_client=self.api_client,
                            budget_service=self.budget_service,
                        )
                    elif action == "attest":
                        result = await publish_agent_attestation(
                            subject_pubkey=arguments.get("subject_pubkey", ""),
                            agreement_id=arguments.get("agreement_id", ""),
                            rating=arguments.get("rating", 0),
                            content=arguments.get("content", ""),
                            proof=arguments.get("proof"),
                            api_client=self.api_client,
                        )
                    elif action == "reputation":
                        result = await get_agent_reputation(
                            pubkey=arguments.get("pubkey", ""),
                            limit=arguments.get("limit", 20),
                            api_client=self.api_client,
                        )
                    elif action == "settle":
                        result = await settle_agent_service(
                            l402_endpoint=arguments.get("l402_endpoint", ""),
                            method=arguments.get("method", "GET"),
                            body=arguments.get("body"),
                            agreement_id=arguments.get("agreement_id"),
                            max_sats=arguments.get("max_sats", 1000),
                            confirmation_nonce=arguments.get("confirmation_nonce"),
                            l402_client=self.l402_client,
                            budget_service=self.budget_service,
                        )
                    else:
                        result = _unknown_action(
                            "agent_services", "action", arguments.get("action")
                        )

                else:
                    result = f"Unknown tool: {requested}"

                result = str(result)
                if alias is not None:
                    result = _mark_deprecated(result, alias)
                return [TextContent(type="text", text=result)]

            except Exception as e:
                logger.exception(f"Error in tool {requested}")
                # Sanitize exception message to avoid leaking credentials
                safe_msg = _sanitize_error(str(e))
                return [TextContent(type="text", text=f"Error in {requested}: {safe_msg}")]

    # Tools that never need a wallet, by canonical (post-alias) name:
    #  - discover_api / verify_confirmation_code use no wallet at all.
    #  - test_l402_payment is allowed through without one so it can return its own
    #    structured no_wallet verdict (parity with .NET) rather than the generic guard
    #    string below (which also wrongly suggests OpenNode, which cannot do L402).
    #  - get_balance is a READ-ONLY balance tool that returns its own
    #    receiving-oriented no-wallet message, so it is exempt from the payment guard.
    #  - l402_producer (create/verify) goes to the Lightning Enable API, not a wallet.
    #  - setup_wallet is how an agent GETS a wallet — gating it behind one would be a
    #    deadlock, and it is the tool the guard message below points at.
    _WALLET_FREE_TOOLS = frozenset(
        {
            "setup_wallet",
            "discover_api",
            "verify_confirmation_code",
            "test_l402_payment",
            "get_balance",
            "l402_producer",
        }
    )

    def _read_receipts_resource(self, uri: str) -> str:
        """Serve ``lightning-enable://receipts`` and ``.../{paymentHash}`` as JSONL.

        Both go through ``ReceiptService.read_recent``, which redacts at the read boundary,
        so a preimage cannot leave here even if one somehow reached the file.
        """
        normalized = uri.rstrip("/")

        if self.receipt_service is None:
            raise ValueError(
                "Receipt logging is not available (no wallet/session initialized), so the "
                "durable receipt log cannot be read."
            )

        if normalized == RECEIPTS_RESOURCE_URI:
            return _to_json_lines(
                self.receipt_service.read_recent(RECEIPTS_RESOURCE_MAX_ROWS)
            )

        prefix = RECEIPTS_RESOURCE_URI + "/"
        if normalized.startswith(prefix):
            payment_hash = normalized[len(prefix):].strip()
            if not payment_hash:
                raise ValueError(
                    "A payment hash is required. Read "
                    f"{RECEIPTS_RESOURCE_URI} to see recent receipts and their payment hashes."
                )

            matches = [
                receipt
                for receipt in self.receipt_service.read_recent(RECEIPT_LOOKUP_WINDOW)
                if str(receipt.get("paymentHash", "")).lower() == payment_hash.lower()
            ]
            if not matches:
                raise ValueError(
                    f"No receipt found for payment hash '{payment_hash}' in the most recent "
                    f"{RECEIPT_LOOKUP_WINDOW:,} entries of the durable log. Read "
                    f"{RECEIPTS_RESOURCE_URI} to see what is there."
                )
            return _to_json_lines(matches)

        raise ValueError(
            f"Unknown resource '{uri}'. This server serves {RECEIPTS_RESOURCE_URI} and "
            f"{RECEIPT_BY_HASH_URI_TEMPLATE}."
        )

    def _requires_wallet(self, name: str, arguments: Mapping[str, Any]) -> bool:
        """Whether a call needs a configured wallet before it can run.

        Preserves the pre-consolidation policy exactly, now expressed per action for
        the merged tools: ``receipts`` reads the durable log without a wallet (it is
        the "pull the plug" audit path) but the session list does not; every
        ``agent_services`` action except ``settle`` talks to the Lightning Enable API
        or the public registry rather than the wallet.
        """
        if name in self._WALLET_FREE_TOOLS:
            return False
        if name == "receipts":
            return arguments.get("source") != "durable"
        if name == "agent_services":
            return arguments.get("action") == "settle"
        return True

    async def _initialize_services(self) -> None:
        """Initialize wallet, L402 client, budget service, and payment history.

        Supports wallet backends (in priority order for L402):
        1. LND - Set LND_REST_HOST + LND_MACAROON_HEX (direct node, always returns preimage)
        2. NWC (Nostr Wallet Connect) - Set NWC_CONNECTION_STRING (returns preimage - best for L402)
        3. Strike - Set STRIKE_API_KEY (returns preimage via lightning.preImage - L402 works)
        4. OpenNode - Set OPENNODE_API_KEY (does NOT return preimage - L402 will NOT work)

        For L402 support, use LND, NWC, or Strike. OpenNode is for general payments only.
        """
        from .config import get_config_service

        config_service = get_config_service()
        wallet_config = config_service.configuration.wallets

        # Read env vars with config file fallback (matching .NET behavior)
        def _get_env_or_config(env_var: str, config_value: str | None) -> str | None:
            """Get value from env var, falling back to config file."""
            value = os.getenv(env_var)
            if not value or value.startswith("${"):
                return config_value
            return value

        lnd_rest_host = _get_env_or_config("LND_REST_HOST", wallet_config.lnd_rest_host)
        lnd_macaroon_hex = _get_env_or_config("LND_MACAROON_HEX", wallet_config.lnd_macaroon_hex)
        nwc_connection = _get_env_or_config("NWC_CONNECTION_STRING", wallet_config.nwc_connection_string)
        strike_api_key = _get_env_or_config("STRIKE_API_KEY", wallet_config.strike_api_key)
        opennode_api_key = _get_env_or_config("OPENNODE_API_KEY", wallet_config.opennode_api_key)

        lnd_skip_tls_verify = os.getenv("LND_SKIP_TLS_VERIFY", "").lower() == "true"

        # Always initialize API client (for producer tools, independent of wallet)
        self.api_client = LightningEnableApiClient()
        if self.api_client.is_configured:
            logger.info("Lightning Enable API client configured - producer tools available")

        has_lnd = bool(lnd_rest_host and lnd_macaroon_hex)
        has_nwc = bool(nwc_connection)
        has_strike = bool(strike_api_key)
        has_opennode = bool(opennode_api_key)

        if not has_lnd and not has_nwc and not has_strike and not has_opennode:
            logger.warning(
                "No wallet configured. Set one L402-capable wallet: STRIKE_API_KEY, "
                "NWC_CONNECTION_STRING, or LND_REST_HOST+LND_MACAROON_HEX. "
                "(OPENNODE_API_KEY is receiving/invoicing only — it cannot pay L402 "
                "challenges.) Call setup_wallet for the guided path, then run "
                "test_l402_payment to confirm the wallet works."
            )
            return

        try:
            # Determine wallet priority
            # Default: LND > NWC > Strike > OpenNode
            # Can be overridden via WALLET_PRIORITY env var or config
            wallet_priority = os.getenv("WALLET_PRIORITY", "").lower() or (wallet_config.priority or "").lower()

            if wallet_priority == "lnd" and has_lnd:
                selected = "lnd"
            elif wallet_priority == "nwc" and has_nwc:
                selected = "nwc"
            elif wallet_priority == "strike" and has_strike:
                selected = "strike"
            elif wallet_priority == "opennode" and has_opennode:
                selected = "opennode"
            elif has_lnd:
                selected = "lnd"
            elif has_nwc:
                selected = "nwc"
            elif has_strike:
                selected = "strike"
            elif has_opennode:
                selected = "opennode"
            else:
                selected = None

            # Initialize wallet based on priority
            if selected == "lnd":
                logger.info("Initializing LND wallet (L402 compatible, direct node)...")
                self.wallet = LndWallet(
                    rest_host=lnd_rest_host,
                    macaroon_hex=lnd_macaroon_hex,
                    skip_tls_verify=lnd_skip_tls_verify,
                )
                await self.wallet.connect()
                logger.info("LND wallet connected - preimage always available")
            elif selected == "nwc":
                logger.info("Initializing NWC wallet (L402 compatible)...")
                self._nwc_config = NWCConfig.from_uri(nwc_connection)
                self.wallet = NWCWallet(nwc_connection)
                await self.wallet.connect()
                logger.info("NWC wallet connected - preimage support available")
            elif selected == "opennode":
                logger.info("Initializing OpenNode wallet...")
                environment = os.getenv("OPENNODE_ENVIRONMENT", "production")
                if not environment or environment.startswith("${"):
                    environment = wallet_config.opennode_environment or "production"
                self.wallet = OpenNodeWallet(
                    api_key=opennode_api_key,
                    environment=environment,
                )
                await self.wallet.connect()
                logger.info(f"OpenNode wallet connected ({environment})")
                logger.warning("OpenNode may not return preimage - L402 may not work")
            elif selected == "strike":
                logger.info("Initializing Strike wallet...")
                self.wallet = StrikeWallet(api_key=strike_api_key)
                await self.wallet.connect()
                logger.info("Strike wallet connected - preimage support available via lightning.preImage")

            # Also initialize Strike for Strike-specific features if available
            if has_strike and not isinstance(self.wallet, StrikeWallet):
                logger.info("Initializing Strike wallet for multi-currency features...")
                self.strike_wallet = StrikeWallet(api_key=strike_api_key)
                await self.strike_wallet.connect()
            elif isinstance(self.wallet, StrikeWallet):
                self.strike_wallet = self.wallet

            # Initialize the BudgetService (single source of truth for spending
            # limits + multi-tier approval + out-of-band confirmation). Uses
            # configuration from ~/.lightning-enable/config.json. The legacy
            # BudgetManager and its L402_MAX_SATS_PER_REQUEST / _PER_SESSION env
            # vars have been removed — runtime sats caps are now tightened via the
            # BudgetService.configure_budget tool (tighten-only).
            self.budget_service = get_budget_service()
            logger.info("BudgetService initialized with multi-tier approval")
            # Where over-threshold confirmation codes go. get_budget_service() resolved this
            # from config + environment + whether stdin is a TTY, and already printed any
            # misconfiguration warning; surface the outcome next to the other startup banners.
            print(
                "[Lightning Enable MCP] Approval channel: "
                f"{self.budget_service.confirmation_channel_name} "
                "(set confirmation.channel in ~/.lightning-enable/config.json, or "
                f"{CHANNEL_ENV_VAR}={VALID_CHANNELS})",
                file=sys.stderr,
                flush=True,
            )

            # Initialize the PaymentHistoryService (separate session audit trail).
            self.payment_history_service = get_payment_history_service()

            # Durable, append-only spend receipts (off the agent's context path).
            # Wallet label is fixed for the session, so bake it in at init.
            self.receipt_service = ReceiptService(wallet_label=wallet_label_from(self.wallet))

            # The receipt seam: every payment tool pays through this decorator, so
            # each payment (pay_invoice, L402 flows, on-chain, and any future tool)
            # leaves exactly one receipts.jsonl line with zero per-tool receipt code.
            # Only wrap a REAL wallet — a truthy wrapper around None would defeat the
            # tools' "wallet not configured" guards in wallet-less mode.
            # Decorator chain (outermost first): IdempotentWallet guards against a blind
            # duplicate payment (durable operation ledger) BEFORE the receipt seam and the
            # real wallet — so a refused duplicate neither pays nor writes a receipt.
            self.operation_ledger = OperationLedger()
            self.paying_wallet = (
                IdempotentWallet(
                    ReceiptRecordingWallet(self.wallet, self.receipt_service, self.budget_service),
                    self.operation_ledger,
                )
                if self.wallet is not None
                else None
            )

            # Initialize L402 client. It is the SINGLE SOURCE OF TRUTH for recording a real
            # payment (spend + payment history + cooldown), so it needs the budget and
            # payment-history services; the consuming tools stay passive and record nothing.
            # It pays through the receipt seam so every L402 payment is receipted.
            self.l402_client = L402Client(
                wallet=self.paying_wallet,
                budget_service=self.budget_service,
                payment_history_service=self.payment_history_service,
            )

            logger.info("Services initialized successfully")

        except Exception as e:
            logger.exception("Failed to initialize services")
            raise RuntimeError(f"Failed to initialize: {e!s}") from e

    async def run(self) -> None:
        """Run the MCP server."""
        logger.info("Starting Lightning Enable MCP server...")

        async with stdio_server() as (read_stream, write_stream):
            await self.server.run(
                read_stream,
                write_stream,
                self.server.create_initialization_options(),
            )


def main() -> None:
    """Entry point for the MCP server."""
    server = LightningEnableServer()

    try:
        asyncio.run(server.run())
    except KeyboardInterrupt:
        logger.info("Server stopped by user")
        sys.exit(0)
    except Exception as e:
        logger.exception("Server error")
        sys.exit(1)


if __name__ == "__main__":
    main()
