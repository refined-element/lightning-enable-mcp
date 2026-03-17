"""
Lightning Enable MCP Server

Main server module providing L402 payment capabilities to AI agents via MCP.
"""

import asyncio
import logging
import os
import sys
from typing import Any

from mcp.server import Server
from mcp.server.stdio import stdio_server
from mcp.types import (
    Tool,
    TextContent,
)

from .budget import BudgetManager
from .budget_service import BudgetService, get_budget_service
from .l402_client import L402Client
from .lnd_wallet import LndWallet
from .lightning_enable_api import LightningEnableApiClient
from .nwc_wallet import NWCWallet, NWCConfig
from .opennode_wallet import OpenNodeWallet
from .strike_wallet import StrikeWallet
from .tools.access_resource import access_l402_resource
from .tools.check_invoice_status import check_invoice_status
from .tools.confirm_payment import confirm_payment
from .tools.create_invoice import create_invoice
from .tools.create_l402_challenge import create_l402_challenge
from .tools.discover_api import discover_api
from .tools.exchange_currency import exchange_currency
from .tools.get_all_balances import get_all_balances
from .tools.get_btc_price import get_btc_price
from .tools.pay_challenge import pay_l402_challenge
from .tools.pay_invoice import pay_invoice
from .tools.send_onchain import send_onchain
from .tools.verify_l402_payment import verify_l402_payment
from .tools.wallet import check_wallet_balance
from .tools.budget import configure_budget, get_payment_history
from .tools.budget_status import get_budget_status

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


