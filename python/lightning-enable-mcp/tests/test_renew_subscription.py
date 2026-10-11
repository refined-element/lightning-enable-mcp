"""
Tests for renew_lightning_enable_subscription (MONEY PATH).

Mirrors test_create_account.py (budget gate, out-of-band confirmation that never
leaks the code, passive spend recording, paid-but-failed signalling, scrubbing) plus
the two things this tool adds: it needs the merchant API key, and that key must
never reach a model-visible result or a log line. Parity target: the .NET
RenewSubscriptionToolTests.
"""

import hashlib
import json
import logging
from decimal import Decimal
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock

import pytest

from lightning_enable_mcp.config import ApprovalCheckResult, ApprovalLevel
from lightning_enable_mcp.l402_client import L402Error
from lightning_enable_mcp.lightning_enable_api import PRODUCER_API_KEY_REQUIRED
from lightning_enable_mcp.receipt_seam import PaymentReceiptScope
from lightning_enable_mcp.tools.renew_subscription import (
    RENEW_LIGHTNING_ENABLE_SUBSCRIPTION_TOOL,
    renew_lightning_enable_subscription,
)
from lightning_enable_mcp.wallet_errors import PaymentPendingError
from tests.confirmation_helpers import (
    DELIVERING_CHANNELS,
    make_pending,
    setup_delivered,
    setup_refused,
)

TOOL = "renew_lightning_enable_subscription"

# Neutral fixture names (gitleaks generic-api-key). Not shaped like a real key, so the
# shared le_live_ scrub pattern can NOT catch it — the tool must redact the configured
# key itself.
FIXTURE_KEY = "fixture-string-key"
FIXTURE_PREIMAGE = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0"
FIXTURE_MACAROON = "fixture-string-macaroon"
SERVER_PAYMENT_HASH = "fixture-string-payment-hash"

_RENEWED = json.dumps({
    "success": True,
    "alreadyCredited": False,
    "planTier": "individual",
    "currentPeriodEnd": "2026-11-10T00:00:00Z",
    "paidSats": 25000,
    "paymentHash": SERVER_PAYMENT_HASH,
})


def _expected_hash() -> str:
    return hashlib.sha256(bytes.fromhex(FIXTURE_PREIMAGE)).hexdigest()


def _api(configured: bool = True, key: "str | None" = FIXTURE_KEY):
    return SimpleNamespace(is_configured=configured, api_key=key)


def _approval(level, **kwargs):
    return ApprovalCheckResult(
        level=level,
        amount_sats=kwargs.get("amount_sats", 50000),
        amount_usd=kwargs.get("amount_usd", Decimal("25.00")),
        denial_reason=kwargs.get("denial_reason"),
        remaining_session_budget_usd=kwargs.get("remaining_session_budget_usd", Decimal("50.00")),
    )


def _budget_with(level, **kwargs):
    budget = MagicMock()
    budget.check_approval_level = AsyncMock(return_value=_approval(level, **kwargs))
    budget.record_spend = MagicMock()
    budget.record_payment_time = MagicMock()
    pending = make_pending(nonce="ABC123", tool_name=TOOL)
    setup_delivered(budget, pending=pending)
    budget.validate_and_consume_confirmation = MagicMock(return_value=pending)
    return budget


def _paid_client(paid_sats: "int | None" = 25000, body: str = _RENEWED):
    client = AsyncMock()
    client.fetch = AsyncMock(return_value=(body, paid_sats, None))
    return client


def _raising_client(err: BaseException):
    client = AsyncMock()
    client.fetch = AsyncMock(side_effect=err)
    return client


def _paid_error(message: str, amount: int = 25000, token: "str | None" = None) -> L402Error:
    err = L402Error(message)
    err.amount_paid = amount
    err.l402_token = token
    return err


async def _renew(**kwargs) -> str:
    kwargs.setdefault("api_client", _api())
    kwargs.setdefault("l402_client", _paid_client())
    return await renew_lightning_enable_subscription(**kwargs)


