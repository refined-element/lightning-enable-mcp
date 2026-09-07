"""
Tests for the approval channel — WHERE an over-threshold confirmation code goes.

Getting this wrong is a funds-safety bug in both directions: a code nobody reads blocks
legitimate payments, and a code the agent can read approves illegitimate ones. These tests
pin the selection matrix, the four channels' behaviour, and the two invariants that bind
them all — the code never reaches the model, and a payment is never approved because its
notification could not be delivered.

Mirrors the .NET ConfirmationChannelTests; the two ports must stay in sync.
"""

import hashlib
import hmac
import importlib
import json
import os
import stat
from decimal import Decimal
from unittest.mock import AsyncMock, MagicMock

import httpx
import pytest

from lightning_enable_mcp.budget_service import BudgetService
from lightning_enable_mcp.config import ApprovalCheckResult, ApprovalLevel, ConfirmationSettings
from lightning_enable_mcp.confirmation_channel import (
    CHANNEL_ENV_VAR,
    DESTINATION_SUMMARY_LENGTH,
    FILE_PATH_ENV_VAR,
    HOSTED_ENV_VAR,
    WEBHOOK_SECRET_ENV_VAR,
    WEBHOOK_URL_ENV_VAR,
    ConfirmationChannelKind,
    ConfirmationRequest,
    DeliveryResult,
    FileConfirmationChannel,
    RefusingConfirmationChannel,
    StderrConfirmationChannel,
    WebhookConfirmationChannel,
    build_webhook_signature,
    create_confirmation_channel,
    default_confirmation_file_path,
    resolve_confirmation_channel,
)
from tests.confirmation_helpers import (
    DELIVERING_CHANNELS,
    make_pending,
    setup_delivered,
    setup_refused,
)

TEST_SECRET = "fixture-string-webhook-signing"
TEST_WEBHOOK_URL = "https://ops.example.com/approvals"
TEST_DESTINATION = "lnbc500u1pj9npjpp5abcdefghijklmnopqrstuvwxyz0123456789"


def sample_request(tool: str = "pay_invoice") -> ConfirmationRequest:
    return ConfirmationRequest(
        amount_sats=50_000,
        amount_usd=Decimal("12.34"),
        tool_name=tool,
        description="lnbc500u1pj9npjpp5...",
        destination=TEST_DESTINATION,
        title="PAYMENT CONFIRMATION REQUIRED",
        summary="pay_invoice — $12.34 (50,000 sats), invoice lnbc500u1pj9npjpp5...",
    )


def sample_pending(nonce: str = "AB12CD"):
    return make_pending(
        nonce=nonce,
        amount_sats=50_000,
        tool_name="pay_invoice",
        destination=TEST_DESTINATION,
        amount_usd=Decimal("12.34"),
    )


# =============================================================================
# Channel selection / precedence
# =============================================================================


class TestChannelSelection:
    def test_environment_variable_beats_config_and_auto(self):
        resolution = resolve_confirmation_channel(
            env_channel="refuse", config_channel="stderr", stdin_is_tty=True, hosted_flag=None
        )
        assert resolution.kind is ConfirmationChannelKind.REFUSE
        assert resolution.source == "env"

    def test_config_value_beats_auto(self):
        resolution = resolve_confirmation_channel(
            env_channel=None, config_channel="webhook", stdin_is_tty=True, hosted_flag="1"
        )
        assert resolution.kind is ConfirmationChannelKind.WEBHOOK
        assert resolution.source == "config"
        assert resolution.warning is None

    @pytest.mark.parametrize(
        "value,expected",
        [
            ("  STDERR ", ConfirmationChannelKind.STDERR),
            ("Refuse", ConfirmationChannelKind.REFUSE),
            ("WEBHOOK", ConfirmationChannelKind.WEBHOOK),
            ("file", ConfirmationChannelKind.FILE),
        ],
    )
    def test_channel_names_are_case_and_whitespace_insensitive(self, value, expected):
        assert resolve_confirmation_channel(value, None, True, None).kind is expected

    def test_invalid_environment_value_fails_closed_to_refuse(self):
        # A typo must not silently restore the posture the operator was moving away from.
        resolution = resolve_confirmation_channel("console", None, True, None)
        assert resolution.kind is ConfirmationChannelKind.REFUSE
        assert resolution.source == "env-invalid"
        assert CHANNEL_ENV_VAR in resolution.warning
        assert "console" in resolution.warning

    def test_invalid_config_value_fails_closed_to_refuse(self):
        resolution = resolve_confirmation_channel(None, "email", True, None)
        assert resolution.kind is ConfirmationChannelKind.REFUSE
        assert resolution.source == "config-invalid"
        assert "confirmation.channel" in resolution.warning


