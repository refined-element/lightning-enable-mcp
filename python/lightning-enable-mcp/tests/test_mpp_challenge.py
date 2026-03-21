"""Tests for MPP (Machine Payments Protocol) challenge parsing."""

import pytest
from lightning_enable_mcp.l402_client import (
    L402Client,
    L402Challenge,
    L402Error,
    L402Token,
    MppChallenge,
    MppToken,
)


class MockWallet:
    """Minimal mock wallet for creating L402Client instances."""

    pass


class TestMppChallengeParsing:
    """Tests for MppChallenge parsing."""

    def setup_method(self):
        """Create a client with a mock wallet."""
        self.client = L402Client(wallet=MockWallet())  # type: ignore

    def test_parse_valid_mpp_header(self):
        header = 'Payment realm="api.example.com", method="lightning", invoice="lnbc100n1pjtest", amount="100", currency="sat"'
        result = self.client.parse_mpp_challenge(header)
        assert isinstance(result, MppChallenge)
        assert result.invoice == "lnbc100n1pjtest"
        assert result.amount == "100"
        assert result.realm == "api.example.com"

    def test_parse_non_lightning_method_raises(self):
        header = 'Payment realm="test", method="stripe", invoice="lnbc100n1pjtest"'
        with pytest.raises(L402Error, match="must be 'lightning'"):
            self.client.parse_mpp_challenge(header)

    def test_parse_missing_invoice_raises(self):
        header = 'Payment realm="test", method="lightning", amount="100"'
        with pytest.raises(L402Error, match="Missing invoice"):
            self.client.parse_mpp_challenge(header)

    def test_parse_non_payment_scheme_raises(self):
        header = 'L402 macaroon="abc", invoice="lnbc100n1pjtest"'
        with pytest.raises(L402Error, match="Invalid MPP"):
            self.client.parse_mpp_challenge(header)

    def test_parse_minimal_header(self):
        header = 'Payment method="lightning", invoice="lnbc100n1pjtest"'
        result = self.client.parse_mpp_challenge(header)
        assert result.invoice == "lnbc100n1pjtest"
        assert result.amount is None
        assert result.realm is None

    def test_parse_case_insensitive_scheme(self):
        header = 'payment method="lightning", invoice="lnbc100n1pjtest"'
        result = self.client.parse_mpp_challenge(header)
        assert result.invoice == "lnbc100n1pjtest"

    def test_parse_case_insensitive_method(self):
        header = 'Payment METHOD="Lightning", invoice="lnbc100n1pjtest"'
        result = self.client.parse_mpp_challenge(header)
        assert result.invoice == "lnbc100n1pjtest"

    def test_parse_missing_method_raises(self):
        header = 'Payment invoice="lnbc100n1pjtest", amount="100"'
        with pytest.raises(L402Error, match="must be 'lightning'"):
            self.client.parse_mpp_challenge(header)


class TestMppChallengeAmountSats:
    """Tests for MppChallenge.amount_sats property."""

    def test_amount_sats_conversion(self):
        challenge = MppChallenge(
            invoice="test",
            amount_msat=10000,
        )
        assert challenge.amount_sats == 10

    def test_amount_sats_none_when_no_msat(self):
        challenge = MppChallenge(
            invoice="test",
            amount_msat=None,
        )
        assert challenge.amount_sats is None

    def test_amount_sats_truncation(self):
        """Millisats that don't divide evenly by 1000 should truncate."""
        challenge = MppChallenge(
            invoice="test",
            amount_msat=10999,
        )
        assert challenge.amount_sats == 10


class TestMppToken:
    """Tests for MppToken."""

    def test_to_header(self):
        token = MppToken(preimage="abcdef1234567890")
        expected = 'Payment method="lightning", preimage="abcdef1234567890"'
        assert token.to_header() == expected

    def test_to_header_full_preimage(self):
        preimage = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        token = MppToken(preimage=preimage)
        assert preimage in token.to_header()
        assert token.to_header().startswith("Payment ")