# ── API key ─────────────────────────────────────────────────────────────────


class TestApiKeyGate:
    @pytest.mark.asyncio
    async def test_no_api_client_points_at_create_account_and_does_not_fetch(self):
        client = _paid_client()
        result = json.loads(await renew_lightning_enable_subscription(l402_client=client, api_client=None))
        assert result["success"] is False
        assert result["error"] == PRODUCER_API_KEY_REQUIRED
        assert "create_lightning_enable_account" in result["error"]
        client.fetch.assert_not_called()

    @pytest.mark.asyncio
    @pytest.mark.parametrize("configured,key", [(False, None), (False, FIXTURE_KEY), (True, ""), (True, "   "), (True, None)])
    async def test_unconfigured_or_blank_key_refuses_before_any_fetch(self, configured, key):
        client = _paid_client()
        budget = _budget_with(ApprovalLevel.AUTO_APPROVE)
        result = json.loads(await renew_lightning_enable_subscription(
            l402_client=client, budget_service=budget, api_client=_api(configured, key),
        ))
        assert result["success"] is False
        assert "create_lightning_enable_account" in result["error"]
        client.fetch.assert_not_called()
        budget.check_approval_level.assert_not_called()

    @pytest.mark.asyncio
    async def test_missing_wallet_returns_error(self):
        result = json.loads(await renew_lightning_enable_subscription(l402_client=None, api_client=_api()))
        assert result["success"] is False
        assert "No wallet configured" in result["error"]


# ── Budget / confirmation ───────────────────────────────────────────────────


class TestBudgetGate:
    @pytest.mark.asyncio
    async def test_budget_deny_blocks_and_does_not_fetch(self):
        client = _paid_client()
        budget = _budget_with(ApprovalLevel.DENY, denial_reason="over session limit")
        result = json.loads(await _renew(l402_client=client, budget_service=budget))
        assert result["success"] is False
        assert "denied by budget policy" in result["error"]
        assert "over session limit" in result["error"]
        client.fetch.assert_not_called()
        budget.record_spend.assert_not_called()

    @pytest.mark.asyncio
    async def test_gate_runs_on_the_default_50000_sat_ceiling(self):
        budget = _budget_with(ApprovalLevel.AUTO_APPROVE)
        await _renew(budget_service=budget)
        budget.check_approval_level.assert_awaited_once_with(50000)

    @pytest.mark.asyncio
    @pytest.mark.parametrize("channel", DELIVERING_CHANNELS)
    async def test_confirmation_required_never_leaks_the_code(self, channel):
        client = _paid_client()
        budget = _budget_with(ApprovalLevel.FORM_CONFIRM)
        setup_delivered(budget, channel, nonce="SECRET1", tool_name=TOOL)

        result_str = await _renew(l402_client=client, budget_service=budget)
        result = json.loads(result_str)

        assert result["success"] is False
        assert result["requiresConfirmation"] is True
        assert result["confirmationChannel"] == channel.value
        assert "howToConfirm" in result
        assert "expiresInSeconds" in result
        assert "SECRET1" not in result_str
        assert "nonce" not in result
        client.fetch.assert_not_called()

        # The dispatched request is bound to this tool and the renew endpoint.
        request = budget.request_confirmation.call_args[0][0]
        assert request.tool_name == TOOL
        assert request.amount_sats == 50000
        assert request.destination.endswith("/api/merchant/subscription/renew")

    @pytest.mark.asyncio
    async def test_refuse_channel_refuses_without_paying(self):
        client = _paid_client()
        budget = _budget_with(ApprovalLevel.FORM_CONFIRM)
        setup_refused(budget)

        result = json.loads(await _renew(l402_client=client, budget_service=budget))

        assert result["success"] is False
        assert result["requiresConfirmation"] is False
        assert result["confirmationChannel"] == "refuse"
        assert "confirmation.channel" in result["error"]
        client.fetch.assert_not_called()

    @pytest.mark.asyncio
    async def test_human_relayed_nonce_is_bound_to_tool_and_renew_url(self):
        client = _paid_client()
        budget = _budget_with(ApprovalLevel.FORM_CONFIRM)

        result = json.loads(await _renew(
            l402_client=client, budget_service=budget, max_sats=30000, confirmation_nonce=" abc123 ",
        ))

        args = budget.validate_and_consume_confirmation.call_args[0]
        assert args[0] == "ABC123"
        assert args[1] == 30000
        assert args[2] == TOOL
        assert args[3].endswith("/api/merchant/subscription/renew")
        assert result["success"] is True
        client.fetch.assert_awaited_once()
        # PASSIVE: the client records spend + cooldown once inside the payment leg.
        budget.record_spend.assert_not_called()
        budget.record_payment_time.assert_not_called()

    @pytest.mark.asyncio
    async def test_invalid_nonce_refuses_without_paying(self):
        client = _paid_client()
        budget = _budget_with(ApprovalLevel.FORM_CONFIRM)
        budget.validate_and_consume_confirmation = MagicMock(return_value=None)

        result = json.loads(await _renew(
            l402_client=client, budget_service=budget, confirmation_nonce="WRONG1",
        ))

        assert result["success"] is False
        assert "Confirmation code is invalid" in result["error"]
        client.fetch.assert_not_called()