# =============================================================================
# Hosted auto-detection matrix
# =============================================================================


class TestHostedAutoDetection:
    def test_tty_defaults_to_stderr_without_warning(self):
        resolution = resolve_confirmation_channel(None, None, stdin_is_tty=True, hosted_flag=None)
        assert resolution.kind is ConfirmationChannelKind.STDERR
        assert resolution.source == "auto"
        assert resolution.warning is None

    def test_tty_ignores_hosted_flag(self):
        # A human IS at the terminal, so stderr is still genuinely out-of-band.
        resolution = resolve_confirmation_channel(None, None, stdin_is_tty=True, hosted_flag="1")
        assert resolution.kind is ConfirmationChannelKind.STDERR

    def test_non_tty_without_hosted_opt_in_keeps_stderr_but_warns(self):
        resolution = resolve_confirmation_channel(None, None, stdin_is_tty=False, hosted_flag=None)
        assert resolution.kind is ConfirmationChannelKind.STDERR
        assert resolution.warning
        assert "stdin is not a TTY" in resolution.warning
        assert HOSTED_ENV_VAR in resolution.warning

    @pytest.mark.parametrize("hosted", ["1", "true", "TRUE"])
    def test_non_tty_with_hosted_opt_in_refuses_and_warns(self, hosted):
        resolution = resolve_confirmation_channel(None, None, stdin_is_tty=False, hosted_flag=hosted)
        assert resolution.kind is ConfirmationChannelKind.REFUSE
        assert "REFUSED" in resolution.warning

    @pytest.mark.parametrize("hosted", ["0", "no", ""])
    def test_non_tty_with_non_affirmative_flag_keeps_stderr(self, hosted):
        resolution = resolve_confirmation_channel(None, None, stdin_is_tty=False, hosted_flag=hosted)
        assert resolution.kind is ConfirmationChannelKind.STDERR

    def test_non_tty_with_explicit_channel_does_not_warn(self):
        # The operator has already answered the question; do not nag on every start.
        resolution = resolve_confirmation_channel("stderr", None, stdin_is_tty=False, hosted_flag="1")
        assert resolution.warning is None


# =============================================================================
# Factory wiring
# =============================================================================


