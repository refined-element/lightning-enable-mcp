"""
Reconciled cross-port send_onchain idempotency contract (mirrors the .NET port):
optional intent_id, response keys, canonical texts, and a blocked retry that never
touches the confirmation gate, the budget, or the wallet send.

No real network, no real funds (StrikeWallet over httpx.MockTransport, mocked LND).
"""

import hashlib
import json
from unittest.mock import AsyncMock, MagicMock

import pytest

from lightning_enable_mcp.operation_ledger import OperationLedger, OperationState
from lightning_enable_mcp.tools.send_onchain import (
    SEND_ONCHAIN_TOOL,
    onchain_operation_id,
    send_onchain,
)
from tests.test_send_onchain_idempotency import (
    ADDR,
    AMOUNT,
    PAYMENT_ID,
    StrikeFake,
    call,
    make_budget,
    op_id,
    wrap,
)

CANON_AMBIGUOUS_WARNING = (
    "The send may have executed at the provider even though this call could not confirm it. "
    "The budget for it has been retained (not released). Calling send_onchain again with the "
    "same address and amount will report this payment's status rather than re-send. Check the "
    "provider dashboard / get_balance BEFORE retrying with any other parameters — on-chain "
    "payments are irreversible."
)
CANON_PENDING_NOTE = (
    "On-chain payments normally stay PENDING for ~10 minutes until confirmed. Do NOT send "
    "again: calling send_onchain again with the same address and amount will report this "
    "payment's status rather than re-send."
)
CANON_PLAIN_FAILURE_WARNING = (
    "If this failure was a network/timeout error, the send may still have executed at "
    "the provider. Check the provider dashboard / get_balance BEFORE retrying — on-chain "
    "payments are irreversible."
)


def canon_blocked_message(pid, provider):
    return (
        "Nothing was sent by this call: an on-chain send for the same address and amount was "
        f"already submitted (payment id {pid}). On-chain payments are irreversible, so it will "
        "not be sent again while that payment may have moved funds. Verify its status at the "
        f"provider ({provider}) before doing anything else. To intentionally pay this address "
        "this amount again, supply a new intent_id."
    )


@pytest.fixture
def ledger_path(tmp_path):
    return tmp_path / "operations.jsonl"


async def call_intent(wallet, budget, ledger, intent_id, nonce="ABC123"):
    raw = await send_onchain(
        address=ADDR, amount_sats=AMOUNT, confirmation_nonce=nonce,
        wallet=wallet, budget_service=budget, operation_ledger=ledger, intent_id=intent_id,
    )
    return json.loads(raw)


class TestIntentId:
    def test_operation_id_with_intent_id(self):
        base = f"onchain:{ADDR}:{AMOUNT}"
        assert onchain_operation_id(ADDR, AMOUNT, " run-2 ") == "onchain:" + hashlib.sha256(
            (base + ":run-2").encode()).hexdigest()
        assert onchain_operation_id(ADDR, AMOUNT, None) == onchain_operation_id(ADDR, AMOUNT)
        assert onchain_operation_id(ADDR, AMOUNT, "") == onchain_operation_id(ADDR, AMOUNT)
        assert onchain_operation_id(ADDR, AMOUNT, "   ") == onchain_operation_id(ADDR, AMOUNT)

    @pytest.mark.asyncio
    async def test_new_intent_id_sends_once_same_intent_blocked(self, ledger_path):
        fake = StrikeFake()
        fake.execute_behaviour = "completed"
        wallet, _ = wrap(fake)
        ledger = OperationLedger(ledger_path)

        assert (await call(wallet, make_budget(), ledger))["success"] is True
        assert fake.execute_calls == 1
        assert (await call(wallet, make_budget(), ledger))["errorCode"] == "ALREADY_SUBMITTED"

        budget = make_budget()
        parsed = await call_intent(wallet, budget, ledger, "second-payment")
        assert parsed["success"] is True
        assert fake.execute_calls == 2
        # A fresh code is still required and consumed for the real send.
        budget.validate_and_consume_confirmation.assert_called_once()

        parsed = await call_intent(wallet, make_budget(), ledger, "second-payment")
        assert parsed["errorCode"] == "ALREADY_SUBMITTED"
        assert fake.execute_calls == 2

    @pytest.mark.parametrize("blank", ["", "   "])
    @pytest.mark.asyncio
    async def test_blank_intent_id_equals_omitted(self, ledger_path, blank):
        fake = StrikeFake()
        fake.execute_behaviour = "completed"
        wallet, _ = wrap(fake)
        ledger = OperationLedger(ledger_path)
        await call(wallet, make_budget(), ledger)
        parsed = await call_intent(wallet, make_budget(), ledger, blank)
        assert parsed["errorCode"] == "ALREADY_SUBMITTED"
        assert fake.execute_calls == 1

    def test_schema_exposes_intent_id(self):
        prop = SEND_ONCHAIN_TOOL.inputSchema["properties"]["intent_id"]
        assert prop["type"] == "string"
        assert prop["description"] == (
            "Optional idempotency scope. Omit for normal use. Supply a NEW value only when you "
            "intentionally need to pay the same address the same amount again; a repeat with the "
            "same address, amount and intent_id is reported as status, never re-sent."
        )
        assert "intent_id" not in SEND_ONCHAIN_TOOL.inputSchema["required"]


