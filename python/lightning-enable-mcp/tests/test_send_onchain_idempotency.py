"""
send_onchain idempotency / pending-awareness after submission.

On-chain sends are irreversible and take ~10 minutes to 1-2 confirmations, so "pending"
is the NORMAL state. Once the Strike quote has been executed, a timeout, a transport
error, or a cancellation is AMBIGUOUS: funds may have moved. These tests pin that:

  - the wallet distinguishes pre-submission failure (``submitted=False``) from
    post-submission ambiguity (``submitted=True``, state UNKNOWN / PENDING);
  - the tool commits (never releases) the budget reservation for submitted outcomes and
    writes a pending receipt;
  - the durable operation ledger, keyed by sha256("onchain:" + address + ":" + amount),
    blocks a second send of the same address+amount while it may have moved money,
    even across a restart, and reports provider status instead of re-sending.

Uses a real StrikeWallet over httpx.MockTransport — no real network, no real funds.
"""

import asyncio
import hashlib
import json
from datetime import datetime, timedelta, timezone
from decimal import Decimal
from unittest.mock import AsyncMock, MagicMock, patch

import httpx
import pytest

from lightning_enable_mcp.budget_service import PendingConfirmation, SpendReservationResult
from lightning_enable_mcp.config import ApprovalLevel
from lightning_enable_mcp.operation_ledger import OperationLedger, OperationState
from lightning_enable_mcp.receipt_seam import ReceiptRecordingWallet
from lightning_enable_mcp.strike_wallet import BASE_URL, StrikeWallet
from lightning_enable_mcp.tools.send_onchain import onchain_operation_id, send_onchain

ADDR = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4"
AMOUNT = 50_000
QUOTE_ID = "quote-123"
PAYMENT_ID = "pay-456"
# 0.00000300 BTC fee in the quote = 300 sats
QUOTE_BODY = {"paymentQuoteId": QUOTE_ID, "onchainFee": {"amount": "0.00000300", "currency": "BTC"}}


# ---------------------------------------------------------------------------
# Fakes
# ---------------------------------------------------------------------------



class _TwoPartyBarrier:
    """Minimal 2-party barrier (asyncio.Barrier needs Python 3.11)."""

    def __init__(self):
        self._arrived = 0
        self._event = asyncio.Event()

    async def wait(self):
        self._arrived += 1
        if self._arrived >= 2:
            self._event.set()
        await self._event.wait()


def _onchain_attr(exc, name):
    """Read a cancel-signal attribute the way the tool must: on the exception itself, or on
    its __cause__/__context__ — Python 3.10's Task re-wraps a CancelledError and chains the
    original (as __context__ on 3.10)."""
    for e in (exc, getattr(exc, "__cause__", None), getattr(exc, "__context__", None)):
        if e is not None and hasattr(e, name):
            return getattr(e, name)
    return None


class StrikeFake:
    """Scriptable Strike API over httpx.MockTransport. Counts calls per step."""

    def __init__(self):
        self.quote_calls = 0
        self.execute_calls = 0
        self.status_calls = 0
        self.quote_behaviour = "ok"        # ok | timeout
        self.execute_behaviour = "pending"  # pending | completed | timeout | hang | reject
        self.poll_state = "PENDING"         # state GET /payments/{id} returns
        self.execute_started = asyncio.Event()

    async def handler(self, request: httpx.Request) -> httpx.Response:
        path = request.url.path
        if request.method == "POST" and path.endswith("/payment-quotes/onchain"):
            self.quote_calls += 1
            if self.quote_behaviour == "timeout":
                raise httpx.ReadTimeout("quote timed out", request=request)
            return httpx.Response(201, json=QUOTE_BODY)
        if request.method == "PATCH" and path.endswith(f"/payment-quotes/{QUOTE_ID}/execute"):
            self.execute_calls += 1
            self.execute_started.set()
            b = self.execute_behaviour
            if b == "timeout":
                raise httpx.ReadTimeout("execute timed out", request=request)
            if b == "hang":
                await asyncio.Event().wait()  # never returns; the test cancels it
            if b == "reject":
                return httpx.Response(422, json={"data": {"code": "INSUFFICIENT_BALANCE"}})
            if b == "rate_limited":
                return httpx.Response(429, json={"data": {"code": "RATE_LIMITED"}})
            if b == "no_payment_id":
                return httpx.Response(200, json={"state": "PENDING"})
            state = "COMPLETED" if b == "completed" else "PENDING"
            return httpx.Response(200, json={"paymentId": PAYMENT_ID, "state": state})
        if request.method == "GET" and path.endswith(f"/payments/{PAYMENT_ID}"):
            self.status_calls += 1
            body = {"paymentId": PAYMENT_ID, "state": self.poll_state}
            if self.poll_state == "COMPLETED":
                body["onchain"] = {"txnId": "txid-abc"}
            return httpx.Response(200, json=body)
        return httpx.Response(404, json={"error": "unexpected " + request.method + " " + path})

    def sends(self) -> int:
        """A 'send' is a new quote being created for execution."""
        return self.quote_calls


