"""``l402_producer(action="sell_url")`` against a scripted claim endpoint.

Quote, phase 1 (402), payment through ``pay_l402_challenge``, phase 2, key storage, error
mapping, phase-2 retries, crash recovery, the money-safety rules for ambiguous payments, and
the redeemed marker. No network: the claim endpoint is an ``httpx.MockTransport`` and the
wallet is a mock.

Mirrors ``dotnet/tests/LightningEnable.Mcp.Tests/Tools/ProducerSellUrlTests.cs``; both read
``shared/l402-producer-sell-url.json``.
"""

from __future__ import annotations

import asyncio
import json
import re
from collections import deque
from datetime import datetime, timedelta, timezone
from decimal import Decimal
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, patch

import httpx
import pytest

from lightning_enable_mcp.budget_service import SpendReservationResult
from lightning_enable_mcp.config import ApprovalLevel
from lightning_enable_mcp.operation_ledger import OperationRecord, OperationState
from lightning_enable_mcp.tools import producer_sell_url as sell
from lightning_enable_mcp.wallet_errors import PaymentPendingError
from tests.confirmation_helpers import make_pending, setup_delivered

TARGET_URL = "https://api.example-weather.dev/v1"
ADDRESS = "alice@wallet.example.com"
TERMS = "2026-11"
MACAROON = "MDAyZGxvY2F0aW9uIGZpeHR1cmU"
PREIMAGE = "ea195ec92c7e00000000000000000000000000000000000000000000000000aa"
INVOICE = "lnbc1500n1pjfixtureinvoice"
FRESH_KEY = "test-fresh-key-not-a-credential-0001"  # gitleaks:allow
CLAIM_URL = "https://api.example.test/api/producer/claim"
PRICING_NOTE = "Server pricing note, verbatim."
SERVER_BODY_RULE = "Server body rule, verbatim."


def _find_shared(name: str) -> Path:
    for parent in Path(__file__).resolve().parents:
        candidate = parent / "shared" / name
        if candidate.exists():
            return candidate
    raise FileNotFoundError(f"shared/{name} not found above {__file__}")


FIXTURE = json.loads(_find_shared("l402-producer-sell-url.json").read_text(encoding="utf-8"))


# ── Fixtures ─────────────────────────────────────────────────────────────────────────


def quote_json(sats: int = 150, terms: str = TERMS) -> dict:
    return {
        "enabled": True,
        "price": {"sats": sats, "usdCents": 10, "validForSeconds": 900},
        "covers": {"days": 30, "plan": "individual"},
        "renewal": {"endpoint": "/api/merchant/subscription/renew", "baseSats": 30000, "periodDays": 30},
        "limits": {"maxProxyPriceSats": 1000000, "minProxyPriceSats": 1},
        "terms": {"version": terms, "url": "https://lightningenable.com/terms"},
        "listing": {"available": False, "note": "pending verification"},
        "pricingNote": PRICING_NOTE,
        "bodyRule": SERVER_BODY_RULE,
    }


def challenge_json(now: datetime, sats: int = 150) -> dict:
    return {
        "invoice": INVOICE,
        "macaroon": MACAROON,
        "paymentHash": "2859e1e143bc0000",
        # .NET round-trip format, seven fractional digits.
        "expiresAt": (now + timedelta(minutes=10)).strftime("%Y-%m-%dT%H:%M:%S.%f0Z"),
        "priceSats": sats,
        "instructions": "Pay the Lightning invoice, then retry.",
    }


def claimed_json(replayed=False, api_key=FRESH_KEY, key_rotated=False, recovery="none") -> dict:
    body = {
        "merchantId": 3,
        "proxy": {
            "proxyId": "claimed-weather-api",
            "url": "https://api.example.test/l402/proxy/claimed-weather-api/",
            "manifest": "https://api.example.test/l402/proxy/claimed-weather-api/.well-known/l402-manifest.json",
            "openapi": "https://api.example.test/l402/proxy/claimed-weather-api/openapi.json",
            "active": True,
        },
        "receive": {"lane": "lightningAddress", "status": "live"},
        "connectUrl": None,
        "paidThrough": "2026-12-08T21:00:30Z",
        "renew": {"endpoint": "/api/merchant/subscription/renew", "quoteSats": 30000, "periodDays": 30},
        "recovery": recovery,
        "listPublicly": False,
        "listing": {"requested": False, "listed": False, "reason": "verification_required"},
        "warnings": ["No recovery binding: this API key is the only credential. Store it securely."],
        "replayed": replayed,
        "keyRotated": key_rotated,
    }
    if api_key is not None:
        body["apiKey"] = api_key
    return body


