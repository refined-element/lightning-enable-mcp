"""``l402_producer(action="sell_url")`` against a scripted claim endpoint.

Quote, phase 1 (402), payment through ``pay_l402_challenge``, phase 2, key storage, error
mapping, phase-2 retries and crash recovery. No network: the claim endpoint is an
``httpx.MockTransport`` and the wallet is a mock.

Mirrors ``dotnet/tests/LightningEnable.Mcp.Tests/Tools/ProducerSellUrlTests.cs``; both read
``shared/l402-producer-sell-url.json``.
"""

from __future__ import annotations

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
from lightning_enable_mcp.tools import producer_sell_url as sell
from tests.confirmation_helpers import make_pending, setup_delivered

TARGET_URL = "https://api.example-weather.dev/v1"
ADDRESS = "alice@wallet.example.com"
TERMS = "2026-11"
MACAROON = "MDAyZGxvY2F0aW9uIGZpeHR1cmU"
PREIMAGE = "ea195ec92c7e00000000000000000000000000000000000000000000000000aa"
INVOICE = "lnbc1500n1pjfixtureinvoice"
FRESH_KEY = "le_fixture_claim_key_1"
CLAIM_URL = "https://api.example.test/api/producer/claim"


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
    }


def challenge_json(sats: int = 150) -> dict:
    return {
        "invoice": INVOICE,
        "macaroon": MACAROON,
        "paymentHash": "2859e1e143bc0000",
        # .NET round-trip format, seven fractional digits.
        "expiresAt": (datetime.now(timezone.utc) + timedelta(minutes=10)).strftime(
            "%Y-%m-%dT%H:%M:%S.%f0Z"
        ),
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
    }
    if key_rotated:
        body["keyRotated"] = True
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
        if isinstance(item, Exception):
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
            "config": tmp_path / "config.json",
            "dir": tmp_path,
            "delays": delays,
        }


async def call(env, **overrides) -> tuple[dict, str]:
    kwargs = dict(
        target_url=TARGET_URL,
        lightning_address=ADDRESS,
        price_sats=21,
        description="Weather for agents",
        accept_terms=TERMS,
        claim_client=env["client"],
        wallet=env["wallet"],
        budget_service=env["budget"],
        payment_history_service=None,
        config_path=str(env["config"]),
    )
    kwargs.update(overrides)
    raw = await sell.sell_url(**kwargs)
    return json.loads(raw), raw


def state_files(env) -> list[Path]:
    d = env["dir"] / "claim"
    return sorted(d.glob("sell-*.json")) if d.exists() else []


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
        assert result["covers"]["days"] == 30
        assert "written once and sent twice" in result["bodyRule"]
        assert f'accept_terms="{TERMS}"' in result["nextStep"]
        assert result["name"] == "api.example-weather.dev", "name defaults to the hostname"
        assert env["stub"].post_requests == []
        env["wallet"].pay_invoice.assert_not_called()
        assert state_files(env) == []

    async def test_bare_404_means_the_feature_is_not_enabled(self, env):
        env["stub"].quotes.append((404, None, None))

        result, _ = await call(env)

        assert result["error"] == "feature_not_enabled"
        assert result["retryable"] is False
        assert result["paid"] is False
        assert env["stub"].post_requests == []


# ── Happy path ───────────────────────────────────────────────────────────────────────


