"""
Tests for the idempotency guard at the wallet seam: the SAME Lightning invoice is never
submitted twice — even across a process restart or an agent retry. Mirrors the .NET
IdempotentWalletServiceTests.
"""

import uuid
from pathlib import Path

import pytest

from lightning_enable_mcp.idempotent_wallet import DuplicateSubmissionError, IdempotentWallet
from lightning_enable_mcp.operation_ledger import OperationLedger
from lightning_enable_mcp.wallet_errors import PaymentPendingError

INVOICE = "lnbc1000n1p3duplicatetest"
VALID_PREIMAGE = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20"


def _temp_path(tmp_path: Path) -> Path:
    return tmp_path / f"op-idem-{uuid.uuid4().hex}.jsonl"


class _CountingWallet:
    """Minimal inner wallet that counts pay calls and returns/raises a fixed outcome."""

    def __init__(self, preimage=VALID_PREIMAGE, raises=None):
        self.pay_count = 0
        self._preimage = preimage
        self._raises = raises

    @property
    def is_configured(self):
        return True

    async def pay_invoice(self, bolt11, *args, **kwargs):
        self.pay_count += 1
        if self._raises is not None:
            raise self._raises
        return self._preimage


@pytest.mark.asyncio
async def test_same_invoice_twice_second_refused_wallet_called_once(tmp_path):
    inner = _CountingWallet()
    svc = IdempotentWallet(inner, OperationLedger(_temp_path(tmp_path)))

    first = await svc.pay_invoice(INVOICE)
    assert first == VALID_PREIMAGE

    with pytest.raises(DuplicateSubmissionError):
        await svc.pay_invoice(INVOICE)

    assert inner.pay_count == 1  # the invoice reached the wallet exactly once


@pytest.mark.asyncio
async def test_duplicate_across_restart_is_refused(tmp_path):
    path = _temp_path(tmp_path)
    inner1 = _CountingWallet()
    await IdempotentWallet(inner1, OperationLedger(path)).pay_invoice(INVOICE)

    # New process: fresh wallet + fresh ledger over the SAME file.
    inner2 = _CountingWallet()
    with pytest.raises(DuplicateSubmissionError):
        await IdempotentWallet(inner2, OperationLedger(path)).pay_invoice(INVOICE)

    assert inner2.pay_count == 0  # a settled invoice from a prior session is not re-paid


@pytest.mark.asyncio
async def test_failed_first_attempt_allows_retry(tmp_path):
    path = _temp_path(tmp_path)
    failing = _CountingWallet(raises=RuntimeError("no route"))
    with pytest.raises(RuntimeError):
        await IdempotentWallet(failing, OperationLedger(path)).pay_invoice(INVOICE)

    # A proven hard failure moved no funds, so a genuine retry MUST be allowed.
    succeeding = _CountingWallet()
    result = await IdempotentWallet(succeeding, OperationLedger(path)).pay_invoice(INVOICE)
    assert result == VALID_PREIMAGE
    assert succeeding.pay_count == 1


@pytest.mark.asyncio
async def test_pending_first_attempt_blocks_resubmission(tmp_path):
    path = _temp_path(tmp_path)
    pending = _CountingWallet(raises=PaymentPendingError(
        "still settling", provider="opennode", tracking_id="t-1", status="pending"))
    with pytest.raises(PaymentPendingError):
        await IdempotentWallet(pending, OperationLedger(path)).pay_invoice(INVOICE)

    # A pending payment may still settle — re-submitting could pay twice, so it is refused.
    again = _CountingWallet()
    with pytest.raises(DuplicateSubmissionError):
        await IdempotentWallet(again, OperationLedger(path)).pay_invoice(INVOICE)
    assert again.pay_count == 0


@pytest.mark.asyncio
async def test_different_invoices_both_pay(tmp_path):
    inner = _CountingWallet()
    svc = IdempotentWallet(inner, OperationLedger(_temp_path(tmp_path)))

    await svc.pay_invoice("lnbc1000n1p3aaaa")
    await svc.pay_invoice("lnbc1000n1p3bbbb")

    assert inner.pay_count == 2  # distinct invoices are distinct operations