def make_strike(fake: StrikeFake) -> StrikeWallet:
    w = StrikeWallet(api_key="test-key-not-real")
    w._client = httpx.AsyncClient(base_url=BASE_URL, transport=httpx.MockTransport(fake.handler))
    w._connected = True
    # Keep the pending-poll short so the "poll times out" path runs in milliseconds.
    w.onchain_poll_timeout_secs = 0.05
    w.onchain_poll_interval_secs = 0.01
    return w


def make_receipts():
    receipts = MagicMock()
    receipts.log_payment = MagicMock(return_value=True)
    return receipts


def wrap(fake: StrikeFake, receipts=None):
    receipts = receipts or make_receipts()
    return ReceiptRecordingWallet(make_strike(fake), receipts, None), receipts


def make_budget():
    budget = MagicMock()
    approval = MagicMock()
    approval.level = ApprovalLevel.AUTO_APPROVE
    approval.amount_usd = Decimal("25.00")
    approval.denial_reason = None
    budget.check_approval_level = AsyncMock(return_value=approval)
    now = datetime.now(timezone.utc)
    pc = PendingConfirmation(
        nonce="ABC123", amount_sats=AMOUNT, amount_usd=Decimal("25.00"),
        tool_name="send_onchain", description=ADDR, destination=ADDR,
        created_at=now, expires_at=now + timedelta(minutes=2),
    )
    budget.validate_and_consume_confirmation = MagicMock(return_value=pc)
    budget.record_payment_time = MagicMock()
    counter = {"n": 0}

    async def _reserve(amt):
        counter["n"] += 1
        return SpendReservationResult.reserved(f"resv-{counter['n']}", amt)

    budget.try_reserve = AsyncMock(side_effect=_reserve)
    budget.commit_reservation = MagicMock()
    budget.release_reservation = MagicMock()
    return budget


async def call(wallet, budget, ledger, nonce="ABC123"):
    raw = await send_onchain(
        address=ADDR, amount_sats=AMOUNT, confirmation_nonce=nonce,
        wallet=wallet, budget_service=budget, operation_ledger=ledger,
    )
    return json.loads(raw)


@pytest.fixture
def ledger_path(tmp_path):
    return tmp_path / "operations.jsonl"


def op_id():
    return onchain_operation_id(ADDR, AMOUNT)


# ---------------------------------------------------------------------------
# Operation id
# ---------------------------------------------------------------------------


def test_operation_id_is_sha256_of_normalized_address_and_amount():
    expected = hashlib.sha256(f"onchain:{ADDR}:{AMOUNT}".encode()).hexdigest()
    assert onchain_operation_id(ADDR, AMOUNT) == "onchain:" + expected
    # bech32 is case-insensitive: the same destination must map to the same operation.
    assert onchain_operation_id("  " + ADDR.upper() + " ", AMOUNT) == onchain_operation_id(ADDR, AMOUNT)
    assert onchain_operation_id(ADDR, AMOUNT + 1) != onchain_operation_id(ADDR, AMOUNT)


# ---------------------------------------------------------------------------
# Wallet-level ambiguity signal
# ---------------------------------------------------------------------------


