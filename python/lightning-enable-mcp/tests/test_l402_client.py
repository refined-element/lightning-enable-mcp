"""
Tests for L402 Client
"""

from unittest.mock import AsyncMock, patch

import pytest
from lightning_enable_mcp.l402_client import (
    L402Client,
    L402Challenge,
    L402Token,
    L402Error,
    L402BudgetExceededError,
    MppToken,
)
from lightning_enable_mcp.wallet_errors import (
    PaymentPendingError,
    PreimageUnavailableError,
)


class TestL402Challenge:
    """Tests for L402Challenge parsing."""

    def test_parse_l402_header(self):
        """Test parsing a standard L402 WWW-Authenticate header."""
        header = 'L402 macaroon="YWJjZGVm", invoice="lnbc10n1..."'

        # Create a mock client (no wallet needed for parsing)
        class MockWallet:
            pass

        client = L402Client(wallet=MockWallet())  # type: ignore
        challenge = client.parse_l402_challenge(header)

        assert challenge.macaroon == "YWJjZGVm"
        assert challenge.invoice == "lnbc10n1..."

    def test_parse_lsat_header(self):
        """Test parsing legacy LSAT WWW-Authenticate header."""
        header = 'LSAT macaroon="bWFjYXJvb24=", invoice="lnbc20n1..."'

        class MockWallet:
            pass

        client = L402Client(wallet=MockWallet())  # type: ignore
        challenge = client.parse_l402_challenge(header)

        assert challenge.macaroon == "bWFjYXJvb24="
        assert challenge.invoice == "lnbc20n1..."

    def test_parse_invalid_header(self):
        """Test parsing invalid header raises error."""
        header = "Basic realm=test"

        class MockWallet:
            pass

        client = L402Client(wallet=MockWallet())  # type: ignore

        with pytest.raises(L402Error):
            client.parse_l402_challenge(header)

    def test_parse_missing_macaroon(self):
        """Test parsing header without macaroon raises error."""
        header = 'L402 invoice="lnbc10n1..."'

        class MockWallet:
            pass

        client = L402Client(wallet=MockWallet())  # type: ignore

        with pytest.raises(L402Error, match="Missing macaroon"):
            client.parse_l402_challenge(header)

    def test_parse_missing_invoice(self):
        """Test parsing header without invoice raises error."""
        header = 'L402 macaroon="YWJjZGVm"'

        class MockWallet:
            pass

        client = L402Client(wallet=MockWallet())  # type: ignore

        with pytest.raises(L402Error, match="Missing invoice"):
            client.parse_l402_challenge(header)

    def test_parse_l402_ows_around_equals(self):
        """Auth-param OWS: whitespace around '=' should be tolerated (RFC 9110)."""
        header = 'L402 macaroon = "YWJjZGVm", invoice = "lnbc10n1..."'

        class MockWallet:
            pass

        client = L402Client(wallet=MockWallet())  # type: ignore
        challenge = client.parse_l402_challenge(header)

        assert challenge.macaroon == "YWJjZGVm"
        assert challenge.invoice == "lnbc10n1..."

    def test_parse_l402_ows_spaces_before_equals(self):
        """Whitespace only before '=' should be tolerated."""
        header = 'L402 macaroon ="YWJjZGVm", invoice ="lnbc10n1..."'

        class MockWallet:
            pass

        client = L402Client(wallet=MockWallet())  # type: ignore
        challenge = client.parse_l402_challenge(header)

        assert challenge.macaroon == "YWJjZGVm"
        assert challenge.invoice == "lnbc10n1..."

    def test_parse_l402_ows_spaces_after_equals(self):
        """Whitespace only after '=' should be tolerated."""
        header = 'L402 macaroon= "YWJjZGVm", invoice= "lnbc10n1..."'

        class MockWallet:
            pass

        client = L402Client(wallet=MockWallet())  # type: ignore
        challenge = client.parse_l402_challenge(header)

        assert challenge.macaroon == "YWJjZGVm"
        assert challenge.invoice == "lnbc10n1..."