class TestHappyPath:
    async def test_quote_pays_redeems_and_stores_the_key_once(self, env):
        stub = env["stub"]
        stub.quotes.append((200, quote_json(), None))
        stub.posts.append((402, challenge_json(), None))
        stub.posts.append((200, claimed_json(), None))

        result, raw = await call(env)

        assert result["success"] is True, raw
        assert result["proxyUrl"].endswith("/l402/proxy/claimed-weather-api/")
        assert result["manifestUrl"].endswith("l402-manifest.json")
        assert result["proxyId"] == "claimed-weather-api"
        assert result["paidThrough"] == "2026-12-08T21:00:30Z"
        assert "renew" in result["renew"]["endpoint"]
        assert result["recovery"] == "none"
        assert result["listing"]["listed"] is False
        assert len(result["warnings"]) == 1
        assert result["apiKeyStorage"]["location"] == "config"
        assert str(env["config"]) in result["apiKeyNote"]

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
        assert body["acceptTerms"] == TERMS
        assert "recovery" not in body

        env["budget"].check_approval_level.assert_awaited_once_with(150)
        env["wallet"].pay_invoice.assert_awaited_once_with(INVOICE)
        assert state_files(env) == [], "the scratch file is deleted once the claim answers 200"

    async def test_replay_with_key_rotated_stores_the_fresh_key_beside_an_existing_config_key(self, env):
        env["config"].write_text(json.dumps({"lightningEnableApiKey": "le_other_account"}))
        stub = env["stub"]
        stub.quotes.append((200, quote_json(), None))
        stub.posts.append((402, challenge_json(), None))
        stub.posts.append((200, claimed_json(replayed=True, key_rotated=True), None))

        result, raw = await call(env)

        assert result["success"] is True, raw
        assert result["replayed"] is True
        assert result["keyRotated"] is True
        assert result["apiKey"] == FRESH_KEY
        assert result["apiKeyStorage"]["location"] == "key_file"
        assert "fresh key" in result["apiKeyNote"]
        key_file = env["dir"] / "claim-claimed-weather-api.key"
        assert key_file.read_text().strip() == FRESH_KEY
        assert json.loads(env["config"].read_text())["lightningEnableApiKey"] == "le_other_account"
        assert_no_secrets(raw)

    async def test_replay_without_a_key_says_it_is_not_returned_again(self, env):
        stub = env["stub"]
        stub.quotes.append((200, quote_json(), None))
        stub.posts.append((402, challenge_json(), None))
        stub.posts.append((200, claimed_json(replayed=True, api_key=None), None))

        result, _ = await call(env)

        assert result["success"] is True
        assert "apiKey" not in result
        assert "only the first answer" in result["apiKeyNote"]
        assert not env["config"].exists()


# ── Errors ───────────────────────────────────────────────────────────────────────────


class TestErrors:
    def test_error_table_matches_the_shared_fixture(self):
        assert {code: retry for code, (_, retry) in sell.ERROR_TABLE.items()} == FIXTURE["errorCodes"]
        assert set(sell.RETRY_AFTER_PAYMENT_ALSO) == set(FIXTURE["retryAfterPaymentAlso"])
        assert sell.MAX_REDEEM_ATTEMPTS == FIXTURE["maxRedeemAttempts"]
        assert all(message.strip() for message, _ in sell.ERROR_TABLE.values())

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

        assert result["success"] is False
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
        assert "outside" in result["detail"]
        assert len(stub.post_requests) == 1, "one phase-1 attempt, no retry before payment"
        env["wallet"].pay_invoice.assert_not_called()
        env["budget"].check_approval_level.assert_not_called()
        assert state_files(env) == []

    async def test_invoice_above_the_quote_is_refused_before_paying(self, env):
        env["stub"].quotes.append((200, quote_json(sats=100), None))
        env["stub"].posts.append((402, challenge_json(sats=150), None))

        result, _ = await call(env)

        assert result["error"] == "price_changed"
        assert result["paid"] is False
        env["wallet"].pay_invoice.assert_not_called()

    async def test_both_recovery_kinds_are_refused_locally(self, env):
        result, _ = await call(env, recovery_email="a@example.com", recovery_nostr_pubkey="a" * 64)

        assert result["error"] == "invalid_recovery"
        assert env["stub"].requests == []


# ── Budget and confirmation ──────────────────────────────────────────────────────────


