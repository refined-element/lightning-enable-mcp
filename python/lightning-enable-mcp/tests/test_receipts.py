"""Tests for the durable payment receipt log (ReceiptService) + get_receipts tool."""

import json

import pytest

from lightning_enable_mcp.receipt_service import ReceiptService, wallet_label_from
from lightning_enable_mcp.tools.get_receipts import get_receipts


def _svc(tmp_path, label="NWC"):
    return ReceiptService(wallet_label=label, receipts_path=tmp_path / "receipts.jsonl")


def test_log_and_read_roundtrip(tmp_path):
    svc = _svc(tmp_path)
    svc.log_payment(
        endpoint="https://api.example.com/x",
        amount_sats=1,
        policy="auto_approve",
        session_spent_sats=1,
        session_remaining_usd=149.0,
    )
    recs = svc.read_recent()
    assert len(recs) == 1
    r = recs[0]
    assert r["type"] == "l402_payment_receipt"
    assert r["amountSats"] == 1
    assert r["wallet"] == "NWC"
    assert r["policy"] == "auto_approve"
    assert r["endpoint"] == "https://api.example.com/x"
    assert r["sessionSpentSats"] == 1
    assert "connection" in r["revokePath"].lower()


def test_receipt_carries_no_secrets(tmp_path):
    svc = _svc(tmp_path)
    svc.log_payment(endpoint="https://api.example.com/x", amount_sats=1, policy="auto_approve")
    raw = (tmp_path / "receipts.jsonl").read_text(encoding="utf-8")
    for forbidden in ("preimage", "macaroon", "nostr+walletconnect", "connectionString", "secret="):
        assert forbidden not in raw


def test_append_preserves_order(tmp_path):
    svc = _svc(tmp_path)
    for i in range(1, 4):
        svc.log_payment(endpoint=f"https://e/{i}", amount_sats=i, policy="auto_approve")
    assert [r["amountSats"] for r in svc.read_recent()] == [1, 2, 3]


def test_read_recent_returns_newest_last_and_respects_limit(tmp_path):
    svc = _svc(tmp_path)
    for i in range(5):
        svc.log_payment(endpoint=f"https://e/{i}", amount_sats=i, policy="p")
    recs = svc.read_recent(limit=2)
    assert len(recs) == 2
    assert recs[-1]["amountSats"] == 4


def test_read_missing_file_is_empty(tmp_path):
    svc = ReceiptService(wallet_label="NWC", receipts_path=tmp_path / "nope.jsonl")
    assert svc.read_recent() == []


def test_read_tolerates_torn_line(tmp_path):
    p = tmp_path / "receipts.jsonl"
    p.write_text('{"amountSats": 1}\nnot valid json\n{"amountSats": 2}\n', encoding="utf-8")
    svc = ReceiptService(wallet_label="NWC", receipts_path=p)
    assert [r["amountSats"] for r in svc.read_recent()] == [1, 2]


def test_rotation_bounds_the_file(tmp_path, monkeypatch):
    monkeypatch.setattr("lightning_enable_mcp.receipt_service.MAX_RECEIPTS_BYTES", 120)
    svc = _svc(tmp_path)
    for i in range(20):
        svc.log_payment(endpoint=f"https://e/{i}", amount_sats=i, policy="auto_approve")
    # once past the cap, the live file is rotated to a .1 backup
    assert (tmp_path / "receipts.jsonl.1").exists()
    # live file still readable and small
    assert svc.read_recent()  # non-empty, current file


def test_write_failure_never_raises(tmp_path):
    # parent path is a FILE, so mkdir under it fails — must be swallowed.
    a_file = tmp_path / "afile"
    a_file.write_text("x", encoding="utf-8")
    svc = ReceiptService(wallet_label="NWC", receipts_path=a_file / "sub" / "receipts.jsonl")
    svc.log_payment(endpoint="https://e", amount_sats=1, policy="p")  # must not raise
    assert svc.read_recent() == []


def test_wallet_label_from():
    class NWCWallet:  # noqa: N801 - mirrors real class name for the mapping
        pass

    class StrikeWallet:  # noqa: N801
        pass

    assert wallet_label_from(NWCWallet()) == "NWC"
    assert wallet_label_from(StrikeWallet()) == "Strike"
    assert wallet_label_from(None) == "unknown"


@pytest.mark.asyncio
async def test_get_receipts_tool_summarizes(tmp_path):
    svc = _svc(tmp_path)
    svc.log_payment(endpoint="https://e/1", amount_sats=2, policy="auto_approve")
    svc.log_payment(endpoint="https://e/2", amount_sats=3, policy="auto_approve")
    out = json.loads(await get_receipts(limit=10, receipt_service=svc))
    assert out["success"] is True
    assert out["count"] == 2
    assert out["totalSatsInView"] == 5
    assert out["logFile"].endswith("receipts.jsonl")
    assert len(out["receipts"]) == 2


@pytest.mark.asyncio
async def test_get_receipts_tool_no_service():
    out = json.loads(await get_receipts(receipt_service=None))
    assert out["success"] is False


@pytest.mark.asyncio
async def test_get_receipts_clamps_limit(tmp_path):
    svc = _svc(tmp_path)
    svc.log_payment(endpoint="https://e", amount_sats=1, policy="p")
    out = json.loads(await get_receipts(limit=99999, receipt_service=svc))
    assert out["success"] is True  # huge limit clamped, no error
