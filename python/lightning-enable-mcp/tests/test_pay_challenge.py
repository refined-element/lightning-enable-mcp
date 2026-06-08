"""Tests for pay_l402_challenge tool — budget and amount validation."""

import json
from datetime import datetime, timezone, timedelta
from decimal import Decimal
from unittest.mock import AsyncMock, MagicMock, patch

import pytest
from lightning_enable_mcp.tools.pay_challenge import pay_l402_challenge
from lightning_enable_mcp.budget_service import PendingConfirmation
from lightning_enable_mcp.config import ApprovalLevel


def _confirming_budget(code: str = "ABC123", sats: int = 10):
    """A BudgetService mock whose check returns requires_confirmation=True."""
    budget = MagicMock()
    approval = MagicMock()
    approval.level = ApprovalLevel.FORM_CONFIRM
    approval.requires_confirmation = True
    approval.amount_usd = Decimal("0.01")
    approval.denial_reason = None
    budget.check_approval_level = AsyncMock(return_value=approval)
    now = datetime.now(timezone.utc)
    pc = PendingConfirmation(
        nonce=code, amount_sats=sats, amount_usd=Decimal("0.01"),
        tool_name="pay_l402_challenge", description="lnbc...",
        created_at=now, expires_at=now + timedelta(minutes=2),
    )
    budget.create_pending_confirmation = MagicMock(return_value=pc)
    budget.validate_and_consume_confirmation = MagicMock(return_value=pc)
    budget.record_spend = MagicMock()
    budget.record_payment_time = MagicMock()
    return budget


class TestPayL402ChallengeOutOfBandConfirmation:
    """Funds-safety: above-threshold L402 payments need a human-relayed, out-of-band code."""

    @pytest.mark.asyncio
    async def test_above_threshold_requests_confirmation_and_does_not_leak_code(self):
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(return_value="preimage")
        mock_decoded = MagicMock()
        mock_decoded.amount_msat = 10000  # 10 sats, within max_sats=1000
        mock_decoded.amount = 10
        budget = _confirming_budget(code="ZZZ999")

        with patch(
            "lightning_enable_mcp.tools.pay_challenge.decode_bolt11",
            return_value=mock_decoded,
        ):
            result = await pay_l402_challenge(
                invoice="lnbc10n1pjtest", macaroon="mac123",
                wallet=mock_wallet, budget_service=budget,
            )
        data = json.loads(result)

        assert data["success"] is False
        assert data.get("requiresConfirmation") is True
        assert "ZZZ999" not in result  # code never reaches the model
        assert "nonce" not in data
        mock_wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_human_relayed_code_unlocks_the_payment(self):
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(return_value="preimage123")
        mock_decoded = MagicMock()
        mock_decoded.amount_msat = 10000
        mock_decoded.amount = 10
        budget = _confirming_budget(code="ABC123", sats=10)

        with patch(
            "lightning_enable_mcp.tools.pay_challenge.decode_bolt11",
            return_value=mock_decoded,
        ):
            result = await pay_l402_challenge(
                invoice="lnbc10n1pjtest", macaroon="mac123",
                wallet=mock_wallet, budget_service=budget,
                confirmation_nonce="abc123",  # tool upcases
            )
        data = json.loads(result)

        assert data["success"] is True
        assert data["preimage"] == "preimage123"
        budget.validate_and_consume_confirmation.assert_called_once_with("ABC123", 10, "pay_l402_challenge")
        mock_wallet.pay_invoice.assert_called_once()

    @pytest.mark.asyncio
    async def test_bad_code_is_rejected_and_not_paid(self):
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(return_value="preimage")
        mock_decoded = MagicMock()
        mock_decoded.amount_msat = 10000
        mock_decoded.amount = 10
        budget = _confirming_budget()
        budget.validate_and_consume_confirmation = MagicMock(return_value=None)

        with patch(
            "lightning_enable_mcp.tools.pay_challenge.decode_bolt11",
            return_value=mock_decoded,
        ):
            result = await pay_l402_challenge(
                invoice="lnbc10n1pjtest", macaroon="mac123",
                wallet=mock_wallet, budget_service=budget,
                confirmation_nonce="WRONG1",
            )
        data = json.loads(result)

        assert data["success"] is False
        assert "amount and tool" in data["error"]
        mock_wallet.pay_invoice.assert_not_called()

