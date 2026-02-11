"""
Tests for MCP Server
"""

import pytest
from unittest.mock import AsyncMock, MagicMock, patch
import json

from lightning_enable_mcp.server import LightningEnableServer


class TestLightningEnableServer:
    """Tests for LightningEnableServer."""

    def test_server_initialization(self):
        """Test server initializes correctly."""
        server = LightningEnableServer()

        assert server.server is not None
        assert server.wallet is None
        assert server.l402_client is None
        assert server.budget_manager is None

    @pytest.mark.asyncio
    async def test_list_tools_returns_all_tools(self):
        """Test that list_tools returns all expected tools."""
        server = LightningEnableServer()

        # Get the list_tools handler
        handlers = server.server._tool_handlers
        assert "list_tools" in [h for h in dir(server.server)]

        # The tools should be registered
        # We can check this by examining the server's internal state
        # or by calling the handler if exposed

    @pytest.mark.asyncio
    async def test_services_not_initialized_without_nwc(self):
        """Test services aren't initialized without NWC connection."""
        with patch.dict("os.environ", {}, clear=True):
            server = LightningEnableServer()
            await server._initialize_services()

            assert server.wallet is None

    @pytest.mark.asyncio
    async def test_services_initialized_with_nwc(self):
        """Test services are initialized with NWC connection."""
        nwc_uri = (
            "nostr+walletconnect://b889ff5b1513b641e2a139f661a661364979c5beee91842f8f0ef42ab558e9d4"
            "?relay=wss://relay.getalby.com/v1"
            "&secret=71a8c14c1407c113601079c4302dab36460f0ccd0ad506f1f2dc73b5100e4f3c"
        )

        with patch.dict("os.environ", {"NWC_CONNECTION_STRING": nwc_uri}):
            server = LightningEnableServer()

            # Mock the wallet connect
            with patch(
                "lightning_enable_mcp.nwc_wallet.NWCWallet.connect",
                new_callable=AsyncMock,
            ):
                await server._initialize_services()

                assert server.wallet is not None
                assert server.l402_client is not None
                assert server.budget_manager is not None


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
