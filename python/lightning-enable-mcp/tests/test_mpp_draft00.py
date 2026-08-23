"""Tests for MPP draft-00 (draft-httpauth-payment-00 + draft-lightning-charge-00) client support.

Modern "Payment" challenges carry a base64url-encoded ``request`` param (JCS JSON with
the invoice inside) instead of a top-level ``invoice=`` param, and are answered with a
single-use ``Authorization: Payment <base64url(JSON)>`` credential. A server may also
send a SUPERSET header carrying BOTH modern params and legacy invoice/amount/currency
params — modern wins, and the legacy params are only a fallback when the modern part is
malformed. Fixtures only — no real invoices, no real payments.
"""

import base64
import json
from unittest.mock import AsyncMock, MagicMock, patch

import httpx
import pytest

from lightning_enable_mcp.budget_service import SpendReservationResult
from lightning_enable_mcp.l402_client import (
    L402Challenge,
    L402Client,
    L402Error,
    MppChallenge,
    MppModernToken,
    parse_payment_receipt,
)

# Deterministic fixtures — never real invoices/preimages.
FIXTURE_INVOICE = "lnbc100n1pjtest"
FIXTURE_PAYMENT_HASH = "b" * 64
FIXTURE_PREIMAGE = "a" * 64
FUTURE_EXPIRY = "2099-01-01T00:00:00Z"


def b64url_nopad(s: str) -> str:
    return base64.urlsafe_b64encode(s.encode()).decode().rstrip("=")


def b64url_pad(s: str) -> str:
    return base64.urlsafe_b64encode(s.encode()).decode()


def request_json(invoice: str = FIXTURE_INVOICE, amount: str = "10") -> str:
    return json.dumps(
        {
            "amount": amount,
            "currency": "sat",
            "methodDetails": {
                "invoice": invoice,
                "paymentHash": FIXTURE_PAYMENT_HASH,
                "network": "mainnet",
            },
        }
    )


def modern_header(request_encoded: str, expires: str = FUTURE_EXPIRY) -> str:
    return (
        f'Payment id="chal-1", realm="api.example.com", method="lightning", '
        f'intent="charge", request="{request_encoded}", expires="{expires}"'
    )


def decode_credential(credential: str) -> dict:
    padded = credential + "=" * ((4 - len(credential) % 4) % 4)
    return json.loads(base64.urlsafe_b64decode(padded))


class MockWallet:
    pass


