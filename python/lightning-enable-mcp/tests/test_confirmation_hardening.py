"""Hardening of the out-of-band confirmation code (mirrors .NET ConfirmationHardeningTests).

Guess limit, cap on outstanding codes, configurable TTL, no oracle/echo in
verify_confirmation_code, atomic consume, and an end-to-end proof through send_onchain with
each delivering channel (the code is captured from the channel, never from a tool result).
"""

import hashlib
import hmac
import io
import json
import os
import re
import stat
import sys
import threading
from decimal import Decimal
from unittest.mock import AsyncMock, MagicMock

import httpx
import pytest

from lightning_enable_mcp.budget_service import BudgetService, SpendReservationResult
from lightning_enable_mcp.config import ApprovalLevel, ConfirmationSettings
from lightning_enable_mcp.confirmation_channel import (
    ConfirmationRequest,
    FileConfirmationChannel,
    StderrConfirmationChannel,
    WebhookConfirmationChannel,
    resolve_confirmation_ttl_seconds,
)
from lightning_enable_mcp.strike_wallet import StrikeWallet
from lightning_enable_mcp.tools.send_onchain import send_onchain
from lightning_enable_mcp.tools.verify_confirmation_code import verify_confirmation_code

ADDRESS = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4"


def _service(channel=None, ttl_seconds=None) -> BudgetService:
    config = MagicMock()
    config.configuration.confirmation = ConfirmationSettings(ttl_seconds=ttl_seconds)
    return BudgetService(
        config_service=config,
        price_service=MagicMock(),
        confirmation_channel=channel or StderrConfirmationChannel(io.StringIO()),
    )


def _wrong(code: str) -> str:
    return "YYYYYY" if code == "ZZZZZZ" else "ZZZZZZ"


def _request(destination="dest-1", sats=1000) -> ConfirmationRequest:
    return ConfirmationRequest(
        amount_sats=sats,
        amount_usd=Decimal("0.01"),
        tool_name="pay_invoice",
        description=destination,
        destination=destination,
        title="T",
        summary="S",
    )


# ---- 1. brute force ------------------------------------------------------------------


def test_guess_limit_revokes_all_pending_after_five_invalid_consumes():
    svc = _service()
    pc = svc.create_pending_confirmation(1000, Decimal("0.01"), "pay_invoice", "d", destination="d")
    for _ in range(BudgetService.MAX_FAILED_CONFIRMATION_ATTEMPTS):
        assert svc.validate_and_consume_confirmation(_wrong(pc.nonce), 1000, "pay_invoice", "d") is None
    assert svc.validate_and_consume_confirmation(pc.nonce, 1000, "pay_invoice", "d") is None


def test_guess_limit_counts_verify_failures_so_it_is_not_a_free_oracle():
    svc = _service()
    pc = svc.create_pending_confirmation(1000, Decimal("0.01"), "pay_invoice", "d", destination="d")
    for _ in range(BudgetService.MAX_FAILED_CONFIRMATION_ATTEMPTS):
        assert svc.validate_confirmation(_wrong(pc.nonce)) is None
    assert svc.validate_confirmation(pc.nonce) is None


def test_guess_limit_below_threshold_still_works_and_success_resets_counter():
    svc = _service()
    for _ in range(2):
        pc = svc.create_pending_confirmation(1000, Decimal("0.01"), "pay_invoice", "d", destination="d")
        for _ in range(BudgetService.MAX_FAILED_CONFIRMATION_ATTEMPTS - 1):
            svc.validate_and_consume_confirmation(_wrong(pc.nonce), 1000, "pay_invoice", "d")
        assert svc.validate_and_consume_confirmation(pc.nonce, 1000, "pay_invoice", "d") is pc


# ---- 2. verify tool --------------------------------------------------------------------


@pytest.mark.asyncio
async def test_verify_tool_does_not_echo_code_or_destination():
    svc = _service()
    pc = svc.create_pending_confirmation(1000, Decimal("0.01"), "send_onchain", ADDRESS, destination=ADDRESS)
    result = await verify_confirmation_code(pc.nonce.lower(), budget_service=svc)
    assert json.loads(result)["valid"] is True
    assert pc.nonce not in result
    assert ADDRESS not in result


# ---- 3. TTL ----------------------------------------------------------------------------


@pytest.mark.parametrize(
    "env,config,expected",
    [
        (None, None, 120),
        (None, 600, 600),
        ("300", 600, 300),
        ("junk", 600, 600),
        ("100000", None, 900),
        ("1", None, 30),
        (None, -5, 30),
    ],
)
def test_ttl_resolves_env_over_config_and_clamps(env, config, expected):
    assert resolve_confirmation_ttl_seconds(env, config) == expected


def test_ttl_config_sets_expiry_of_minted_codes():
    svc = _service(ttl_seconds=600)
    pc = svc.create_pending_confirmation(1000, Decimal("0.01"), "pay_invoice", "d", destination="d")
    assert abs((pc.expires_at - pc.created_at).total_seconds() - 600) < 1


def test_ttl_settings_parse_from_config_dict():
    assert ConfirmationSettings.from_dict({"ttlSeconds": 450}).ttl_seconds == 450


@pytest.mark.asyncio
async def test_ttl_stderr_channel_prints_real_expiry():
    stream = io.StringIO()
    svc = _service(StderrConfirmationChannel(stream), ttl_seconds=600)
    dispatch = await svc.request_confirmation(_request())
    assert dispatch.delivered
    assert dispatch.expires_in_seconds == 600
    assert "Expires in 600s" in stream.getvalue()