class TestStrikeWalletSubmissionSignal:
    @pytest.mark.asyncio
    async def test_poll_timeout_after_execute_is_submitted_pending_not_failure(self):
        fake = StrikeFake()
        result = await make_strike(fake).send_onchain(ADDR, AMOUNT)
        assert result.submitted is True
        assert result.success is True
        assert result.state == "PENDING"
        assert result.payment_id == PAYMENT_ID
        assert result.quote_id == QUOTE_ID

    @pytest.mark.asyncio
    async def test_execute_transport_timeout_is_submitted_unknown(self):
        fake = StrikeFake()
        fake.execute_behaviour = "timeout"
        result = await make_strike(fake).send_onchain(ADDR, AMOUNT)
        assert result.submitted is True
        assert result.success is False
        assert result.state == "UNKNOWN"
        assert result.quote_id == QUOTE_ID
        assert result.payment_id is None  # the quote id is NOT a payment id (lookup would 404)
        assert result.fee_sats == 300

    @pytest.mark.asyncio
    async def test_quote_timeout_is_not_submitted(self):
        fake = StrikeFake()
        fake.quote_behaviour = "timeout"
        result = await make_strike(fake).send_onchain(ADDR, AMOUNT)
        assert result.submitted is False
        assert result.success is False
        assert fake.execute_calls == 0

    @pytest.mark.asyncio
    async def test_execute_rejected_4xx_is_provider_failed(self):
        fake = StrikeFake()
        fake.execute_behaviour = "reject"
        result = await make_strike(fake).send_onchain(ADDR, AMOUNT)
        assert result.success is False
        assert result.state == "FAILED"

    @pytest.mark.asyncio
    async def test_cancel_after_execute_marks_exception_submitted(self):
        fake = StrikeFake()
        fake.execute_behaviour = "hang"
        task = asyncio.create_task(make_strike(fake).send_onchain(ADDR, AMOUNT))
        await asyncio.wait({task, asyncio.ensure_future(fake.execute_started.wait())}, timeout=5, return_when=asyncio.FIRST_COMPLETED)
        assert not task.done(), "send returned before the execute call was issued"
        task.cancel()
        with pytest.raises(asyncio.CancelledError) as ei:
            await task
        assert _onchain_attr(ei.value, "onchain_submitted") is True
        assert _onchain_attr(ei.value, "onchain_quote_id") == QUOTE_ID
        # A quote id must never masquerade as a payment id (status lookup would 404).
        assert _onchain_attr(ei.value, "onchain_payment_id") is None

    @pytest.mark.asyncio
    async def test_execute_429_is_ambiguous_not_rejected(self):
        """Parity with .NET: 408/409/429 on execute are NOT proof of rejection."""
        fake = StrikeFake()
        fake.execute_behaviour = "rate_limited"
        result = await make_strike(fake).send_onchain(ADDR, AMOUNT)
        assert result.success is False
        assert result.submitted is True
        assert result.state == "UNKNOWN"
        assert result.quote_id == QUOTE_ID

    @pytest.mark.asyncio
    async def test_execute_without_payment_id_keeps_payment_id_none(self):
        fake = StrikeFake()
        fake.execute_behaviour = "no_payment_id"
        result = await make_strike(fake).send_onchain(ADDR, AMOUNT)
        assert result.submitted is True
        assert result.quote_id == QUOTE_ID
        assert result.payment_id is None, "quote id must not be reported as a payment id"
        assert fake.status_calls == 0, "must not poll /payments/{quote_id}"

    @pytest.mark.asyncio
    async def test_get_onchain_payment_status(self):
        fake = StrikeFake()
        fake.poll_state = "COMPLETED"
        status = await make_strike(fake).get_onchain_payment_status(PAYMENT_ID)
        assert status.success is True
        assert status.state == "COMPLETED"
        assert status.txid == "txid-abc"
        assert status.payment_id == PAYMENT_ID
        assert fake.status_calls == 1


# ---------------------------------------------------------------------------
# Ledger
# ---------------------------------------------------------------------------