class TestBudgetAndConfirmation:
    async def test_budget_refusal_when_the_price_exceeds_the_ceiling_pays_nothing(self, env):
        env["budget"] = make_budget(
            ApprovalLevel.DENY, denial="Payment of 150 sats exceeds the per-payment limit"
        )
        env["stub"].quotes.append((200, quote_json(), None))
        env["stub"].posts.append((402, challenge_json(), None))

        result, raw = await call(env)

        assert result["success"] is False
        assert result["error"] == "payment_refused"
        assert "per-payment limit" in result["message"]
        assert result["paid"] is False
        assert len(env["stub"].post_requests) == 1, "no phase 2 without a payment"
        env["wallet"].pay_invoice.assert_not_called()
        assert_no_secrets(raw)

    async def test_over_threshold_requests_the_code_then_same_arguments_plus_code_pay(self, env):
        budget = make_budget(ApprovalLevel.FORM_CONFIRM)
        pending = make_pending(
            nonce="ZZZ999", amount_sats=150, tool_name="pay_l402_challenge", destination=INVOICE
        )
        setup_delivered(budget, pending=pending)
        budget.validate_and_consume_confirmation = MagicMock(return_value=pending)
        env["budget"] = budget
        env["stub"].quotes.append((200, quote_json(), None))
        env["stub"].posts.append((402, challenge_json(), None))

        first, raw = await call(env)

        assert first["requiresConfirmation"] is True
        assert "sell_url" in first["howToConfirm"] and "confirmation_nonce" in first["howToConfirm"]
        assert "ZZZ999" not in raw
        budget.request_confirmation.assert_awaited_once()
        env["wallet"].pay_invoice.assert_not_called()
        assert len(state_files(env)) == 1, "the challenge is saved so the code binds to the same invoice"

        env["stub"].posts.append((200, claimed_json(), None))
        second, _ = await call(env, confirmation_nonce="zzz999")

        assert second["success"] is True
        budget.validate_and_consume_confirmation.assert_called_once_with(
            "ZZZ999", 150, "pay_l402_challenge", INVOICE
        )
        assert len(env["stub"].post_requests) == 2, "the second call skipped phase 1 and redeemed"
        assert env["stub"].quote_calls == 1
        env["wallet"].pay_invoice.assert_awaited_once()


# ── Phase 2 retries and recovery ─────────────────────────────────────────────────────


class TestRedeem:
    async def test_transient_phase2_failure_is_retried_with_the_same_body_and_token(self, env):
        stub = env["stub"]
        stub.quotes.append((200, quote_json(), None))
        stub.posts.append((402, challenge_json(), None))
        stub.posts.append((503, problem(503, "claim_contended", "Try again"), {"Retry-After": "1"}))
        stub.posts.append((400, problem(400, "invalid_lightning_address", "LNURL lookup failed"), None))
        stub.posts.append((200, claimed_json(), None))

        result, _ = await call(env)

        assert result["success"] is True
        posts = stub.post_requests
        assert len(posts) == 4
        assert {p.content for p in posts} == {posts[0].content}
        assert len({p.headers["authorization"] for p in posts[1:]}) == 1
        assert env["delays"] == [1.0, 2.0], "Retry-After is honoured, then backoff"
        env["wallet"].pay_invoice.assert_awaited_once()

    async def test_giving_up_returns_recovery_without_secrets_and_keeps_the_token(self, env):
        stub = env["stub"]
        stub.quotes.append((200, quote_json(), None))
        stub.posts.append((402, challenge_json(), None))
        for _ in range(sell.MAX_REDEEM_ATTEMPTS):
            stub.posts.append((503, problem(503, "upstream_unavailable", "down"), None))

        result, raw = await call(env)

        assert result["success"] is False
        assert result["paid"] is True
        assert result["error"] == "upstream_unavailable"
        assert result["redeemAttempts"] == sell.MAX_REDEEM_ATTEMPTS
        recovery = result["recovery"]
        assert recovery["paymentSpent"] is True
        assert recovery["paymentHash"] == "2859e1e143bc0000"
        assert "same arguments" in recovery["instructions"]
        assert "do not pay again" in recovery["instructions"]
        saved = json.loads(Path(recovery["stateFile"]).read_text())
        assert saved["macaroon"] == MACAROON
        assert saved["preimage"] == PREIMAGE
        assert saved["body"] == stub.post_requests[0].content.decode()
        assert len(stub.post_requests) == 1 + sell.MAX_REDEEM_ATTEMPTS
        assert_no_secrets(raw)

    async def test_non_retryable_phase2_refusal_gives_up_at_once_with_recovery(self, env):
        stub = env["stub"]
        stub.quotes.append((200, quote_json(), None))
        stub.posts.append((402, challenge_json(), None))
        stub.posts.append((409, problem(409, "merchant_exists", "Account exists"), None))

        result, _ = await call(env, recovery_email="op@example.com")

        assert result["error"] == "merchant_exists"
        assert result["paid"] is True
        assert result["recovery"]["paymentSpent"] is True
        assert len(stub.post_requests) == 2
        assert env["delays"] == []

    async def test_crash_after_paying_is_recovered_from_the_saved_token_without_paying_again(self, env):
        stub = env["stub"]
        stub.quotes.append((200, quote_json(), None))
        stub.posts.append((402, challenge_json(), None))
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
        last = stub.post_requests[-1]
        assert last.headers["authorization"] == f"L402 {MACAROON}:{PREIMAGE}"
        assert last.content == stub.post_requests[0].content
        env["wallet"].pay_invoice.assert_awaited_once()
        assert state_files(env) == []

    async def test_crash_before_paying_resumes_from_the_saved_challenge_without_another_mint(self, env):
        body = sell.build_body(
            TARGET_URL, "api.example-weather.dev", "Weather for agents", 21, ADDRESS,
            None, None, False, TERMS,
        )
        sell.ClaimState(
            directory=env["dir"] / "claim",
            body=body,
            claim_url=CLAIM_URL,
            invoice=INVOICE,
            macaroon=MACAROON,
            payment_hash="2859e1e143bc0000",
            expires_at=(datetime.now(timezone.utc) + timedelta(minutes=5)).isoformat(),
            price_sats=150,
            phase=sell.CHALLENGE,
        ).save()
        env["stub"].posts.append((200, claimed_json(), None))

        result, _ = await call(env)

        assert result["success"] is True
        assert env["stub"].quote_calls == 0
        (only,) = env["stub"].post_requests
        assert only.headers["authorization"].startswith("L402 ")
        env["wallet"].pay_invoice.assert_awaited_once_with(INVOICE)

    async def test_no_wallet_refuses_before_minting(self, env):
        env["stub"].quotes.append((200, quote_json(), None))

        result, _ = await call(env, wallet=None)

        assert result["error"] == "no_wallet"
        assert env["stub"].post_requests == []


