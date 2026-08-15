"""
Tests for L402 Client
"""

from unittest.mock import AsyncMock, MagicMock, patch

import httpx
import pytest
from lightning_enable_mcp.l402_client import (
    L402Client,
    L402Challenge,
    L402Token,
    L402Error,
    L402RedirectError,
    L402BudgetExceededError,
    MppToken,
)
from lightning_enable_mcp._redirect import resolve_redirect_location
from lightning_enable_mcp.budget_service import SpendReservationResult
from lightning_enable_mcp.wallet_errors import (
    PaymentPendingError,
    PreimageUnavailableError,
)

# Reservation id every budget mock hands back from try_reserve; commit/release assert on it.
_RESV_ID = "resv-1"


def _grant_reservation(budget):
    """Wire a MagicMock BudgetService with the reserve/commit/release API used by the client:
    try_reserve grants a reservation (echoing the requested sats), commit/release are spies."""
    budget.try_reserve = AsyncMock(
        side_effect=lambda amt: SpendReservationResult.reserved(_RESV_ID, amt)
    )
    budget.commit_reservation = MagicMock()
    budget.release_reservation = MagicMock()
    return budget


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


class TestFetchRedirectPosture:
    """
    FIX A / FIX D (Python) — redirects are NOT followed (follow_redirects=False).

    Following a 3xx would (1) re-send agent-supplied custom headers (X-Api-Key, Cookie)
    to a cross-origin target and (2) on the L402 path pay a provider that host-redirects
    before its 402 and then loses its Authorization header on the host change. Instead a
    3xx is surfaced as an actionable L402RedirectError, matching the .NET port.
    """

    def setup_method(self):
        class MockWallet:
            pass

        self.client = L402Client(wallet=MockWallet())  # type: ignore

    def test_client_does_not_follow_redirects(self):
        """The underlying httpx client is pinned to follow_redirects=False."""
        assert self.client._http_client.follow_redirects is False

    @pytest.mark.asyncio
    async def test_302_to_different_host_surfaces_redirect_not_followed(self):
        """A 302 to a different host is surfaced as actionable, not followed — so the
        agent's custom headers are never re-sent cross-origin (only one request is made)."""
        request = httpx.Request("GET", "https://original.example.com/data")
        response = httpx.Response(
            302, headers={"location": "https://attacker.example.net/collect"}, request=request
        )
        mock_wallet = AsyncMock()
        self.client.wallet = mock_wallet
        self.client._http_client.request = AsyncMock(return_value=response)

        with pytest.raises(L402RedirectError) as exc:
            await self.client.fetch(
                "https://original.example.com/data",
                headers={"X-Api-Key": "super-secret"},
            )

        assert exc.value.location == "https://attacker.example.net/collect"
        # Exactly one request issued (no follow), and the money path never touched.
        assert self.client._http_client.request.await_count == 1
        mock_wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_l402_provider_host_redirect_does_not_pay(self):
        """A provider that host-redirects BEFORE its 402 must not be paid: the 3xx is seen
        first, so pay_invoice is never called."""
        request = httpx.Request("GET", "https://api.provider.com/premium")
        response = httpx.Response(
            302, headers={"location": "https://paywall.other-host.com/402"}, request=request
        )
        mock_wallet = AsyncMock()
        self.client.wallet = mock_wallet
        self.client._http_client.request = AsyncMock(return_value=response)

        with pytest.raises(L402RedirectError) as exc:
            await self.client.fetch("https://api.provider.com/premium", max_sats=5000)

        assert exc.value.location == "https://paywall.other-host.com/402"
        mock_wallet.pay_invoice.assert_not_called()
        # No amount_paid attached on the pre-payment redirect (nothing was spent).
        assert getattr(exc.value, "amount_paid", None) is None

    @pytest.mark.asyncio
    async def test_relative_redirect_is_resolved_to_absolute(self):
        """A relative Location resolves against the request URL so the agent gets a usable URL."""
        request = httpx.Request("GET", "https://api.example.com/v1/data")
        response = httpx.Response(301, headers={"location": "/v2/data"}, request=request)
        self.client._http_client.request = AsyncMock(return_value=response)

        with pytest.raises(L402RedirectError) as exc:
            await self.client.fetch("https://api.example.com/v1/data")

        assert exc.value.location == "https://api.example.com/v2/data"

    @pytest.mark.asyncio
    async def test_304_not_modified_is_not_a_redirect(self):
        """A 304 with no Location is not a redirect — it flows through normal handling."""
        request = httpx.Request("GET", "https://api.example.com/data")
        response = httpx.Response(304, request=request)
        self.client._http_client.request = AsyncMock(return_value=response)

        # 304 < 400 and != 402, so fetch returns the (empty) body, no redirect raised.
        text, amount = await self.client.fetch("https://api.example.com/data")
        assert amount is None

    @pytest.mark.asyncio
    async def test_paid_retry_redirect_attaches_amount_and_token(self):
        """FIX 1 — 402 -> pay -> 302 on the retry: the redirect error carries BOTH the
        settled amount AND the paid token, so the caller records the spend once and the
        agent can reuse the credential against the redirect target instead of re-paying."""
        req = httpx.Request("GET", "https://api.provider.com/premium")
        challenge = httpx.Response(
            402,
            headers=[("WWW-Authenticate", 'L402 macaroon="YWJjZGVm", invoice="lnbc100n1pjtest"')],
            request=req,
        )
        redirect = httpx.Response(
            302, headers={"location": "https://cdn.example.com/asset"}, request=req
        )
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(return_value="preimage456")
        self.client.wallet = mock_wallet
        self.client._http_client.request = AsyncMock(side_effect=[challenge, redirect])

        with patch.object(self.client, "_get_invoice_amount_msat", return_value=10_000):
            with pytest.raises(L402RedirectError) as exc:
                await self.client.fetch("https://api.provider.com/premium", max_sats=1000)

        assert exc.value.location == "https://cdn.example.com/asset"
        assert exc.value.amount_paid == 10  # 10_000 msat -> 10 sats
        assert exc.value.l402_token == "YWJjZGVm:preimage456"
        # Paid exactly once; the retry was issued (2 requests) but the redirect target
        # was NEVER fetched (no third request).
        mock_wallet.pay_invoice.assert_awaited_once()
        assert self.client._http_client.request.await_count == 2


