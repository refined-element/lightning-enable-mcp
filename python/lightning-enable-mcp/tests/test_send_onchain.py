"""
Tests for send_onchain tool
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.tools.send_onchain import send_onchain
from lightning_enable_mcp.strike_wallet import StrikeWallet


def _make_strike_wallet_mock(**kwargs):
    """Create a mock that passes isinstance(mock, StrikeWallet) checks."""
    mock = AsyncMock(spec=StrikeWallet, **kwargs)
    return mock


class TestSendOnchain:
    """Tests for send_onchain tool."""

    @pytest.mark.asyncio
    async def test_missing_address_returns_error(self):
        """Test that empty address returns an error."""
        result = await send_onchain(address="", amount_sats=1000)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Bitcoin address is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_whitespace_address_returns_error(self):
        """Test that whitespace-only address returns an error."""
        result = await send_onchain(address="   ", amount_sats=1000)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Bitcoin address is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_zero_amount_returns_error(self):
        """Test that zero amount returns an error."""
        result = await send_onchain(
            address="bc1qtest123", amount_sats=0
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Amount must be greater than 0" in parsed["error"]

    @pytest.mark.asyncio
    async def test_negative_amount_returns_error(self):
        """Test that negative amount returns an error."""
        result = await send_onchain(
            address="bc1qtest123", amount_sats=-500
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Amount must be greater than 0" in parsed["error"]

    @pytest.mark.asyncio
    async def test_no_wallet_returns_error(self):
        """Test that missing wallet returns an error."""
        result = await send_onchain(
            address="bc1qtest123", amount_sats=1000, wallet=None
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Wallet not configured" in parsed["error"]

    @pytest.mark.asyncio
    async def test_non_strike_wallet_returns_error(self):
        """Test that non-Strike wallet returns unsupported error."""
        wallet = AsyncMock()  # Not spec'd as StrikeWallet

        result = await send_onchain(
            address="bc1qtest123", amount_sats=1000, wallet=wallet
        )
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "does not support on-chain" in parsed["error"]

    @pytest.mark.asyncio
    async def test_successful_completed_payment(self):
        """Test successful completed on-chain payment."""
        wallet = _make_strike_wallet_mock()

        onchain_result = MagicMock()
        onchain_result.success = True
        onchain_result.payment_id = "pay-001"
        onchain_result.txid = "txid-abc123"
        onchain_result.state = "COMPLETED"
        onchain_result.amount_sats = 50000
        onchain_result.fee_sats = 500

        wallet.send_onchain = AsyncMock(return_value=onchain_result)

        result = await send_onchain(
            address="bc1qtest123", amount_sats=50000, wallet=wallet
        )
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["provider"] == "Strike"
        assert parsed["payment"]["id"] == "pay-001"
        assert parsed["payment"]["txId"] == "txid-abc123"
        assert parsed["payment"]["state"] == "COMPLETED"
        assert parsed["payment"]["feeSats"] == 500
        assert "sent to" in parsed["message"]

    @pytest.mark.asyncio
    async def test_pending_payment(self):
        """Test on-chain payment in pending state."""
        wallet = _make_strike_wallet_mock()

        onchain_result = MagicMock()
        onchain_result.success = True
        onchain_result.payment_id = "pay-002"
        onchain_result.txid = None
        onchain_result.state = "PENDING"
        onchain_result.amount_sats = 10000
        onchain_result.fee_sats = 200

        wallet.send_onchain = AsyncMock(return_value=onchain_result)

        result = await send_onchain(
            address="bc1qtest456", amount_sats=10000, wallet=wallet
        )
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["payment"]["state"] == "PENDING"
        assert "initiated" in parsed["message"].lower()

    @pytest.mark.asyncio
    async def test_failed_payment(self):
        """Test failed on-chain payment from wallet."""
        wallet = _make_strike_wallet_mock()

        onchain_result = MagicMock()
        onchain_result.success = False
        onchain_result.error_message = "Insufficient funds"
        onchain_result.error_code = "INSUFFICIENT_FUNDS"

        wallet.send_onchain = AsyncMock(return_value=onchain_result)

        result = await send_onchain(
            address="bc1qtest123", amount_sats=50000, wallet=wallet
        )
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "Insufficient funds" in parsed["error"]

    @pytest.mark.asyncio
    async def test_exception_handling(self):
        """Test that exceptions are caught and returned as errors."""
        wallet = _make_strike_wallet_mock()
        wallet.send_onchain = AsyncMock(
            side_effect=Exception("Timeout")
        )

        result = await send_onchain(
            address="bc1qtest123", amount_sats=1000, wallet=wallet
        )
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "Timeout" in parsed["error"]