# ── Advertised surface ───────────────────────────────────────────────────────────────


class TestAdvertisedSurface:
    def test_schema_matches_the_shared_fixture_so_both_ports_agree(self):
        from lightning_enable_mcp.tools.consolidated import ACTION_TOOLS, L402_PRODUCER_TOOL

        properties = L402_PRODUCER_TOOL.inputSchema["properties"]
        assert set(properties) == set(FIXTURE["arguments"])
        assert properties["action"]["enum"] == FIXTURE["actions"]
        assert ACTION_TOOLS["l402_producer"] == ("action", tuple(FIXTURE["actions"]))
        for argument in FIXTURE["sellUrlArguments"]:
            assert "sell_url" in properties[argument]["description"], argument

    def test_description_lands_on_the_operators_words_without_prices_or_custody_claims(self):
        from lightning_enable_mcp.tools.consolidated import L402_PRODUCER_TOOL

        description = L402_PRODUCER_TOOL.description
        assert "charge for your API" in description
        assert "monetize an endpoint" in description
        assert not re.search(r"\d+\s*(sats?|USD|\$)", description)
        for word in ("non-custodial", "self-custody", "trustless", "decentralized"):
            assert word not in description.lower()

    async def test_server_dispatches_sell_url_with_the_paying_wallet(self):
        from mcp.types import CallToolRequest, CallToolRequestParams

        from lightning_enable_mcp.server import LightningEnableServer

        server = LightningEnableServer()
        server.wallet = MagicMock()
        server.paying_wallet = MagicMock()
        server.l402_client = MagicMock()
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
                            "accept_terms": TERMS,
                            "confirmation_nonce": "ABC123",
                        },
                    ),
                )
            )
        kwargs = tool.await_args.kwargs
        assert kwargs["wallet"] is server.paying_wallet
        assert kwargs["target_url"] == TARGET_URL
        assert kwargs["accept_terms"] == TERMS
        assert kwargs["confirmation_nonce"] == "ABC123"
        assert isinstance(kwargs["claim_client"], sell.ProducerClaimClient)