class TestResponses:
    @pytest.mark.parametrize("nonce", ["ABC123", None])
    @pytest.mark.asyncio
    async def test_blocked_retry_keys_text_and_no_side_effects(self, ledger_path, nonce):
        ledger = OperationLedger(ledger_path)
        ledger.record_submitted(op_id(), AMOUNT, "lnd", kind="onchain")
        ledger.record_outcome(op_id(), OperationState.UNKNOWN, None, payment_id="pay-xyz")
        from lightning_enable_mcp.lnd_wallet import LndWallet
        lnd = MagicMock(spec=LndWallet)
        lnd.send_onchain = AsyncMock()
        budget = make_budget()
        budget.request_confirmation = AsyncMock()

        parsed = await call(lnd, budget, ledger, nonce=nonce)

        lnd.send_onchain.assert_not_called()
        budget.request_confirmation.assert_not_called()
        budget.validate_and_consume_confirmation.assert_not_called()
        budget.try_reserve.assert_not_called()
        budget.check_approval_level.assert_not_called()
        assert parsed["success"] is False
        assert parsed["errorCode"] == "ALREADY_SUBMITTED"
        assert parsed["duplicate"] is True
        assert parsed["provider"] == "LND"
        assert parsed["receipt_written"] is False
        assert parsed["statusLookup"] == {"attempted": False, "succeeded": False}
        for k in ("state", "paymentId", "quoteId", "txId"):
            assert k in parsed
        assert parsed["message"] == canon_blocked_message("pay-xyz", "LND")

    @pytest.mark.asyncio
    async def test_blocked_retry_without_payment_id_says_not_recorded(self, ledger_path):
        ledger = OperationLedger(ledger_path)
        ledger.record_submitted(op_id(), AMOUNT, "strike", kind="onchain")
        wallet, _ = wrap(StrikeFake())
        parsed = await call(wallet, make_budget(), ledger)
        assert parsed["provider"] == "Strike"
        assert parsed["message"] == canon_blocked_message("not recorded", "Strike")

    @pytest.mark.asyncio
    async def test_ambiguous_keys_and_warning(self, ledger_path):
        fake = StrikeFake()
        fake.execute_behaviour = "timeout"
        wallet, _ = wrap(fake)
        parsed = await call(wallet, make_budget(), OperationLedger(ledger_path))
        assert parsed["success"] is False
        assert parsed["errorCode"] == "OUTCOME_UNKNOWN"
        assert parsed["state"] == "UNKNOWN"
        assert parsed["provider"] == "Strike"
        assert parsed["amountSats"] == AMOUNT
        for k in ("paymentId", "quoteId", "txId", "receipt_written"):
            assert k in parsed
        assert parsed["warning"] == CANON_AMBIGUOUS_WARNING

    @pytest.mark.asyncio
    async def test_pending_success_has_note_state_payment_id(self, ledger_path):
        wallet, _ = wrap(StrikeFake())  # execute -> PENDING, poll stays PENDING
        parsed = await call(wallet, make_budget(), OperationLedger(ledger_path))
        assert parsed["success"] is True
        assert parsed["state"] == "PENDING"
        assert parsed["paymentId"] == PAYMENT_ID
        assert parsed["note"] == CANON_PENDING_NOTE

    def test_canonical_warning_constants(self):
        import importlib
        mod = importlib.import_module("lightning_enable_mcp.tools.send_onchain")
        assert mod.AMBIGUOUS_SEND_WARNING == CANON_AMBIGUOUS_WARNING
        assert mod.PLAIN_FAILURE_WARNING == CANON_PLAIN_FAILURE_WARNING
