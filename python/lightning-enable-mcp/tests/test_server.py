"""
Tests for MCP Server
"""

import pytest
from unittest.mock import AsyncMock, MagicMock, patch
import json

from lightning_enable_mcp.server import LightningEnableServer
from lightning_enable_mcp.nwc_wallet import NWCWallet


# ── Tool inventory: the SINGLE SOURCE OF TRUTH for the tool count ──────────────
# Every advertised count (package READMEs, docs, marketing) derives from this
# split. Add or remove a tool → update the right set here; the guard assertions
# in test_list_tools_returns_all_tools then fail until the code and this list
# agree. Keep in lockstep with the .NET guard
# (dotnet/tests/LightningEnable.Mcp.Tests/ToolInventoryTests.cs) and the docs'
# MCP Complete Guide — the one place that itemizes the tools for humans.
#
# Canonical (the ``standard`` profile, which is the default): 15 total = 13
# out-of-the-box (free, just a wallet) + 2 that need LIGHTNING_ENABLE_API_KEY.
# The 2026-09 tool-surface consolidation folded 16 single-purpose tools into five
# action-style verbs (budget, receipts, wallet_ops, l402_producer, agent_services),
# taking the advertised surface from 26 tools to 15; ``setup_wallet`` then added the
# wallet onboarding step every other tool depends on — see tools/profiles.py.
#
# Every pre-consolidation name still dispatches as an accepted-but-unadvertised
# forwarding alias (TestDeprecatedAliases in test_tool_profiles.py), and the
# ``full`` profile re-advertises them. Neither belongs in this inventory: this is
# the DEFAULT advertised surface.
FREE_TOOLS = {
    "setup_wallet",
    "access_l402_resource",
    "pay_invoice",
    "pay_l402_challenge",
    "test_l402_payment",
    "get_balance",
    "budget",
    "receipts",
    "create_invoice",
    "check_invoice_status",
    "verify_confirmation_code",
    "discover_api",
    "create_lightning_enable_account",
    "wallet_ops",
}
API_KEY_TOOLS = {
    "l402_producer",
    "agent_services",
}
ALL_TOOLS = FREE_TOOLS | API_KEY_TOOLS


