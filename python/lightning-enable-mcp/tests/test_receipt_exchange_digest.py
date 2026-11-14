"""Request/response SHA-256 capture for paid L402 exchanges (hashes only, never bodies)."""

import hashlib
import json
from pathlib import Path
from unittest.mock import AsyncMock, patch

import httpx
import pytest

from lightning_enable_mcp.l402_client import L402Client
from lightning_enable_mcp.receipt_service import (
    EXCHANGE_DIGEST_TYPE,
    ReceiptService,
    request_sha256,
    response_sha256,
)


def _find_shared(name: str) -> Path:
    for parent in Path(__file__).resolve().parents:
        candidate = parent / "shared" / name
        if candidate.exists():
            return candidate
    raise FileNotFoundError(name)


DIGESTS = json.loads(
    _find_shared("attestation-golden-vector.json").read_text(encoding="utf-8")
)["exchangeDigests"]

PREIMAGE = "ab" * 32
PAYMENT_HASH = hashlib.sha256(bytes.fromhex(PREIMAGE)).hexdigest()


@pytest.mark.parametrize("case", DIGESTS["requests"], ids=lambda c: c["method"])
def test_request_digest_matches_shared_vector(case):
    body = case["bodyUtf8"].encode("utf-8") if case["bodyUtf8"] is not None else None
    assert request_sha256(case["method"], case["url"], body) == case["requestSha256"]


@pytest.mark.parametrize("case", DIGESTS["responses"], ids=lambda c: str(len(c["bodyUtf8"])))
def test_response_digest_matches_shared_vector(case):
    assert response_sha256(case["bodyUtf8"].encode("utf-8")) == case["responseSha256"]


def test_digest_is_folded_into_its_receipt_and_not_listed_alone(tmp_path):
    svc = ReceiptService(wallet_label="NWC", receipts_path=tmp_path / "receipts.jsonl")
    svc.log_payment(kind="l402", amount_sats=10, status="settled", payment_hash=PAYMENT_HASH)
    svc.log_payment(kind="invoice", amount_sats=5, status="settled", payment_hash="cd" * 32)
    assert svc.log_exchange_digest(PAYMENT_HASH, "11" * 32, "22" * 32) is True

    rows = svc.read_recent(10)
    assert [r["type"] for r in rows] == ["payment_receipt", "payment_receipt"]
    assert rows[0]["requestSha256"] == "11" * 32
    assert rows[0]["responseSha256"] == "22" * 32
    assert "requestSha256" not in rows[1]

    raw = (tmp_path / "receipts.jsonl").read_text(encoding="utf-8").splitlines()
    assert json.loads(raw[-1])["type"] == EXCHANGE_DIGEST_TYPE


def test_read_recent_limit_still_counts_receipts(tmp_path):
    svc = ReceiptService(receipts_path=tmp_path / "r.jsonl")
    for i in range(5):
        h = f"{i:02x}" * 32
        svc.log_payment(kind="l402", amount_sats=i + 1, payment_hash=h)
        svc.log_exchange_digest(h, "11" * 32, "22" * 32)
    rows = svc.read_recent(3)
    assert [r["amountSats"] for r in rows] == [3, 4, 5]
    assert all(r["requestSha256"] == "11" * 32 for r in rows)


def test_lookup_payment(tmp_path):
    svc = ReceiptService(receipts_path=tmp_path / "r.jsonl")
    svc.log_payment(kind="l402", amount_sats=10, payment_hash=PAYMENT_HASH)
    svc.log_payment(kind="invoice", amount_sats=5, payment_hash="cd" * 32)
    svc.log_exchange_digest(PAYMENT_HASH, "11" * 32, "22" * 32)

    assert svc.lookup_payment(PAYMENT_HASH.upper()) == {
        "paymentHash": PAYMENT_HASH,
        "requestSha256": "11" * 32,
        "responseSha256": "22" * 32,
    }
    assert svc.lookup_payment("cd" * 32) == {"paymentHash": "cd" * 32}
    assert svc.lookup_payment("ef" * 32) is None


def test_bodies_are_never_written(tmp_path):
    svc = ReceiptService(receipts_path=tmp_path / "r.jsonl")
    svc.log_exchange_digest(PAYMENT_HASH, request_sha256("GET", "https://x/", None), response_sha256(b"SECRET-BODY"))
    assert "SECRET-BODY" not in (tmp_path / "r.jsonl").read_text(encoding="utf-8")


@pytest.mark.asyncio
async def test_paid_l402_fetch_records_the_exchange_digest(tmp_path):
    url = "https://api.provider.com/premium"
    req = httpx.Request("GET", url)
    challenge = httpx.Response(
        402,
        headers=[("WWW-Authenticate", 'L402 macaroon="YWJjZGVm", invoice="lnbc100n1pjtest"')],
        request=req,
    )
    ok = httpx.Response(200, request=req, content=b'{"forecast":"sun"}')
    wallet = AsyncMock()
    wallet.pay_invoice = AsyncMock(return_value=PREIMAGE)
    svc = ReceiptService(receipts_path=tmp_path / "r.jsonl")
    client = L402Client(wallet=wallet, budget_service=None, receipt_service=svc)
    client._http_client.request = AsyncMock(side_effect=[challenge, ok])

    with patch.object(client, "_get_invoice_amount_msat", return_value=10_000):
        text, amount, _ = await client.fetch(url, max_sats=1000)

    assert text == '{"forecast":"sun"}' and amount == 10
    assert svc.lookup_payment(PAYMENT_HASH) == {
        "paymentHash": PAYMENT_HASH,
        "requestSha256": request_sha256("GET", url, None),
        "responseSha256": hashlib.sha256(b'{"forecast":"sun"}').hexdigest(),
    }
    assert "sun" not in (tmp_path / "r.jsonl").read_text(encoding="utf-8")
    await client.close()