class TestPayL402ChallengeNoAmountRejection:
    """Tests that pay_l402_challenge rejects invoices without an explicit amount."""

    @pytest.mark.asyncio
    async def test_rejects_no_amount_invoice(self):
        """Zero-amount invoices must be rejected to prevent budget bypass."""
        mock_wallet = AsyncMock()
        mock_decoded = MagicMock()
        mock_decoded.amount_msat = None
        mock_decoded.amount = None

        with patch(
            "lightning_enable_mcp.tools.pay_challenge.decode_bolt11",
            return_value=mock_decoded,
        ):
            result = json.loads(
                await pay_l402_challenge(
                    invoice="lnbc1pjtest",
                    macaroon=None,
                    wallet=mock_wallet,
                )
            )

        assert result["success"] is False
        assert "no amount" in result["error"].lower()
        mock_wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_rejects_zero_amount_invoice(self):
        """Invoice with amount_msat = 0 must be rejected."""
        mock_wallet = AsyncMock()
        mock_decoded = MagicMock()
        mock_decoded.amount_msat = 0
        mock_decoded.amount = 0

        with patch(
            "lightning_enable_mcp.tools.pay_challenge.decode_bolt11",
            return_value=mock_decoded,
        ):
            result = json.loads(
                await pay_l402_challenge(
                    invoice="lnbc1pjtest",
                    macaroon="mac123",
                    wallet=mock_wallet,
                )
            )

        assert result["success"] is False
        assert "no amount" in result["error"].lower()
        mock_wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_rejects_no_amount_mpp_mode(self):
        """MPP mode (macaroon=None) must also reject no-amount invoices."""
        mock_wallet = AsyncMock()
        mock_decoded = MagicMock()
        mock_decoded.amount_msat = None
        mock_decoded.amount = None

        with patch(
            "lightning_enable_mcp.tools.pay_challenge.decode_bolt11",
            return_value=mock_decoded,
        ):
            result = json.loads(
                await pay_l402_challenge(
                    invoice="lnbc1pjtest",
                    macaroon=None,
                    wallet=mock_wallet,
                )
            )

        assert result["success"] is False
        assert "no amount" in result["error"].lower()
        mock_wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_accepts_valid_amount(self):
        """Invoices with a valid amount should proceed to payment."""
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(return_value="preimage123")
        mock_decoded = MagicMock()
        mock_decoded.amount_msat = 10000
        mock_decoded.amount = 10

        with patch(
            "lightning_enable_mcp.tools.pay_challenge.decode_bolt11",
            return_value=mock_decoded,
        ):
            result = json.loads(
                await pay_l402_challenge(
                    invoice="lnbc10n1pjtest",
                    macaroon="mac123",
                    wallet=mock_wallet,
                )
            )

        assert result["success"] is True
        assert result["preimage"] == "preimage123"
        assert result["protocol"] == "L402"
        mock_wallet.pay_invoice.assert_called_once()

    @pytest.mark.asyncio
    async def test_accepts_valid_amount_mpp_mode(self):
        """MPP mode with a valid amount should succeed."""
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(return_value="preimage456")
        mock_decoded = MagicMock()
        mock_decoded.amount_msat = 5000
        mock_decoded.amount = 5

        with patch(
            "lightning_enable_mcp.tools.pay_challenge.decode_bolt11",
            return_value=mock_decoded,
        ):
            result = json.loads(
                await pay_l402_challenge(
                    invoice="lnbc5n1pjtest",
                    macaroon=None,
                    wallet=mock_wallet,
                )
            )

        assert result["success"] is True
        assert result["preimage"] == "preimage456"
        assert result["protocol"] == "MPP"
        mock_wallet.pay_invoice.assert_called_once()

    @pytest.mark.asyncio
    async def test_sub_sat_amount_rounds_up_to_one(self):
        """Sub-satoshi invoices (1-999 msat) should round up to 1 sat, not 0."""
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(return_value="preimage_sub")
        mock_decoded = MagicMock()
        mock_decoded.amount_msat = 500
        mock_decoded.amount = None

        with patch(
            "lightning_enable_mcp.tools.pay_challenge.decode_bolt11",
            return_value=mock_decoded,
        ):
            result = json.loads(
                await pay_l402_challenge(
                    invoice="lnbc1pjtest",
                    macaroon="mac123",
                    wallet=mock_wallet,
                )
            )

        assert result["success"] is True
        assert result["amount_sats"] == 1
        mock_wallet.pay_invoice.assert_called_once()

    @pytest.mark.asyncio
    async def test_sub_sat_amount_enforces_budget(self):
        """Sub-sat amounts rounded up to 1 sat should still be checked against max_sats."""
        mock_wallet = AsyncMock()
        mock_decoded = MagicMock()
        mock_decoded.amount_msat = 500
        mock_decoded.amount = None

        with patch(
            "lightning_enable_mcp.tools.pay_challenge.decode_bolt11",
            return_value=mock_decoded,
        ):
            result = json.loads(
                await pay_l402_challenge(
                    invoice="lnbc1pjtest",
                    macaroon="mac123",
                    max_sats=0,
                    wallet=mock_wallet,
                )
            )

        assert result["success"] is False
        assert "exceeds maximum" in result["error"]
        mock_wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_budget_check_not_skipped_for_valid_amount(self):
        """BudgetService.check_approval_level must be invoked with the decoded amount."""
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(return_value="preimage789")

        budget = MagicMock()
        approval = MagicMock()
        approval.level = ApprovalLevel.AUTO_APPROVE
        approval.requires_confirmation = False
        approval.amount_usd = Decimal("0.10")
        approval.denial_reason = None
        budget.check_approval_level = AsyncMock(return_value=approval)
        budget.record_spend = MagicMock()
        budget.record_payment_time = MagicMock()

        mock_decoded = MagicMock()
        mock_decoded.amount_msat = 100000
        mock_decoded.amount = 100  # 100 sats

        with patch(
            "lightning_enable_mcp.tools.pay_challenge.decode_bolt11",
            return_value=mock_decoded,
        ):
            result = json.loads(
                await pay_l402_challenge(
                    invoice="lnbc100n1pjtest",
                    wallet=mock_wallet,
                    budget_service=budget,
                )
            )

        assert result["success"] is True
        budget.check_approval_level.assert_awaited_once_with(100)
        budget.record_spend.assert_called_once_with(100)


