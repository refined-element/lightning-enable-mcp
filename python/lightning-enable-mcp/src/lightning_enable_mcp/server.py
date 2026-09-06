"""
Lightning Enable MCP Server

Main server module providing L402 payment capabilities to AI agents via MCP.
"""

import asyncio
import json
import logging
import os
import sys
from typing import Any

from mcp.server import Server
from mcp.server.stdio import stdio_server
from mcp.types import (
    TextContent,
    Tool,
)

from . import __version__
from .budget_service import BudgetService, get_budget_service
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
from .tools.publish_agent_attestation import publish_agent_attestation
from .tools.publish_agent_capability import publish_agent_capability
from .tools.registry import ALL_TOOLS
from .tools.request_agent_service import request_agent_service
from .tools.send_onchain import send_onchain
from .tools.settle_agent_service import settle_agent_service
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
# aliases for one minor cycle. An alias dispatches to the new implementation and the
# forwarded JSON gains a `deprecated` marker. The aliases are intentionally ABSENT from
# list_tools() (dispatcher-only), so the advertised surface is the new names.
DEPRECATED_ALIASES = {
    "confirm_payment": "verify_confirmation_code",
    "check_wallet_balance": "get_balance",
    "get_all_balances": "get_balance",
}
_ALIAS_REMOVAL = "v2.0.0"


def _mark_deprecated(result: str, replaced_by: str) -> str:
    """Annotate a forwarded tool result (JSON string) with a deprecation marker.

    Parses the underlying tool's JSON response and injects
    ``deprecated: {replaced_by, removal}``. If the payload is not a JSON object
    (unexpected), the original string is returned unchanged so an alias never breaks
    a caller that the new tool would have served.
    """
    try:
        data = json.loads(result)
    except (ValueError, TypeError):
        return result
    if not isinstance(data, dict):
        return result
    data["deprecated"] = {"replaced_by": replaced_by, "removal": _ALIAS_REMOVAL}
    return json.dumps(data, indent=2)


