"""
Tests for the durable operation ledger — the idempotency/restart-safety record that stops
a retry (even across a process restart) from causing a blind duplicate payment. Mirrors the
.NET OperationLedgerTests.
"""

import uuid
from pathlib import Path

from lightning_enable_mcp.operation_ledger import (
    OperationLedger,
    OperationRecord,
    OperationState,
)


def _temp_path(tmp_path: Path) -> Path:
    return tmp_path / f"op-ledger-{uuid.uuid4().hex}.jsonl"


def test_unknown_operation_returns_none(tmp_path):
    ledger = OperationLedger(_temp_path(tmp_path))
    assert ledger.lookup("ln:unknown") is None


def test_record_submitted_then_lookup(tmp_path):
    ledger = OperationLedger(_temp_path(tmp_path))
    ledger.record_submitted("ln:abc", 100, "NWC")

    rec = ledger.lookup("ln:abc")
    assert isinstance(rec, OperationRecord)
    assert rec.state == OperationState.SUBMITTED
    assert rec.amount_sats == 100


def test_record_outcome_takes_latest_state(tmp_path):
    ledger = OperationLedger(_temp_path(tmp_path))
    ledger.record_submitted("ln:abc", 100, "NWC")
    ledger.record_outcome("ln:abc", OperationState.SETTLED, payment_hash="deadbeef")

    assert ledger.lookup("ln:abc").state == OperationState.SETTLED


def test_lookup_survives_restart(tmp_path):
    path = _temp_path(tmp_path)
    OperationLedger(path).record_submitted("ln:abc", 100, "NWC")

    # A brand-new ledger over the same file (simulating a process restart) must see the
    # prior state — this is what stops a blind duplicate payment after a crash/restart.
    assert OperationLedger(path).lookup("ln:abc").state == OperationState.SUBMITTED


def test_distinct_operations_do_not_interfere(tmp_path):
    ledger = OperationLedger(_temp_path(tmp_path))
    ledger.record_submitted("ln:aaa", 100, "NWC")
    ledger.record_outcome("ln:aaa", OperationState.FAILED_NO_FUNDS, None)
    ledger.record_submitted("ln:bbb", 200, "NWC")

    assert ledger.lookup("ln:aaa").state == OperationState.FAILED_NO_FUNDS
    assert ledger.lookup("ln:bbb").state == OperationState.SUBMITTED


def test_never_persists_secrets(tmp_path):
    path = _temp_path(tmp_path)
    ledger = OperationLedger(path)
    ledger.record_submitted("ln:abc", 100, "NWC")
    ledger.record_outcome("ln:abc", OperationState.SETTLED, "deadbeefcafe")

    contents = path.read_text(encoding="utf-8")
    # A payment hash (public routing data) MAY appear; a preimage/macaroon/invoice must NOT.
    assert "preimage" not in contents
    assert "macaroon" not in contents
    assert "lnbc" not in contents