class TestChannelFactory:
    def test_hosted_non_tty_builds_refusing_channel(self):
        warnings = []
        channel = create_confirmation_channel(
            ConfirmationSettings(),
            warn=warnings.append,
            stdin_is_tty=False,
            environ={HOSTED_ENV_VAR: "1"},
        )
        assert isinstance(channel, RefusingConfirmationChannel)
        assert HOSTED_ENV_VAR in channel.refusal_reason
        assert len(warnings) == 1

    def test_file_channel_prefers_environment_path(self):
        channel = create_confirmation_channel(
            ConfirmationSettings(channel="file", file_path="/from/config.jsonl"),
            stdin_is_tty=True,
            environ={FILE_PATH_ENV_VAR: "/from/env.jsonl"},
        )
        assert isinstance(channel, FileConfirmationChannel)
        assert channel.path == "/from/env.jsonl"

    def test_file_channel_falls_back_to_default_path(self):
        channel = create_confirmation_channel(
            ConfirmationSettings(channel="file"), stdin_is_tty=True, environ={}
        )
        assert isinstance(channel, FileConfirmationChannel)
        assert channel.path == default_confirmation_file_path()

    def test_webhook_channel_is_built_when_fully_configured(self):
        channel = create_confirmation_channel(
            ConfirmationSettings(
                channel="webhook", webhook_url=TEST_WEBHOOK_URL, webhook_secret=TEST_SECRET
            ),
            stdin_is_tty=True,
            environ={},
        )
        assert isinstance(channel, WebhookConfirmationChannel)
        assert channel.url == TEST_WEBHOOK_URL

    def test_webhook_without_url_refuses_instead_of_falling_back_to_stderr(self):
        warnings = []
        channel = create_confirmation_channel(
            ConfirmationSettings(channel="webhook", webhook_secret=TEST_SECRET),
            warn=warnings.append,
            stdin_is_tty=True,
            environ={},
        )
        assert channel.kind is ConfirmationChannelKind.REFUSE
        assert "webhookUrl" in channel.refusal_reason
        assert len(warnings) == 1 and "REFUSED" in warnings[0]

    def test_webhook_without_secret_refuses(self):
        channel = create_confirmation_channel(
            ConfirmationSettings(channel="webhook", webhook_url=TEST_WEBHOOK_URL),
            stdin_is_tty=True,
            environ={},
        )
        assert channel.kind is ConfirmationChannelKind.REFUSE
        assert "webhookSecret" in channel.refusal_reason

    @pytest.mark.parametrize(
        "url",
        [
            "http://127.0.0.1/approve",
            "http://localhost:9000/approve",
            "http://169.254.169.254/latest/meta-data",
            "file:///etc/passwd",
        ],
    )
    def test_webhook_with_non_public_url_is_refused_by_ssrf_guard(self, url):
        channel = create_confirmation_channel(
            ConfirmationSettings(channel="webhook", webhook_url=url, webhook_secret=TEST_SECRET),
            stdin_is_tty=True,
            environ={},
        )
        assert channel.kind is ConfirmationChannelKind.REFUSE
        assert "SSRF" in channel.refusal_reason

    def test_environment_channel_overrides_config_channel(self):
        channel = create_confirmation_channel(
            ConfirmationSettings(channel="stderr"),
            stdin_is_tty=True,
            environ={CHANNEL_ENV_VAR: "refuse"},
        )
        assert channel.kind is ConfirmationChannelKind.REFUSE

    def test_webhook_credentials_can_come_from_the_environment(self):
        channel = create_confirmation_channel(
            ConfirmationSettings(channel="webhook"),
            stdin_is_tty=True,
            environ={
                WEBHOOK_URL_ENV_VAR: TEST_WEBHOOK_URL,
                WEBHOOK_SECRET_ENV_VAR: TEST_SECRET,
            },
        )
        assert isinstance(channel, WebhookConfirmationChannel)


# =============================================================================
# stderr channel (unchanged local behaviour)
# =============================================================================


class TestStderrChannel:
    @pytest.mark.asyncio
    async def test_writes_the_code_in_the_historical_format(self, capsys):
        result = await StderrConfirmationChannel().deliver(
            sample_pending("ZZ9Z9Z"), sample_request()
        )
        assert result.success is True

        captured = capsys.readouterr().err
        assert "*** PAYMENT CONFIRMATION REQUIRED ***" in captured
        assert "Confirmation code: ZZ9Z9Z" in captured
        assert "Expires in 120s" in captured


# =============================================================================
# file channel
# =============================================================================


