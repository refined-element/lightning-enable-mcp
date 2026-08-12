"""Tests for the durable payment receipt log (ReceiptService) + get_receipts tool."""

import json

import pytest

from lightning_enable_mcp.receipt_service import ReceiptService, wallet_label_from
from lightning_enable_mcp.tools.get_receipts import get_receipts


def _svc(tmp_path, label="NWC"):
    return ReceiptService(wallet_label=label, receipts_path=tmp_path / "receipts.jsonl")


def _log(svc, amount_sats=1, context=None, policy="auto_approve", **kwargs):
    return svc.log_payment(
        kind="l402",
        amount_sats=amount_sats,
        status="settled",
        context=context,
        policy=policy,
        **kwargs,
    )


def test_log_and_read_roundtrip(tmp_path):
    svc = _svc(tmp_path)
    assert _log(svc, amount_sats=1, context="https://api.example.com/x", session_spent_sats=1) is True
    recs = svc.read_recent()
    assert len(recs) == 1
    r = recs[0]
    assert r["type"] == "payment_receipt"
    assert r["kind"] == "l402"
    assert r["amountSats"] == 1
    assert r["wallet"] == "NWC"
    assert r["status"] == "settled"
    assert r["policy"] == "auto_approve"
    assert r["context"] == "https://api.example.com/x"
    assert r["sessionSpentSats"] == 1
    assert "connection" in r["revokePath"].lower()
    # timestamp is the canonical millisecond Z form (parity with .NET)
    assert r["timestamp"].endswith("Z")


def test_optional_fields_are_omitted_not_null(tmp_path):
    svc = _svc(tmp_path)
    svc.log_payment(kind="invoice", amount_sats=5)
    r = svc.read_recent()[0]
    for absent in ("status", "paymentHash", "context", "policy", "sessionSpentSats", "feeSats", "txId"):
        assert absent not in r, f"'{absent}' was not supplied and must be omitted"


def test_per_call_wallet_label_overrides_bound_label(tmp_path):
    # The seam passes the actual paying wallet's label (e.g. an on-chain send via
    # a secondary Strike wallet while the primary is LND).
    svc = _svc(tmp_path, label="LND")
    svc.log_payment(kind="onchain", amount_sats=5, wallet_label="Strike")
    r = svc.read_recent()[0]
    assert r["wallet"] == "Strike"
    assert "strike" in r["revokePath"].lower()


def test_read_recent_zero_or_negative_is_empty(tmp_path):
    svc = _svc(tmp_path)
    for i in range(3):
        _log(svc, amount_sats=i, context=f"https://e/{i}")
    assert svc.read_recent(0) == []
    assert svc.read_recent(-5) == []


def test_read_recent_skips_non_object_lines(tmp_path):
    p = tmp_path / "receipts.jsonl"
    p.write_text('{"amountSats": 1}\n42\n"a string"\n[1,2]\n{"amountSats": 2}\n', encoding="utf-8")
    svc = ReceiptService(wallet_label="NWC", receipts_path=p)
    assert [r["amountSats"] for r in svc.read_recent()] == [1, 2]


def test_read_recent_includes_rotated_backup(tmp_path):
    p = tmp_path / "receipts.jsonl"
    p.with_name(p.name + ".1").write_text('{"amountSats": 1}\n{"amountSats": 2}\n', encoding="utf-8")
    p.write_text('{"amountSats": 3}\n', encoding="utf-8")
    svc = ReceiptService(wallet_label="NWC", receipts_path=p)
    # backup (older) first, then live (newer)
    assert [r["amountSats"] for r in svc.read_recent()] == [1, 2, 3]


def test_old_l402_receipt_lines_remain_readable(tmp_path):
    # Pre-generalization lines written by older releases must keep reading fine.
    p = tmp_path / "receipts.jsonl"
    p.write_text(
        '{"type":"l402_payment_receipt","timestamp":"2026-08-01T00:00:00.000Z",'
        '"endpoint":"https://e/x","amountSats":7,"wallet":"NWC","policy":"auto_approve",'
        '"sessionSpentSats":7,"revokePath":"r"}\n',
        encoding="utf-8",
    )
    svc = ReceiptService(wallet_label="NWC", receipts_path=p)
    _log(svc, amount_sats=21)
    assert [r["type"] for r in svc.read_recent()] == ["l402_payment_receipt", "payment_receipt"]


def test_receipt_carries_no_secrets(tmp_path):
    svc = _svc(tmp_path)
    _log(svc, context="https://api.example.com/x")
    raw = (tmp_path / "receipts.jsonl").read_text(encoding="utf-8")
    for forbidden in ("preimage", "macaroon", "nostr+walletconnect", "connectionString", "secret="):
        assert forbidden not in raw


def test_append_preserves_order(tmp_path):
    svc = _svc(tmp_path)
    for i in range(1, 4):
        _log(svc, amount_sats=i, context=f"https://e/{i}")
    assert [r["amountSats"] for r in svc.read_recent()] == [1, 2, 3]


def test_read_recent_returns_newest_last_and_respects_limit(tmp_path):
    svc = _svc(tmp_path)
    for i in range(5):
        _log(svc, amount_sats=i, context=f"https://e/{i}")
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
        _log(svc, amount_sats=i, context=f"https://e/{i}")
    # once past the cap, the live file is rotated to a .1 backup
    assert (tmp_path / "receipts.jsonl.1").exists()
    # live file still readable and small
    assert svc.read_recent()  # non-empty, current file


def test_write_failure_never_raises_and_returns_false(tmp_path):
    # parent path is a FILE, so mkdir under it fails — must be swallowed, and the
    # failure reported through the return value (the receipt_written signal).
    a_file = tmp_path / "afile"
    a_file.write_text("x", encoding="utf-8")
    svc = ReceiptService(wallet_label="NWC", receipts_path=a_file / "sub" / "receipts.jsonl")
    assert _log(svc) is False  # must not raise
    assert svc.read_recent() == []


def test_wallet_label_from():
    class NWCWallet:  # noqa: N801 - mirrors real class name for the mapping
        pass

    class StrikeWallet:  # noqa: N801
        pass

    assert wallet_label_from(NWCWallet()) == "NWC"
    assert wallet_label_from(StrikeWallet()) == "Strike"
    assert wallet_label_from(None) == "unknown"


def test_wallet_label_from_unwraps_receipt_seam():
    from lightning_enable_mcp.receipt_seam import ReceiptRecordingWallet

    class LndWallet:  # noqa: N801
        pass

    seam = ReceiptRecordingWallet(LndWallet(), ReceiptService(wallet_label="unknown"), None)
    assert wallet_label_from(seam) == "LND"


@pytest.mark.asyncio
async def test_get_receipts_tool_summarizes(tmp_path):
    svc = _svc(tmp_path)
    _log(svc, amount_sats=2, context="https://e/1")
    _log(svc, amount_sats=3, context="https://e/2")
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
    _log(svc)
    out = json.loads(await get_receipts(limit=99999, receipt_service=svc))
    assert out["success"] is True  # huge limit clamped, no error


@pytest.mark.asyncio
async def test_get_receipts_tolerates_bad_amount(tmp_path):
    # A stray non-numeric amountSats must never crash the whole read.
    p = tmp_path / "receipts.jsonl"
    p.write_text('{"amountSats": "5"}\n{"amountSats": 3}\n', encoding="utf-8")
    svc = ReceiptService(wallet_label="NWC", receipts_path=p)
    out = json.loads(await get_receipts(limit=10, receipt_service=svc))
    assert out["success"] is True
    assert out["totalSatsInView"] == 3  # numeric one counted, string one skipped