class TestClientCentralizedRecording:
    """
    The CLIENT is the single source of truth for recording a real payment: whenever the
    L402 invoice is actually paid, it records the spend + payment history + cooldown
    EXACTLY ONCE — regardless of whether the authorized retry returns 2xx / 3xx-redirect /
    4xx / 5xx — and NEVER records a failed payment for a settlement whose invoice was paid.
    The consuming tools are passive and must not record any of this again.
    """

    def _build(self, retry_responses):
        """Client wired with budget + history mocks; initial 402 (10-sat L402 challenge)
        then the given paid-retry response(s)."""
        req = httpx.Request("GET", "https://api.provider.com/premium")
        challenge = httpx.Response(
            402,
            headers=[("WWW-Authenticate", 'L402 macaroon="YWJjZGVm", invoice="lnbc100n1pjtest"')],
            request=req,
        )
        budget = _grant_reservation(MagicMock())
        history = MagicMock()
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(return_value="preimage456")
        client = L402Client(
            wallet=mock_wallet, budget_service=budget, payment_history_service=history
        )
        client._http_client.request = AsyncMock(side_effect=[challenge, *retry_responses])
        return client, budget, history, mock_wallet

    def _assert_recorded_once(self, budget, history):
        # Spend is committed against the reservation (10_000 msat -> 10 sats), exactly once.
        budget.commit_reservation.assert_called_once_with(_RESV_ID, 10)
        budget.record_payment_time.assert_called_once()
        history.record_payment.assert_called_once()
        # A settled payment is always recorded status="success" (never failed).
        assert history.record_payment.call_args.kwargs.get("status") == "success"

    @pytest.mark.asyncio
    async def test_paid_retry_2xx_records_spend_payment_cooldown_exactly_once(self):
        req = httpx.Request("GET", "https://api.provider.com/premium")
        ok = httpx.Response(200, request=req, content=b'{"ok":true}')
        client, budget, history, _ = self._build([ok])

        with patch.object(client, "_get_invoice_amount_msat", return_value=10_000):
            text, amount = await client.fetch("https://api.provider.com/premium", max_sats=1000)

        assert amount == 10
        assert '"ok":true' in text
        self._assert_recorded_once(budget, history)

    @pytest.mark.asyncio
    async def test_paid_retry_redirect_records_exactly_once(self):
        req = httpx.Request("GET", "https://api.provider.com/premium")
        redirect = httpx.Response(302, headers={"location": "https://cdn.example.com/asset"}, request=req)
        client, budget, history, _ = self._build([redirect])

        with patch.object(client, "_get_invoice_amount_msat", return_value=10_000):
            with pytest.raises(L402RedirectError) as exc:
                await client.fetch("https://api.provider.com/premium", max_sats=1000)

        assert exc.value.amount_paid == 10
        assert exc.value.l402_token == "YWJjZGVm:preimage456"
        self._assert_recorded_once(budget, history)

    @pytest.mark.asyncio
    async def test_paid_retry_500_records_settled_once_and_surfaces_token(self):
        req = httpx.Request("GET", "https://api.provider.com/premium")
        boom = httpx.Response(500, request=req, content=b"boom")
        client, budget, history, _ = self._build([boom])

        with patch.object(client, "_get_invoice_amount_msat", return_value=10_000):
            with pytest.raises(L402Error) as exc:
                await client.fetch("https://api.provider.com/premium", max_sats=1000)

        # NOT a redirect, but the settled amount + token are surfaced so the tool can warn
        # the agent off a double-pay and hand back the credential.
        assert not isinstance(exc.value, L402RedirectError)
        assert exc.value.amount_paid == 10
        assert exc.value.l402_token == "YWJjZGVm:preimage456"
        self._assert_recorded_once(budget, history)

    @pytest.mark.asyncio
    async def test_genuine_pre_payment_failure_records_nothing(self):
        """A non-402 error on the INITIAL request means no invoice was ever paid — the
        client records no spend, no payment, no cooldown."""
        req = httpx.Request("GET", "https://api.provider.com/premium")
        not_found = httpx.Response(404, request=req, content=b"nope")
        budget = MagicMock()
        history = MagicMock()
        mock_wallet = AsyncMock()
        client = L402Client(
            wallet=mock_wallet, budget_service=budget, payment_history_service=history
        )
        client._http_client.request = AsyncMock(return_value=not_found)

        with pytest.raises(L402Error):
            await client.fetch("https://api.provider.com/premium", max_sats=1000)

        mock_wallet.pay_invoice.assert_not_called()
        budget.record_spend.assert_not_called()
        budget.record_payment_time.assert_not_called()
        history.record_payment.assert_not_called()

    @pytest.mark.asyncio
    async def test_no_preimage_records_spend_and_cooldown_but_no_history(self):
        """The wallet paid but returned no preimage: money moved, so the client records the
        spend + arms the cooldown (funds-safety), but writes NO 'success' history entry for
        an unprovable/possibly-pending payment — and never a 'failed' one for paid funds."""
        req = httpx.Request("GET", "https://api.provider.com/premium")
        challenge = httpx.Response(
            402,
            headers=[("WWW-Authenticate", 'L402 macaroon="YWJjZGVm", invoice="lnbc100n1pjtest"')],
            request=req,
        )
        budget = _grant_reservation(MagicMock())
        history = MagicMock()
        mock_wallet = AsyncMock()
        mock_wallet.pay_invoice = AsyncMock(side_effect=PreimageUnavailableError(
            "no preimage", provider="opennode", tracking_id="w-1", status="paid",
        ))
        client = L402Client(
            wallet=mock_wallet, budget_service=budget, payment_history_service=history
        )
        client._http_client.request = AsyncMock(return_value=challenge)

        with patch.object(client, "_get_invoice_amount_msat", return_value=10_000):
            with pytest.raises(PreimageUnavailableError) as exc:
                await client.fetch("https://api.provider.com/premium", max_sats=1000)

        assert exc.value.amount_paid == 10
        # Money moved (settled, unprovable) — spend is committed against the reservation.
        budget.commit_reservation.assert_called_once_with(_RESV_ID, 10)
        budget.record_payment_time.assert_called_once()
        history.record_payment.assert_not_called()


