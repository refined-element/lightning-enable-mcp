"""
Coverage for the wallet-seam receipt writer (parity with the .NET
ReceiptRecordingWalletService): EVERY payment that moves value through a
wallet must leave exactly one durable receipt in receipts.jsonl, regardless
of which tool initiated it. Before this seam existed only access_l402_resource
(and create_lightning_enable_account) wrote receipts — a pay_invoice purchase
left no durable record at all.
"""

import hashlib
import importlib
import json
from decimal import Decimal
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

from lightning_enable_mcp.config import ApprovalLevel
from lightning_enable_mcp.l402_client import L402Error
from lightning_enable_mcp.receipt_seam import (
    PaymentReceiptScope,
    ReceiptRecordingWallet,
    unwrap_wallet,
)
from lightning_enable_mcp.receipt_service import ReceiptService
from lightning_enable_mcp.wallet_errors import (
    PaymentPendingError,
    PreimageUnavailableError,
)

TEST_INVOICE = "lnbc210n1p3abcdef"  # placeholder; decode is patched to 21 sats
TEST_SATS = 21
TEST_PREIMAGE = "5f78ca4b8e2c11d3a9b0f6e1d2c3b4a5968778695a4b3c2d1e0f9a8b7c6d5e4f"
EXPECTED_PAYMENT_HASH = hashlib.sha256(bytes.fromhex(TEST_PREIMAGE)).hexdigest()
DECODED_PAYMENT_HASH = "ab" * 32  # what the (patched) bolt11 decode reports

_seam_module = importlib.import_module("lightning_enable_mcp.receipt_seam")
_pay_invoice_module = importlib.import_module("lightning_enable_mcp.tools.pay_invoice")


@pytest.fixture(autouse=True)
def _patch_decode():
    """The placeholder invoice doesn't really decode — patch to a fixed amount+hash."""
    def fake_decode(_invoice):
        m = MagicMock()
        m.amount_msat = TEST_SATS * 1000
        m.amount = TEST_SATS
        m.payment_hash = DECODED_PAYMENT_HASH
        return m

    with patch.object(_seam_module, "decode_bolt11", side_effect=fake_decode), \
         patch.object(_pay_invoice_module, "decode_bolt11", side_effect=fake_decode):
        yield


def _svc(tmp_path):
    return ReceiptService(wallet_label="unknown", receipts_path=tmp_path / "receipts.jsonl")


def _read(tmp_path):
    return _svc(tmp_path).read_recent(200)


def _make_wallet(cls_name="NWCWallet", pay=None, onchain=None):
    """Fake wallet whose CLASS NAME drives wallet_label_from (NWC/Strike/LND/...)."""
    async def default_pay(self, bolt11, *a, **k):
        return TEST_PREIMAGE

    attrs = {"pay_invoice": pay or default_pay}
    if onchain is not None:
        attrs["send_onchain"] = onchain
    return type(cls_name, (), attrs)()


def _budget(spent=100):
    budget = MagicMock()
    budget.get_status = MagicMock(return_value={
        "session": {"spentSats": spent, "spentUsd": 0.1, "remainingUsd": 99.9, "requestCount": 1}
    })
    approval = MagicMock()
    approval.level = ApprovalLevel.AUTO_APPROVE
    approval.requires_confirmation = False
    approval.amount_usd = Decimal("0.02")
    approval.denial_reason = None
    approval.remaining_session_budget_usd = Decimal("100.00")
    budget.check_approval_level = AsyncMock(return_value=approval)
    return budget


# ---------------------------------------------------------------
# The decorator seam: one receipt per settled payment, any wallet
# ---------------------------------------------------------------


@pytest.mark.asyncio
async def test_settled_invoice_writes_one_generic_receipt(tmp_path):
    seam = ReceiptRecordingWallet(_make_wallet(), _svc(tmp_path), _budget())

    preimage = await seam.pay_invoice(TEST_INVOICE)

    assert preimage == TEST_PREIMAGE
    recs = _read(tmp_path)
    assert len(recs) == 1
    r = recs[0]
    assert r["type"] == "payment_receipt"
    assert r["kind"] == "invoice"
    assert r["wallet"] == "NWC"
    assert r["amountSats"] == TEST_SATS
    assert r["status"] == "settled"
    assert r["paymentHash"] == EXPECTED_PAYMENT_HASH
    assert "connection" in r["revokePath"].lower()
    assert r["timestamp"].endswith("Z")


