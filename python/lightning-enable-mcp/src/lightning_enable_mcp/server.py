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
from .nwc_wallet import NWCWallet, NWCConfig
from .opennode_wallet import OpenNodeWallet
from .strike_wallet import StrikeWallet
from .tools.access_resource import access_l402_resource
from .tools.pay_challenge import pay_l402_challenge
from .tools.pay_invoice import pay_invoice
from .tools.wallet import check_wallet_balance
from .tools.budget import configure_budget, get_payment_history
from .tools.budget_status import get_budget_status

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s - %(name)s - %(levelname)s - %(message)s",
)
logger = logging.getLogger("lightning-enable-mcp")


class LightningEnableServer:
    """MCP Server for L402 Lightning payments."""

    def __init__(self) -> None:
        self.server = Server("lightning-enable")
        self.wallet: NWCWallet | OpenNodeWallet | StrikeWallet | None = None
        self.strike_wallet: StrikeWallet | None = None  # For Strike-specific features
        self.l402_client: L402Client | None = None
        self.budget_manager: BudgetManager | None = None
        self.budget_service: BudgetService | None = None  # New multi-tier approval system
        self._nwc_config: NWCConfig | None = None  # Store NWC config for pubkey access

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
            ]

        @self.server.call_tool()
        async def call_tool(name: str, arguments: dict[str, Any]) -> list[TextContent]:
            """Handle tool invocations."""
            try:
                # Ensure services are initialized
                if self.wallet is None or self.l402_client is None:
                    await self._initialize_services()

                if self.wallet is None:
                    return [
                        TextContent(
                            type="text",
                            text="Error: NWC wallet not configured. "
                            "Set NWC_CONNECTION_STRING environment variable.",
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
                    result = await self._create_invoice(
                        amount_sats=arguments.get("amount_sats", 0),
                        memo=arguments.get("memo"),
                        expiry_secs=arguments.get("expiry_secs", 3600),
                    )

                elif name == "check_invoice_status":
                    result = await self._check_invoice_status(
                        invoice_id=arguments.get("invoice_id", ""),
                    )

                elif name == "get_all_balances":
                    result = await self._get_all_balances()

                elif name == "get_btc_price":
                    result = await self._get_btc_price()

                elif name == "exchange_currency":
                    result = await self._exchange_currency(
                        source_currency=arguments.get("source_currency", ""),
                        target_currency=arguments.get("target_currency", ""),
                        amount=arguments.get("amount", 0),
                    )

                elif name == "send_onchain":
                    result = await self._send_onchain(
                        address=arguments.get("address", ""),
                        amount_sats=arguments.get("amount_sats", 0),
                    )

                elif name == "get_budget_status":
                    result = await get_budget_status(
                        budget_service=self.budget_service,
                    )

                else:
                    result = f"Unknown tool: {name}"

                return [TextContent(type="text", text=str(result))]

            except Exception as e:
                logger.exception(f"Error in tool {name}")
                return [TextContent(type="text", text=f"Error: {e!s}")]

    async def _initialize_services(self) -> None:
        """Initialize wallet, L402 client, and budget manager.

        Supports wallet backends (in priority order for L402):
        1. NWC (Nostr Wallet Connect) - Set NWC_CONNECTION_STRING (returns preimage - best for L402)
        2. Strike - Set STRIKE_API_KEY (returns preimage via lightning.preImage - L402 works)
        3. OpenNode - Set OPENNODE_API_KEY (does NOT return preimage - L402 will NOT work)

        For L402 support, use NWC or Strike. OpenNode is for general payments only.
        """
        nwc_connection = os.getenv("NWC_CONNECTION_STRING")
        opennode_api_key = os.getenv("OPENNODE_API_KEY")
        strike_api_key = os.getenv("STRIKE_API_KEY")

        if not nwc_connection and not opennode_api_key and not strike_api_key:
            logger.warning(
                "No wallet configured. Set NWC_CONNECTION_STRING, OPENNODE_API_KEY, or STRIKE_API_KEY"
            )
            return

        try:
            # Initialize wallet - priority: NWC > OpenNode > Strike (for L402 compatibility)
            if nwc_connection:
                logger.info("Initializing NWC wallet (L402 compatible)...")
                self._nwc_config = NWCConfig.from_uri(nwc_connection)
                self.wallet = NWCWallet(nwc_connection)
                await self.wallet.connect()
                logger.info("NWC wallet connected - preimage support available")
            elif opennode_api_key:
                logger.info("Initializing OpenNode wallet...")
                environment = os.getenv("OPENNODE_ENVIRONMENT", "production")
                self.wallet = OpenNodeWallet(
                    api_key=opennode_api_key,
                    environment=environment,
                )
                await self.wallet.connect()
                logger.info(f"OpenNode wallet connected ({environment})")
                logger.warning("OpenNode may not return preimage - L402 may not work")
            elif strike_api_key:
                logger.info("Initializing Strike wallet...")
                self.wallet = StrikeWallet(api_key=strike_api_key)
                await self.wallet.connect()
                logger.info("Strike wallet connected - preimage support available via lightning.preImage")

            # Also initialize Strike for Strike-specific features if available
            if strike_api_key and not isinstance(self.wallet, StrikeWallet):
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

    async def _create_invoice(
        self, amount_sats: int, memo: str | None, expiry_secs: int
    ) -> str:
        """Create a Lightning invoice to receive payment."""
        if self.strike_wallet:
            # Strike supports invoice creation - would need to implement
            # For now, return not supported
            return "Invoice creation not yet implemented for Strike wallet"
        return "Invoice creation requires Strike wallet. Set STRIKE_API_KEY."

    async def _check_invoice_status(self, invoice_id: str) -> str:
        """Check status of an invoice."""
        if self.strike_wallet:
            # Would need to implement invoice status check
            return f"Invoice status check not yet implemented for Strike wallet"
        return "Invoice status check requires Strike wallet. Set STRIKE_API_KEY."

    async def _get_all_balances(self) -> str:
        """Get all currency balances."""
        if self.strike_wallet:
            result = await self.strike_wallet.get_all_balances()
            if result.success:
                lines = ["Currency Balances:"]
                for b in result.balances:
                    lines.append(f"  {b.currency}: {b.available} available ({b.total} total)")
                return "\n".join(lines)
            return f"Error: {result.error_message}"

        # Fallback to regular balance for non-Strike wallets
        if self.wallet:
            try:
                balance = await self.wallet.get_balance()
                return f"BTC Balance: {balance} sats"
            except Exception as e:
                return f"Error getting balance: {e}"

        return "No wallet configured"

    async def _get_btc_price(self) -> str:
        """Get BTC/USD price."""
        if self.strike_wallet:
            result = await self.strike_wallet.get_btc_price()
            if result.success:
                return f"BTC/USD: ${result.btc_usd_price:,.2f}"
            return f"Error: {result.error_message}"
        return "BTC price requires Strike wallet. Set STRIKE_API_KEY."

    async def _exchange_currency(
        self, source_currency: str, target_currency: str, amount: float
    ) -> str:
        """Exchange currency."""
        if self.strike_wallet:
            from decimal import Decimal
            result = await self.strike_wallet.exchange_currency(
                source_currency=source_currency,
                target_currency=target_currency,
                amount=Decimal(str(amount)),
            )
            if result.success:
                return (
                    f"Exchange completed!\n"
                    f"  Sold: {result.source_amount} {result.source_currency}\n"
                    f"  Received: {result.target_amount} {result.target_currency}\n"
                    f"  Rate: {result.rate}\n"
                    f"  Fee: {result.fee or 'None'}"
                )
            return f"Error: {result.error_message}"
        return "Currency exchange requires Strike wallet. Set STRIKE_API_KEY."

    async def _send_onchain(self, address: str, amount_sats: int) -> str:
        """Send on-chain Bitcoin payment."""
        if self.strike_wallet:
            result = await self.strike_wallet.send_onchain(address, amount_sats)
            if result.success:
                return (
                    f"On-chain payment sent!\n"
                    f"  Payment ID: {result.payment_id}\n"
                    f"  Amount: {result.amount_sats} sats\n"
                    f"  Fee: {result.fee_sats} sats\n"
                    f"  State: {result.state}"
                )
            return f"Error: {result.error_message}"
        return "On-chain payments require Strike wallet. Set STRIKE_API_KEY."

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