class TestFileChannel:
    @pytest.mark.asyncio
    async def test_appends_one_json_line_per_confirmation(self, tmp_path):
        path = tmp_path / "nested" / "confirmations.jsonl"
        channel = FileConfirmationChannel(str(path))

        assert (await channel.deliver(sample_pending("AAA111"), sample_request())).success
        assert (await channel.deliver(sample_pending("BBB222"), sample_request())).success

        lines = path.read_text(encoding="utf-8").splitlines()
        assert len(lines) == 2

        first = json.loads(lines[0])
        assert first["type"] == "payment.confirmation_required"
        assert first["nonce"] == "AAA111"
        assert first["tool"] == "pay_invoice"
        assert first["amountSats"] == 50_000
        assert first["amountUsd"] == 12.34
        assert first["expiresInSeconds"] == 120
        assert first["expiresAt"]
        # Destination is a SUMMARY, not the whole blob.
        assert first["destination"].startswith("lnbc500u1pj9npjpp5")
        assert len(first["destination"]) <= DESTINATION_SUMMARY_LENGTH + 3

        assert json.loads(lines[1])["nonce"] == "BBB222"

    @pytest.mark.asyncio
    @pytest.mark.skipif(os.name != "posix", reason="POSIX modes do not exist on Windows")
    async def test_restricts_permissions_to_0600_on_posix(self, tmp_path):
        path = tmp_path / "confirmations.jsonl"
        await FileConfirmationChannel(str(path)).deliver(sample_pending(), sample_request())

        assert stat.S_IMODE(path.stat().st_mode) == 0o600

    @pytest.mark.asyncio
    async def test_unwritable_path_reports_failure_instead_of_raising(self, tmp_path):
        # A directory where the file should be: the append must fail, not crash the tool.
        directory = tmp_path / "a-directory"
        directory.mkdir()

        result = await FileConfirmationChannel(str(directory)).deliver(
            sample_pending(), sample_request()
        )

        assert result.success is False
        assert result.error


# =============================================================================
# webhook channel
# =============================================================================


def _stub_webhook(responder, recorded):
    """Build a client factory whose transport records requests and replays a canned response."""

    def handler(request: httpx.Request) -> httpx.Response:
        recorded.append(request)
        return responder(request)

    def factory() -> httpx.AsyncClient:
        return httpx.AsyncClient(
            transport=httpx.MockTransport(handler), follow_redirects=False
        )

    return factory


class TestWebhookChannel:
    @pytest.mark.asyncio
    async def test_posts_signed_payload_to_the_configured_url(self):
        recorded = []
        channel = WebhookConfirmationChannel(
            TEST_WEBHOOK_URL,
            TEST_SECRET,
            client_factory=_stub_webhook(lambda _: httpx.Response(200), recorded),
        )

        result = await channel.deliver(sample_pending("QQ7Q7Q"), sample_request())

        assert result.success is True
        assert len(recorded) == 1
        request = recorded[0]
        assert request.method == "POST"
        assert str(request.url) == TEST_WEBHOOK_URL

        body = request.content.decode("utf-8")
        payload = json.loads(body)
        assert payload["nonce"] == "QQ7Q7Q"
        assert payload["tool"] == "pay_invoice"
        assert payload["amountSats"] == 50_000
        assert payload["amountUsd"] == 12.34
        assert payload["expiresInSeconds"] == 120
        assert payload["destination"].startswith("lnbc500u1pj9npjpp5")
        # The payload carries nothing that could be used to spend directly.
        assert "preimage" not in body
        assert "macaroon" not in body
        assert request.headers["X-LightningEnable-Signature"]

    @pytest.mark.asyncio
    async def test_signature_verifies_with_the_configured_secret(self):
        recorded = []
        channel = WebhookConfirmationChannel(
            TEST_WEBHOOK_URL,
            TEST_SECRET,
            client_factory=_stub_webhook(lambda _: httpx.Response(204), recorded),
        )

        await channel.deliver(sample_pending(), sample_request())

        signature = recorded[0].headers["X-LightningEnable-Signature"]
        body = recorded[0].content.decode("utf-8")

        # Verify exactly as an operator would: split t=/v1=, recompute over "t.body".
        timestamp_part, signature_part = signature.split(",")
        timestamp = int(timestamp_part.removeprefix("t="))
        provided = signature_part.removeprefix("v1=")

        expected = hmac.new(
            TEST_SECRET.encode("utf-8"), f"{timestamp}.{body}".encode(), hashlib.sha256
        ).hexdigest()
        assert provided == expected
        assert signature == build_webhook_signature(TEST_SECRET, timestamp, body)

        # A different secret must NOT verify.
        wrong = hmac.new(
            b"fixture-string-other", f"{timestamp}.{body}".encode(), hashlib.sha256
        ).hexdigest()
        assert provided != wrong

    @pytest.mark.asyncio
    async def test_does_not_follow_redirects(self):
        recorded = []
        channel = WebhookConfirmationChannel(
            TEST_WEBHOOK_URL,
            TEST_SECRET,
            client_factory=_stub_webhook(
                lambda _: httpx.Response(
                    302, headers={"Location": "https://attacker.example.com/collect"}
                ),
                recorded,
            ),
        )

        result = await channel.deliver(sample_pending(), sample_request())

        # A 3xx is a delivery FAILURE, and exactly one request left the process — the signed
        # approval never chases a Location header to a host the operator did not configure.
        assert result.success is False
        assert "redirect" in result.error
        assert len(recorded) == 1
        assert str(recorded[0].url) == TEST_WEBHOOK_URL

    @pytest.mark.asyncio
    async def test_server_error_is_a_delivery_failure(self):
        recorded = []
        channel = WebhookConfirmationChannel(
            TEST_WEBHOOK_URL,
            TEST_SECRET,
            client_factory=_stub_webhook(lambda _: httpx.Response(500), recorded),
        )

        result = await channel.deliver(sample_pending(), sample_request())

        assert result.success is False
        assert "500" in result.error

    @pytest.mark.asyncio
    async def test_transport_exception_is_a_delivery_failure_not_a_raise(self):
        def exploding_factory():
            def handler(request):
                raise httpx.ConnectError("connection refused")

            return httpx.AsyncClient(transport=httpx.MockTransport(handler))

        channel = WebhookConfirmationChannel(
            TEST_WEBHOOK_URL, TEST_SECRET, client_factory=exploding_factory
        )

        result = await channel.deliver(sample_pending(), sample_request())

        assert result.success is False
        assert "could not be reached" in result.error