class TestL402Token:
    """Tests for L402Token."""

    def test_to_header(self):
        """Test token formats correctly as header value."""
        token = L402Token(
            macaroon="YWJjZGVm",
            preimage="0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        )

        header = token.to_header()

        assert header.startswith("L402 ")
        assert "YWJjZGVm:" in header
        assert header.endswith(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        )


class TestL402ChallengeAmount:
    """Tests for L402Challenge amount parsing."""

    def test_amount_sats_conversion(self):
        """Test millisatoshi to satoshi conversion."""
        challenge = L402Challenge(
            macaroon="test",
            invoice="test",
            amount_msat=10000,
        )

        assert challenge.amount_sats == 10

    def test_amount_sats_none(self):
        """Test amount_sats returns None when no amount."""
        challenge = L402Challenge(
            macaroon="test",
            invoice="test",
            amount_msat=None,
        )

        assert challenge.amount_sats is None

    def test_amount_sats_sub_sat_rounds_up(self):
        """Sub-satoshi amounts (1-999 msat) should round up to 1 sat, not 0."""
        challenge = L402Challenge(
            macaroon="test",
            invoice="test",
            amount_msat=500,
        )

        assert challenge.amount_sats == 1

    def test_amount_sats_rounds_up(self):
        """Millisats that don't divide evenly by 1000 should round up (ceiling)."""
        challenge = L402Challenge(
            macaroon="test",
            invoice="test",
            amount_msat=10999,
        )

        assert challenge.amount_sats == 11


class TestPayChallengeNoAmountRejection:
    """Tests that pay_challenge rejects invoices without an explicit amount (security)."""

    def setup_method(self):
        """Create a client with a mock wallet."""

        class MockWallet:
            pass

        self.client = L402Client(wallet=MockWallet())  # type: ignore

    @pytest.mark.asyncio
    async def test_pay_challenge_rejects_no_amount_invoice(self):
        """Invoices without an amount should be rejected for security."""
        with patch.object(
            self.client, "_get_invoice_amount_msat", return_value=None
        ):
            with pytest.raises(L402Error, match="no amount specified"):
                await self.client.pay_challenge(invoice="lnbc1pjtest")

    @pytest.mark.asyncio
    async def test_pay_challenge_rejects_zero_amount_invoice(self):
        """Invoices with zero amount should be rejected for security."""
        with patch.object(
            self.client, "_get_invoice_amount_msat", return_value=0
        ):
            with pytest.raises(L402Error, match="no amount specified"):
                await self.client.pay_challenge(invoice="lnbc1pjtest")

    @pytest.mark.asyncio
    async def test_pay_challenge_rejects_no_amount_mpp_mode(self):
        """MPP mode (no macaroon) should also reject no-amount invoices."""
        with patch.object(
            self.client, "_get_invoice_amount_msat", return_value=None
        ):
            with pytest.raises(L402Error, match="no amount specified"):
                await self.client.pay_challenge(invoice="lnbc1pjtest", macaroon=None)

    @pytest.mark.asyncio
    async def test_pay_challenge_accepts_valid_amount(self):
        """Invoices with a valid amount should proceed to payment."""
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(return_value="preimage123")
        self.client.wallet = mock_wallet

        with patch.object(
            self.client, "_get_invoice_amount_msat", return_value=10000
        ):
            result = await self.client.pay_challenge(
                invoice="lnbc10n1pjtest", macaroon="mac123"
            )
            assert isinstance(result, L402Token)
            assert result.preimage == "preimage123"

    @pytest.mark.asyncio
    async def test_pay_challenge_mpp_accepts_valid_amount(self):
        """MPP mode with a valid amount should return MppToken."""
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(return_value="preimage456")
        self.client.wallet = mock_wallet

        with patch.object(
            self.client, "_get_invoice_amount_msat", return_value=5000
        ):
            result = await self.client.pay_challenge(
                invoice="lnbc5n1pjtest", macaroon=None
            )
            assert isinstance(result, MppToken)
            assert result.preimage == "preimage456"

    @pytest.mark.asyncio
    async def test_pay_challenge_sub_sat_rounds_up_and_checks_budget(self):
        """Sub-satoshi invoices (1-999 msat) should round up to 1 sat for budget checks."""
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(return_value="preimage_subsats")
        self.client.wallet = mock_wallet

        with patch.object(
            self.client, "_get_invoice_amount_msat", return_value=500
        ):
            result = await self.client.pay_challenge(
                invoice="lnbc1pjtest", macaroon="mac123", max_sats=1
            )
            assert isinstance(result, L402Token)
            assert result.preimage == "preimage_subsats"

    @pytest.mark.asyncio
    async def test_pay_challenge_sub_sat_exceeds_budget(self):
        """Sub-sat amount rounded up to 1 should fail if max_sats is 0."""
        with patch.object(
            self.client, "_get_invoice_amount_msat", return_value=500
        ):
            with pytest.raises(L402BudgetExceededError):
                await self.client.pay_challenge(
                    invoice="lnbc1pjtest", macaroon="mac123", max_sats=0
                )


class TestPayChallengeProofUnavailable:
    """
    When the wallet has no preimage, L402 cannot complete — but the funds still
    left. The error must stay typed (so callers can tell "paid, unprovable" from
    "payment failed") and must carry the amount so the spend is not lost.
    """

    def setup_method(self):
        class MockWallet:
            pass

        self.client = L402Client(wallet=MockWallet())  # type: ignore

    @pytest.mark.asyncio
    async def test_preimage_unavailable_is_not_rewrapped_as_payment_failed(self):
        """
        Rewrapping this as L402PaymentError("Payment failed") would tell the
        caller the money is still theirs. It isn't.
        """
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(side_effect=PreimageUnavailableError(
            "no preimage", provider="opennode", tracking_id="w-1", status="paid",
        ))
        self.client.wallet = mock_wallet

        with patch.object(self.client, "_get_invoice_amount_msat", return_value=10_000):
            with pytest.raises(PreimageUnavailableError) as exc:
                await self.client.pay_challenge(invoice="lnbc10n1pjtest", macaroon="mac123")

        assert exc.value.tracking_id == "w-1"

    @pytest.mark.asyncio
    async def test_settled_amount_is_attached_so_the_spend_can_be_recorded(self):
        """Mirrors the paid-but-retry-failed idiom: callers read e.amount_paid."""
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(side_effect=PreimageUnavailableError(
            "no preimage", provider="opennode", tracking_id="w-1", status="paid",
        ))
        self.client.wallet = mock_wallet

        with patch.object(self.client, "_get_invoice_amount_msat", return_value=10_000):
            with pytest.raises(PreimageUnavailableError) as exc:
                await self.client.pay_challenge(invoice="lnbc10n1pjtest", macaroon="mac123")

        assert exc.value.amount_paid == 10  # 10_000 msat -> 10 sats

    @pytest.mark.asyncio
    async def test_pending_payment_is_not_reported_as_a_token(self):
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(side_effect=PaymentPendingError(
            "in flight", provider="opennode", tracking_id="w-2", status="pending",
        ))
        self.client.wallet = mock_wallet

        with patch.object(self.client, "_get_invoice_amount_msat", return_value=10_000):
            with pytest.raises(PaymentPendingError):
                await self.client.pay_challenge(invoice="lnbc10n1pjtest", macaroon="mac123")

    @pytest.mark.asyncio
    async def test_real_payment_failures_still_become_l402_payment_error(self):
        """Regression guard: ordinary wallet failures keep their old behavior."""
        from lightning_enable_mcp.l402_client import L402PaymentError

        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(side_effect=RuntimeError("node offline"))
        self.client.wallet = mock_wallet

        with patch.object(self.client, "_get_invoice_amount_msat", return_value=10_000):
            with pytest.raises(L402PaymentError):
                await self.client.pay_challenge(invoice="lnbc10n1pjtest", macaroon="mac123")