class TestPayL402ChallengeNoPreimage:
    """Funds-safety: a falsy preimage means the payment did not settle — never record it."""

    @pytest.mark.asyncio
    async def test_no_preimage_does_not_record_spend_or_history(self):
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(return_value=None)  # wallet returned no preimage
        budget = MagicMock()
        approval = MagicMock()
        approval.level = ApprovalLevel.AUTO_APPROVE
        approval.requires_confirmation = False
        approval.amount_usd = Decimal("0.10")
        approval.denial_reason = None
        budget.check_approval_level = AsyncMock(return_value=approval)
        budget.record_spend = MagicMock()
        budget.record_payment_time = MagicMock()
        history = MagicMock()

        mock_decoded = MagicMock()
        mock_decoded.amount_msat = 100000
        mock_decoded.amount = 100

        with patch(
            "lightning_enable_mcp.tools.pay_challenge.decode_bolt11",
            return_value=mock_decoded,
        ):
            result = json.loads(
                await pay_l402_challenge(
                    invoice="lnbc100n1pjtest",
                    wallet=mock_wallet,
                    budget_service=budget,
                    payment_history_service=history,
                )
            )

        assert result["success"] is False
        assert "no preimage" in result["error"].lower()
        budget.record_spend.assert_not_called()
        history.record_payment.assert_not_called()