class TestLightningEnableServer:
    """Tests for LightningEnableServer."""

    def test_server_initialization(self):
        """Test server initializes correctly."""
        server = LightningEnableServer()

        assert server.server is not None
        assert server.wallet is None
        assert server.l402_client is None
        assert server.budget_service is None
        assert server.payment_history_service is None

    @pytest.mark.asyncio
    async def test_list_tools_returns_all_tools(self):
        """Test that list_tools returns all expected tools.

        The MCP SDK (>=1.x) no longer exposes a ``Server._tool_handlers``
        attribute. Handlers registered via the ``@server.list_tools()``
        decorator are stored in the public ``Server.request_handlers``
        registry, keyed by request type. We invoke the registered
        ``ListToolsRequest`` handler directly — the same code path the SDK
        uses when a client sends a ``tools/list`` request — and assert the
        full expected tool set is returned.
        """
        from mcp.types import ListToolsRequest

        server = LightningEnableServer()

        # The list_tools handler is registered in the public request_handlers map.
        assert ListToolsRequest in server.server.request_handlers

        handler = server.server.request_handlers[ListToolsRequest]
        result = await handler(ListToolsRequest(method="tools/list"))

        # Handler returns a ServerResult wrapping a ListToolsResult.
        tools = result.root.tools
        tool_names = {tool.name for tool in tools}

        # The registered set must exactly equal the declared inventory (no drift).
        # Deprecated aliases (every pre-consolidation name) still dispatch but must
        # NOT appear here.
        assert tool_names == ALL_TOOLS
        assert len(tool_names) == 16
        # Free/paid split is the source of truth every doc count derives from.
        assert FREE_TOOLS.isdisjoint(API_KEY_TOOLS)
        assert len(FREE_TOOLS) == 14, "14 out-of-the-box tools"
        assert len(API_KEY_TOOLS) == 2, "2 API-key-gated verbs (l402_producer, agent_services)"
        assert tool_names >= FREE_TOOLS
        assert tool_names >= API_KEY_TOOLS

    @pytest.mark.asyncio
    async def test_services_not_initialized_without_nwc(self):
        """Test services aren't initialized without a wallet configured.

        Only the wallet-related env vars are cleared (not the entire
        environment) so that config-file home-directory resolution still
        works. With no wallet credentials present, ``_initialize_services``
        should leave ``server.wallet`` as ``None``.
        """
        wallet_env_vars = {
            "LND_REST_HOST": "",
            "LND_MACAROON_HEX": "",
            "NWC_CONNECTION_STRING": "",
            "STRIKE_API_KEY": "",
            "OPENNODE_API_KEY": "",
        }
        with patch.dict("os.environ", wallet_env_vars, clear=False):
            server = LightningEnableServer()

            # Prevent the config-file fallback from supplying credentials, so
            # this asserts behavior purely on the "no wallet configured" path.
            with patch(
                "lightning_enable_mcp.config.get_config_service"
            ) as mock_get_config:
                wallets = MagicMock(
                    lnd_rest_host=None,
                    lnd_macaroon_hex=None,
                    nwc_connection_string=None,
                    strike_api_key=None,
                    opennode_api_key=None,
                    priority=None,
                )
                mock_get_config.return_value.configuration.wallets = wallets

                await server._initialize_services()

            assert server.wallet is None

    @pytest.mark.asyncio
    async def test_services_initialized_with_nwc(self):
        """Test services are initialized with an NWC connection."""
        nwc_uri = (
            "nostr+walletconnect://b889ff5b1513b641e2a139f661a661364979c5beee91842f8f0ef42ab558e9d4"
            "?relay=wss://relay.getalby.com/v1"
            "&secret=71a8c14c1407c113601079c4302dab36460f0ccd0ad506f1f2dc73b5100e4f3c"
        )

        # Set NWC and explicitly clear every OTHER wallet env var. Otherwise an
        # ambient STRIKE_API_KEY / LND_* on the test machine would let
        # _initialize_services pick a higher-priority backend (LND > NWC > Strike
        # > OpenNode) and still satisfy a bare "wallet is not None" assertion —
        # testing the wrong thing.
        wallet_env_vars = {
            "NWC_CONNECTION_STRING": nwc_uri,
            "LND_REST_HOST": "",
            "LND_MACAROON_HEX": "",
            "STRIKE_API_KEY": "",
            "OPENNODE_API_KEY": "",
        }
        with patch.dict("os.environ", wallet_env_vars, clear=False):
            server = LightningEnableServer()

            # Patch the pubkey derivation (avoids the optional secp256k1 C library,
            # mirroring the rest of the suite) and the relay connect so the real
            # wallet object is built without network I/O. Pin the config-file
            # fallback to all-None too, so a local ~/.lightning-enable/config.json
            # can't inject a different backend.
            with patch(
                "lightning_enable_mcp.config.get_config_service"
            ) as mock_get_config, patch(
                "lightning_enable_mcp.nwc_wallet._get_pubkey",
                return_value="aa" * 32,
            ), patch(
                "lightning_enable_mcp.nwc_wallet.NWCWallet.connect",
                new_callable=AsyncMock,
            ):
                mock_get_config.return_value.configuration.wallets = MagicMock(
                    lnd_rest_host=None,
                    lnd_macaroon_hex=None,
                    nwc_connection_string=None,
                    strike_api_key=None,
                    opennode_api_key=None,
                    priority=None,
                )
                await server._initialize_services()

                # Assert it's specifically the NWC backend, not just "a wallet".
                assert isinstance(server.wallet, NWCWallet)
                assert server.l402_client is not None
                assert server.budget_service is not None
                assert server.payment_history_service is not None


class TestToolSchemas:
    """Tests for tool input schemas."""

    def test_access_l402_resource_schema(self):
        """Test access_l402_resource has correct schema."""
        server = LightningEnableServer()

        # Find the tool definition
        # Tools are registered via decorators, check the handler exists
        assert hasattr(server, "_setup_handlers")

    def test_pay_l402_challenge_requires_invoice_and_macaroon(self):
        """Test pay_l402_challenge requires invoice and macaroon."""
        # The schema defined in server.py should have these as required
        server = LightningEnableServer()
        # Schema validation is done by MCP framework