class TestModernChallengeParsing:
    def setup_method(self):
        self.client = L402Client(wallet=MockWallet())  # type: ignore

    def test_parse_modern_happy_path(self):
        encoded = b64url_nopad(request_json())
        result = self.client.parse_mpp_challenge(modern_header(encoded))

        assert isinstance(result, MppChallenge)
        assert result.is_modern is True
        assert result.id == "chal-1"
        assert result.realm == "api.example.com"
        assert result.method == "lightning"
        assert result.intent == "charge"
        assert result.request_encoded == encoded
        assert result.expires == FUTURE_EXPIRY
        assert result.invoice == FIXTURE_INVOICE
        assert result.amount == "10"
        assert result.payment_hash == FIXTURE_PAYMENT_HASH
        assert result.network == "mainnet"

    def test_parse_modern_optional_params_captured(self):
        encoded = b64url_nopad(request_json())
        header = (
            modern_header(encoded)
            + ', digest="fixture-digest", description="Premium API call", opaque="fixture-opaque"'
        )
        result = self.client.parse_mpp_challenge(header)

        assert result.digest == "fixture-digest"
        assert result.description == "Premium API call"
        assert result.opaque == "fixture-opaque"

    def test_parse_modern_optional_params_absent_are_none(self):
        result = self.client.parse_mpp_challenge(modern_header(b64url_nopad(request_json())))
        assert result.digest is None
        assert result.description is None
        assert result.opaque is None

    def test_parse_modern_base64url_with_padding_accepted(self):
        encoded = b64url_pad(request_json())
        result = self.client.parse_mpp_challenge(modern_header(encoded))

        assert result.is_modern is True
        assert result.invoice == FIXTURE_INVOICE
        # The received string (padding included) is preserved byte-exact for the echo.
        assert result.request_encoded == encoded

    def test_parse_superset_header_modern_wins(self):
        encoded = b64url_nopad(request_json())
        header = (
            modern_header(encoded)
            + f', invoice="{FIXTURE_INVOICE}", amount="10", currency="sat"'
        )
        result = self.client.parse_mpp_challenge(header)

        assert result.is_modern is True
        assert result.invoice == FIXTURE_INVOICE

    def test_parse_malformed_modern_bad_base64_no_legacy_raises(self):
        with pytest.raises(L402Error):
            self.client.parse_mpp_challenge(modern_header("!!!not-base64url!!!"))

    def test_parse_malformed_modern_bad_json_no_legacy_raises(self):
        with pytest.raises(L402Error):
            self.client.parse_mpp_challenge(modern_header(b64url_nopad("this is not json")))

    def test_parse_malformed_modern_missing_invoice_no_legacy_raises(self):
        payload = json.dumps({"amount": "10", "currency": "sat", "methodDetails": {}})
        with pytest.raises(L402Error):
            self.client.parse_mpp_challenge(modern_header(b64url_nopad(payload)))

    def test_parse_malformed_modern_with_legacy_invoice_falls_back_to_legacy(self):
        # Rule 4: legacy params in the SAME header are an intentional fallback.
        header = (
            modern_header("!!!not-base64url!!!")
            + f', invoice="{FIXTURE_INVOICE}", amount="10", currency="sat"'
        )
        result = self.client.parse_mpp_challenge(header)

        assert result.is_modern is False
        assert result.invoice == FIXTURE_INVOICE

    def test_parse_modern_wrong_intent_raises(self):
        encoded = b64url_nopad(request_json())
        header = (
            f'Payment id="chal-1", realm="r", method="lightning", intent="refund", '
            f'request="{encoded}", expires="{FUTURE_EXPIRY}"'
        )
        with pytest.raises(L402Error):
            self.client.parse_mpp_challenge(header)

    def test_parse_modern_missing_intent_raises(self):
        encoded = b64url_nopad(request_json())
        header = (
            f'Payment id="chal-1", realm="r", method="lightning", '
            f'request="{encoded}", expires="{FUTURE_EXPIRY}"'
        )
        with pytest.raises(L402Error):
            self.client.parse_mpp_challenge(header)

    def test_parse_modern_wrong_currency_raises(self):
        payload = json.dumps(
            {"amount": "10", "currency": "usd", "methodDetails": {"invoice": FIXTURE_INVOICE}}
        )
        with pytest.raises(L402Error):
            self.client.parse_mpp_challenge(modern_header(b64url_nopad(payload)))

    def test_parse_modern_no_currency_accepted(self):
        payload = json.dumps({"amount": "10", "methodDetails": {"invoice": FIXTURE_INVOICE}})
        result = self.client.parse_mpp_challenge(modern_header(b64url_nopad(payload)))
        assert result.is_modern is True

    def test_parse_legacy_unchanged(self):
        header = (
            f'Payment realm="api.example.com", method="lightning", '
            f'invoice="{FIXTURE_INVOICE}", amount="100", currency="sat"'
        )
        result = self.client.parse_mpp_challenge(header)

        assert result.is_modern is False
        assert result.invoice == FIXTURE_INVOICE
        assert result.amount == "100"


class TestModernChallengeExpiry:
    def setup_method(self):
        self.client = L402Client(wallet=MockWallet())  # type: ignore

    def _parse(self, expires: str) -> MppChallenge:
        return self.client.parse_mpp_challenge(
            modern_header(b64url_nopad(request_json()), expires=expires)
        )

    def test_future_expiry_not_expired(self):
        assert self._parse(FUTURE_EXPIRY).is_expired() is False

    def test_past_expiry_is_expired(self):
        challenge = self._parse("2001-01-01T00:00:00Z")
        # An expired challenge still parses — the flow refuses it before paying.
        assert challenge.is_expired() is True

    def test_no_expires_not_expired(self):
        encoded = b64url_nopad(request_json())
        header = (
            f'Payment id="chal-1", realm="r", method="lightning", intent="charge", '
            f'request="{encoded}"'
        )
        assert self.client.parse_mpp_challenge(header).is_expired() is False

    def test_unparseable_expires_fails_closed(self):
        assert self._parse("not-a-date").is_expired() is True


