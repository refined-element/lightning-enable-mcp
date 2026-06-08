"""
Tests for Pay Invoice Tool

The legacy BudgetManager path was removed: BudgetService is the single source of
truth for spending limits + the out-of-band confirmation flow, and the separate
PaymentHistoryService records the audit trail (never the preimage).
"""

import json
import pytest
from datetime import datetime, timezone, timedelta
from decimal import Decimal
from unittest.mock import AsyncMock, MagicMock, patch

from lightning_enable_mcp.tools.pay_invoice import pay_invoice
from lightning_enable_mcp.budget_service import PendingConfirmation
from lightning_enable_mcp.payment_history_service import PaymentHistoryService
from lightning_enable_mcp.config import ApprovalLevel


# pay_invoice now decodes the BOLT11 amount and budgets against IT (not max_sats).
# The placeholder invoices in these tests don't really decode, so patch decode_bolt11
# to a per-test amount; a test that cares about the paid amount sets _DECODE_SATS["v"].
_DECODE_SATS = {"v": 10}


@pytest.fixture(autouse=True)
def _patch_decode():
    def fake_decode(_invoice):
        m = MagicMock()
        m.amount_msat = _DECODE_SATS["v"] * 1000
        m.amount = _DECODE_SATS["v"]
        return m

    with patch("lightning_enable_mcp.tools.pay_invoice.decode_bolt11", side_effect=fake_decode):
        _DECODE_SATS["v"] = 10
        yield


def _approving_budget():
    """A BudgetService mock that auto-approves (no confirmation required)."""
    budget = MagicMock()
    approval = MagicMock()
    approval.level = ApprovalLevel.AUTO_APPROVE
    approval.requires_confirmation = False
    approval.amount_usd = Decimal("0.10")
    approval.denial_reason = None
    approval.remaining_session_budget_usd = Decimal("100.00")
    budget.check_approval_level = AsyncMock(return_value=approval)
    budget.record_spend = MagicMock()
    budget.record_payment_time = MagicMock()
    budget.get_status = MagicMock(return_value={
        "session": {"spentSats": 500, "spentUsd": 0.5, "remainingUsd": 99.5, "requestCount": 1}
    })
    return budget


def _denying_budget(reason="exceeds limit"):
    budget = MagicMock()
    approval = MagicMock()
    approval.level = ApprovalLevel.DENY
    approval.requires_confirmation = False
    approval.amount_usd = Decimal("10.00")
    approval.denial_reason = reason
    approval.remaining_session_budget_usd = Decimal("0.00")
    budget.check_approval_level = AsyncMock(return_value=approval)
    return budget


