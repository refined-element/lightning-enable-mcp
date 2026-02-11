"""
Tests for L402 Client
"""

import pytest
from lightning_enable_mcp.l402_client import (
    L402Client,
    L402Challenge,
    L402Token,
    L402Error,
    L402BudgetExceededError,
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