@pytest.mark.asyncio
async def test_receipt_readable_by_fresh_reader(tmp_path):
    seam = ReceiptRecordingWallet(_make_wallet(), _svc(tmp_path), None)
    await seam.pay_invoice(TEST_INVOICE)

    # A brand-new reader on the same path (fresh process) must see the receipt.
    fresh = ReceiptService(wallet_label="unknown", receipts_path=tmp_path / "receipts.jsonl")
    recs = fresh.read_recent(10)
    assert len(recs) == 1
    assert recs[0]["amountSats"] == TEST_SATS


@pytest.mark.asyncio
@pytest.mark.parametrize("cls_name,label", [
    ("NWCWallet", "NWC"),
    ("StrikeWallet", "Strike"),
    ("LndWallet", "LND"),
])
async def test_every_provider_writes_via_seam(tmp_path, cls_name, label):
    seam = ReceiptRecordingWallet(_make_wallet(cls_name), _svc(tmp_path), None)
    await seam.pay_invoice(TEST_INVOICE)
    assert _read(tmp_path)[0]["wallet"] == label


@pytest.mark.asyncio
async def test_pending_writes_pending_receipt_and_reraises(tmp_path):
    # Funds are committed on a pending payment (the budget records them); the
    # durable log must not under-report the budget.
    async def pay(self, bolt11, *a, **k):
        raise PaymentPendingError("in flight", provider="NWC", tracking_id="trk_1")

    seam = ReceiptRecordingWallet(_make_wallet(pay=pay), _svc(tmp_path), None)
    with pytest.raises(PaymentPendingError):
        await seam.pay_invoice(TEST_INVOICE)

    r = _read(tmp_path)[0]
    assert r["status"] == "pending"
    # No preimage exists yet — the hash comes from the decoded invoice instead.
    assert r["paymentHash"] == DECODED_PAYMENT_HASH


@pytest.mark.asyncio
async def test_settled_without_preimage_writes_settled_receipt_and_reraises(tmp_path):
    async def pay(self, bolt11, *a, **k):
        raise PreimageUnavailableError("no proof", provider="OpenNode", tracking_id="wd_1")

    seam = ReceiptRecordingWallet(_make_wallet("OpenNodeWallet", pay=pay), _svc(tmp_path), None)
    with pytest.raises(PreimageUnavailableError):
        await seam.pay_invoice(TEST_INVOICE)

    r = _read(tmp_path)[0]
    assert r["status"] == "settled"
    assert r["wallet"] == "OpenNode"


@pytest.mark.asyncio
async def test_hard_failure_writes_no_receipt(tmp_path):
    async def pay(self, bolt11, *a, **k):
        raise L402Error("no route")

    seam = ReceiptRecordingWallet(_make_wallet(pay=pay), _svc(tmp_path), None)
    with pytest.raises(L402Error):
        await seam.pay_invoice(TEST_INVOICE)
    assert _read(tmp_path) == []


@pytest.mark.asyncio
async def test_falsy_preimage_writes_no_receipt(tmp_path):
    # Callers treat a falsy preimage as a failed payment; no money provably moved.
    async def pay(self, bolt11, *a, **k):
        return ""

    seam = ReceiptRecordingWallet(_make_wallet(pay=pay), _svc(tmp_path), None)
    await seam.pay_invoice(TEST_INVOICE)
    assert _read(tmp_path) == []


@pytest.mark.asyncio
async def test_send_onchain_success_writes_onchain_receipt(tmp_path):
    async def onchain(self, address, amount_sats, *a, **k):
        return SimpleNamespace(
            success=True, payment_id="pay_1", txid="txid_abc", state="COMPLETED",
            amount_sats=4990, fee_sats=250, error_message=None, error_code=None,
        )

    seam = ReceiptRecordingWallet(
        _make_wallet("StrikeWallet", onchain=onchain), _svc(tmp_path), _budget(spent=100))

    result = await seam.send_onchain("bc1qtestaddr", 5000)

    assert result.success is True
    r = _read(tmp_path)[0]
    assert r["kind"] == "onchain"
    assert r["wallet"] == "Strike"
    assert r["amountSats"] == 4990          # what the provider says was sent
    assert r["feeSats"] == 250
    assert r["txId"] == "txid_abc"
    assert r["status"] == "settled"
    # Projection uses the REQUESTED amount + fee — the same figures the tool
    # records into the budget — so receipts reconcile with get_budget_status.
    assert r["sessionSpentSats"] == 100 + 5000 + 250