class TestModernCredentialBuild:
    def setup_method(self):
        self.client = L402Client(wallet=MockWallet())  # type: ignore

    def test_credential_echoes_challenge_byte_exact(self):
        encoded = b64url_nopad(request_json())
        challenge = self.client.parse_mpp_challenge(modern_header(encoded))

        credential = challenge.build_modern_credential(FIXTURE_PREIMAGE)

        assert "=" not in credential, "base64url output must have no padding"
        decoded = decode_credential(credential)
        echo = decoded["challenge"]
        assert echo["id"] == "chal-1"
        assert echo["realm"] == "api.example.com"
        assert echo["method"] == "lightning"
        assert echo["intent"] == "charge"
        assert echo["request"] == encoded, "the encoded request string must be echoed byte-exact"
        assert echo["expires"] == FUTURE_EXPIRY
        assert decoded["payload"]["preimage"] == FIXTURE_PREIMAGE

    def test_padded_request_echoed_with_padding(self):
        encoded = b64url_pad(request_json())
        challenge = self.client.parse_mpp_challenge(modern_header(encoded))

        decoded = decode_credential(challenge.build_modern_credential(FIXTURE_PREIMAGE))
        assert decoded["challenge"]["request"] == encoded, "echo as received — never re-encode"

    def test_uppercase_preimage_lowercased(self):
        challenge = self.client.parse_mpp_challenge(modern_header(b64url_nopad(request_json())))
        decoded = decode_credential(challenge.build_modern_credential(FIXTURE_PREIMAGE.upper()))
        assert decoded["payload"]["preimage"] == FIXTURE_PREIMAGE

    def test_optional_params_echoed_when_present(self):
        encoded = b64url_nopad(request_json())
        header = (
            modern_header(encoded)
            + ', digest="fixture-digest", description="desc", opaque="fixture-opaque"'
        )
        challenge = self.client.parse_mpp_challenge(header)

        echo = decode_credential(challenge.build_modern_credential(FIXTURE_PREIMAGE))["challenge"]
        assert echo["digest"] == "fixture-digest"
        assert echo["description"] == "desc"
        assert echo["opaque"] == "fixture-opaque"

    def test_absent_optional_params_omitted(self):
        challenge = self.client.parse_mpp_challenge(modern_header(b64url_nopad(request_json())))
        echo = decode_credential(challenge.build_modern_credential(FIXTURE_PREIMAGE))["challenge"]
        assert "digest" not in echo
        assert "description" not in echo
        assert "opaque" not in echo

    def test_legacy_superset_extras_not_echoed(self):
        encoded = b64url_nopad(request_json())
        header = (
            modern_header(encoded)
            + f', invoice="{FIXTURE_INVOICE}", amount="10", currency="sat"'
        )
        challenge = self.client.parse_mpp_challenge(header)

        echo = decode_credential(challenge.build_modern_credential(FIXTURE_PREIMAGE))["challenge"]
        assert "invoice" not in echo, "legacy extras are unknown params — never echoed"
        assert "amount" not in echo
        assert "currency" not in echo

    def test_modern_token_header_format(self):
        token = MppModernToken(credential="abc123")
        assert token.to_header() == "Payment abc123"

    def test_legacy_challenge_cannot_build_modern_credential(self):
        header = f'Payment method="lightning", invoice="{FIXTURE_INVOICE}"'
        challenge = self.client.parse_mpp_challenge(header)
        with pytest.raises(L402Error):
            challenge.build_modern_credential(FIXTURE_PREIMAGE)


class TestPaymentReceiptParsing:
    def test_valid_receipt(self):
        payload = json.dumps(
            {
                "challengeId": "chal-1",
                "method": "lightning",
                "reference": FIXTURE_PAYMENT_HASH,
                "status": "settled",
                "timestamp": "2026-08-23T12:00:00Z",
            }
        )
        receipt = parse_payment_receipt(b64url_nopad(payload))

        assert receipt is not None
        assert receipt["challengeId"] == "chal-1"
        assert receipt["method"] == "lightning"
        assert receipt["reference"] == FIXTURE_PAYMENT_HASH
        assert receipt["status"] == "settled"
        assert receipt["timestamp"] == "2026-08-23T12:00:00Z"

    def test_padded_receipt_accepted(self):
        payload = json.dumps({"challengeId": "chal-1", "status": "settled"})
        receipt = parse_payment_receipt(b64url_pad(payload))
        assert receipt is not None
        assert receipt["challengeId"] == "chal-1"

    def test_partial_fields_tolerated(self):
        receipt = parse_payment_receipt(b64url_nopad(json.dumps({"status": "settled"})))
        assert receipt is not None
        assert receipt["status"] == "settled"
        assert receipt.get("challengeId") is None

    def test_none_or_empty_returns_none(self):
        assert parse_payment_receipt(None) is None
        assert parse_payment_receipt("") is None
        assert parse_payment_receipt("   ") is None

    def test_bad_base64_returns_none(self):
        assert parse_payment_receipt("!!!not-base64!!!") is None

    def test_bad_json_returns_none(self):
        assert parse_payment_receipt(b64url_nopad("not json")) is None