# ── The request ─────────────────────────────────────────────────────────────


class TestRenewRequest:
    @pytest.mark.asyncio
    async def test_posts_empty_json_with_api_key_header(self):
        client = _paid_client()
        await _renew(l402_client=client)

        client.fetch.assert_awaited_once()
        kwargs = client.fetch.call_args.kwargs
        assert kwargs["url"].endswith("/api/merchant/subscription/renew")
        assert kwargs["method"] == "POST"
        assert kwargs["body"] == "{}"
        assert kwargs["headers"]["X-Api-Key"] == "fixture-string-key"
        assert kwargs["headers"]["Content-Type"] == "application/json"
        assert kwargs["max_sats"] == 50000
        # A3: POST is only reachable through the internal first-party allowance.
        assert kwargs["first_party_write"] is True

    @pytest.mark.asyncio
    async def test_base_url_honors_lightning_enable_api_url(self, monkeypatch):
        monkeypatch.setenv("LIGHTNING_ENABLE_API_URL", "https://sandbox.example.com/")
        client = _paid_client()
        await _renew(l402_client=client)
        assert client.fetch.call_args.kwargs["url"] == "https://sandbox.example.com/api/merchant/subscription/renew"

    @pytest.mark.asyncio
    async def test_unexpanded_placeholder_falls_back_to_default_base(self, monkeypatch):
        monkeypatch.setenv("LIGHTNING_ENABLE_API_URL", "${LIGHTNING_ENABLE_API_URL}")
        client = _paid_client()
        await _renew(l402_client=client)
        assert client.fetch.call_args.kwargs["url"] == "https://api.lightningenable.com/api/merchant/subscription/renew"


# ── Success ─────────────────────────────────────────────────────────────────