class TestPayInvoice:
    """Tests for pay_invoice tool."""

    @pytest.mark.asyncio
    async def test_empty_invoice_returns_error(self):
        result = await pay_invoice(invoice="", wallet=MagicMock())
        data = json.loads(result)
        assert data["success"] is False
        assert "Invoice is required" in data["error"]

    @pytest.mark.asyncio
    async def test_none_invoice_returns_error(self):
        result = await pay_invoice(invoice=None, wallet=MagicMock())
        data = json.loads(result)
        assert data["success"] is False
        assert "Invoice is required" in data["error"]

    @pytest.mark.asyncio
    async def test_whitespace_invoice_returns_error(self):
        result = await pay_invoice(invoice="   ", wallet=MagicMock())
        data = json.loads(result)
        assert data["success"] is False
        assert "Invoice is required" in data["error"]

    @pytest.mark.asyncio
    async def test_invalid_prefix_returns_error(self):
        result = await pay_invoice(invoice="invalid_invoice_format", wallet=MagicMock())
        data = json.loads(result)
        assert data["success"] is False
        assert "Invalid invoice format" in data["error"]
        assert "lnbc" in data["error"]
        assert "lntb" in data["error"]

    @pytest.mark.asyncio
    async def test_no_wallet_returns_error(self):
        result = await pay_invoice(invoice="lnbc100n...", wallet=None)
        data = json.loads(result)
        assert data["success"] is False
        assert "Wallet not configured" in data["error"]

    @pytest.mark.asyncio
    async def test_budget_deny_returns_error(self):
        """A BudgetService DENY refuses the payment and does not pay."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")

        result = await pay_invoice(
            invoice="lnbc1000n1...",
            max_sats=1000,
            wallet=wallet,
            budget_service=_denying_budget("Payment exceeds session limit"),
        )
        data = json.loads(result)

        assert data["success"] is False
        assert "denied by budget policy" in data["error"]
        assert "session limit" in data["denialReason"]
        wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_successful_payment_returns_preimage(self):
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="abc123preimage")

        result = await pay_invoice(
            invoice="lnbc100n1pj9npjpp5abcdef...",
            max_sats=1000,
            wallet=wallet,
            budget_service=_approving_budget(),
        )
        data = json.loads(result)

        assert data["success"] is True
        assert data["preimage"] == "abc123preimage"
        assert "Payment successful" in data["message"]

    @pytest.mark.asyncio
    async def test_successful_payment_recorded_in_history_without_preimage(self):
        """The payment is recorded in history, but the preimage is NEVER stored."""
        _DECODE_SATS["v"] = 500  # decoded invoice amount under test
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="secretpreimage123")
        history = PaymentHistoryService()

        await pay_invoice(
            invoice="lnbc100n1...",
            max_sats=500,
            wallet=wallet,
            budget_service=_approving_budget(),
            payment_history_service=history,
        )

        assert history.total_payments == 1
        records = history.get_history()
        assert records[0].status == "success"
        assert records[0].amount_sats == 500
        # FUNDS-SAFETY: the preimage must not appear anywhere in the record.
        assert "secretpreimage123" not in json.dumps(records[0].to_dict())
        assert not hasattr(records[0], "preimage")

    @pytest.mark.asyncio
    async def test_mainnet_invoice_accepted(self):
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")
        result = await pay_invoice(invoice="lnbc100n1pj9npjpp5...", wallet=wallet)
        data = json.loads(result)
        assert data["success"] is True
        wallet.pay_invoice.assert_called_once()

    @pytest.mark.asyncio
    async def test_testnet_invoice_accepted(self):
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")
        result = await pay_invoice(invoice="lntb100n1pj9npjpp5...", wallet=wallet)
        data = json.loads(result)
        assert data["success"] is True
        wallet.pay_invoice.assert_called_once()

    @pytest.mark.asyncio
    async def test_invoice_normalized_to_lowercase(self):
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")
        await pay_invoice(invoice="LNBC100N1PJ9NPJPP5...", wallet=wallet)
        call_args = wallet.pay_invoice.call_args[0][0]
        assert call_args == "lnbc100n1pj9npjpp5..."
        assert call_args.islower()

    @pytest.mark.asyncio
    async def test_invoice_trimmed(self):
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")
        await pay_invoice(invoice="  lnbc100n1pj9npjpp5...  ", wallet=wallet)
        call_args = wallet.pay_invoice.call_args[0][0]
        assert not call_args.startswith(" ")
        assert not call_args.endswith(" ")

    @pytest.mark.asyncio
    async def test_payment_failure_returns_error(self):
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(side_effect=Exception("Payment failed: insufficient funds"))
        result = await pay_invoice(invoice="lnbc100n1...", wallet=wallet)
        data = json.loads(result)
        assert data["success"] is False
        assert "insufficient funds" in data["error"] or "Payment failed" in data["error"]

    @pytest.mark.asyncio
    async def test_no_preimage_returns_error(self):
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value=None)
        result = await pay_invoice(
            invoice="lnbc100n1...",
            wallet=wallet,
            budget_service=_approving_budget(),
        )
        data = json.loads(result)
        assert data["success"] is False
        assert "no preimage" in data["error"].lower()

    @pytest.mark.asyncio
    async def test_empty_preimage_returns_error(self):
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="")
        result = await pay_invoice(
            invoice="lnbc100n1...",
            wallet=wallet,
            budget_service=_approving_budget(),
        )
        data = json.loads(result)
        assert data["success"] is False
        assert "no preimage" in data["error"].lower()

    @pytest.mark.asyncio
    async def test_failed_payment_recorded_in_history(self):
        """A failed payment is recorded with failed status."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value=None)  # simulate failure
        history = PaymentHistoryService()

        await pay_invoice(
            invoice="lnbc100n1...",
            max_sats=100,
            wallet=wallet,
            budget_service=_approving_budget(),
            payment_history_service=history,
        )

        assert history.total_payments == 1
        assert history.get_history()[0].status == "failed"
        # Failed payments don't count toward spend.
        assert history.total_sats_spent == 0

    @pytest.mark.asyncio
    async def test_result_includes_truncated_invoice(self):
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")
        long_invoice = "lnbc100n1pj9npjpp5" + "x" * 100
        result = await pay_invoice(invoice=long_invoice, wallet=wallet)
        data = json.loads(result)
        assert data["success"] is True
        assert "invoice" in data
        assert data["invoice"]["paid"].endswith("...")
        assert len(data["invoice"]["paid"]) == 33  # 30 chars + "..."

    @pytest.mark.asyncio
    async def test_works_without_budget_service(self):
        """pay_invoice still works (no enforcement) when no budget service is wired."""
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")
        result = await pay_invoice(invoice="lnbc100n1...", wallet=wallet, budget_service=None)
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
        assert "ZZZ999" not in result
        assert "nonce" not in data
        wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_human_relayed_code_unlocks_the_payment(self):
        """With a valid human-relayed code (bound to amount+tool), the payment proceeds."""
        _DECODE_SATS["v"] = 50000  # decoded invoice amount under test
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


class TestPayInvoiceAmountDecoding:
    """Funds-safety: pay_invoice budgets against the DECODED invoice amount, not max_sats."""

    @pytest.mark.asyncio
    async def test_invoice_amount_exceeding_max_sats_is_rejected(self):
        _DECODE_SATS["v"] = 50000
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")
        result = await pay_invoice(invoice="lnbc500u1...", max_sats=100, wallet=wallet)
        data = json.loads(result)
        assert data["success"] is False
        assert "exceeds the maximum" in data["error"]
        assert data["amount_sats"] == 50000
        wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_amountless_invoice_is_rejected(self):
        _DECODE_SATS["v"] = 0  # fake_decode -> amount falsy -> no amount
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")
        result = await pay_invoice(invoice="lnbc1...", max_sats=1000, wallet=wallet)
        data = json.loads(result)
        assert data["success"] is False
        assert "no amount" in data["error"].lower()
        wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_budget_is_checked_against_decoded_amount_not_max_sats(self):
        _DECODE_SATS["v"] = 50000
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value="preimage")
        budget = _approving_budget()
        result = await pay_invoice(
            invoice="lnbc500u1...", max_sats=100000, wallet=wallet, budget_service=budget,
        )
        data = json.loads(result)
        assert data["success"] is True
        budget.check_approval_level.assert_awaited_once_with(50000)
        budget.record_spend.assert_called_once_with(50000)
