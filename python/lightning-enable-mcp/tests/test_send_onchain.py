"""
Tests for send_onchain tool.

Funds-safety (PY-FAILOPEN / PY-C1 / PY-C2):
  - the address is validated before any send (a real mainnet address is required),
  - on-chain sends ALWAYS require explicit confirmation (irreversible),
  - the budget check fails CLOSED (a budget error refuses the send).
"""

import json
import pytest
from datetime import datetime, timezone, timedelta
from decimal import Decimal
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.tools.send_onchain import send_onchain
from lightning_enable_mcp.strike_wallet import StrikeWallet
from lightning_enable_mcp.budget_service import PendingConfirmation
from lightning_enable_mcp.config import ApprovalLevel

# A real BIP173 mainnet P2WPKH address (passes validation).
VALID_ADDR = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4"


def _make_strike_wallet_mock(**kwargs):
    """Create a mock that passes isinstance(mock, StrikeWallet) checks."""
    mock = AsyncMock(spec=StrikeWallet, **kwargs)
    return mock


def _approving_budget(code: str = "ABC123"):
    """A budget service mock that approves (non-DENY) and accepts the given code.

    Used by tests that need to reach the actual send: send_onchain ALWAYS requires
    out-of-band confirmation and fails closed without a budget service, so a 'send'
    test must supply one and pass a confirmation_nonce it will accept.
    """
    budget = MagicMock()
    approval = MagicMock()
    approval.level = ApprovalLevel.AUTO_APPROVE
    approval.amount_usd = Decimal("5.00")
    approval.denial_reason = None
    budget.check_approval_level = AsyncMock(return_value=approval)
    now = datetime.now(timezone.utc)
    pc = PendingConfirmation(
        nonce=code, amount_sats=0, amount_usd=Decimal("5.00"),
        tool_name="send_onchain", description=VALID_ADDR,
        created_at=now, expires_at=now + timedelta(minutes=2),
    )
    budget.create_pending_confirmation = MagicMock(return_value=pc)
    budget.validate_and_consume_confirmation = MagicMock(return_value=pc)
    budget.record_spend = MagicMock()
    budget.record_payment_time = MagicMock()
    return budget


class TestSendOnchain:
    """Tests for send_onchain tool."""

    @pytest.mark.asyncio
    async def test_missing_address_returns_error(self):
        result = await send_onchain(address="", amount_sats=1000)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Bitcoin address is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_whitespace_address_returns_error(self):
        result = await send_onchain(address="   ", amount_sats=1000)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Bitcoin address is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_invalid_address_is_rejected_and_not_sent(self):
        """PY-C2: an invalid/garbage address must be rejected before any send."""
        wallet = _make_strike_wallet_mock()
        wallet.send_onchain = AsyncMock()

        result = await send_onchain(
            address="bc1qtest123", amount_sats=1000, wallet=wallet
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Invalid Bitcoin address" in parsed["error"]
        wallet.send_onchain.assert_not_called()

    @pytest.mark.asyncio
    async def test_zero_amount_returns_error(self):
        result = await send_onchain(address=VALID_ADDR, amount_sats=0)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Amount must be greater than 0" in parsed["error"]

    @pytest.mark.asyncio
    async def test_negative_amount_returns_error(self):
        result = await send_onchain(address=VALID_ADDR, amount_sats=-500)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Amount must be greater than 0" in parsed["error"]

    @pytest.mark.asyncio
    async def test_no_wallet_returns_error(self):
        result = await send_onchain(address=VALID_ADDR, amount_sats=1000, wallet=None)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Wallet not configured" in parsed["error"]

    @pytest.mark.asyncio
    async def test_non_strike_wallet_returns_error(self):
        wallet = AsyncMock()  # Not spec'd as StrikeWallet/LndWallet
        result = await send_onchain(address=VALID_ADDR, amount_sats=1000, wallet=wallet)
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "does not support on-chain" in parsed["error"]

    @pytest.mark.asyncio
    async def test_requires_confirmation_without_nonce(self):
        """PY-C1: on-chain is irreversible — first call must require confirmation, not send."""
        wallet = _make_strike_wallet_mock()
        wallet.send_onchain = AsyncMock()

        result = await send_onchain(
            address=VALID_ADDR, amount_sats=50000, wallet=wallet,
            budget_service=_approving_budget(),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert parsed.get("requiresConfirmation") is True
        # The confirmation code must NOT appear anywhere in the model-visible result.
        assert "ABC123" not in result
        assert "nonce" not in parsed
        wallet.send_onchain.assert_not_called()

    @pytest.mark.asyncio
    async def test_budget_check_exception_fails_closed(self):
        """PY-FAILOPEN: a budget-check error must REFUSE the send, not pay anyway."""
        wallet = _make_strike_wallet_mock()
        wallet.send_onchain = AsyncMock()
        budget = MagicMock()
        budget.check_approval_level = AsyncMock(side_effect=Exception("price service down"))

        result = await send_onchain(
            address=VALID_ADDR, amount_sats=50000,
            wallet=wallet, budget_service=budget,
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "fail-closed" in parsed["error"].lower()
        wallet.send_onchain.assert_not_called()

    @pytest.mark.asyncio
    async def test_successful_completed_payment(self):
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
            address=VALID_ADDR, amount_sats=50000, wallet=wallet,
            budget_service=_approving_budget(), confirmation_nonce="ABC123",
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
            address=VALID_ADDR, amount_sats=10000, wallet=wallet,
            budget_service=_approving_budget(), confirmation_nonce="ABC123",
        )
        parsed = json.loads(result)
        assert parsed["success"] is True
        assert parsed["payment"]["state"] == "PENDING"
        assert "initiated" in parsed["message"].lower()

    @pytest.mark.asyncio
    async def test_failed_payment(self):
        wallet = _make_strike_wallet_mock()
        onchain_result = MagicMock()
        onchain_result.success = False
        onchain_result.error_message = "Insufficient funds"
        onchain_result.error_code = "INSUFFICIENT_FUNDS"
        wallet.send_onchain = AsyncMock(return_value=onchain_result)

        result = await send_onchain(
            address=VALID_ADDR, amount_sats=50000, wallet=wallet,
            budget_service=_approving_budget(), confirmation_nonce="ABC123",
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Insufficient funds" in parsed["error"]

    @pytest.mark.asyncio
    async def test_exception_handling(self):
        wallet = _make_strike_wallet_mock()
        wallet.send_onchain = AsyncMock(side_effect=Exception("Timeout"))

        result = await send_onchain(
            address=VALID_ADDR, amount_sats=1000, wallet=wallet,
            budget_service=_approving_budget(), confirmation_nonce="ABC123",
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Timeout" in parsed["error"]