class TestRenewSuccess:
    @pytest.mark.asyncio
    async def test_success_returns_period_and_payment_details(self):
        result = json.loads(await _renew())
        assert result["success"] is True
        assert result["currentPeriodEnd"] == "2026-11-10T00:00:00Z"
        assert result["paidSats"] == 25000
        assert result["paymentHash"] == SERVER_PAYMENT_HASH
        assert result["alreadyCredited"] is False
        assert result["planTier"] == "individual"
        assert result["activation"] == {"paid": True, "amountSats": 25000}
        assert "receipt_written" in result
        assert "2026-11-10T00:00:00Z" in result["message"]

    @pytest.mark.asyncio
    async def test_already_credited_says_so(self):
        body = json.dumps({**json.loads(_RENEWED), "alreadyCredited": True})
        result = json.loads(await _renew(l402_client=_paid_client(body=body)))
        assert result["success"] is True
        assert result["alreadyCredited"] is True
        assert "already credited" in result["message"]

    @pytest.mark.asyncio
    async def test_success_never_echoes_the_api_key(self, caplog):
        with caplog.at_level(logging.DEBUG):
            result = await _renew()
        assert FIXTURE_KEY not in result
        assert FIXTURE_KEY not in caplog.text

    @pytest.mark.asyncio
    async def test_success_writes_durable_receipt_via_wallet_seam(self, tmp_path):
        from lightning_enable_mcp.receipt_seam import ReceiptRecordingWallet
        from lightning_enable_mcp.receipt_service import ReceiptService

        receipts = ReceiptService(wallet_label="unknown", receipts_path=tmp_path / "receipts.jsonl")

        class NWCWallet:  # noqa: N801 - class name drives the wallet label
            async def pay_invoice(self, bolt11, *a, **k):
                return FIXTURE_PREIMAGE

        seam = ReceiptRecordingWallet(NWCWallet(), receipts, None)

        async def fetch(url, method, headers, body, max_sats, *, first_party_write=False):
            await seam.pay_invoice("lnbc-placeholder")
            return (_RENEWED, 25000, None)

        result = json.loads(await _renew(
            l402_client=SimpleNamespace(fetch=fetch),
            budget_service=_budget_with(ApprovalLevel.AUTO_APPROVE),
        ))

        assert result["success"] is True
        assert result["receipt_written"] is True
        recs = receipts.read_recent(10)
        assert len(recs) == 1
        assert recs[0]["kind"] == "l402"
        assert recs[0]["context"] == "l402_subscription_renewal"

    @pytest.mark.asyncio
    async def test_non_json_response_keeps_paid_signal(self):
        result = json.loads(await _renew(l402_client=_paid_client(body="<html>gateway error</html>")))
        assert result["success"] is False
        assert "not valid JSON" in result["error"]
        assert result["activation"]["paid"] is True
        assert "Do NOT re-run" in result["warning"]

    @pytest.mark.asyncio
    async def test_missing_period_end_keeps_paid_signal(self):
        body = json.dumps({"success": True, "planTier": "individual"})
        result = json.loads(await _renew(l402_client=_paid_client(body=body)))
        assert result["success"] is False
        assert "currentPeriodEnd" in result["error"]
        assert result["activation"]["paid"] is True
        assert "Do NOT re-run" in result["warning"]


# ── Paid-but-failed (money already left the wallet) ─────────────────────────


