"""Tests for pay_l402_challenge tool — budget and amount validation."""

import json
from unittest.mock import AsyncMock, MagicMock, patch

import pytest
from lightning_enable_mcp.tools.pay_challenge import pay_l402_challenge


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
    async def test_budget_check_not_skipped_for_valid_amount(self):
        """Budget manager should be checked when amount is present."""
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(return_value="preimage789")
        mock_budget = MagicMock()
        mock_budget.check_payment = MagicMock()  # no exception = within budget
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
                    budget_manager=mock_budget,
                )
            )

        assert result["success"] is True
        mock_budget.check_payment.assert_called_once_with(100, 1000)