def problem(status: int, code: str, detail: str, **extra) -> dict:
    body = {
        "type": "https://lightningenable.com/problems/" + code,
        "title": code,
        "status": status,
        "detail": detail,
        "error": code,
        "message": detail,
    }
    body.update(extra)
    return body


class StubClaim:
    """Scripted claim endpoint. Each entry: (status, json-or-None, headers) or an exception."""

    def __init__(self):
        self.quotes: deque = deque()
        self.posts: deque = deque()
        self.requests: list[httpx.Request] = []

    def handler(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        queue = self.quotes if request.method == "GET" else self.posts
        item = queue.popleft()
        if isinstance(item, BaseException):
            raise item
        status, body, headers = item
        content = b"" if body is None else json.dumps(body).encode()
        return httpx.Response(status, content=content, headers=headers or {})

    @property
    def post_requests(self) -> list[httpx.Request]:
        return [r for r in self.requests if r.method == "POST"]

    @property
    def quote_calls(self) -> int:
        return sum(1 for r in self.requests if r.method == "GET")


def make_claim_client(stub: StubClaim) -> sell.ProducerClaimClient:
    client = sell.ProducerClaimClient()
    client.claim_url = CLAIM_URL
    client._client = httpx.AsyncClient(transport=httpx.MockTransport(stub.handler))
    return client


def make_budget(level=ApprovalLevel.AUTO_APPROVE, denial=None):
    budget = MagicMock()
    approval = MagicMock()
    approval.level = level
    approval.requires_confirmation = level in (ApprovalLevel.FORM_CONFIRM, ApprovalLevel.URL_CONFIRM)
    approval.amount_usd = Decimal("0.10")
    approval.denial_reason = denial
    budget.check_approval_level = AsyncMock(return_value=approval)
    budget.try_reserve = AsyncMock(side_effect=lambda amt: SpendReservationResult.reserved("resv-1", amt))
    budget.commit_reservation = MagicMock()
    budget.release_reservation = MagicMock()
    budget.record_payment_time = MagicMock()
    return budget


def ledger_with_record():
    ledger = MagicMock()
    ledger.lookup = MagicMock(
        return_value=OperationRecord("ln:x", OperationState.SUBMITTED, 150)
    )
    return ledger


@pytest.fixture
def env(tmp_path, monkeypatch):
    """Isolated config dir, no merchant key in the environment, instant retries, 150-sat invoices."""
    monkeypatch.delenv("LIGHTNING_ENABLE_API_KEY", raising=False)
    delays: list[float] = []

    async def fake_sleep(seconds):
        delays.append(seconds)

    monkeypatch.setattr(sell, "_sleep", fake_sleep)
    decoded = MagicMock()
    decoded.amount_msat = 150_000
    decoded.amount = 150
    with patch("lightning_enable_mcp.tools.pay_challenge.decode_bolt11", return_value=decoded):
        stub = StubClaim()
        wallet = AsyncMock()
        wallet.pay_invoice = AsyncMock(return_value=PREIMAGE)
        yield {
            "stub": stub,
            "client": make_claim_client(stub),
            "wallet": wallet,
            "budget": make_budget(),
            "ledger": None,
            "now": datetime.now(timezone.utc),
            "config": tmp_path / "config.json",
            "dir": tmp_path,
            "delays": delays,
        }


async def call(env, **overrides) -> tuple[dict, str]:
    kwargs = dict(
        target_url=TARGET_URL,
        lightning_address=ADDRESS,
        default_price_sats=21,
        description="Weather for agents",
        accept_terms=TERMS,
        claim_client=env["client"],
        wallet=env["wallet"],
        budget_service=env["budget"],
        payment_history_service=None,
        operation_ledger=env["ledger"],
        config_path=str(env["config"]),
        now=lambda: env["now"],
    )
    kwargs.update(overrides)
    raw = await sell.sell_url(**kwargs)
    return json.loads(raw), raw


def queue_quote_and_challenge(env) -> None:
    env["stub"].quotes.append((200, quote_json(), None))
    env["stub"].posts.append((402, challenge_json(env["now"]), None))


def state_files(env) -> list[Path]:
    d = env["dir"] / "claim"
    return sorted(d.glob("sell-*.json")) if d.exists() else []


def only_state(env) -> dict:
    (path,) = state_files(env)
    return json.loads(path.read_text())


def body_for_defaults() -> str:
    return sell.build_body(
        TARGET_URL, "api.example-weather.dev", "Weather for agents", 21, ADDRESS,
        None, None, False, TERMS,
    )


def save_state(env, phase: str, preimage=None, **extra) -> Path:
    state = sell.ClaimState(
        directory=env["dir"] / "claim",
        body=body_for_defaults(),
        claim_url=CLAIM_URL,
        invoice=INVOICE,
        macaroon=MACAROON,
        payment_hash="2859e1e143bc0000",
        expires_at=(env["now"] + timedelta(minutes=5)).isoformat(),
        price_sats=150,
        phase=phase,
        preimage=preimage,
    )
    for key, value in extra.items():
        setattr(state, key, value)
    state.save()
    return Path(state.file_path)


def assert_no_secrets(raw: str) -> None:
    assert MACAROON not in raw, "the macaroon never enters a tool result"
    assert PREIMAGE not in raw, "the preimage never enters a tool result"


# ── Quote ────────────────────────────────────────────────────────────────────────────


class TestQuote:
    @pytest.mark.parametrize("accept_terms", [None, "2025-01"])
    async def test_without_accepted_terms_returns_the_quote_and_posts_nothing(self, env, accept_terms):
        env["stub"].quotes.append((200, quote_json(), None))

        result, _ = await call(env, accept_terms=accept_terms)

        assert result["success"] is True
        assert result["stage"] == "quote"
        assert result["requiresTermsAcceptance"] is True
        assert result["price"]["sats"] == 150
        assert result["terms"] == {"version": TERMS, "url": "https://lightningenable.com/terms"}
        assert f'accept_terms="{TERMS}"' in result["nextStep"]
        assert result["name"] == "api.example-weather.dev", "name defaults to the hostname"
        assert "recoveryEmailWarning" not in result
        assert env["stub"].post_requests == []
        env["wallet"].pay_invoice.assert_not_called()
        assert state_files(env) == []

    async def test_quote_carries_the_servers_pricing_note_and_body_rule_verbatim(self, env):
        env["stub"].quotes.append((200, quote_json(), None))

        result, _ = await call(env, accept_terms=None)

        assert result["pricingNote"] == PRICING_NOTE
        assert result["bodyRule"] == SERVER_BODY_RULE

    async def test_recovery_email_warns_a_registered_email_is_refused_only_after_payment(self, env):
        env["stub"].quotes.append((200, quote_json(), None))

        result, _ = await call(env, accept_terms=None, recovery_email="op@example.com")

        assert "only after payment" in result["recoveryEmailWarning"]

    async def test_bare_404_means_the_feature_is_not_enabled(self, env):
        env["stub"].quotes.append((404, None, None))

        result, _ = await call(env)

        assert result["error"] == "feature_not_enabled"
        assert result["retryable"] is False
        assert result["paid"] is False
        assert env["stub"].post_requests == []

    async def test_http_target_is_refused_locally(self, env):
        result, _ = await call(env, target_url="http://api.example.com")

        assert result["error"] == "invalid_target_url"
        assert env["stub"].requests == []


# ── Happy path ───────────────────────────────────────────────────────────────────────


class TestHappyPath:
    async def test_quote_pays_redeems_stores_the_key_once_and_leaves_a_secret_free_marker(self, env):
        stub = env["stub"]
        queue_quote_and_challenge(env)
        stub.posts.append((200, claimed_json(), None))

        result, raw = await call(env)

        assert result["success"] is True, raw
        assert result["proxyUrl"].endswith("/l402/proxy/claimed-weather-api/")
        assert result["manifestUrl"].endswith("l402-manifest.json")
        assert result["proxyId"] == "claimed-weather-api"
        assert result["paidThrough"] == "2026-12-08T21:00:30Z"
        assert result["recovery"] == "none"
        assert result["listing"]["listed"] is False
        assert result["apiKeyStorage"]["location"] == "config"
        assert str(env["config"]) in result["apiKeyNote"]
        assert "shown once" in result["apiKeyNote"]

        assert raw.count(FRESH_KEY) == 1
        assert json.loads(env["config"].read_text())["lightningEnableApiKey"] == FRESH_KEY
        assert_no_secrets(raw)

        first, second = stub.post_requests
        assert "authorization" not in first.headers
        assert second.headers["authorization"] == f"L402 {MACAROON}:{PREIMAGE}"
        assert second.content == first.content, "phase 2 resends the byte-identical body"

        body = json.loads(first.content)
        assert body["targetUrl"] == TARGET_URL
        assert body["name"] == "api.example-weather.dev"
        assert body["defaultPriceSats"] == 21
        assert body["receive"] == {"lightningAddress": ADDRESS}
        assert "recovery" not in body

        env["budget"].check_approval_level.assert_awaited_once_with(150)
        env["wallet"].pay_invoice.assert_awaited_once_with(INVOICE)

        marker = only_state(env)
        assert marker["phase"] == "redeemed"
        assert marker["proxyId"] == "claimed-weather-api"
        assert marker["keyLocation"] == "config"
        text = json.dumps(marker)
        assert MACAROON not in text and PREIMAGE not in text and INVOICE not in text

    async def test_re_call_after_success_answers_already_claimed_without_minting_or_paying(self, env):
        queue_quote_and_challenge(env)
        env["stub"].posts.append((200, claimed_json(), None))
        await call(env)

        result, raw = await call(env)

        assert result["success"] is True
        assert result["alreadyClaimed"] is True
        assert result["paidThisCall"] is False
        assert result["proxyUrl"].endswith("/claimed-weather-api/")
        assert result["apiKeyStorage"]["path"] == str(env["config"])
        assert FRESH_KEY not in raw
        assert env["stub"].quote_calls == 1
        assert len(env["stub"].post_requests) == 2
        env["wallet"].pay_invoice.assert_awaited_once()

    async def test_replay_with_key_rotated_stores_the_fresh_key_beside_another_accounts_key(self, env):
        env["config"].write_text(json.dumps({"lightningEnableApiKey": "le_other_account"}))
        queue_quote_and_challenge(env)
        env["stub"].posts.append((200, claimed_json(replayed=True, key_rotated=True), None))

        result, raw = await call(env)

        assert result["success"] is True, raw
        assert result["keyRotated"] is True
        assert result["apiKey"] == FRESH_KEY
        assert result["apiKeyStorage"]["location"] == "key_file"
        note = result["apiKeyNote"]
        assert "rotated" in note and "only valid one" in note
        assert "never" not in note and "not return the API key again" not in note
        key_file = env["dir"] / "claim-claimed-weather-api.key"
        assert key_file.read_text().strip() == FRESH_KEY
        assert json.loads(env["config"].read_text())["lightningEnableApiKey"] == "le_other_account"
        assert_no_secrets(raw)

    async def test_replay_with_key_rotated_replaces_this_claims_own_dead_key_in_config(self, env):
        # The first redeem stored this claim's key in config, then the result was lost before the
        # marker landed: the state still says 'paid', and records where the key went.
        env["config"].write_text(json.dumps({"lightningEnableApiKey": "le_this_claims_old_key"}))
        save_state(env, sell.PAID, PREIMAGE, key_location="config", key_path=str(env["config"]))
        env["stub"].posts.append((200, claimed_json(replayed=True, key_rotated=True), None))

        result, _ = await call(env)

        assert result["apiKeyStorage"]["location"] == "config"
        assert result["apiKeyStorage"]["replacedRotatedKey"] is True
        assert json.loads(env["config"].read_text())["lightningEnableApiKey"] == FRESH_KEY
        assert not (env["dir"] / "claim-claimed-weather-api.key").exists()
        env["wallet"].pay_invoice.assert_not_called()

    async def test_replay_without_a_key_does_not_claim_the_server_never_returns_it(self, env):
        queue_quote_and_challenge(env)
        env["stub"].posts.append((200, claimed_json(replayed=True, api_key=None), None))

        result, _ = await call(env)

        assert result["success"] is True
        assert "apiKey" not in result
        assert "without an API key" in result["apiKeyNote"]
        assert "rotates" in result["apiKeyNote"]
        assert "only the first answer" not in result["apiKeyNote"]


# ── Errors ───────────────────────────────────────────────────────────────────────────


class TestErrors:
    def test_error_table_matches_the_shared_fixture(self):
        assert {code: retry for code, (_, retry) in sell.ERROR_TABLE.items()} == FIXTURE["errorCodes"]
        assert set(sell.RETRY_AFTER_PAYMENT_ALSO) == set(FIXTURE["retryAfterPaymentAlso"])
        assert sell.MAX_REDEEM_ATTEMPTS == FIXTURE["maxRedeemAttempts"]

    def test_canonical_body_matches_the_shared_vector_bytes_and_file_name(self):
        vector = FIXTURE["canonicalBody"]
        i = vector["inputs"]
        body = sell.build_body(
            i["targetUrl"], i["name"], i["description"], i["defaultPriceSats"],
            i["lightningAddress"], i["recoveryEmail"], i["recoveryNostrPubkey"],
            i["listPublicly"], i["acceptTerms"],
        )
        assert body == vector["expectedBody"]
        assert sell.state_file_name(body) == vector["expectedFileName"]

    @pytest.mark.parametrize("code,retryable", sorted(FIXTURE["errorCodes"].items()))
    async def test_every_phase1_code_maps_to_its_message_and_retry_flag_and_costs_nothing(
        self, env, code, retryable
    ):
        stub = env["stub"]
        stub.quotes.append((200, quote_json(), None))
        if code == "feature_not_enabled":
            stub.posts.append((404, None, None))
        elif code == "rate_limited":
            stub.posts.append((429, None, {"Retry-After": "7"}))
        elif code == "network_error":
            stub.posts.append(httpx.ConnectError("connection refused"))
        else:
            status = 501 if code == "connect_not_available" else 400
            stub.posts.append((status, problem(400, code, "server detail for " + code), None))

        result, _ = await call(env)

        assert result["error"] == code
        assert result["retryable"] is retryable
        assert result["message"] == sell.ERROR_TABLE[code][0]
        assert result["paid"] is False
        assert result["stage"] == "phase1"
        env["wallet"].pay_invoice.assert_not_called()
        assert state_files(env) == []

    async def test_pre_payment_refusal_mints_nothing_spends_nothing_and_surfaces_bounds(self, env):
        stub = env["stub"]
        stub.quotes.append((200, quote_json(), None))
        stub.posts.append(
            (
                400,
                problem(
                    400,
                    "invalid_lightning_address",
                    "The price per call is outside what this Lightning Address accepts.",
                    minSendableSats=1,
                    maxSendableSats=10,
                ),
                None,
            )
        )

        result, _ = await call(env)

        assert result["error"] == "invalid_lightning_address"
        assert result["paid"] is False
        assert "costs nothing" in result["note"]
        assert result["problem"] == {"minSendableSats": 1, "maxSendableSats": 10}
        assert len(stub.post_requests) == 1
        env["wallet"].pay_invoice.assert_not_called()
        env["budget"].check_approval_level.assert_not_called()
        assert state_files(env) == []

    async def test_invoice_above_the_quote_is_refused_before_paying(self, env):
        env["stub"].quotes.append((200, quote_json(sats=100), None))
        env["stub"].posts.append((402, challenge_json(env["now"], sats=150), None))

        result, _ = await call(env)

        assert result["error"] == "price_changed"
        assert result["paid"] is False
        env["wallet"].pay_invoice.assert_not_called()

    async def test_both_recovery_kinds_are_refused_locally(self, env):
        result, _ = await call(env, recovery_email="a@example.com", recovery_nostr_pubkey="a" * 64)

        assert result["error"] == "invalid_recovery"
        assert env["stub"].requests == []

    async def test_no_wallet_refuses_before_minting(self, env):
        env["stub"].quotes.append((200, quote_json(), None))

        result, _ = await call(env, wallet=None)

        assert result["error"] == "no_wallet"
        assert env["stub"].post_requests == []


# ── Budget and confirmation ──────────────────────────────────────────────────────────


class TestBudgetAndConfirmation:
    async def test_budget_refusal_pays_nothing_and_says_so(self, env):
        env["budget"] = make_budget(
            ApprovalLevel.DENY, denial="Payment of 150 sats exceeds the per-payment limit"
        )
        queue_quote_and_challenge(env)

        result, raw = await call(env)

        assert result["error"] == "payment_refused"
        assert "per-payment limit" in result["message"]
        assert "Nothing was charged" in result["message"]
        assert result["paid"] is False
        assert len(env["stub"].post_requests) == 1, "no phase 2 without a payment"
        env["wallet"].pay_invoice.assert_not_called()
        assert only_state(env)["phase"] == "challenge"
        assert_no_secrets(raw)

    async def test_over_threshold_requests_the_code_then_same_arguments_plus_code_pay(self, env):
        budget = make_budget(ApprovalLevel.FORM_CONFIRM)
        pending = make_pending(
            nonce="ZZZ999", amount_sats=150, tool_name="pay_l402_challenge", destination=INVOICE
        )
        setup_delivered(budget, pending=pending)
        budget.validate_and_consume_confirmation = MagicMock(return_value=pending)
        env["budget"] = budget
        queue_quote_and_challenge(env)

        first, raw = await call(env)

        assert first["requiresConfirmation"] is True
        assert "sell_url" in first["howToConfirm"] and "confirmation_nonce" in first["howToConfirm"]
        assert "ZZZ999" not in raw
        env["wallet"].pay_invoice.assert_not_called()
        assert only_state(env)["phase"] == "challenge"

        env["stub"].posts.append((200, claimed_json(), None))
        second, _ = await call(env, confirmation_nonce="zzz999")

        assert second["success"] is True
        budget.validate_and_consume_confirmation.assert_called_once_with(
            "ZZZ999", 150, "pay_l402_challenge", INVOICE
        )
        assert env["stub"].quote_calls == 1
        env["wallet"].pay_invoice.assert_awaited_once()


# ── C1: ambiguous payments are never reported as unpaid ─────────────────────────────


class TestAmbiguousPayments:
    async def test_wallet_failure_after_submit_is_payment_state_unknown_and_keeps_paying(self, env):
        env["ledger"] = ledger_with_record()
        env["wallet"].pay_invoice = AsyncMock(side_effect=Exception("NO_RESPONSE"))
        queue_quote_and_challenge(env)

        result, raw = await call(env)

        assert result["error"] == "payment_state_unknown"
        assert result["paid"] == "unknown"
        assert "Do NOT pay again" in result["message"]
        assert "Nothing was charged" not in raw
        assert result["recovery"]["paymentHash"] == "2859e1e143bc0000"
        assert only_state(env)["phase"] == "paying"
        assert_no_secrets(raw)

    async def test_known_pre_wallet_shape_but_ledger_saw_the_invoice_is_still_unknown(self, env):
        env["ledger"] = ledger_with_record()
        env["budget"] = make_budget(ApprovalLevel.DENY, denial="over the cap")
        queue_quote_and_challenge(env)

        result, _ = await call(env)

        assert result["error"] == "payment_state_unknown"
        assert only_state(env)["phase"] == "paying"

    async def test_payment_unproven_pending_is_paid_true(self, env):
        env["wallet"].pay_invoice = AsyncMock(
            side_effect=PaymentPendingError("not settled yet", provider="nwc")
        )
        queue_quote_and_challenge(env)

        result, raw = await call(env)

        assert result["error"] == "payment_unproven"
        assert result["paid"] is True
        assert "Nothing was charged" not in raw
        assert only_state(env)["phase"] == "paying"

    async def test_resume_from_paying_returns_unknown_without_paying_or_minting(self, env):
        save_state(env, sell.PAYING)

        result, _ = await call(env)

        assert result["error"] == "payment_state_unknown"
        assert result["paid"] == "unknown"
        assert env["stub"].requests == []
        env["wallet"].pay_invoice.assert_not_called()

    async def test_resume_from_paying_when_the_ledger_never_saw_the_invoice_pays(self, env):
        ledger = MagicMock()
        ledger.lookup = MagicMock(return_value=None)
        env["ledger"] = ledger
        save_state(env, sell.PAYING)
        env["stub"].posts.append((200, claimed_json(), None))

        result, _ = await call(env)

        assert result["success"] is True
        env["wallet"].pay_invoice.assert_awaited_once()

    async def test_expired_after_ambiguous_failure_never_mints_or_pays_again(self, env):
        env["wallet"].pay_invoice = AsyncMock(side_effect=Exception("NO_RESPONSE"))
        queue_quote_and_challenge(env)
        await call(env)

        env["now"] = env["now"] + timedelta(hours=2)
        result, _ = await call(env)

        assert result["error"] == "payment_state_unknown"
        assert env["stub"].quote_calls == 1
        assert len(env["stub"].post_requests) == 1, "no second mint"
        assert env["wallet"].pay_invoice.await_count == 1
        assert only_state(env)["phase"] == "paying"

    async def test_two_concurrent_identical_calls_pay_once_and_never_downgrade(self, env):
        gate = asyncio.Event()

        async def slow_pay(_invoice, *args, **kwargs):
            await gate.wait()
            return PREIMAGE

        env["wallet"].pay_invoice = AsyncMock(side_effect=slow_pay)
        queue_quote_and_challenge(env)
        env["stub"].posts.append((200, claimed_json(), None))

        first = asyncio.create_task(call(env))
        second = asyncio.create_task(call(env))
        await asyncio.sleep(0.05)
        gate.set()
        results = [r for r, _ in await asyncio.gather(first, second)]

        assert sum(1 for r in results if "apiKey" in r) == 1
        assert sum(1 for r in results if r.get("alreadyClaimed")) == 1
        assert env["wallet"].pay_invoice.await_count == 1
        assert env["stub"].quote_calls == 1
        assert only_state(env)["phase"] == "redeemed"

    def test_stale_save_cannot_move_the_state_backwards(self, env):
        save_state(env, sell.PAID, PREIMAGE)
        stale = sell.ClaimState(directory=env["dir"] / "claim", body=body_for_defaults(), phase=sell.PAYING)
        downgrade = sell.ClaimState(
            directory=env["dir"] / "claim", body=body_for_defaults(), phase=sell.CHALLENGE
        )

        with pytest.raises(sell.ClaimStateConflict):
            stale.save()
        with pytest.raises(sell.ClaimStateConflict):
            downgrade.save(allow_downgrade_from_paying=True)
        assert only_state(env)["preimage"] == PREIMAGE


# ── Phase 2 retries and recovery ─────────────────────────────────────────────────────


class TestRedeem:
    async def test_transient_phase2_failure_is_retried_with_the_same_body_and_token(self, env):
        stub = env["stub"]
        queue_quote_and_challenge(env)
        stub.posts.append((503, problem(503, "claim_contended", "Try again"), {"Retry-After": "1"}))
        stub.posts.append((400, problem(400, "invalid_lightning_address", "LNURL lookup failed"), None))
        stub.posts.append((200, claimed_json(), None))

        result, _ = await call(env)

        assert result["success"] is True
        posts = stub.post_requests
        assert len(posts) == 4
        assert {p.content for p in posts} == {posts[0].content}
        assert env["delays"] == [1.0, 2.0], "Retry-After is honoured, then backoff"
        env["wallet"].pay_invoice.assert_awaited_once()

    async def test_giving_up_returns_recovery_without_secrets_and_keeps_the_token(self, env):
        stub = env["stub"]
        queue_quote_and_challenge(env)
        for _ in range(sell.MAX_REDEEM_ATTEMPTS):
            stub.posts.append((503, problem(503, "upstream_unavailable", "down"), None))

        result, raw = await call(env)

        assert result["paid"] is True
        assert result["error"] == "upstream_unavailable"
        assert result["redeemAttempts"] == sell.MAX_REDEEM_ATTEMPTS
        recovery = result["recovery"]
        assert recovery["paymentSpent"] is True
        assert "same arguments" in recovery["instructions"]
        assert "do not pay again" in recovery["instructions"]
        saved = json.loads(Path(recovery["stateFile"]).read_text())
        assert saved["macaroon"] == MACAROON
        assert saved["preimage"] == PREIMAGE
        assert_no_secrets(raw)

    async def test_merchant_exists_after_payment_says_do_not_change_arguments(self, env):
        queue_quote_and_challenge(env)
        env["stub"].posts.append((409, problem(409, "merchant_exists", "Account exists"), None))

        result, _ = await call(env, recovery_email="op@example.com")

        assert result["error"] == "merchant_exists"
        assert result["paid"] is True
        assert "Do not change the" in result["message"]
        assert "payment hash" in result["message"]
        assert "use a different recovery" not in result["message"]
        assert env["delays"] == []

    async def test_unexpected_exception_after_payment_reports_paid_with_recovery(self, env):
        queue_quote_and_challenge(env)
        env["stub"].posts.append(RuntimeError("boom"))

        result, raw = await call(env)

        assert result["paid"] is True
        assert result["recovery"]["paymentSpent"] is True
        assert_no_secrets(raw)

    async def test_crash_after_paying_is_recovered_from_the_saved_token(self, env):
        stub = env["stub"]
        queue_quote_and_challenge(env)
        for _ in range(sell.MAX_REDEEM_ATTEMPTS):
            stub.posts.append((503, problem(503, "upstream_unavailable", "down"), None))
        await call(env)
        posts_before = len(stub.post_requests)

        stub.posts.append((200, claimed_json(), None))
        result, raw = await call(env)

        assert result["success"] is True, raw
        assert result["apiKey"] == FRESH_KEY
        assert stub.quote_calls == 1
        assert len(stub.post_requests) == posts_before + 1
        assert stub.post_requests[-1].headers["authorization"] == f"L402 {MACAROON}:{PREIMAGE}"
        env["wallet"].pay_invoice.assert_awaited_once()
        assert only_state(env)["phase"] == "redeemed"

    async def test_crash_before_paying_resumes_from_the_saved_challenge(self, env):
        save_state(env, sell.CHALLENGE)
        env["stub"].posts.append((200, claimed_json(), None))

        result, _ = await call(env)

        assert result["success"] is True
        assert env["stub"].quote_calls == 0
        (only,) = env["stub"].post_requests
        assert only.headers["authorization"].startswith("L402 ")
        env["wallet"].pay_invoice.assert_awaited_once_with(INVOICE)


# ── Advertised surface ───────────────────────────────────────────────────────────────


class TestAdvertisedSurface:
    def test_list_publicly_string_false_is_false(self):
        from lightning_enable_mcp.server import _as_bool

        assert _as_bool("false") is False
        assert _as_bool("true") is True
        assert _as_bool(None) is False
        assert _as_bool(True) is True

    def test_schema_matches_the_shared_fixture_so_both_ports_agree(self):
        from lightning_enable_mcp.tools.consolidated import ACTION_TOOLS, L402_PRODUCER_TOOL

        properties = L402_PRODUCER_TOOL.inputSchema["properties"]
        assert set(properties) == set(FIXTURE["arguments"])
        assert properties["action"]["enum"] == FIXTURE["actions"]
        assert ACTION_TOOLS["l402_producer"] == ("action", tuple(FIXTURE["actions"]))
        for argument in FIXTURE["sellUrlArguments"]:
            assert "sell_url" in properties[argument]["description"], argument
        assert "sell_url" not in properties["price_sats"]["description"]
        assert "buyers pay" in properties["default_price_sats"]["description"]

    def test_description_lands_on_the_operators_words_without_prices_or_custody_claims(self):
        from lightning_enable_mcp.tools.consolidated import L402_PRODUCER_TOOL

        description = L402_PRODUCER_TOOL.description
        assert "charge for your API" in description
        assert "monetize an endpoint" in description
        assert not re.search(r"\d+\s*(sats?|USD|\$)", description)
        for word in ("non-custodial", "self-custody", "trustless", "decentralized"):
            assert word not in description.lower()

    async def test_server_dispatches_sell_url_with_the_paying_wallet_and_ledger(self):
        from mcp.types import CallToolRequest, CallToolRequestParams

        from lightning_enable_mcp.server import LightningEnableServer

        server = LightningEnableServer()
        server.wallet = MagicMock()
        server.paying_wallet = MagicMock()
        server.l402_client = MagicMock()
        server.operation_ledger = MagicMock()
        handler = server.server.request_handlers[CallToolRequest]
        with patch(
            "lightning_enable_mcp.server.sell_url",
            new=AsyncMock(return_value='{"success": true}'),
        ) as tool:
            await handler(
                CallToolRequest(
                    method="tools/call",
                    params=CallToolRequestParams(
                        name="l402_producer",
                        arguments={
                            "action": "sell_url",
                            "target_url": TARGET_URL,
                            "lightning_address": ADDRESS,
                            "default_price_sats": 21,
                            "list_publicly": True,
                            "accept_terms": TERMS,
                            "confirmation_nonce": "ABC123",
                        },
                    ),
                )
            )
        kwargs = tool.await_args.kwargs
        assert kwargs["wallet"] is server.paying_wallet
        assert kwargs["operation_ledger"] is server.operation_ledger
        assert kwargs["default_price_sats"] == 21
        assert kwargs["list_publicly"] is True
        assert kwargs["confirmation_nonce"] == "ABC123"
        assert isinstance(kwargs["claim_client"], sell.ProducerClaimClient)