class TestModernPrecedence:
    def setup_method(self):
        self.client = L402Client(wallet=MockWallet())  # type: ignore

    def test_modern_preferred_over_legacy_separate_headers(self):
        headers = [
            'Payment realm="legacy", method="lightning", invoice="lnbc200n1pjlegacy", amount="20", currency="sat"',
            modern_header(b64url_nopad(request_json())),
        ]
        result = self.client._select_best_challenge(headers)

        assert isinstance(result, MppChallenge)
        assert result.is_modern is True
        assert result.invoice == FIXTURE_INVOICE

    def test_modern_preferred_over_legacy_modern_first(self):
        headers = [
            modern_header(b64url_nopad(request_json())),
            'Payment realm="legacy", method="lightning", invoice="lnbc200n1pjlegacy"',
        ]
        result = self.client._select_best_challenge(headers)

        assert result.is_modern is True
        assert result.invoice == FIXTURE_INVOICE

    def test_l402_still_preferred_over_modern_payment(self):
        # The existing L402-vs-Payment order must NOT change — only modern-vs-legacy
        # preference WITHIN the Payment scheme is new.
        headers = [
            modern_header(b64url_nopad(request_json())),
            'L402 macaroon="YWJjZGVm", invoice="lnbc100n1pjl402"',
        ]
        result = self.client._select_best_challenge(headers)

        assert isinstance(result, L402Challenge)
        assert result.invoice == "lnbc100n1pjl402"

    def test_multi_challenge_single_header_l402_plus_modern(self):
        combined = (
            'L402 macaroon="YWJjZGVm", invoice="lnbc100n1pjl402", '
            + modern_header(b64url_nopad(request_json()))
        )
        result = self.client._select_best_challenge([combined])
        assert isinstance(result, L402Challenge)

        # And with only the modern Payment part, the modern challenge is selected.
        modern_only = self.client._select_best_challenge(
            [modern_header(b64url_nopad(request_json()))]
        )
        assert isinstance(modern_only, MppChallenge)
        assert modern_only.is_modern is True

    def test_legacy_only_still_works(self):
        headers = [
            f'Payment realm="legacy", method="lightning", invoice="{FIXTURE_INVOICE}", amount="10", currency="sat"'
        ]
        result = self.client._select_best_challenge(headers)
        assert isinstance(result, MppChallenge)
        assert result.is_modern is False


def _grant_reservation(budget):
    budget.try_reserve = AsyncMock(
        side_effect=lambda amt: SpendReservationResult.reserved("resv-1", amt)
    )
    budget.commit_reservation = MagicMock()
    budget.release_reservation = MagicMock()
    return budget