@pytest.mark.asyncio
async def test_send_onchain_failure_writes_nothing(tmp_path):
    async def onchain(self, address, amount_sats, *a, **k):
        return SimpleNamespace(success=False, error_message="nope", error_code="NOT_SUPPORTED")

    seam = ReceiptRecordingWallet(_make_wallet("StrikeWallet", onchain=onchain), _svc(tmp_path), None)
    await seam.send_onchain("bc1qtestaddr", 5000)
    assert _read(tmp_path) == []


@pytest.mark.asyncio
async def test_receipt_file_carries_no_secrets(tmp_path):
    seam = ReceiptRecordingWallet(_make_wallet(), _svc(tmp_path), _budget())
    with PaymentReceiptScope("l402", context="https://api.example.com/paid", policy="auto_approve"):
        await seam.pay_invoice(TEST_INVOICE)

    raw = (tmp_path / "receipts.jsonl").read_text(encoding="utf-8")
    assert TEST_PREIMAGE not in raw, "the preimage is proof-of-payment and must never persist"
    assert TEST_INVOICE not in raw, "the BOLT11 invoice must never persist"
    for forbidden in ("preimage", "macaroon", "nostr+walletconnect", "connectionString"):
        assert forbidden not in raw


# ---------------------------------------------------------------
# Ambient intent scope: context enrichment + honest write signal
# ---------------------------------------------------------------


@pytest.mark.asyncio
async def test_scope_enriches_receipt_and_signals_written(tmp_path):
    seam = ReceiptRecordingWallet(_make_wallet(), _svc(tmp_path), None)

    with PaymentReceiptScope("l402", context="https://api.example.com/data", policy="auto_approve") as scope:
        await seam.pay_invoice(TEST_INVOICE)
        assert scope.receipt_written is True

    r = _read(tmp_path)[0]
    assert r["kind"] == "l402"
    assert r["context"] == "https://api.example.com/data"
    assert r["policy"] == "auto_approve"


def test_scope_receipt_written_is_none_before_any_payment():
    with PaymentReceiptScope("invoice") as scope:
        assert scope.receipt_written is None


def test_scope_is_reset_after_exit():
    with PaymentReceiptScope("invoice"):
        assert PaymentReceiptScope.current() is not None
    assert PaymentReceiptScope.current() is None


@pytest.mark.asyncio
async def test_write_failure_signals_false_payment_still_succeeds(tmp_path):
    # Parent path is a FILE so the write must fail — the payment result must be
    # untouched, and the failure must be VISIBLE via the scope, never silent.
    a_file = tmp_path / "afile"
    a_file.write_text("x", encoding="utf-8")
    bad = ReceiptService(wallet_label="NWC", receipts_path=a_file / "sub" / "receipts.jsonl")

    seam = ReceiptRecordingWallet(_make_wallet(), bad, None)
    with PaymentReceiptScope("invoice") as scope:
        preimage = await seam.pay_invoice(TEST_INVOICE)

    assert preimage == TEST_PREIMAGE, "a receipt failure must NEVER break a payment"
    assert scope.receipt_written is False, "a failed write must be visible, not hidden"


@pytest.mark.asyncio
async def test_session_spent_projects_post_payment_total(tmp_path):
    # The seam writes BEFORE the caller records the spend, so the receipt carries
    # the projected post-payment session total (current + this payment).
    seam = ReceiptRecordingWallet(_make_wallet(), _svc(tmp_path), _budget(spent=100))
    await seam.pay_invoice(TEST_INVOICE)
    assert _read(tmp_path)[0]["sessionSpentSats"] == 100 + TEST_SATS


