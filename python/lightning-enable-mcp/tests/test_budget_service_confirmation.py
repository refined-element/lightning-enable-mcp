"""
Tests for BudgetService out-of-band confirmation machinery.

These guard the funds-safety property: a confirmation code is bound to the EXACT amount
AND tool it was approved for (no cross-amount / cross-tool replay), is one-time use, and
expires. Mirrors the .NET BudgetService confirmation tests.
"""

from datetime import datetime, timezone, timedelta
from decimal import Decimal
from unittest.mock import MagicMock

from lightning_enable_mcp.budget_service import BudgetService


def _service() -> BudgetService:
    # The confirmation methods are pure (no price/config), so mocks are fine here.
    return BudgetService(config_service=MagicMock(), price_service=MagicMock())


def test_create_pending_confirmation_generates_code_and_peek_does_not_consume():
    svc = _service()
    pc = svc.create_pending_confirmation(1000, Decimal("0.01"), "pay_invoice", "inv...", destination="d")

    assert pc.nonce and len(pc.nonce) == 6
    assert pc.amount_sats == 1000
    assert pc.tool_name == "pay_invoice"
    # validate_confirmation PEEKS — it must not consume (callable twice).
    assert svc.validate_confirmation(pc.nonce) is pc
    assert svc.validate_confirmation(pc.nonce) is pc


def test_confirmation_bound_to_amount_and_tool_and_not_consumed_on_mismatch():
    svc = _service()
    pc = svc.create_pending_confirmation(1000, Decimal("0.01"), "pay_invoice", "inv...", destination="lnbc-dest")

    # Wrong AMOUNT (right tool + dest) -> rejected; a 1,000-sat approval can't authorize 1,000,000.
    assert svc.validate_and_consume_confirmation(pc.nonce, 1_000_000, "pay_invoice", "lnbc-dest") is None
    # Wrong TOOL (right amount + dest) -> rejected; a pay_invoice code can't move send_onchain funds.
    assert svc.validate_and_consume_confirmation(pc.nonce, 1000, "send_onchain", "lnbc-dest") is None

    # Neither mismatch consumed it — the correct (amount, tool) still works.
    ok = svc.validate_and_consume_confirmation(pc.nonce, 1000, "pay_invoice", "lnbc-dest")
    assert ok is pc
    # One-time use: a second consume fails.
    assert svc.validate_and_consume_confirmation(pc.nonce, 1000, "pay_invoice", "lnbc-dest") is None


def test_confirmation_bound_to_destination_and_not_consumed_on_mismatch():
    """#21 anti-redirect: a code approved for one destination (invoice / URL / on-chain
    address) must not authorize a payment to a DIFFERENT destination, even when the amount
    and tool match. A mismatch must NOT consume the code, so the legitimate retry still works.
    """
    svc = _service()
    pc = svc.create_pending_confirmation(
        1000, Decimal("0.01"), "pay_invoice", "lnbc1...AAA", destination="lnbc1aaa"
    )
    assert pc.destination == "lnbc1aaa"

    # Same amount + tool, but a DIFFERENT destination -> rejected (the redirect attack).
    assert svc.validate_and_consume_confirmation(pc.nonce, 1000, "pay_invoice", "lnbc1bbb") is None
    # Whitespace-only difference is tolerated (trim), not a bypass either way.
    assert svc.validate_and_consume_confirmation(pc.nonce, 1000, "pay_invoice", "  lnbc1aaa  ") is pc
    # Consumed once -> gone.
    assert svc.validate_and_consume_confirmation(pc.nonce, 1000, "pay_invoice", "lnbc1aaa") is None


def test_expired_confirmation_is_rejected():
    svc = _service()
    pc = svc.create_pending_confirmation(1000, Decimal("0.01"), "pay_invoice", "inv...", destination="d")
    pc.expires_at = datetime.now(timezone.utc) - timedelta(seconds=1)  # force expiry

    assert svc.validate_confirmation(pc.nonce) is None
    assert svc.validate_and_consume_confirmation(pc.nonce, 1000, "pay_invoice", "d") is None


def test_create_pending_confirmation_regenerates_on_collision(monkeypatch):
    import lightning_enable_mcp.budget_service as bs

    svc = _service()
    existing = svc.create_pending_confirmation(1000, Decimal("0.01"), "pay_invoice", "a", destination="da")

    # Force the next code to first collide with the live one, then resolve to ZZZZZZ.
    seq = iter(list(existing.nonce) + list("ZZZZZZ"))
    monkeypatch.setattr(bs.secrets, "choice", lambda _chars: next(seq))

    new = svc.create_pending_confirmation(2000, Decimal("0.02"), "send_onchain", "b", destination="db")
    assert new.nonce == "ZZZZZZ"
    assert new.nonce != existing.nonce
    # The original human-approved confirmation must survive — never overwritten.
    assert svc.validate_confirmation(existing.nonce) is existing


def test_blank_or_unknown_code_returns_none():
    svc = _service()
    assert svc.validate_confirmation("") is None
    assert svc.validate_confirmation("UNKNOWN") is None
    assert svc.validate_and_consume_confirmation("", 1000, "pay_invoice", "d") is None
    assert svc.validate_and_consume_confirmation("NOPE12", 1000, "pay_invoice", "d") is None