class TestModernFlow:
    """402 with a modern Payment challenge → pay → retry with the modern credential →
    surface the Payment-Receipt header."""

    def _build(self, challenge_header: str, retry_response: httpx.Response):
        req = httpx.Request("GET", "https://api.provider.com/premium")
        challenge = httpx.Response(
            402, headers=[("WWW-Authenticate", challenge_header)], request=req
        )
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value=FIXTURE_PREIMAGE)
        client = L402Client(wallet=wallet)
        client._http_client.request = AsyncMock(side_effect=[challenge, retry_response])
        return client, wallet

    @staticmethod
    def _ok(headers: dict | None = None) -> httpx.Response:
        req = httpx.Request("GET", "https://api.provider.com/premium")
        return httpx.Response(200, headers=headers or {}, request=req, content=b"ok")

    @pytest.mark.asyncio
    async def test_modern_challenge_retries_with_modern_credential(self):
        encoded = b64url_nopad(request_json())
        client, wallet = self._build(modern_header(encoded), self._ok())

        with patch.object(client, "_get_invoice_amount_msat", return_value=10_000):
            text, amount, receipt = await client.fetch(
                "https://api.provider.com/premium", max_sats=1000
            )

        assert text == "ok"
        assert amount == 10
        assert receipt is None
        wallet.pay_invoice.assert_awaited_once_with(FIXTURE_INVOICE)

        auth = client._http_client.request.call_args_list[1].kwargs["headers"]["Authorization"]
        assert auth.startswith("Payment ")
        assert "preimage=" not in auth, "modern credential is a base64url blob, not the legacy format"
        decoded = decode_credential(auth[len("Payment "):])
        assert decoded["challenge"]["request"] == encoded
        assert decoded["payload"]["preimage"] == FIXTURE_PREIMAGE

    @pytest.mark.asyncio
    async def test_superset_header_uses_modern_credential(self):
        encoded = b64url_nopad(request_json())
        superset = (
            modern_header(encoded)
            + f', invoice="{FIXTURE_INVOICE}", amount="10", currency="sat"'
        )
        client, _ = self._build(superset, self._ok())

        with patch.object(client, "_get_invoice_amount_msat", return_value=10_000):
            text, amount, _receipt = await client.fetch(
                "https://api.provider.com/premium", max_sats=1000
            )

        auth = client._http_client.request.call_args_list[1].kwargs["headers"]["Authorization"]
        assert auth.startswith("Payment ")
        assert "preimage=" not in auth

    @pytest.mark.asyncio
    async def test_legacy_payment_challenge_still_uses_legacy_header(self):
        legacy = (
            f'Payment realm="api.example.com", method="lightning", '
            f'invoice="{FIXTURE_INVOICE}", amount="10", currency="sat"'
        )
        client, _ = self._build(legacy, self._ok())

        with patch.object(client, "_get_invoice_amount_msat", return_value=10_000):
            await client.fetch("https://api.provider.com/premium", max_sats=1000)

        auth = client._http_client.request.call_args_list[1].kwargs["headers"]["Authorization"]
        assert auth == f'Payment method="lightning", preimage="{FIXTURE_PREIMAGE}"'

    @pytest.mark.asyncio
    async def test_expired_modern_challenge_refused_before_payment(self):
        encoded = b64url_nopad(request_json())
        client, wallet = self._build(
            modern_header(encoded, expires="2001-01-01T00:00:00Z"), self._ok()
        )

        with patch.object(client, "_get_invoice_amount_msat", return_value=10_000):
            with pytest.raises(L402Error, match="expired"):
                await client.fetch("https://api.provider.com/premium", max_sats=1000)

        wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_modern_amount_mismatch_refused_before_payment(self):
        # Challenge declares 9999 sats but the invoice is 10 sats — inconsistent, refuse.
        encoded = b64url_nopad(request_json(amount="9999"))
        client, wallet = self._build(modern_header(encoded), self._ok())

        with patch.object(client, "_get_invoice_amount_msat", return_value=10_000):
            with pytest.raises(L402Error):
                await client.fetch("https://api.provider.com/premium", max_sats=100000)

        wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_payment_receipt_surfaced(self):
        encoded = b64url_nopad(request_json())
        receipt_payload = json.dumps(
            {
                "challengeId": "chal-1",
                "method": "lightning",
                "reference": FIXTURE_PAYMENT_HASH,
                "status": "settled",
                "timestamp": "2026-08-23T12:00:00Z",
            }
        )
        client, _ = self._build(
            modern_header(encoded),
            self._ok(headers={"Payment-Receipt": b64url_nopad(receipt_payload)}),
        )

        with patch.object(client, "_get_invoice_amount_msat", return_value=10_000):
            text, amount, receipt = await client.fetch(
                "https://api.provider.com/premium", max_sats=1000
            )

        assert receipt is not None
        assert receipt["challengeId"] == "chal-1"
        assert receipt["reference"] == FIXTURE_PAYMENT_HASH
        assert receipt["status"] == "settled"

    @pytest.mark.asyncio
    async def test_malformed_receipt_does_not_fail_success(self):
        encoded = b64url_nopad(request_json())
        client, _ = self._build(
            modern_header(encoded), self._ok(headers={"Payment-Receipt": "!!!garbage!!!"})
        )

        with patch.object(client, "_get_invoice_amount_msat", return_value=10_000):
            text, amount, receipt = await client.fetch(
                "https://api.provider.com/premium", max_sats=1000
            )

        assert text == "ok"
        assert amount == 10
        assert receipt is None

    @pytest.mark.asyncio
    async def test_non_402_returns_no_receipt(self):
        req = httpx.Request("GET", "https://api.example.com/data")
        response = httpx.Response(200, request=req, content=b"free")
        client = L402Client(wallet=MockWallet())  # type: ignore
        client._http_client.request = AsyncMock(return_value=response)

        text, amount, receipt = await client.fetch("https://api.example.com/data")

        assert text == "free"
        assert amount is None
        assert receipt is None

    @pytest.mark.asyncio
    async def test_modern_credential_is_single_use_never_replayed(self):
        # Two sequential fetches: each starts unauthenticated (fresh 402) and pays anew.
        # The modern credential from the first fetch must NOT be replayed on the second.
        encoded = b64url_nopad(request_json())
        req = httpx.Request("GET", "https://api.provider.com/premium")

        def challenge():
            return httpx.Response(
                402, headers=[("WWW-Authenticate", modern_header(encoded))], request=req
            )

        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value=FIXTURE_PREIMAGE)
        client = L402Client(wallet=wallet)
        client._http_client.request = AsyncMock(
            side_effect=[challenge(), self._ok(), challenge(), self._ok()]
        )

        with patch.object(client, "_get_invoice_amount_msat", return_value=10_000):
            await client.fetch("https://api.provider.com/premium", max_sats=1000)
            await client.fetch("https://api.provider.com/premium", max_sats=1000)

        assert wallet.pay_invoice.await_count == 2
        assert client._http_client.request.await_count == 4
        # The first request of the SECOND fetch carries no Authorization — no cached credential.
        third_headers = client._http_client.request.call_args_list[2].kwargs["headers"]
        assert "Authorization" not in third_headers, (
            "modern credentials are single-use and must never be served from a cache"
        )