# =============================================================================
# BudgetService.request_confirmation
# =============================================================================


class _SpyChannel:
    """Records what it was asked to deliver and replays a canned outcome."""

    def __init__(self, kind, result, refusal_reason=None):
        self.kind = kind
        self._result = result
        self.refusal_reason = refusal_reason
        self.operator_hint = "sent to the test channel"
        self.deliver_calls = 0
        self.last_pending = None

    async def deliver(self, pending, request):
        self.deliver_calls += 1
        self.last_pending = pending
        return self._result


class _ThrowingChannel:
    kind = ConfirmationChannelKind.FILE
    operator_hint = "never"
    refusal_reason = None

    async def deliver(self, pending, request):
        raise OSError("disk on fire")


def _budget_with_channel(channel) -> BudgetService:
    config_service = MagicMock()
    config_service.configuration = MagicMock()
    return BudgetService(
        config_service=config_service,
        price_service=MagicMock(),
        confirmation_channel=channel,
    )


class TestBudgetServiceRequestConfirmation:
    @pytest.mark.asyncio
    async def test_refuse_channel_creates_no_pending_confirmation_at_all(self):
        channel = _SpyChannel(
            ConfirmationChannelKind.REFUSE,
            DeliveryResult.fail("unused"),
            refusal_reason="no approval channel is configured on this server",
        )
        service = _budget_with_channel(channel)

        result = await service.request_confirmation(sample_request())

        assert result.delivered is False
        assert result.pending is None
        assert result.channel_name == "refuse"
        assert "no approval channel" in result.refusal_reason
        # The refuse channel short-circuits BEFORE minting a code.
        assert channel.deliver_calls == 0
        assert service._pending_confirmations == {}

    @pytest.mark.asyncio
    async def test_refuse_channel_leaves_the_budget_untouched(self):
        service = _budget_with_channel(
            _SpyChannel(
                ConfirmationChannelKind.REFUSE, DeliveryResult.fail("unused"), "refused"
            )
        )

        await service.request_confirmation(sample_request())

        assert service.session_spent_sats == 0
        assert service.request_count == 0

    @pytest.mark.asyncio
    async def test_delivered_channel_returns_a_consumable_pending(self):
        channel = _SpyChannel(ConfirmationChannelKind.WEBHOOK, DeliveryResult.ok())
        service = _budget_with_channel(channel)
        request = sample_request()

        result = await service.request_confirmation(request)

        assert result.delivered is True
        assert result.pending is not None
        assert result.channel_name == "webhook"
        assert result.operator_hint == "sent to the test channel"
        assert channel.deliver_calls == 1

        # The code is real: it consumes for the exact amount + tool + destination.
        assert service.validate_and_consume_confirmation(
            result.pending.nonce,
            request.amount_sats,
            request.tool_name,
            request.destination,
        ) is not None

    @pytest.mark.asyncio
    async def test_delivery_failure_refuses_and_cancels_the_code(self):
        channel = _SpyChannel(
            ConfirmationChannelKind.WEBHOOK,
            DeliveryResult.fail("the approval webhook answered HTTP 502"),
        )
        service = _budget_with_channel(channel)

        result = await service.request_confirmation(sample_request())

        assert result.delivered is False
        assert "REFUSED" in result.refusal_reason
        assert "502" in result.refusal_reason
        assert channel.deliver_calls == 1
        # The minted code was withdrawn — an undelivered code is not an approval.
        assert service._pending_confirmations == {}
        assert service.validate_confirmation(channel.last_pending.nonce) is None

    @pytest.mark.asyncio
    async def test_channel_raising_is_a_refusal_not_an_approval(self):
        service = _budget_with_channel(_ThrowingChannel())

        result = await service.request_confirmation(sample_request())

        assert result.delivered is False
        assert "REFUSED" in result.refusal_reason
        assert service._pending_confirmations == {}

    @pytest.mark.asyncio
    async def test_defaults_to_stderr_when_no_channel_is_injected(self):
        service = BudgetService(
            config_service=MagicMock(), price_service=MagicMock()
        )

        result = await service.request_confirmation(sample_request())

        assert result.delivered is True
        assert result.channel_name == "stderr"

    def test_cancel_pending_confirmation_removes_the_code(self):
        service = _budget_with_channel(StderrConfirmationChannel())
        pending = service.create_pending_confirmation(
            1000, Decimal("0.01"), "pay_invoice", "inv...", destination="lnbc-destination"
        )

        service.cancel_pending_confirmation(pending.nonce)

        assert service.validate_confirmation(pending.nonce) is None
        assert service._pending_confirmations == {}