class TestOperationLedgerOnchain:
    def test_unknown_is_money_moving_and_ids_persist(self, ledger_path):
        from lightning_enable_mcp.operation_ledger import MONEY_MOVING_STATES
        assert OperationState.UNKNOWN in MONEY_MOVING_STATES
        assert OperationState.FAILED not in MONEY_MOVING_STATES

        ledger = OperationLedger(ledger_path)
        ledger.record_submitted("onchain:x", 100, "strike", kind="onchain")
        ledger.record_outcome("onchain:x", OperationState.PENDING, None,
                              payment_id="p1", quote_id="q1", tx_id=None)
        rec = OperationLedger(ledger_path).lookup("onchain:x")
        assert rec.state == OperationState.PENDING
        assert rec.payment_id == "p1"
        assert rec.quote_id == "q1"
        assert rec.kind == "onchain"
        assert rec.amount_sats == 100


# ---------------------------------------------------------------------------
# Tool
# ---------------------------------------------------------------------------


class TestSendOnchainTool:
    @pytest.mark.asyncio
    async def test_poll_timeout_commits_writes_pending_receipt_and_records_pending(self, ledger_path):
        fake = StrikeFake()
        wallet, receipts = wrap(fake)
        budget = make_budget()
        ledger = OperationLedger(ledger_path)

        parsed = await call(wallet, budget, ledger)

        assert parsed["state"] == "PENDING"
        assert parsed["paymentId"] == PAYMENT_ID
        assert parsed["receipt_written"] is True
        budget.commit_reservation.assert_called_once_with("resv-1", AMOUNT + 300)
        budget.release_reservation.assert_not_called()
        assert receipts.log_payment.call_args.kwargs["status"] == "pending"
        rec = ledger.lookup(op_id())
        assert rec.state == OperationState.PENDING
        assert rec.payment_id == PAYMENT_ID
        assert rec.quote_id == QUOTE_ID

    @pytest.mark.asyncio
    async def test_execute_timeout_is_ambiguous_commits_and_records_unknown(self, ledger_path):
        fake = StrikeFake()
        fake.execute_behaviour = "timeout"
        wallet, receipts = wrap(fake)
        budget = make_budget()
        ledger = OperationLedger(ledger_path)

        parsed = await call(wallet, budget, ledger)

        assert parsed["success"] is False
        assert parsed["state"] == "UNKNOWN"
        assert parsed["paymentId"] is None
        assert parsed["quoteId"] == QUOTE_ID
        assert parsed["receipt_written"] is True
        warning = parsed["warning"].lower()
        assert "may have executed" in warning
        assert "budget" in warning and "retained" in warning
        assert "same address and amount" in warning
        # Known fee from the quote -> principal + fee, not released.
        budget.commit_reservation.assert_called_once_with("resv-1", AMOUNT + 300)
        budget.release_reservation.assert_not_called()
        assert receipts.log_payment.call_args.kwargs["status"] == "pending"
        assert ledger.lookup(op_id()).state == OperationState.UNKNOWN
        assert "test-key-not-real" not in json.dumps(parsed)

    @pytest.mark.asyncio
    async def test_quote_timeout_releases_records_failed_and_retry_sends(self, ledger_path):
        fake = StrikeFake()
        fake.quote_behaviour = "timeout"
        wallet, receipts = wrap(fake)
        budget = make_budget()
        ledger = OperationLedger(ledger_path)

        parsed = await call(wallet, budget, ledger)
        assert parsed["success"] is False
        budget.release_reservation.assert_called_once_with("resv-1")
        budget.commit_reservation.assert_not_called()
        assert ledger.lookup(op_id()).state == OperationState.FAILED
        receipts.log_payment.assert_not_called()

        fake.quote_behaviour = "ok"
        fake.execute_behaviour = "completed"
        parsed2 = await call(wallet, budget, ledger)
        assert parsed2["success"] is True
        assert fake.execute_calls == 1
        assert ledger.lookup(op_id()).state == OperationState.SETTLED

    @pytest.mark.parametrize("execute_behaviour", ["pending", "timeout"])
    @pytest.mark.asyncio
    async def test_retry_after_pending_or_unknown_reports_status_without_sending(
        self, ledger_path, execute_behaviour
    ):
        fake = StrikeFake()
        fake.execute_behaviour = execute_behaviour
        wallet, _ = wrap(fake)
        ledger = OperationLedger(ledger_path)
        await call(wallet, make_budget(), ledger)
        sends_before = fake.sends()
        executes_before = fake.execute_calls
        fake.status_calls = 0
        recorded = ledger.lookup(op_id())

        budget2 = make_budget()
        parsed = await call(wallet, budget2, ledger)

        assert fake.sends() == sends_before, "retry must NOT create a new quote/send"
        assert fake.execute_calls == executes_before
        assert parsed["success"] is False
        assert parsed["errorCode"] == "ALREADY_SUBMITTED"
        assert parsed["paymentId"] == recorded.payment_id
        budget2.try_reserve.assert_not_called()
        # Status lookup is attempted exactly once when a payment id was recorded; with no
        # payment id (execute response lost) there is nothing to look up and the message
        # says so rather than probing /payments/{quote_id}.
        has_payment_id = recorded.payment_id is not None
        assert has_payment_id == (execute_behaviour == "pending")
        assert fake.status_calls == (1 if has_payment_id else 0)
        assert parsed["statusLookup"]["attempted"] is has_payment_id
        if not has_payment_id:
            assert "not recorded" in parsed["message"]

    @pytest.mark.asyncio
    async def test_retry_status_lookup_updates_ledger(self, ledger_path):
        fake = StrikeFake()
        wallet, _ = wrap(fake)
        ledger = OperationLedger(ledger_path)
        await call(wallet, make_budget(), ledger)
        assert ledger.lookup(op_id()).state == OperationState.PENDING

        fake.poll_state = "COMPLETED"
        parsed = await call(wallet, make_budget(), ledger)
        assert parsed["state"] == "COMPLETED"
        assert parsed["txId"] == "txid-abc"
        rec = ledger.lookup(op_id())
        assert rec.state == OperationState.SETTLED
        assert rec.tx_id == "txid-abc"
        assert fake.execute_calls == 1

    @pytest.mark.asyncio
    async def test_retry_without_status_lookup_refuses_naming_payment_id(self, ledger_path):
        ledger = OperationLedger(ledger_path)
        ledger.record_submitted(op_id(), AMOUNT, "lnd", kind="onchain")
        ledger.record_outcome(op_id(), OperationState.UNKNOWN, None, payment_id="pay-xyz")

        from lightning_enable_mcp.lnd_wallet import LndWallet
        lnd = MagicMock(spec=LndWallet)  # LND has no get_onchain_payment_status
        lnd.send_onchain = AsyncMock()
        parsed = await call(lnd, make_budget(), ledger)
        lnd.send_onchain.assert_not_called()
        assert parsed["errorCode"] == "ALREADY_SUBMITTED"
        assert "pay-xyz" in parsed["message"]
        assert "verify" in parsed["message"].lower()

    @pytest.mark.asyncio
    async def test_retry_after_proven_presubmit_failure_sends_exactly_once(self, ledger_path):
        ledger = OperationLedger(ledger_path)
        ledger.record_submitted(op_id(), AMOUNT, "strike", kind="onchain")
        ledger.record_outcome(op_id(), OperationState.FAILED, None)
        fake = StrikeFake()
        fake.execute_behaviour = "completed"
        wallet, _ = wrap(fake)
        parsed = await call(wallet, make_budget(), ledger)
        assert parsed["success"] is True
        assert fake.sends() == 1
        assert fake.execute_calls == 1

    @pytest.mark.asyncio
    async def test_ledger_survives_restart_and_still_refuses(self, ledger_path):
        fake = StrikeFake()
        fake.execute_behaviour = "timeout"
        wallet, _ = wrap(fake)
        await call(wallet, make_budget(), OperationLedger(ledger_path))
        assert fake.sends() == 1

        # "Restart": fresh wallet, fresh budget, fresh ledger instance over the same file.
        fake2 = StrikeFake()
        wallet2, _ = wrap(fake2)
        parsed = await call(wallet2, make_budget(), OperationLedger(ledger_path))
        assert fake2.sends() == 0
        assert fake2.execute_calls == 0
        assert parsed["errorCode"] == "ALREADY_SUBMITTED"

    @pytest.mark.asyncio
    async def test_cancel_after_submission_commits_and_reraises(self, ledger_path):
        fake = StrikeFake()
        fake.execute_behaviour = "hang"
        wallet, receipts = wrap(fake)
        budget = make_budget()
        ledger = OperationLedger(ledger_path)
        task = asyncio.create_task(call(wallet, budget, ledger))
        await asyncio.wait({task, asyncio.ensure_future(fake.execute_started.wait())}, timeout=5, return_when=asyncio.FIRST_COMPLETED)
        assert not task.done(), "send returned before the execute call was issued"
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task
        budget.commit_reservation.assert_called_once_with("resv-1", AMOUNT + 300)
        budget.release_reservation.assert_not_called()
        assert ledger.lookup(op_id()).state == OperationState.UNKNOWN
        assert receipts.log_payment.call_args.kwargs["status"] == "pending"

    @pytest.mark.asyncio
    async def test_cancel_before_submission_releases(self, ledger_path):
        fake = StrikeFake()
        wallet, _ = wrap(fake)
        budget = make_budget()
        ledger = OperationLedger(ledger_path)
        started = asyncio.Event()
        inner = wallet._inner
        orig_request = inner._request

        async def slow_request(method, path, json_data=None):
            if path.endswith("/payment-quotes/onchain"):
                started.set()
                await asyncio.Event().wait()  # cancelled while still quoting (pre-execute)
            return await orig_request(method, path, json_data)

        inner._request = slow_request
        task = asyncio.create_task(call(wallet, budget, ledger))
        waiter = asyncio.create_task(started.wait())
        await asyncio.wait({task, waiter}, timeout=5, return_when=asyncio.FIRST_COMPLETED)
        waiter.cancel()
        assert not task.done(), "send_onchain returned before reaching the quote step"
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task
        budget.release_reservation.assert_called_once_with("resv-1")
        budget.commit_reservation.assert_not_called()
        assert ledger.lookup(op_id()).state == OperationState.FAILED

    @pytest.mark.asyncio
    async def test_concurrent_same_send_executes_at_most_once(self, ledger_path):
        fake = StrikeFake()
        fake.execute_behaviour = "completed"
        wallet, _ = wrap(fake)
        ledger = OperationLedger(ledger_path)
        barrier = _TwoPartyBarrier()  # asyncio.Barrier is 3.11+; CI runs 3.10 too

        def budget_with_barrier():
            b = make_budget()
            orig = b.try_reserve.side_effect

            async def reserve(amt):
                r = await orig(amt)
                await barrier.wait()  # both calls past every gate before either claims
                return r

            b.try_reserve = AsyncMock(side_effect=reserve)
            return b

        b1, b2 = budget_with_barrier(), budget_with_barrier()
        r1, r2 = await asyncio.gather(call(wallet, b1, ledger), call(wallet, b2, ledger))

        assert fake.sends() <= 1
        assert fake.execute_calls == 1
        outcomes = sorted([r1.get("errorCode") or "SENT", r2.get("errorCode") or "SENT"])
        assert outcomes == ["ALREADY_SUBMITTED", "SENT"]
        # The loser's reservation is released (it moved nothing).
        assert b1.release_reservation.call_count + b2.release_reservation.call_count == 1


class TestLndSubmissionSignal:
    @pytest.mark.asyncio
    async def test_lnd_read_timeout_is_submitted_unknown_connect_error_is_not(self):
        from lightning_enable_mcp.lnd_wallet import LndError, LndWallet

        async def raise_timeout(*a, **k):
            try:
                raise httpx.ReadTimeout("t")
            except httpx.ReadTimeout as e:
                raise LndError("Failed to connect to LND: t") from e

        async def raise_connect(*a, **k):
            try:
                raise httpx.ConnectError("c")
            except httpx.ConnectError as e:
                raise LndError("Failed to connect to LND: c") from e

        w = LndWallet.__new__(LndWallet)
        with patch.object(LndWallet, "is_configured", property(lambda self: True)):
            w._request = raise_timeout
            r = await w.send_onchain(ADDR, AMOUNT)
            assert r.submitted is True and r.state == "UNKNOWN" and r.success is False
            w._request = raise_connect
            r2 = await w.send_onchain(ADDR, AMOUNT)
            assert r2.submitted is False and r2.success is False