class TestPayChallengeToolModern:
    """pay_l402_challenge with challenge_header: pays the invoice inside a modern Payment
    challenge and returns a single-use Payment credential."""

    def _decoded_invoice(self):
        decoded = MagicMock()
        decoded.amount_msat = 10_000  # 10 sats
        decoded.amount = 10
        return decoded

    @pytest.mark.asyncio
    async def test_modern_challenge_header_pays_and_returns_credential(self):
        from lightning_enable_mcp.tools.pay_challenge import pay_l402_challenge

        encoded = b64url_nopad(request_json())
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value=FIXTURE_PREIMAGE)

        with patch(
            "lightning_enable_mcp.tools.pay_challenge.decode_bolt11",
            return_value=self._decoded_invoice(),
        ):
            result = await pay_l402_challenge(
                invoice="",
                challenge_header=modern_header(encoded),
                wallet=wallet,
            )
        data = json.loads(result)

        assert data["success"] is True
        assert data["singleUse"] is True
        header_value = data["usage"]["headerValue"]
        assert header_value.startswith("Payment ")
        assert "preimage=" not in header_value
        decoded = decode_credential(header_value[len("Payment "):])
        assert decoded["challenge"]["request"] == encoded
        assert decoded["payload"]["preimage"] == FIXTURE_PREIMAGE
        assert data["authorization_header"] == header_value
        # The invoice was taken from inside the challenge.
        wallet.pay_invoice.assert_awaited_once_with(FIXTURE_INVOICE)

    @pytest.mark.asyncio
    async def test_uppercase_preimage_lowercased_in_credential(self):
        from lightning_enable_mcp.tools.pay_challenge import pay_l402_challenge

        encoded = b64url_nopad(request_json())
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value=FIXTURE_PREIMAGE.upper())

        with patch(
            "lightning_enable_mcp.tools.pay_challenge.decode_bolt11",
            return_value=self._decoded_invoice(),
        ):
            result = await pay_l402_challenge(
                invoice="", challenge_header=modern_header(encoded), wallet=wallet
            )
        data = json.loads(result)

        assert data["success"] is True
        decoded = decode_credential(data["usage"]["headerValue"][len("Payment "):])
        assert decoded["payload"]["preimage"] == FIXTURE_PREIMAGE

    @pytest.mark.asyncio
    async def test_expired_modern_challenge_refused_before_paying(self):
        from lightning_enable_mcp.tools.pay_challenge import pay_l402_challenge

        encoded = b64url_nopad(request_json())
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value=FIXTURE_PREIMAGE)

        result = await pay_l402_challenge(
            invoice="",
            challenge_header=modern_header(encoded, expires="2001-01-01T00:00:00Z"),
            wallet=wallet,
        )
        data = json.loads(result)

        assert data["success"] is False
        assert "expired" in data["error"]
        wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_invoice_mismatching_challenge_refused(self):
        from lightning_enable_mcp.tools.pay_challenge import pay_l402_challenge

        encoded = b64url_nopad(request_json())
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value=FIXTURE_PREIMAGE)

        result = await pay_l402_challenge(
            invoice="lnbc200n1pjother",
            challenge_header=modern_header(encoded),
            wallet=wallet,
        )
        data = json.loads(result)

        assert data["success"] is False
        assert "does not match" in data["error"]
        wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_macaroon_with_modern_challenge_refused(self):
        from lightning_enable_mcp.tools.pay_challenge import pay_l402_challenge

        encoded = b64url_nopad(request_json())
        wallet = AsyncMock()

        result = await pay_l402_challenge(
            invoice="",
            macaroon="YWJjZGVm",
            challenge_header=modern_header(encoded),
            wallet=wallet,
        )
        data = json.loads(result)

        assert data["success"] is False
        assert "macaroon" in data["error"]

    @pytest.mark.asyncio
    async def test_unparseable_challenge_header_errors(self):
        from lightning_enable_mcp.tools.pay_challenge import pay_l402_challenge

        wallet = AsyncMock()
        result = await pay_l402_challenge(
            invoice="", challenge_header="Bearer not-a-payment-challenge", wallet=wallet
        )
        data = json.loads(result)

        assert data["success"] is False
        assert data["error"]

    @pytest.mark.asyncio
    async def test_no_invoice_and_no_challenge_header_errors(self):
        from lightning_enable_mcp.tools.pay_challenge import pay_l402_challenge

        wallet = AsyncMock()
        result = await pay_l402_challenge(invoice="", wallet=wallet)
        data = json.loads(result)

        assert data["success"] is False
        assert "Invoice" in data["error"]

    @pytest.mark.asyncio
    async def test_legacy_payment_challenge_header_pays_legacy_mpp(self):
        from lightning_enable_mcp.tools.pay_challenge import pay_l402_challenge

        legacy = (
            f'Payment realm="api.example.com", method="lightning", '
            f'invoice="{FIXTURE_INVOICE}", amount="10", currency="sat"'
        )
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value=FIXTURE_PREIMAGE)

        with patch(
            "lightning_enable_mcp.tools.pay_challenge.decode_bolt11",
            return_value=self._decoded_invoice(),
        ):
            result = await pay_l402_challenge(
                invoice="", challenge_header=legacy, wallet=wallet
            )
        data = json.loads(result)

        assert data["success"] is True
        assert data["usage"]["headerValue"] == (
            f'Payment method="lightning", preimage="{FIXTURE_PREIMAGE}"'
        ), "a legacy Payment challenge keeps the legacy credential format"