# =============================================================================
# Every payment tool, on every channel: the code never reaches the model, and a
# refusing channel refuses instead of paying.
# =============================================================================


def _confirming_budget():
    """A budget mock that demands confirmation for whatever it is asked about."""
    budget = MagicMock()
    budget.check_approval_level = AsyncMock(
        return_value=ApprovalCheckResult(
            level=ApprovalLevel.FORM_CONFIRM,
            amount_sats=1000,
            amount_usd=Decimal("5.00"),
            remaining_session_budget_usd=Decimal("50.00"),
        )
    )
    budget.record_spend = MagicMock()
    budget.record_payment_time = MagicMock()
    budget.try_reserve = AsyncMock()
    return budget


async def _call_pay_invoice(budget):
    module = importlib.import_module("lightning_enable_mcp.tools.pay_invoice")
    wallet = AsyncMock()
    decoded = MagicMock()
    decoded.amount_msat = 1_000_000
    decoded.amount = 1000
    original = module.decode_bolt11
    module.decode_bolt11 = lambda _invoice: decoded
    try:
        result = await module.pay_invoice(
            invoice="lnbc10u1pjtest", max_sats=1000, wallet=wallet, budget_service=budget
        )
    finally:
        module.decode_bolt11 = original
    return result, wallet.pay_invoice


async def _call_pay_l402_challenge(budget):
    module = importlib.import_module("lightning_enable_mcp.tools.pay_challenge")
    wallet = AsyncMock()
    decoded = MagicMock()
    decoded.amount_msat = 1_000_000
    decoded.amount = 1000
    original = module.decode_bolt11
    module.decode_bolt11 = lambda _invoice: decoded
    try:
        result = await module.pay_l402_challenge(
            invoice="lnbc10u1pjtest", macaroon="mac123", wallet=wallet, budget_service=budget
        )
    finally:
        module.decode_bolt11 = original
    return result, wallet.pay_invoice