class TestParseBestChallenge:
    """Tests for parse_best_challenge — prefers L402, falls back to MPP."""

    def setup_method(self):
        self.client = L402Client(wallet=MockWallet())  # type: ignore

    def test_l402_header_returns_l402(self):
        header = 'L402 macaroon="abc123", invoice="lnbc100n1pjtest"'
        result = self.client.parse_best_challenge(header)
        assert isinstance(result, L402Challenge)
        assert result.macaroon == "abc123"
        assert result.invoice == "lnbc100n1pjtest"

    def test_lsat_header_returns_l402(self):
        header = 'LSAT macaroon="abc123", invoice="lnbc100n1pjtest"'
        result = self.client.parse_best_challenge(header)
        assert isinstance(result, L402Challenge)
        assert result.macaroon == "abc123"

    def test_mpp_header_returns_mpp(self):
        header = 'Payment method="lightning", invoice="lnbc100n1pjtest"'
        result = self.client.parse_best_challenge(header)
        assert isinstance(result, MppChallenge)
        assert result.invoice == "lnbc100n1pjtest"

    def test_invalid_header_raises(self):
        with pytest.raises(L402Error, match="No valid"):
            self.client.parse_best_challenge("Bearer token123")

    def test_empty_header_raises(self):
        with pytest.raises(L402Error, match="No valid"):
            self.client.parse_best_challenge("")

    def test_l402_preferred_over_mpp_when_both_valid(self):
        """When the header starts with L402, it should be parsed as L402 even though
        it could theoretically contain MPP-style fields."""
        header = 'L402 macaroon="mac123", invoice="lnbc100n1pjtest"'
        result = self.client.parse_best_challenge(header)
        assert isinstance(result, L402Challenge)

    def test_mpp_header_with_full_fields(self):
        header = 'Payment realm="weather.api.com", method="lightning", invoice="lnbc50n1pj...", amount="50", currency="sat"'
        result = self.client.parse_best_challenge(header)
        assert isinstance(result, MppChallenge)
        assert result.realm == "weather.api.com"
        assert result.amount == "50"

    def test_case_insensitive_l402_scheme(self):
        """L402 scheme detection should be case-insensitive per HTTP spec."""
        header = 'l402 macaroon="abc123", invoice="lnbc100n1pjtest"'
        result = self.client.parse_best_challenge(header)
        assert isinstance(result, L402Challenge)
        assert result.macaroon == "abc123"

    def test_case_insensitive_lsat_scheme(self):
        """LSAT scheme detection should be case-insensitive per HTTP spec."""
        header = 'lsat macaroon="abc123", invoice="lnbc100n1pjtest"'
        result = self.client.parse_best_challenge(header)
        assert isinstance(result, L402Challenge)
        assert result.macaroon == "abc123"

    def test_mixed_case_l402_scheme(self):
        """Mixed case like 'L402' or 'l402' should both work."""
        for scheme in ("L402", "l402", "Lsat", "lSAT"):
            header = f'{scheme} macaroon="mac", invoice="lnbc100n1pjtest"'
            result = self.client.parse_best_challenge(header)
            assert isinstance(result, L402Challenge)


class TestSelectBestChallenge:
    """Tests for _select_best_challenge — handles multiple WWW-Authenticate header values."""

    def setup_method(self):
        self.client = L402Client(wallet=MockWallet())  # type: ignore

    def test_single_l402_header(self):
        headers = ['L402 macaroon="abc", invoice="lnbc100n1pjtest"']
        result = self.client._select_best_challenge(headers)
        assert isinstance(result, L402Challenge)

    def test_single_mpp_header(self):
        headers = ['Payment method="lightning", invoice="lnbc100n1pjtest"']
        result = self.client._select_best_challenge(headers)
        assert isinstance(result, MppChallenge)

    def test_l402_preferred_when_both_present(self):
        """When server sends both L402 and MPP headers, L402 should be preferred."""
        headers = [
            'Payment method="lightning", invoice="lnbc200n1pjmpp"',
            'L402 macaroon="mac123", invoice="lnbc100n1pjl402"',
        ]
        result = self.client._select_best_challenge(headers)
        assert isinstance(result, L402Challenge)
        assert result.invoice == "lnbc100n1pjl402"

    def test_l402_preferred_even_when_mpp_first(self):
        """L402 should be preferred regardless of header order."""
        headers = [
            'Payment method="lightning", invoice="lnbc200n1pjmpp"',
            'L402 macaroon="mac123", invoice="lnbc100n1pjl402"',
        ]
        result = self.client._select_best_challenge(headers)
        assert isinstance(result, L402Challenge)

    def test_falls_back_to_mpp_when_no_l402(self):
        """When only MPP is present, it should be returned."""
        headers = [
            'Bearer realm="test"',
            'Payment method="lightning", invoice="lnbc100n1pjmpp"',
        ]
        result = self.client._select_best_challenge(headers)
        assert isinstance(result, MppChallenge)

    def test_no_valid_headers_raises(self):
        headers = ["Bearer token123", "Basic realm=test"]
        with pytest.raises(L402Error, match="No valid"):
            self.client._select_best_challenge(headers)

    def test_empty_list_raises(self):
        with pytest.raises(L402Error, match="No valid"):
            self.client._select_best_challenge([])

    def test_whitespace_headers_ignored(self):
        headers = ["  ", "", 'L402 macaroon="abc", invoice="lnbc100n1pjtest"']
        result = self.client._select_best_challenge(headers)
        assert isinstance(result, L402Challenge)