class TestAccessResourceSurfacesReceipt:
    @pytest.mark.asyncio
    async def test_payment_receipt_included_in_tool_result(self):
        from lightning_enable_mcp.tools.access_resource import access_l402_resource

        receipt = {
            "challengeId": "chal-1",
            "method": "lightning",
            "reference": FIXTURE_PAYMENT_HASH,
            "status": "settled",
            "timestamp": "2026-08-23T12:00:00Z",
        }
        l402_client = AsyncMock()
        l402_client.fetch = AsyncMock(return_value=("paid body", 10, receipt))

        result = await access_l402_resource(
            url="https://api.example.com/data", l402_client=l402_client
        )
        data = json.loads(result)

        assert data["success"] is True
        assert data["paymentReceipt"]["challengeId"] == "chal-1"
        assert data["paymentReceipt"]["reference"] == FIXTURE_PAYMENT_HASH

    @pytest.mark.asyncio
    async def test_no_receipt_key_absent_or_none(self):
        from lightning_enable_mcp.tools.access_resource import access_l402_resource

        l402_client = AsyncMock()
        l402_client.fetch = AsyncMock(return_value=("paid body", 10, None))

        result = await access_l402_resource(
            url="https://api.example.com/data", l402_client=l402_client
        )
        data = json.loads(result)

        assert data["success"] is True
        assert data.get("paymentReceipt") is None
