"""
P0 concurrency regression for the check-then-pay-then-record race in BudgetService
(atomic spend-reservations remediation) — the Python mirror of the .NET
SpendReservationConcurrencyTests.

The session cap is only a real cap if a payment cannot be authorized against a
balance that another in-flight payment is about to consume. ``check_approval_level``
evaluates the limit, releases its lock, and ``record_spend`` runs only AFTER the
wallet call — so two concurrent payments can both pass their check against the same
pre-payment balance and collectively exceed the cap.

This drives the REAL ``BudgetService`` through the REAL ``pay_invoice`` tool with a
gate-controlled fake wallet that parks each payment at the point AFTER the budget
check but BEFORE any spend is recorded — the exact window the race lives in. It
asserts the invariant (one payment may settle, total <= cap), not the current buggy
behavior, so it stays valid through the reservation-lifecycle refactor.
"""

import asyncio
import importlib
import json
from decimal import Decimal
from unittest.mock import AsyncMock, MagicMock

import pytest

from lightning_enable_mcp.budget_service import BudgetService
from lightning_enable_mcp.config import (
    UserBudgetConfiguration,
    PaymentLimits,
    TierThresholds,
    SessionSettings,
)
from lightning_enable_mcp.tools.pay_invoice import pay_invoice

# tools/__init__ re-exports the pay_invoice function, shadowing the submodule; import
# the real module so decode_bolt11 can be patched on it (same trick as test_pay_invoice).
_pay_invoice_module = importlib.import_module("lightning_enable_mcp.tools.pay_invoice")

# 60 sats per payment; 100-sat session cap. One fits, two (120) must not.
PAYMENT_SATS = 60
SESSION_CAP_SATS = 100
INVOICE_A = "lnbc600n1p3aaaaaa"
INVOICE_B = "lnbc600n1p3bbbbbb"


def _fake_price_service():
    """Deterministic mock rate: 1 USD <-> 1000 sats (NOT a real BTC price)."""
    price = MagicMock()
    price.usd_to_sats = AsyncMock(side_effect=lambda usd: int(Decimal(str(usd)) * 1000))
    price.sats_to_usd = AsyncMock(side_effect=lambda sats: Decimal(sats) / 1000)
    price.get_btc_price = AsyncMock(return_value=Decimal("100000"))
    price.get_cached_btc_price = MagicMock(return_value=Decimal("100000"))
    price.get_last_snapshot = MagicMock(return_value=None)
    return price


def _config_service():
    """Session + per-payment cap = $0.10 (= 100 sats at the mock rate). Tiers leave a
    $0.06 (60-sat) payment in the auto-approve band; first-payment approval and cooldown
    off — so the ONLY thing that can stop the second payment is the session cap."""
    cfg = UserBudgetConfiguration(
        tiers=TierThresholds(
            auto_approve=Decimal("1.00"),
            log_and_approve=Decimal("10.00"),
            form_confirm=Decimal("100.00"),
            url_confirm=Decimal("1000.00"),
        ),
        limits=PaymentLimits(
            max_per_payment=Decimal("0.10"),
            max_per_session=Decimal("0.10"),
        ),
        session=SessionSettings(require_approval_for_first_payment=False, cooldown_seconds=0),
    )
    svc = MagicMock()
    svc.configuration = cfg
    return svc


class _GatedWallet:
    """Parks every ``pay_invoice`` after signaling it has been entered, holding it until
    the test opens the gate. Keeps both concurrent tool calls at the window between the
    budget check and the spend record."""

    def __init__(self):
        self.pay_calls = 0
        self.first_entry = asyncio.Event()
        self.release = asyncio.Event()

    async def pay_invoice(self, invoice: str) -> str:
        self.pay_calls += 1
        self.first_entry.set()      # announce "a payment reached the wallet"
        await self.release.wait()   # park until the test opens the gate
        return "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20"


@pytest.mark.asyncio
async def test_two_concurrent_payments_cannot_exceed_session_cap(monkeypatch):
    # Both invoices decode to 60 sats regardless of content.
    def fake_decode(_invoice):
        m = MagicMock()
        m.amount_msat = PAYMENT_SATS * 1000
        m.amount = PAYMENT_SATS
        return m

    monkeypatch.setattr(_pay_invoice_module, "decode_bolt11", fake_decode)

    budget = BudgetService(config_service=_config_service(), price_service=_fake_price_service())
    wallet = _GatedWallet()

    # Fire both payments concurrently. Each does check -> (park in wallet) -> record.
    task_a = asyncio.create_task(
        pay_invoice(invoice=INVOICE_A, max_sats=1000, wallet=wallet, budget_service=budget)
    )
    task_b = asyncio.create_task(
        pay_invoice(invoice=INVOICE_B, max_sats=1000, wallet=wallet, budget_service=budget)
    )

    # At least one payment always reaches the wallet.
    await asyncio.wait_for(wallet.first_entry.wait(), timeout=5)
    # Let a would-be second entry happen (buggy) or the loser be denied (fixed).
    await asyncio.sleep(0.2)
    second_entered = wallet.pay_calls >= 2

    # Open the gate and let both tool calls finish.
    wallet.release.set()
    result_a, result_b = await asyncio.gather(task_a, task_b)

    # The cap is a cap.
    assert wallet.pay_calls == 1, (
        "exactly one payment may reach the wallet; the other must be denied before it can spend"
    )
    assert not second_entered, (
        "the second concurrent payment must be denied at the budget check, not reach the wallet"
    )

    successes = sum(1 for r in (result_a, result_b) if json.loads(r).get("success") is True)
    assert successes == 1, "exactly one of two over-cap concurrent payments may succeed"

    assert budget.session_spent_sats <= SESSION_CAP_SATS, (
        "settled spend must never exceed the configured session cap"
    )
    assert budget.session_spent_sats == PAYMENT_SATS, "only the single winning payment settled"