class LightningEnableServer:
    """MCP Server for L402 Lightning payments."""

    def __init__(self) -> None:
        self.server = Server("lightning-enable")
        self.wallet: LndWallet | NWCWallet | OpenNodeWallet | StrikeWallet | None = None
        self.strike_wallet: StrikeWallet | None = None  # For Strike-specific features
        self.l402_client: L402Client | None = None
        self.budget_manager: BudgetManager | None = None
        self.budget_service: BudgetService | None = None  # New multi-tier approval system
        self._nwc_config: NWCConfig | None = None  # Store NWC config for pubkey access
        self.api_client: LightningEnableApiClient | None = None  # For L402 producer tools

        self._setup_handlers()

    def _setup_handlers(self) -> None:
        """Register MCP tool handlers."""

        @self.server.list_tools()
        async def list_tools() -> list[Tool]:
            """Return the list of available tools."""
            return [
                Tool(
                    name="access_l402_resource",
                    description=(
                        "Fetch a URL with automatic L402 payment handling. "
                        "If the server returns a 402 Payment Required response, "
                        "the invoice will be automatically paid and the request retried."
                    ),
                    inputSchema={
                        "type": "object",
                        "properties": {
                            "url": {
                                "type": "string",
                                "description": "The URL to fetch",
                            },
                            "method": {
                                "type": "string",
                                "description": "HTTP method (GET, POST, PUT, DELETE)",
                                "default": "GET",
                                "enum": ["GET", "POST", "PUT", "DELETE"],
                            },
                            "headers": {
                                "type": "object",
                                "description": "Optional additional request headers",
                                "additionalProperties": {"type": "string"},
                            },
                            "body": {
                                "type": "string",
                                "description": "Optional request body for POST/PUT requests",
                            },
                            "max_sats": {
                                "type": "integer",
                                "description": "Maximum satoshis to pay for this request",
                                "default": 1000,
                            },
                            "confirmed": {
                                "type": "boolean",
                                "description": "Set to true to confirm a payment that requires approval. Use when previous call returned requiresConfirmation=true.",
                                "default": False,
                            },
                        },
                        "required": ["url"],
                    },
                ),
                Tool(
                    name="pay_l402_challenge",
                    description=(
                        "Manually pay an L402 invoice and receive the authorization token. "
                        "Use this if you need to handle the L402 flow yourself."
                    ),
                    inputSchema={
                        "type": "object",
                        "properties": {
                            "invoice": {
                                "type": "string",
                                "description": "BOLT11 Lightning invoice string",
                            },
                            "macaroon": {
                                "type": "string",
                                "description": "Base64-encoded macaroon from the L402 challenge",
                            },
                            "max_sats": {
                                "type": "integer",
                                "description": "Maximum satoshis allowed for this payment",
                                "default": 1000,
                            },
                        },
                        "required": ["invoice", "macaroon"],
                    },
                ),
                Tool(
                    name="check_wallet_balance",
                    description="Check the connected Lightning wallet balance via NWC.",
                    inputSchema={
                        "type": "object",
                        "properties": {},
                    },
                ),
                Tool(
                    name="get_payment_history",
                    description="List recent L402 payments made during this session.",
                    inputSchema={
                        "type": "object",
                        "properties": {
                            "limit": {
                                "type": "integer",
                                "description": "Maximum number of payments to return",
                                "default": 10,
                            },
                            "since": {
                                "type": "string",
                                "description": "ISO timestamp to filter payments from",
                            },
                        },
                    },
                ),
                Tool(
                    name="configure_budget",
                    description="Set spending limits for the session.",
                    inputSchema={
                        "type": "object",
                        "properties": {
                            "per_request": {
                                "type": "integer",
                                "description": "Maximum satoshis per individual request",
                                "default": 1000,
                            },
                            "per_session": {
                                "type": "integer",
                                "description": "Maximum total satoshis for the entire session",
                                "default": 10000,
                            },
                        },
                    },
                ),
                Tool(
                    name="pay_invoice",
                    description=(
                        "Pay a Lightning invoice directly and get the preimage as proof of payment. "
                        "Use this to pay any BOLT11 Lightning invoice without L402 protocol overhead."
                    ),
                    inputSchema={
                        "type": "object",
                        "properties": {
                            "invoice": {
                                "type": "string",
                                "description": "BOLT11 Lightning invoice string to pay",
                            },
                            "max_sats": {
                                "type": "integer",
                                "description": "Maximum satoshis allowed to pay. Defaults to 1000",
                                "default": 1000,
                            },
                            "confirmed": {
                                "type": "boolean",
                                "description": "Set to true to confirm a payment that requires approval. Use when previous call returned requiresConfirmation=true.",
                                "default": False,
                            },
                        },
                        "required": ["invoice"],
                    },
                ),
                Tool(
                    name="create_invoice",
                    description=(
                        "Create a Lightning invoice to receive a payment. "
                        "Returns a BOLT11 invoice string to share with the payer."
                    ),
                    inputSchema={
                        "type": "object",
                        "properties": {
                            "amount_sats": {
                                "type": "integer",
                                "description": "Amount to receive in satoshis",
                            },
                            "memo": {
                                "type": "string",
                                "description": "Optional description/memo for the invoice",
                            },
                            "expiry_secs": {
                                "type": "integer",
                                "description": "Invoice expiry time in seconds. Defaults to 3600 (1 hour)",
                                "default": 3600,
                            },
                        },
                        "required": ["amount_sats"],
                    },
                ),
                Tool(
                    name="check_invoice_status",
                    description=(
                        "Check if a Lightning invoice has been paid. "
                        "Use the invoice ID from create_invoice."
                    ),
                    inputSchema={
                        "type": "object",
                        "properties": {
                            "invoice_id": {
                                "type": "string",
                                "description": "The invoice ID returned from create_invoice",
                            },
                        },
                        "required": ["invoice_id"],
                    },
                ),
                Tool(
                    name="get_all_balances",
                    description=(
                        "Get all currency balances from your wallet (USD, BTC, etc.). "
                        "Most useful with Strike wallet which supports multiple currencies."
                    ),
                    inputSchema={
                        "type": "object",
                        "properties": {},
                    },
                ),
                Tool(
                    name="get_btc_price",
                    description=(
                        "Get the current Bitcoin price in USD. "
                        "Only available with Strike wallet."
                    ),
                    inputSchema={
                        "type": "object",
                        "properties": {},
                    },
                ),
                Tool(
                    name="exchange_currency",
                    description=(
                        "Exchange currency within your wallet (USD to BTC or BTC to USD). "
                        "Currently only available with Strike wallet."
                    ),
                    inputSchema={
                        "type": "object",
                        "properties": {
                            "source_currency": {
                                "type": "string",
                                "description": "Currency to convert from: USD or BTC",
                            },
                            "target_currency": {
                                "type": "string",
                                "description": "Currency to convert to: BTC or USD",
                            },
                            "amount": {
                                "type": "number",
                                "description": "Amount in source currency (e.g., 100 for $100 or 0.001 for 0.001 BTC)",
                            },
                        },
                        "required": ["source_currency", "target_currency", "amount"],
                    },
                ),
                Tool(
                    name="send_onchain",
                    description=(
                        "Send an on-chain Bitcoin payment to a Bitcoin address. "
                        "Currently only available with Strike wallet."
                    ),
                    inputSchema={
                        "type": "object",
                        "properties": {
                            "address": {
                                "type": "string",
                                "description": "Bitcoin address to send to (e.g., bc1q...)",
                            },
                            "amount_sats": {
                                "type": "integer",
                                "description": "Amount to send in satoshis",
                            },
                        },
                        "required": ["address", "amount_sats"],
                    },
                ),
                Tool(
                    name="get_budget_status",
                    description=(
                        "View current budget status and spending limits (read-only). "
                        "Edit ~/.lightning-enable/config.json to change limits."
                    ),
                    inputSchema={
                        "type": "object",
                        "properties": {},
                    },
                ),
                Tool(
                    name="create_l402_challenge",
                    description=(
                        "Create an L402 payment challenge to charge another agent or user for accessing a resource. "
                        "Returns a Lightning invoice and macaroon. The payer must pay the invoice and present "
                        "the L402 token (macaroon:preimage) back to you for verification. "
                        "Requires LIGHTNING_ENABLE_API_KEY with an Agentic Commerce subscription."
                    ),
                    inputSchema={
                        "type": "object",
                        "properties": {
                            "resource": {
                                "type": "string",
                                "description": "Resource identifier - URL, service name, or description of what you're charging for",
                            },
                            "price_sats": {
                                "type": "integer",
                                "description": "Price in satoshis to charge",
                            },
                            "description": {
                                "type": "string",
                                "description": "Description shown on the Lightning invoice",
                            },
                        },
                        "required": ["resource", "price_sats"],
                    },
                ),
                Tool(
                    name="verify_l402_payment",
                    description=(
                        "Verify an L402 token (macaroon + preimage) to confirm payment was made. "
                        "Use this after receiving an L402 token from a payer to validate they paid "
                        "before granting access to the resource. "
                        "Requires LIGHTNING_ENABLE_API_KEY with an Agentic Commerce subscription."
                    ),
                    inputSchema={
                        "type": "object",
                        "properties": {
                            "macaroon": {
                                "type": "string",
                                "description": "Base64-encoded macaroon from the L402 token",
                            },
                            "preimage": {
                                "type": "string",
                                "description": "Hex-encoded preimage (proof of payment)",
                            },
                        },
                        "required": ["macaroon", "preimage"],
                    },
                ),
                Tool(
                    name="confirm_payment",
                    description=(
                        "Confirm a pending payment using the nonce code from a previous payment request. "
                        "Call this after a payment tool returns requiresConfirmation=true with a nonce."
                    ),
                    inputSchema={
                        "type": "object",
                        "properties": {
                            "nonce": {
                                "type": "string",
                                "description": "The 6-character confirmation code from the payment request",
                            },
                        },
                        "required": ["nonce"],
                    },
                ),
                Tool(
                    name="discover_api",
                    description=(
                        "Discover L402-enabled APIs. Use 'query' to search the registry for available APIs by keyword, "
                        "or use 'url' to fetch a specific API's manifest with full endpoint details and pricing. "
                        "Use 'category' to browse by category. With budget_aware=true, shows how many calls you can afford."
                    ),
                    inputSchema={
                        "type": "object",
                        "properties": {
                            "url": {
                                "type": "string",
                                "description": "Base URL of the L402-enabled API, or direct URL to the manifest JSON file. If omitted, searches the registry instead.",
                            },
                            "query": {
                                "type": "string",
                                "description": "Search the L402 API registry by keyword (e.g., 'weather', 'ai', 'geocoding').",
                            },
                            "category": {
                                "type": "string",
                                "description": "Filter registry results by category (e.g., 'ai', 'data', 'finance').",
                            },
                            "budget_aware": {
                                "type": "boolean",
                                "description": "If true, annotate endpoints with affordable call counts based on remaining budget. Default: true.",
                                "default": True,
                            },
                        },
                    },
                ),
            ]

        @self.server.call_tool()
        async def call_tool(name: str, arguments: dict[str, Any]) -> list[TextContent]:
            """Handle tool invocations."""
            try:
                # Ensure services are initialized
                if self.wallet is None or self.l402_client is None:
                    await self._initialize_services()

                # Tools that don't require a wallet connection
                producer_tools = {"create_l402_challenge", "verify_l402_payment", "discover_api", "confirm_payment"}

                if self.wallet is None and name not in producer_tools:
                    return [
                        TextContent(
                            type="text",
                            text="Error: No wallet configured. "
                            "Set LND_REST_HOST+LND_MACAROON_HEX, NWC_CONNECTION_STRING, "
                            "STRIKE_API_KEY, or OPENNODE_API_KEY environment variable.",
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
                        confirmed=arguments.get("confirmed", False),
                        l402_client=self.l402_client,
                        budget_manager=self.budget_manager,
                        budget_service=self.budget_service,
                    )

                elif name == "pay_l402_challenge":
                    result = await pay_l402_challenge(
                        invoice=arguments["invoice"],
                        macaroon=arguments["macaroon"],
                        max_sats=arguments.get("max_sats", 1000),
                        wallet=self.wallet,
                        budget_manager=self.budget_manager,
                    )

                elif name == "check_wallet_balance":
                    result = await check_wallet_balance(wallet=self.wallet)

                elif name == "get_payment_history":
                    result = await get_payment_history(
                        limit=arguments.get("limit", 10),
                        since=arguments.get("since"),
                        budget_manager=self.budget_manager,
                    )

                elif name == "configure_budget":
                    result = await configure_budget(
                        per_request=arguments.get("per_request", 1000),
                        per_session=arguments.get("per_session", 10000),
                        budget_manager=self.budget_manager,
                    )

                elif name == "pay_invoice":
                    result = await pay_invoice(
                        invoice=arguments.get("invoice", ""),
                        max_sats=arguments.get("max_sats", 1000),
                        confirmed=arguments.get("confirmed", False),
                        wallet=self.wallet,
                        budget_manager=self.budget_manager,
                        budget_service=self.budget_service,
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

                elif name == "get_all_balances":
                    result = await get_all_balances(
                        wallet=self.wallet,
                        strike_wallet=self.strike_wallet,
                        budget_service=self.budget_service,
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
                    result = await send_onchain(
                        address=arguments.get("address", ""),
                        amount_sats=arguments.get("amount_sats", 0),
                        wallet=onchain_wallet,
                        budget_service=self.budget_service,
                    )

                elif name == "get_budget_status":
                    result = await get_budget_status(
                        budget_service=self.budget_service,
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

                elif name == "confirm_payment":
                    result = await confirm_payment(
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

                else:
                    result = f"Unknown tool: {name}"

                return [TextContent(type="text", text=str(result))]

            except Exception as e:
                logger.exception(f"Error in tool {name}")
                # Sanitize exception message to avoid leaking credentials
                safe_msg = _sanitize_error(str(e))
                return [TextContent(type="text", text=f"Error in {name}: {safe_msg}")]

    async def _initialize_services(self) -> None:
        """Initialize wallet, L402 client, and budget manager.

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
                "No wallet configured. Set LND_REST_HOST+LND_MACAROON_HEX, "
                "NWC_CONNECTION_STRING, STRIKE_API_KEY, or OPENNODE_API_KEY"
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

            # Initialize budget manager (legacy)
            max_per_request = int(os.getenv("L402_MAX_SATS_PER_REQUEST", "1000"))
            max_per_session = int(os.getenv("L402_MAX_SATS_PER_SESSION", "10000"))
            self.budget_manager = BudgetManager(
                max_per_request=max_per_request, max_per_session=max_per_session
            )

            # Initialize new BudgetService (multi-tier approval system)
            # Uses configuration from ~/.lightning-enable/config.json
            self.budget_service = get_budget_service()
            logger.info("BudgetService initialized with multi-tier approval")

            # Initialize L402 client
            self.l402_client = L402Client(wallet=self.wallet)

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