class TestPaidButFailed:
    @pytest.mark.asyncio
    async def test_paid_but_refused_409_surfaces_paid_signal_error_code_and_do_not_retry(self):
        err = _paid_error(
            'Request failed after payment: 409 {"error":"prepaid_cap_reached","message":"already prepaid"}',
            token=f"{FIXTURE_MACAROON}:{FIXTURE_PREIMAGE}",
        )
        budget = _budget_with(ApprovalLevel.AUTO_APPROVE)
        history = MagicMock()

        result_str = await _renew(l402_client=_raising_client(err), budget_service=budget)
        result = json.loads(result_str)

        assert result["success"] is False
        assert result["errorCode"] == "prepaid_cap_reached"
        assert result["statusCode"] == 409
        assert result["activation"] == {"paid": True, "amountSats": 25000}
        assert "receipt_written" in result
        assert "Do NOT re-run" in result["warning"]
        assert "support@lightningenable.com" in result["warning"]
        # Support needs the payment hash; it is derived, the preimage stays out.
        assert result["paymentHash"] == _expected_hash()
        assert FIXTURE_PREIMAGE not in result_str
        assert FIXTURE_MACAROON not in result_str
        assert FIXTURE_KEY not in result_str
        # PASSIVE: the client recorded the spend once before raising.
        budget.record_spend.assert_not_called()
        history.record_payment.assert_not_called()

    @pytest.mark.asyncio
    async def test_paid_but_pending_does_not_blame_the_wallet(self):
        err = PaymentPendingError(
            "Payment has not settled yet (tracking ID: strike-123) — it may still succeed or fail. "
            "Do not retry it; check its status with the provider.",
            provider="strike",
            tracking_id="strike-123",
        )
        err.amount_paid = 25000
        result_str = await _renew(l402_client=_raising_client(err))
        result = json.loads(result_str)

        assert result["success"] is False
        assert result["activation"] == {"paid": True, "amountSats": 25000}
        assert "Do NOT re-run" in result["warning"]
        assert "do not re-pay" in result["hint"]
        assert "Check wallet balance" not in result_str

    @pytest.mark.asyncio
    async def test_unpaid_challenge_failure_does_not_claim_paid(self):
        from lightning_enable_mcp.l402_client import L402BudgetExceededError

        err = L402BudgetExceededError("Invoice amount 25000 sats exceeds maximum 1000 sats")
        result = json.loads(await _renew(l402_client=_raising_client(err), max_sats=1000))

        assert result["success"] is False
        assert result["activation"] == {"paid": False, "amountSats": 0}
        assert "warning" not in result
        assert "no payment was made" in result["hint"]

    @pytest.mark.asyncio
    async def test_unpaid_409_surfaces_server_error_code_verbatim(self):
        err = L402Error(
            'Request failed: 409 {"error":"stripe_subscription_active","message":"This account is billed by card."}'
        )
        result = json.loads(await _renew(l402_client=_raising_client(err)))

        assert result["success"] is False
        assert result["errorCode"] == "stripe_subscription_active"
        assert result["statusCode"] == 409
        assert "billed by card" in result["error"]
        assert result["activation"] == {"paid": False, "amountSats": 0}
        assert "warning" not in result

    @pytest.mark.asyncio
    async def test_error_code_survives_a_truncated_server_body(self):
        # L402Client caps the body at 200 chars, so the JSON can arrive cut off.
        body = '{"error":"business_plan_contact_support","message":"' + ("x" * 300)
        err = L402Error(f"Request failed: 409 {body[:200]}")
        result = json.loads(await _renew(l402_client=_raising_client(err)))
        assert result["errorCode"] == "business_plan_contact_support"

    @pytest.mark.asyncio
    async def test_non_identifier_error_is_not_surfaced_as_a_code(self):
        err = L402Error('Request failed: 500 {"error":"Something Broke Badly!"}')
        result = json.loads(await _renew(l402_client=_raising_client(err)))
        assert result["errorCode"] is None

    @pytest.mark.asyncio
    async def test_rejected_api_key_hint_names_the_key_setting(self):
        err = L402Error('Request failed: 401 {"error":"invalid_api_key","message":"bad key"}')
        result = json.loads(await _renew(l402_client=_raising_client(err)))
        assert result["statusCode"] == 401
        assert "LIGHTNING_ENABLE_API_KEY" in result["hint"]
        assert result["activation"]["paid"] is False

    @pytest.mark.asyncio
    async def test_throw_after_receipt_recorded_but_no_amount_still_flags_paid(self):
        async def fetch(url, method, headers, body, max_sats, *, first_party_write=False):
            # The wallet seam recorded a receipt write under this call, then the client blew up
            # without attaching amount_paid.
            PaymentReceiptScope.current().record_write(True)
            raise ConnectionError(f"connection reset after paying with {FIXTURE_KEY}")

        result_str = await _renew(l402_client=SimpleNamespace(fetch=fetch))
        result = json.loads(result_str)

        assert result["success"] is False
        assert result["receipt_written"] is True
        assert result["activation"]["paid"] is True
        assert "may have been taken" in result["warning"]
        assert "Do NOT re-run" in result["warning"]
        assert "support@lightningenable.com" in result["warning"]
        assert FIXTURE_KEY not in result_str

    @pytest.mark.asyncio
    async def test_plain_throw_without_payment_keeps_plain_shape(self):
        result_str = await _renew(
            l402_client=_raising_client(RuntimeError(f"boom with {FIXTURE_KEY} and le_live_otherkey"))
        )
        result = json.loads(result_str)

        assert result["success"] is False
        assert result["receipt_written"] is None
        assert "activation" not in result
        assert "warning" not in result
        assert FIXTURE_KEY not in result_str
        assert "otherkey" not in result_str


