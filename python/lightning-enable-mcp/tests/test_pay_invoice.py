"""
Tests for Pay Invoice Tool
"""

import json
import pytest
from datetime import datetime, timezone, timedelta
from decimal import Decimal
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.tools.pay_invoice import pay_invoice
from lightning_enable_mcp.budget import BudgetManager, BudgetExceededError
from lightning_enable_mcp.budget_service import PendingConfirmation
from lightning_enable_mcp.config import ApprovalLevel


class TestPayInvoice:
    """Tests for pay_invoice tool."""

    @pytest.mark.asyncio
    async def test_empty_invoice_returns_error(self):
        """Test that empty invoice returns an error."""
        result = await pay_invoice(invoice="", wallet=MagicMock())
        data = json.loads(result)

        assert data["success"] is False
        assert "Invoice is required" in data["error"]

    @pytest.mark.asyncio
    async def test_none_invoice_returns_error(self):
        """Test that None invoice returns an error."""
        result = await pay_invoice(invoice=None, wallet=MagicMock())
        data = json.loads(result)

        assert data["success"] is False
        assert "Invoice is required" in data["error"]

    @pytest.mark.asyncio
    async def test_whitespace_invoice_returns_error(self):
        """Test that whitespace-only invoice returns an error."""
        result = await pay_invoice(invoice="   ", wallet=MagicMock())
        data = json.loads(result)

        assert data["success"] is False
        assert "Invoice is required" in data["error"]

    @pytest.mark.asyncio
    async def test_invalid_prefix_returns_error(self):
        """Test that invoice without valid prefix returns an error."""
        result = await pay_invoice(
            invoice="invalid_invoice_format",
            wallet=MagicMock()
        )
        data = json.loads(result)

        assert data["success"] is False
        assert "Invalid invoice format" in data["error"]
        assert "lnbc" in data["error"]
        assert "lntb" in data["error"]

    @pytest.mark.asyncio
    async def test_no_wallet_returns_error(self):
        """Test that missing wallet returns an error."""
        result = await pay_invoice(invoice="lnbc100n...", wallet=None)
        data = json.loads(result)

        assert data["success"] is False
        assert "Wallet not configured" in data["error"]

    @pytest.mark.asyncio
    async def test_exceeds_budget_returns_error(self):
        """Test that exceeding budget returns an error."""
        # Create a budget manager with low limits
        budget_manager = BudgetManager(max_per_request=100, max_per_session=100)
        budget_manager.session_spent = 90  # Almost exhausted

        # Create a mock wallet
        wallet = MagicMock()

        # Try to pay 1000 sats (exceeds remaining budget of 10)
        result = await pay_invoice(
            invoice="lnbc1000n1...",
            max_sats=1000,
            wallet=wallet,
            budget_manager=budget_manager,
        )
        data = json.loads(result)

        assert data["success"] is False
        assert "budget" in data or "limit" in data["error"].lower()

    @pytest.mark.asyncio
    async def test_exceeds_per_request_limit_returns_error(self):
        """Test that exceeding per-request limit returns an error."""
        # Create a budget manager with low per-request limit
        budget_manager = BudgetManager(max_per_request=100, max_per_session=10000)

        wallet = MagicMock()

        # Try to pay 1000 sats (exceeds per-request limit of 100)
        result = await pay_invoice(
            invoice="lnbc1000n1...",
            max_sats=1000,
            wallet=wallet,
            budget_manager=budget_manager,
        )
        data = json.loads(result)

        assert data["success"] is False
        assert "per-request limit" in data["error"] or "budget" in data

    @pytest.mark.asyncio
    async def test_successful_payment_returns_preimage(self):
        """Test that successful payment returns the preimage."""
        # Create a mock wallet that returns a preimage
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="abc123preimage")

        budget_manager = BudgetManager()

        result = await pay_invoice(
            invoice="lnbc100n1pj9npjpp5abcdef...",
            max_sats=1000,
            wallet=wallet,
            budget_manager=budget_manager,
        )
        data = json.loads(result)

        assert data["success"] is True
        assert data["preimage"] == "abc123preimage"
        assert "Payment successful" in data["message"]

    @pytest.mark.asyncio
    async def test_successful_payment_records_to_budget(self):
        """Test that successful payment is recorded in budget manager."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage123")

        budget_manager = BudgetManager()

        await pay_invoice(
            invoice="lnbc100n1...",
            max_sats=500,
            wallet=wallet,
            budget_manager=budget_manager,
        )

        # Verify payment was recorded
        assert len(budget_manager.payments) == 1
        assert budget_manager.payments[0].status == "success"
        assert budget_manager.session_spent == 500

    @pytest.mark.asyncio
    async def test_mainnet_invoice_accepted(self):
        """Test that mainnet (lnbc) invoices are accepted."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")

        result = await pay_invoice(
            invoice="lnbc100n1pj9npjpp5...",
            wallet=wallet,
        )
        data = json.loads(result)

        assert data["success"] is True
        wallet.pay_invoice.assert_called_once()

    @pytest.mark.asyncio
    async def test_testnet_invoice_accepted(self):
        """Test that testnet (lntb) invoices are accepted."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")

        result = await pay_invoice(
            invoice="lntb100n1pj9npjpp5...",
            wallet=wallet,
        )
        data = json.loads(result)

        assert data["success"] is True
        wallet.pay_invoice.assert_called_once()

    @pytest.mark.asyncio
    async def test_invoice_normalized_to_lowercase(self):
        """Test that invoice is normalized to lowercase before payment."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")

        await pay_invoice(
            invoice="LNBC100N1PJ9NPJPP5...",
            wallet=wallet,
        )

        # Verify the normalized invoice was passed
        call_args = wallet.pay_invoice.call_args[0][0]
        assert call_args == "lnbc100n1pj9npjpp5..."
        assert call_args.islower()

    @pytest.mark.asyncio
    async def test_invoice_trimmed(self):
        """Test that invoice whitespace is trimmed."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")

        await pay_invoice(
            invoice="  lnbc100n1pj9npjpp5...  ",
            wallet=wallet,
        )

        call_args = wallet.pay_invoice.call_args[0][0]
        assert not call_args.startswith(" ")
        assert not call_args.endswith(" ")

    @pytest.mark.asyncio
    async def test_payment_failure_returns_error(self):
        """Test that wallet payment failure is handled."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(side_effect=Exception("Payment failed: insufficient funds"))

        result = await pay_invoice(
            invoice="lnbc100n1...",
            wallet=wallet,
        )
        data = json.loads(result)

        assert data["success"] is False
        assert "insufficient funds" in data["error"] or "Payment failed" in data["error"]

    @pytest.mark.asyncio
    async def test_no_preimage_returns_error(self):
        """Test that missing preimage is handled as failure."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value=None)

        budget_manager = BudgetManager()

        result = await pay_invoice(
            invoice="lnbc100n1...",
            wallet=wallet,
            budget_manager=budget_manager,
        )
        data = json.loads(result)

        assert data["success"] is False
        assert "no preimage" in data["error"].lower()

    @pytest.mark.asyncio
    async def test_empty_preimage_returns_error(self):
        """Test that empty preimage is handled as failure."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="")

        budget_manager = BudgetManager()

        result = await pay_invoice(
            invoice="lnbc100n1...",
            wallet=wallet,
            budget_manager=budget_manager,
        )
        data = json.loads(result)

        assert data["success"] is False
        assert "no preimage" in data["error"].lower()

    @pytest.mark.asyncio
    async def test_default_max_sats(self):
        """Test that default max_sats is 1000."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")

        # Budget manager with per-request limit lower than default
        budget_manager = BudgetManager(max_per_request=500, max_per_session=10000)

        result = await pay_invoice(
            invoice="lnbc100n1...",
            wallet=wallet,
            budget_manager=budget_manager,
        )
        data = json.loads(result)

        # Default max_sats of 1000 should fail against 500 per-request limit
        assert data["success"] is False

    @pytest.mark.asyncio
    async def test_custom_max_sats(self):
        """Test that custom max_sats is respected."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")

        budget_manager = BudgetManager(max_per_request=1000, max_per_session=10000)

        result = await pay_invoice(
            invoice="lnbc100n1...",
            max_sats=100,  # Custom lower max
            wallet=wallet,
            budget_manager=budget_manager,
        )
        data = json.loads(result)

        assert data["success"] is True
        # Verify the lower max was recorded
        assert budget_manager.session_spent == 100

    @pytest.mark.asyncio
    async def test_failed_payment_recorded_in_budget(self):
        """Test that failed payments are recorded with failed status."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value=None)  # Simulate failure

        budget_manager = BudgetManager()

        await pay_invoice(
            invoice="lnbc100n1...",
            max_sats=100,
            wallet=wallet,
            budget_manager=budget_manager,
        )

        # Verify failure was recorded
        assert len(budget_manager.payments) == 1
        assert budget_manager.payments[0].status == "failed"
        # Failed payments should NOT add to session spent
        assert budget_manager.session_spent == 0

    @pytest.mark.asyncio
    async def test_result_includes_truncated_invoice(self):
        """Test that result includes truncated invoice for reference."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")

        long_invoice = "lnbc100n1pj9npjpp5" + "x" * 100

        result = await pay_invoice(
            invoice=long_invoice,
            wallet=wallet,
        )
        data = json.loads(result)

        assert data["success"] is True
        assert "invoice" in data
        assert data["invoice"]["paid"].endswith("...")
        assert len(data["invoice"]["paid"]) == 33  # 30 chars + "..."

    @pytest.mark.asyncio
    async def test_works_without_budget_manager(self):
        """Test that pay_invoice works without a budget manager."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")

        result = await pay_invoice(
            invoice="lnbc100n1...",
            wallet=wallet,
            budget_manager=None,
        )
        data = json.loads(result)

        assert data["success"] is True
        assert data["preimage"] == "preimage"


def _confirming_budget(code: str = "ABC123", sats: int = 50000):
    """A BudgetService mock whose check returns requires_confirmation=True."""
    budget = MagicMock()
    approval = MagicMock()
    approval.level = ApprovalLevel.FORM_CONFIRM
    approval.requires_confirmation = True
    approval.amount_usd = Decimal("50.00")
    approval.denial_reason = None
    approval.remaining_session_budget_usd = Decimal("100.00")
    budget.check_approval_level = AsyncMock(return_value=approval)
    now = datetime.now(timezone.utc)
    pc = PendingConfirmation(
        nonce=code, amount_sats=sats, amount_usd=Decimal("50.00"),
        tool_name="pay_invoice", description="lnbc...",
        created_at=now, expires_at=now + timedelta(minutes=2),
    )
    budget.create_pending_confirmation = MagicMock(return_value=pc)
    budget.validate_and_consume_confirmation = MagicMock(return_value=pc)
    budget.record_spend = MagicMock()
    budget.record_payment_time = MagicMock()
    budget.get_status = MagicMock(return_value={
        "session": {"spentSats": sats, "spentUsd": 50.0, "remainingUsd": 50.0, "requestCount": 1}
    })
    return budget


class TestPayInvoiceOutOfBandConfirmation:
    """Funds-safety: above-threshold payments need a human-relayed, out-of-band code."""

    @pytest.mark.asyncio
    async def test_above_threshold_requests_confirmation_and_does_not_leak_code(self):
        """The confirmation code must NEVER appear in the model-visible tool result."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")
        budget = _confirming_budget(code="ZZZ999")

        result = await pay_invoice(
            invoice="lnbc500u1pj9npjpp5...",
            max_sats=50000,
            wallet=wallet,
            budget_service=budget,
        )
        data = json.loads(result)

        assert data["success"] is False
        assert data.get("requiresConfirmation") is True
        # The actual code printed to stderr must NOT be in the result, anywhere.
        assert "ZZZ999" not in result
        assert "nonce" not in data
        # No payment happened on the request-confirmation path.
        wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_human_relayed_code_unlocks_the_payment(self):
        """With a valid human-relayed code (bound to amount+tool), the payment proceeds."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage123")
        budget = _confirming_budget(code="ABC123", sats=50000)

        result = await pay_invoice(
            invoice="lnbc500u1pj9npjpp5...",
            max_sats=50000,
            wallet=wallet,
            budget_service=budget,
            confirmation_nonce="abc123",  # lowercase from human — tool upcases it
        )
        data = json.loads(result)

        assert data["success"] is True
        assert data["preimage"] == "preimage123"
        budget.validate_and_consume_confirmation.assert_called_once_with("ABC123", 50000, "pay_invoice")
        wallet.pay_invoice.assert_called_once()

    @pytest.mark.asyncio
    async def test_bad_code_is_rejected_and_not_paid(self):
        """A code that fails validation (wrong/expired/replayed) must refuse to pay."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")
        budget = _confirming_budget()
        budget.validate_and_consume_confirmation = MagicMock(return_value=None)  # rejected

        result = await pay_invoice(
            invoice="lnbc500u1pj9npjpp5...",
            max_sats=50000,
            wallet=wallet,
            budget_service=budget,
            confirmation_nonce="WRONG1",
        )
        data = json.loads(result)

        assert data["success"] is False
        assert "amount and tool" in data["error"]
        wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_legacy_budget_manager_fails_closed_above_threshold(self):
        """The legacy BudgetManager has no confirmation flow — it must refuse, not self-approve."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")
        budget_manager = BudgetManager(max_per_request=100000, max_per_session=1000000)

        result = await pay_invoice(
            invoice="lnbc500u1pj9npjpp5...",
            max_sats=50000,  # above the 1000-sat auto-approve floor
            wallet=wallet,
            budget_manager=budget_manager,
        )
        data = json.loads(result)

        assert data["success"] is False
        assert "auto-approve threshold" in data["error"]
        wallet.pay_invoice.assert_not_called()