async def _call_access_l402_resource(budget):
    from lightning_enable_mcp.tools.access_resource import access_l402_resource

    client = AsyncMock()
    result = await access_l402_resource(
        url="https://api.example.com/data",
        max_sats=1000,
        l402_client=client,
        budget_service=budget,
    )
    return result, client.fetch


async def _call_settle_agent_service(budget):
    from lightning_enable_mcp.tools.settle_agent_service import settle_agent_service

    client = AsyncMock()
    result = await settle_agent_service(
        l402_endpoint="https://example.com/l402",
        max_sats=1000,
        l402_client=client,
        budget_service=budget,
    )
    return result, client.fetch


async def _call_create_account(budget, tmp_path):
    from lightning_enable_mcp.tools.create_account import create_lightning_enable_account

    client = AsyncMock()
    result = await create_lightning_enable_account(
        email="agent@example.com",
        max_sats=500,
        l402_client=client,
        budget_service=budget,
        config_path=str(tmp_path / "config.json"),
    )
    return result, client.fetch


async def _call_send_onchain(budget):
    from lightning_enable_mcp.strike_wallet import StrikeWallet
    from lightning_enable_mcp.tools.send_onchain import send_onchain

    wallet = AsyncMock(spec=StrikeWallet)
    result = await send_onchain(
        address="bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4",
        amount_sats=1000,
        wallet=wallet,
        budget_service=budget,
    )
    return result, wallet.send_onchain


TOOL_CALLS = {
    "pay_invoice": _call_pay_invoice,
    "pay_l402_challenge": _call_pay_l402_challenge,
    "access_l402_resource": _call_access_l402_resource,
    "settle_agent_service": _call_settle_agent_service,
    "send_onchain": _call_send_onchain,
}


class TestEveryToolOnEveryChannel:
    @pytest.mark.asyncio
    @pytest.mark.parametrize("tool_name", sorted(TOOL_CALLS))
    @pytest.mark.parametrize("channel", DELIVERING_CHANNELS)
    async def test_code_never_reaches_the_model(self, tool_name, channel):
        budget = _confirming_budget()
        setup_delivered(budget, channel, nonce="SECRET1", tool_name=tool_name)

        result, spend = await TOOL_CALLS[tool_name](budget)
        data = json.loads(result)

        assert data["success"] is False
        assert data["requiresConfirmation"] is True
        assert data["confirmationChannel"] == channel.value
        assert "SECRET1" not in result
        assert "nonce" not in data
        spend.assert_not_called()

    @pytest.mark.asyncio
    @pytest.mark.parametrize("tool_name", sorted(TOOL_CALLS))
    async def test_refuse_channel_refuses_without_spending(self, tool_name):
        budget = _confirming_budget()
        setup_refused(budget)

        result, spend = await TOOL_CALLS[tool_name](budget)
        data = json.loads(result)

        assert data["success"] is False
        # There is no code to ask a human for — asking would send the agent in circles.
        assert data["requiresConfirmation"] is False
        assert data["confirmationChannel"] == "refuse"
        assert "confirmation.channel" in data["error"]
        spend.assert_not_called()
        budget.try_reserve.assert_not_called()

    @pytest.mark.asyncio
    @pytest.mark.parametrize("channel", DELIVERING_CHANNELS)
    async def test_create_account_code_never_reaches_the_model(self, channel, tmp_path):
        budget = _confirming_budget()
        setup_delivered(
            budget, channel, nonce="SECRET1", tool_name="create_lightning_enable_account"
        )

        result, fetch = await _call_create_account(budget, tmp_path)
        data = json.loads(result)

        assert data["success"] is False
        assert data["requiresConfirmation"] is True
        assert data["confirmationChannel"] == channel.value
        assert "SECRET1" not in result
        fetch.assert_not_called()

    @pytest.mark.asyncio
    async def test_create_account_refuse_channel_refuses(self, tmp_path):
        budget = _confirming_budget()
        setup_refused(budget)

        result, fetch = await _call_create_account(budget, tmp_path)
        data = json.loads(result)

        assert data["success"] is False
        assert data["requiresConfirmation"] is False
        assert data["confirmationChannel"] == "refuse"
        fetch.assert_not_called()