class TestExpandChallenges:
    """Tests for _expand_challenges — handles comma-separated challenges in a single header."""

    def setup_method(self):
        self.client = L402Client(wallet=MockWallet())  # type: ignore

    def test_single_challenge_unchanged(self):
        values = ['L402 macaroon="abc", invoice="lnbc100n1pjtest"']
        result = L402Client._expand_challenges(values)
        assert len(result) == 1
        assert result[0].startswith("L402")

    def test_single_mpp_challenge_unchanged(self):
        values = ['Payment method="lightning", invoice="lnbc100n1pjtest"']
        result = L402Client._expand_challenges(values)
        assert len(result) == 1
        assert result[0].startswith("Payment")

    def test_comma_joined_payment_then_l402(self):
        """A single header value with Payment and L402 should expand to two challenges."""
        combined = (
            'Payment method="lightning", invoice="lnbc200n1pjmpp", '
            'L402 macaroon="mac123", invoice="lnbc100n1pjl402"'
        )
        result = L402Client._expand_challenges([combined])
        assert len(result) == 2
        assert result[0].startswith("Payment")
        assert result[1].startswith("L402")

    def test_comma_joined_l402_then_payment(self):
        """L402 first, then Payment in single value — should expand to two challenges."""
        combined = (
            'L402 macaroon="mac123", invoice="lnbc100n1pjl402", '
            'Payment method="lightning", invoice="lnbc200n1pjmpp"'
        )
        result = L402Client._expand_challenges([combined])
        assert len(result) == 2
        assert result[0].startswith("L402")
        assert result[1].startswith("Payment")

    def test_empty_and_whitespace_values_ignored(self):
        result = L402Client._expand_challenges(["", "  ", ""])
        assert len(result) == 0

    def test_multiple_separate_values_passthrough(self):
        """Multiple separate header values should pass through unchanged."""
        values = [
            'Payment method="lightning", invoice="lnbc200n1pjmpp"',
            'L402 macaroon="mac123", invoice="lnbc100n1pjl402"',
        ]
        result = L402Client._expand_challenges(values)
        assert len(result) == 2

    def test_select_best_prefers_l402_in_comma_joined(self):
        """When a single header has Payment then L402 comma-joined, L402 should be selected."""
        combined = (
            'Payment method="lightning", invoice="lnbc200n1pjmpp", '
            'L402 macaroon="mac123", invoice="lnbc100n1pjl402"'
        )
        result = self.client._select_best_challenge([combined])
        assert isinstance(result, L402Challenge)
        assert result.invoice == "lnbc100n1pjl402"


class TestPayChallengeProtocol:
    """Tests for pay_challenge return types based on macaroon presence."""

    def test_l402_token_has_macaroon_and_preimage(self):
        token = L402Token(macaroon="mac123", preimage="pre456")
        assert "L402" in token.to_header()
        assert "mac123" in token.to_header()
        assert "pre456" in token.to_header()

    def test_mpp_token_has_only_preimage(self):
        token = MppToken(preimage="pre456")
        header = token.to_header()
        assert "Payment" in header
        assert "pre456" in header
        assert "macaroon" not in header.lower()

    def test_l402_and_mpp_tokens_have_different_headers(self):
        l402 = L402Token(macaroon="mac", preimage="pre")
        mpp = MppToken(preimage="pre")
        assert l402.to_header() != mpp.to_header()
        assert l402.to_header().startswith("L402 ")
        assert mpp.to_header().startswith("Payment ")