class LightningEnableServer:
    """MCP Server for L402 Lightning payments."""

    def __init__(self) -> None:
        self.server = Server("lightning-enable", version=__version__)
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
        """Register MCP tool handlers."""

        @self.server.list_tools()
        async def list_tools() -> list[Tool]:
            """Return the list of available tools."""
            return list(ALL_TOOLS)

        @self.server.call_tool()
        async def call_tool(name: str, arguments: dict[str, Any]) -> list[TextContent]:
            """Handle tool invocations."""
            try:
                # Ensure services are initialized
                if self.wallet is None or self.l402_client is None:
                    await self._initialize_services()

                # Tools that don't require a wallet connection.
                # The ASA discovery/publish/request/attestation/reputation tools
                # use the Lightning Enable API (or public registry), not the
                # wallet — only settle_agent_service needs a wallet, so it is
                # intentionally NOT in this set.
                producer_tools = {
                    "create_l402_challenge",
                    "verify_l402_payment",
                    "discover_api",
                    "verify_confirmation_code",
                    "confirm_payment",  # deprecated alias of verify_confirmation_code
                    "discover_agent_services",
                    "publish_agent_capability",
                    "unpublish_agent_capability",
                    "request_agent_service",
                    "publish_agent_attestation",
                    "get_agent_reputation",
                }

                # test_l402_payment is allowed through even without a wallet so it can
                # return its own structured no_wallet verdict (parity with .NET), instead
                # of this generic guard string (which also wrongly suggests OpenNode, which
                # cannot do L402).
                #
                # get_balance (and its deprecated aliases) is a READ-ONLY balance tool: it
                # returns its own receiving-oriented no-wallet message (which correctly does
                # NOT claim OpenNode can't pay L402), so it is exempt from this payment guard.
                balance_read_tools = {"get_balance", "check_wallet_balance", "get_all_balances"}
                if (
                    self.wallet is None
                    and name not in producer_tools
                    and name not in balance_read_tools
                    and name != "test_l402_payment"
                    and name != "get_receipts"  # reads the durable log; no wallet needed
                ):
                    return [
                        TextContent(
                            type="text",
                            text="Error: No wallet configured. Set one L402-capable wallet: "
                            "STRIKE_API_KEY, NWC_CONNECTION_STRING, or "
                            "LND_REST_HOST+LND_MACAROON_HEX. "
                            "(OPENNODE_API_KEY is receiving/invoicing only — it cannot pay "
                            "L402 challenges.) "
                            "Then run test_l402_payment to confirm the wallet works end to end.",
                        )
                    ]

                # Route to appropriate handler
                if name == "access_l402_resource":
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

                elif name == "get_receipts":
                    result = await get_receipts(
                        limit=arguments.get("limit", 20),
                        receipt_service=self.receipt_service,
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

                elif name in ("get_balance", "check_wallet_balance", "get_all_balances"):
                    # check_wallet_balance and get_all_balances are deprecated, unadvertised
                    # aliases that forward to the unified get_balance implementation.
                    result = await get_balance(
                        wallet=self.wallet,
                        strike_wallet=self.strike_wallet,
                        budget_service=self.budget_service,
                    )
                    if name in DEPRECATED_ALIASES:
                        result = _mark_deprecated(result, DEPRECATED_ALIASES[name])

                elif name == "get_payment_history":
                    result = await get_payment_history(
                        limit=arguments.get("limit", 10),
                        since=arguments.get("since"),
                        payment_history_service=self.payment_history_service,
                    )

                elif name == "configure_budget":
                    result = await configure_budget(
                        per_request=arguments.get("per_request", 1000),
                        per_session=arguments.get("per_session", 10000),
                        budget_service=self.budget_service,
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

                elif name == "get_btc_price":
                    result = await get_btc_price(
                        wallet=self.strike_wallet,
                    )

                elif name == "exchange_currency":
                    result = await exchange_currency(
                        source_currency=arguments.get("source_currency", ""),
                        target_currency=arguments.get("target_currency", ""),
                        amount=arguments.get("amount", 0),
                        wallet=self.strike_wallet,
                    )

                elif name == "send_onchain":
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

                elif name == "get_budget_status":
                    result = await get_budget_status(
                        budget_service=self.budget_service,
                        payment_history_service=self.payment_history_service,
                    )

                elif name == "create_l402_challenge":
                    result = await create_l402_challenge(
                        resource=arguments.get("resource", ""),
                        price_sats=arguments.get("price_sats", 0),
                        description=arguments.get("description"),
                        api_client=self.api_client,
                    )

                elif name == "verify_l402_payment":
                    result = await verify_l402_payment(
                        macaroon=arguments.get("macaroon", ""),
                        preimage=arguments.get("preimage", ""),
                        api_client=self.api_client,
                    )

                elif name in ("verify_confirmation_code", "confirm_payment"):
                    # confirm_payment is a deprecated, unadvertised alias that forwards here.
                    result = await verify_confirmation_code(
                        nonce=arguments.get("nonce", ""),
                        budget_service=self.budget_service,
                    )
                    if name in DEPRECATED_ALIASES:
                        result = _mark_deprecated(result, DEPRECATED_ALIASES[name])

                elif name == "discover_api":
                    result = await discover_api(
                        url=arguments.get("url"),
                        query=arguments.get("query"),
                        category=arguments.get("category"),
                        budget_aware=arguments.get("budget_aware", True),
                        budget_service=self.budget_service,
                    )

                elif name == "discover_agent_services":
                    result = await discover_agent_services(
                        category=arguments.get("category"),
                        hashtags=arguments.get("hashtags"),
                        query=arguments.get("query"),
                        limit=arguments.get("limit", 20),
                        api_client=self.api_client,
                        budget_service=self.budget_service,
                    )

                elif name == "publish_agent_capability":
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

                elif name == "unpublish_agent_capability":
                    result = await unpublish_agent_capability(
                        service_id=arguments.get("service_id", ""),
                        reason=arguments.get("reason"),
                        api_client=self.api_client,
                    )

                elif name == "request_agent_service":
                    result = await request_agent_service(
                        capability_event_id=arguments.get("capability_event_id", ""),
                        budget_sats=arguments.get("budget_sats", 0),
                        parameters=arguments.get("parameters"),
                        api_client=self.api_client,
                        budget_service=self.budget_service,
                    )

                elif name == "publish_agent_attestation":
                    result = await publish_agent_attestation(
                        subject_pubkey=arguments.get("subject_pubkey", ""),
                        agreement_id=arguments.get("agreement_id", ""),
                        rating=arguments.get("rating", 0),
                        content=arguments.get("content", ""),
                        proof=arguments.get("proof"),
                        api_client=self.api_client,
                    )

                elif name == "get_agent_reputation":
                    result = await get_agent_reputation(
                        pubkey=arguments.get("pubkey", ""),
                        limit=arguments.get("limit", 20),
                        api_client=self.api_client,
                    )

                elif name == "settle_agent_service":
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
                    result = f"Unknown tool: {name}"

                return [TextContent(type="text", text=str(result))]

            except Exception as e:
                logger.exception(f"Error in tool {name}")
                # Sanitize exception message to avoid leaking credentials
                safe_msg = _sanitize_error(str(e))
                return [TextContent(type="text", text=f"Error in {name}: {safe_msg}")]

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
                "challenges.) Then run test_l402_payment to confirm the wallet works."
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