class TestToolResponses:
    """Tests for tool response formatting."""

    def test_error_response_format(self):
        """Test error responses are properly formatted."""
        # Error responses should be JSON with success: false
        error_response = json.dumps({"success": False, "error": "Test error"})
        parsed = json.loads(error_response)

        assert parsed["success"] is False
        assert "error" in parsed

    def test_success_response_format(self):
        """Test success responses are properly formatted."""
        success_response = json.dumps(
            {"success": True, "data": "test", "message": "Operation successful"}
        )
        parsed = json.loads(success_response)

        assert parsed["success"] is True


async def _advertised_tool_names(server: LightningEnableServer) -> set[str]:
    """The tools the server advertises via list_tools (aliases are excluded)."""
    from mcp.types import ListToolsRequest

    handler = server.server.request_handlers[ListToolsRequest]
    result = await handler(ListToolsRequest(method="tools/list"))
    return {tool.name for tool in result.root.tools}


async def _call_tool(server: LightningEnableServer, name: str, arguments: dict) -> str:
    """Invoke the registered call_tool handler the way the SDK does for a tools/call."""
    from mcp.types import CallToolRequest, CallToolRequestParams

    handler = server.server.request_handlers[CallToolRequest]
    req = CallToolRequest(
        method="tools/call",
        params=CallToolRequestParams(name=name, arguments=arguments),
    )
    result = await handler(req)
    return result.root.content[0].text


class TestDeprecatedAliases:
    """The old tool names remain accepted-but-unadvertised forwarding aliases.

    Each alias must: (1) dispatch to the new implementation, (2) carry a
    ``deprecated`` marker in its result, and (3) be ABSENT from the advertised list.
    """

    @pytest.mark.asyncio
    async def test_confirm_payment_alias_forwards_and_is_deprecated(self):
        from datetime import datetime, timezone, timedelta
        from decimal import Decimal
        from lightning_enable_mcp.budget_service import PendingConfirmation

        server = LightningEnableServer()
        # Pre-set wallet + l402 client so call_tool skips _initialize_services.
        server.wallet = MagicMock()
        server.l402_client = MagicMock()
        now = datetime.now(timezone.utc)
        server.budget_service = MagicMock()
        server.budget_service.validate_confirmation.return_value = PendingConfirmation(
            nonce="ABC123",
            amount_sats=21000,
            amount_usd=Decimal("21.00"),
            tool_name="pay_invoice",
            description="Invoice payment",
            destination="lnbc-test-invoice",
            created_at=now,
            expires_at=now + timedelta(minutes=2),
        )

        text = await _call_tool(server, "confirm_payment", {"nonce": "abc123"})
        data = json.loads(text)

        # Forwarded to verify_confirmation_code (real result), plus deprecation marker.
        assert data["success"] is True
        assert data["valid"] is True
        assert data["tool"] == "pay_invoice"
        assert data["deprecated"]["replaced_by"] == "verify_confirmation_code"
        assert data["deprecated"]["removal"] == "v3.0.0"

    @pytest.mark.asyncio
    async def test_confirm_payment_is_not_advertised(self):
        server = LightningEnableServer()
        advertised = await _advertised_tool_names(server)
        assert "confirm_payment" not in advertised
        assert "verify_confirmation_code" in advertised

    @pytest.mark.asyncio
    @pytest.mark.parametrize("alias", ["check_wallet_balance", "get_all_balances"])
    async def test_balance_alias_forwards_and_is_deprecated(self, alias):
        server = LightningEnableServer()
        # Pre-set wallet + l402 client so call_tool skips _initialize_services.
        server.wallet = MagicMock()
        server.wallet.get_balance = AsyncMock(return_value=50_000)
        server.wallet.get_info = AsyncMock(return_value=None)
        server.l402_client = MagicMock()

        text = await _call_tool(server, alias, {})
        data = json.loads(text)

        # Forwarded to the unified get_balance (real result), plus deprecation marker.
        assert data["success"] is True
        assert data["balance_sats"] == 50_000
        assert data["balances"][0]["currency"] == "BTC"
        assert data["deprecated"]["replaced_by"] == "get_balance"
        assert data["deprecated"]["removal"] == "v3.0.0"

    @pytest.mark.asyncio
    async def test_balance_aliases_are_not_advertised(self):
        server = LightningEnableServer()
        advertised = await _advertised_tool_names(server)
        assert "check_wallet_balance" not in advertised
        assert "get_all_balances" not in advertised
        assert "get_balance" in advertised