# ---- 5. pending cap --------------------------------------------------------------------


@pytest.mark.asyncio
async def test_pending_cap_refuses_new_mints_until_one_is_consumed():
    stream = io.StringIO()
    svc = _service(StderrConfirmationChannel(stream))
    delivered = []
    for i in range(BudgetService.MAX_PENDING_CONFIRMATIONS):
        d = await svc.request_confirmation(_request(f"dest-{i}"))
        assert d.delivered
        delivered.append(d.pending)
    before = stream.getvalue()

    refused = await svc.request_confirmation(_request("dest-x"))
    assert not refused.delivered
    assert refused.pending is None
    assert "outstanding" in refused.refusal_reason
    assert stream.getvalue() == before, "a refused mint must not notify the operator"

    assert svc.validate_and_consume_confirmation(delivered[0].nonce, 1000, "pay_invoice", "dest-0")
    assert (await svc.request_confirmation(_request("dest-y"))).delivered


# ---- 7. concurrency --------------------------------------------------------------------


def test_parallel_consumes_of_one_code_exactly_one_succeeds():
    svc = _service()
    pc = svc.create_pending_confirmation(1000, Decimal("0.01"), "pay_invoice", "d", destination="d")
    barrier = threading.Barrier(16)
    results = []

    def worker():
        barrier.wait()
        results.append(svc.validate_and_consume_confirmation(pc.nonce, 1000, "pay_invoice", "d"))

    threads = [threading.Thread(target=worker) for _ in range(16)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    assert sum(1 for r in results if r is not None) == 1


# ---- 8. end to end ---------------------------------------------------------------------

_SECRET = "e2e-fixture-signing"


@pytest.mark.asyncio
@pytest.mark.parametrize("kind", ["stderr", "file", "webhook"])
async def test_end_to_end_send_onchain_code_from_channel_only_one_send_no_replay(kind, tmp_path):
    stream = io.StringIO()
    file_path = tmp_path / "confirmations.jsonl"
    posted = []

    def handler(request: httpx.Request) -> httpx.Response:
        posted.append(request)
        return httpx.Response(200)

    if kind == "stderr":
        channel = StderrConfirmationChannel(stream)
    elif kind == "file":
        channel = FileConfirmationChannel(str(file_path))
    else:
        channel = WebhookConfirmationChannel(
            "https://ops.example.com/approve",
            _SECRET,
            client_factory=lambda: httpx.AsyncClient(transport=httpx.MockTransport(handler)),
        )

    svc = _service(channel)
    # Fake the price-dependent gates; the confirmation machinery under test is real.
    approval = MagicMock(level=ApprovalLevel.AUTO_APPROVE, amount_usd=Decimal("5.00"), denial_reason=None)
    svc.check_approval_level = AsyncMock(return_value=approval)
    svc.try_reserve = AsyncMock(side_effect=lambda amt: SpendReservationResult.reserved("r1", amt))
    svc.commit_reservation = MagicMock()
    svc.release_reservation = MagicMock()

    wallet = AsyncMock(spec=StrikeWallet)
    wallet.send_onchain = AsyncMock(
        return_value=MagicMock(
            success=True, payment_id="p1", txid="tx1", state="COMPLETED",
            amount_sats=5000, fee_sats=10, error_message=None, error_code=None,
        )
    )

    first = await send_onchain(ADDRESS, 5000, None, wallet=wallet, budget_service=svc)
    assert json.loads(first)["requiresConfirmation"] is True

    if kind == "stderr":
        code = re.search(r"Confirmation code: ([A-Z0-9]{6})", stream.getvalue()).group(1)
    elif kind == "file":
        lines = file_path.read_text(encoding="utf-8").splitlines()
        assert len(lines) == 1
        code = json.loads(lines[0])["nonce"]
        if sys.platform != "win32":
            assert stat.S_IMODE(os.stat(file_path).st_mode) == 0o600
    else:
        assert len(posted) == 1
        body = posted[0].content.decode("utf-8")
        parts = dict(p.split("=", 1) for p in posted[0].headers["X-LightningEnable-Signature"].split(","))
        expected = hmac.new(_SECRET.encode(), f"{parts['t']}.{body}".encode(), hashlib.sha256).hexdigest()
        assert hmac.compare_digest(parts["v1"], expected), "webhook signature must verify"
        code = json.loads(body)["nonce"]

    assert re.fullmatch(r"[A-Z0-9]{6}", code)
    assert code not in first

    second = await send_onchain(ADDRESS, 5000, code, wallet=wallet, budget_service=svc)
    assert json.loads(second)["success"] is True, second
    assert wallet.send_onchain.await_count == 1

    third = await send_onchain(ADDRESS, 5000, code, wallet=wallet, budget_service=svc)
    assert json.loads(third)["success"] is False
    assert wallet.send_onchain.await_count == 1


# ---- 6. leakage ------------------------------------------------------------------------


def test_no_tool_logs_or_prints_a_confirmation_code():
    """The code belongs only on the configured approval channel, never in a log line."""
    import pathlib

    tools = pathlib.Path(__file__).resolve().parents[1] / "src" / "lightning_enable_mcp" / "tools"
    offenders = [
        f"{path.name}:{number}"
        for path in tools.glob("*.py")
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1)
        if re.search(r"(print|logger\.\w+|logging\.\w+)\(", line) and re.search(r"\{[\w.]*nonce\}", line)
    ]
    assert offenders == []