# ── Key redaction / scrubbing ───────────────────────────────────────────────


class TestKeyNeverEscapes:
    @pytest.mark.asyncio
    async def test_server_echoing_the_configured_key_is_redacted(self, caplog):
        err = L402Error(
            f'Request failed: 401 {{"error":"invalid_api_key","message":"key {FIXTURE_KEY} is not valid"}}'
        )
        with caplog.at_level(logging.DEBUG):
            result_str = await _renew(l402_client=_raising_client(err))

        assert FIXTURE_KEY not in result_str
        assert FIXTURE_KEY not in caplog.text
        assert json.loads(result_str)["errorCode"] == "invalid_api_key"

    @pytest.mark.asyncio
    async def test_paid_error_echoing_the_key_is_redacted(self, caplog):
        err = _paid_error(f"Request failed after payment: 500 key={FIXTURE_KEY}")
        with caplog.at_level(logging.DEBUG):
            result_str = await _renew(l402_client=_raising_client(err))
        assert FIXTURE_KEY not in result_str
        assert FIXTURE_KEY not in caplog.text
        assert json.loads(result_str)["activation"]["paid"] is True

    @pytest.mark.asyncio
    async def test_server_body_is_scrubbed_and_truncated(self):
        leaky = "Request failed: 500 le_live_leakedkey :: " + ("y" * 400)
        result = json.loads(await _renew(l402_client=_raising_client(L402Error(leaky))))
        assert "leakedkey" not in result["error"]
        assert len(result["error"]) <= 203


# ── Server dispatch ─────────────────────────────────────────────────────────


async def _call_tool(server, name: str, arguments: dict) -> str:
    from mcp.types import CallToolRequest, CallToolRequestParams

    handler = server.server.request_handlers[CallToolRequest]
    req = CallToolRequest(method="tools/call", params=CallToolRequestParams(name=name, arguments=arguments))
    result = await handler(req)
    return result.root.content[0].text


class TestServerDispatch:
    @pytest.mark.asyncio
    async def test_call_tool_routes_with_the_servers_api_client_and_default_ceiling(self):
        from lightning_enable_mcp.server import LightningEnableServer

        server = LightningEnableServer()
        # Pre-set wallet + l402 client so call_tool skips _initialize_services.
        server.wallet = MagicMock()
        server.l402_client = _paid_client()
        server.api_client = _api()

        text = await _call_tool(server, TOOL, {})
        data = json.loads(text)

        assert data["success"] is True
        kwargs = server.l402_client.fetch.call_args.kwargs
        assert kwargs["headers"]["X-Api-Key"] == FIXTURE_KEY
        assert kwargs["max_sats"] == 50000
        assert FIXTURE_KEY not in text

    def test_the_tool_needs_a_wallet(self):
        from lightning_enable_mcp.server import LightningEnableServer

        assert TOOL not in LightningEnableServer._WALLET_FREE_TOOLS
        assert LightningEnableServer()._requires_wallet(TOOL, {}) is True


# ── Schema ──────────────────────────────────────────────────────────────────


class TestSchema:
    def test_schema_has_only_optional_ceiling_and_nonce(self):
        schema = RENEW_LIGHTNING_ENABLE_SUBSCRIPTION_TOOL.inputSchema
        assert RENEW_LIGHTNING_ENABLE_SUBSCRIPTION_TOOL.name == TOOL
        assert set(schema["properties"]) == {"max_sats", "confirmation_nonce"}
        assert schema["properties"]["max_sats"]["default"] == 50000
        assert not schema.get("required")