def test_old_l402_receipt_lines_remain_readable(tmp_path):
    p = tmp_path / "receipts.jsonl"
    p.write_text(
        '{"type":"l402_payment_receipt","timestamp":"2026-08-01T00:00:00.000Z",'
        '"endpoint":"https://e/x","amountSats":7,"wallet":"NWC","policy":"auto_approve",'
        '"sessionSpentSats":7,"revokePath":"r"}\n',
        encoding="utf-8",
    )
    svc = ReceiptService(wallet_label="NWC", receipts_path=p)
    assert svc.log_payment(kind="invoice", amount_sats=21, status="settled") is True

    recs = svc.read_recent(10)
    assert [r["type"] for r in recs] == ["l402_payment_receipt", "payment_receipt"]


def test_unwrap_wallet_returns_inner():
    inner = _make_wallet()
    seam = ReceiptRecordingWallet(inner, ReceiptService(wallet_label="NWC"), None)
    assert unwrap_wallet(seam) is inner
    assert unwrap_wallet(inner) is inner


# ---------------------------------------------------------------
# Tool level: the actual reported bug (pay_invoice left no record)
# ---------------------------------------------------------------


@pytest.mark.asyncio
async def test_pay_invoice_tool_persists_receipt_and_reports_receipt_written(tmp_path):
    from lightning_enable_mcp.tools.pay_invoice import pay_invoice

    seam = ReceiptRecordingWallet(_make_wallet(), _svc(tmp_path), None)
    result = json.loads(await pay_invoice(invoice=TEST_INVOICE, wallet=seam))

    assert result["success"] is True
    assert result["receipt_written"] is True

    recs = _read(tmp_path)
    assert len(recs) == 1
    assert recs[0]["kind"] == "invoice"
    assert recs[0]["amountSats"] == TEST_SATS


@pytest.mark.asyncio
async def test_access_resource_writes_exactly_one_receipt(tmp_path):
    # The per-tool log_payment call is gone; the seam is the only writer. An L402
    # payment must produce exactly ONE receipt line, enriched with the endpoint.
    from lightning_enable_mcp.tools.access_resource import access_l402_resource

    seam = ReceiptRecordingWallet(_make_wallet(), _svc(tmp_path), _budget())

    async def fetch(url, method, headers, body, max_sats):
        # Simulate the client's payment leg: pays via the (decorated) wallet.
        await seam.pay_invoice(TEST_INVOICE)
        return ("paid content", TEST_SATS)

    client = SimpleNamespace(fetch=fetch)

    with patch("lightning_enable_mcp.tools.access_resource.validate_url_allowed", new=AsyncMock()):
        result = json.loads(await access_l402_resource(
            url="https://api.example.com/paid/data",
            l402_client=client,
            budget_service=_budget(),
        ))

    assert result["success"] is True
    assert result["receipt_written"] is True

    recs = _read(tmp_path)
    assert len(recs) == 1, "the seam is the single receipt writer — no per-tool double write"
    assert recs[0]["kind"] == "l402"
    assert "api.example.com" in recs[0]["context"]


@pytest.mark.asyncio
async def test_access_resource_paid_then_error_still_reports_receipt(tmp_path):
    # Paid, then the authorized retry failed: the seam already wrote the receipt
    # inside the client's payment leg — the tool result must carry receipt_written.
    from lightning_enable_mcp.tools.access_resource import access_l402_resource

    seam = ReceiptRecordingWallet(_make_wallet(), _svc(tmp_path), _budget())

    async def fetch(url, method, headers, body, max_sats):
        await seam.pay_invoice(TEST_INVOICE)
        err = L402Error("Request failed after payment: 500 boom")
        err.amount_paid = TEST_SATS
        err.l402_token = "mac:preimage"
        raise err

    client = SimpleNamespace(fetch=fetch)

    with patch("lightning_enable_mcp.tools.access_resource.validate_url_allowed", new=AsyncMock()):
        result = json.loads(await access_l402_resource(
            url="https://api.example.com/paid/data",
            l402_client=client,
            budget_service=_budget(),
        ))

    assert result["success"] is False
    assert result["alreadyPaid"] is True
    assert result["receipt_written"] is True
    assert len(_read(tmp_path)) == 1