class _FakeResponse:
    """Duck-typed response: status_code + headers.get('location'), like httpx / a mock."""

    def __init__(self, status_code, location=None):
        self.status_code = status_code
        self.headers = {} if location is None else {"location": location}


class TestResolveRedirectLocationParity:
    """resolve_redirect_location treats a Location that does NOT resolve to a valid absolute
    http/https URL as NO redirect (returns None), identical to the .NET RedirectResolver."""

    REQ = "https://api.example.com/v1/data"

    def test_absolute_http_location_is_returned(self):
        r = _FakeResponse(302, "https://cdn.example.com/asset")
        assert resolve_redirect_location(self.REQ, r) == "https://cdn.example.com/asset"

    def test_relative_location_resolves_to_absolute(self):
        r = _FakeResponse(301, "/v2/data")
        assert resolve_redirect_location(self.REQ, r) == "https://api.example.com/v2/data"

    @pytest.mark.parametrize(
        "location",
        ["ftp://files.example.com/x", "javascript:alert(1)", "mailto:a@example.com"],
    )
    def test_non_http_scheme_is_not_a_redirect(self, location):
        r = _FakeResponse(302, location)
        assert resolve_redirect_location(self.REQ, r) is None

    def test_304_without_location_is_not_a_redirect(self):
        assert resolve_redirect_location(self.REQ, _FakeResponse(304)) is None

    def test_3xx_without_location_is_not_a_redirect(self):
        assert resolve_redirect_location(self.REQ, _FakeResponse(302)) is None

    def test_non_3xx_is_not_a_redirect(self):
        assert resolve_redirect_location(self.REQ, _FakeResponse(200, "https://cdn.example.com/x")) is None
