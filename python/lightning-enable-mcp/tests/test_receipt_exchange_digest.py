"""Salted request/response digests for paid L402 exchanges (digests only, never bodies)."""

import hashlib
import hmac
import json
from pathlib import Path
from unittest.mock import AsyncMock, patch

import httpx
import pytest

from lightning_enable_mcp.l402_client import L402Client
from lightning_enable_mcp.receipt_service import (
    DIGEST_SCHEME,
    EXCHANGE_DIGEST_TYPE,
    ReceiptService,
    request_digest,
    response_digest,
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
NONCE = bytes.fromhex(DIGESTS["nonceHex"])

PREIMAGE = "ab" * 32
PAYMENT_HASH = hashlib.sha256(bytes.fromhex(PREIMAGE)).hexdigest()


@pytest.mark.parametrize("case", DIGESTS["requests"], ids=lambda c: c["method"])
def test_request_digest_matches_shared_vector(case):
    body = case["bodyUtf8"].encode("utf-8") if case["bodyUtf8"] is not None else None
    assert request_digest(NONCE, case["method"], case["url"], body) == case["requestDigest"]


@pytest.mark.parametrize("case", DIGESTS["responses"], ids=lambda c: str(len(c["bodyUtf8"])))
def test_response_digest_matches_shared_vector(case):
    assert response_digest(NONCE, case["bodyUtf8"].encode("utf-8")) == case["responseDigest"]


def test_request_digest_is_hmac_with_the_nonce_as_key():
    msg = b"GET https://x.example/a?city=Paris\n"
    assert request_digest(NONCE, "get", "https://x.example/a?city=Paris", None) == hmac.new(
        NONCE, msg, hashlib.sha256
    ).hexdigest()


def test_digest_cannot_be_matched_by_guessing_without_the_nonce(tmp_path):
    """The reviewer's attack: hash candidate requests and compare. Without the nonce no
    unsalted candidate matches what was recorded (and would be published)."""
    svc = ReceiptService(receipts_path=tmp_path / "r.jsonl")
    url = "https://api.example.com/v1/forecast?city=Paris"
    svc.log_exchange_digest(PAYMENT_HASH, "GET", url, None, b"{}")
    recorded = svc.lookup_payment(PAYMENT_HASH)["requestDigest"]
    for city in ("London", "Paris", "Rome"):
        guess = hashlib.sha256(
            f"GET https://api.example.com/v1/forecast?city={city}\n".encode()
        ).hexdigest()
        assert guess != recorded
    line = json.loads((tmp_path / "r.jsonl").read_text(encoding="utf-8").splitlines()[-1])
    nonce = bytes.fromhex(line["digestNonce"])
    assert len(nonce) == 32
    # Disclosing the nonce proves the digest.
    assert request_digest(nonce, "GET", url, None) == recorded


def test_each_exchange_gets_a_fresh_nonce(tmp_path):
    svc = ReceiptService(receipts_path=tmp_path / "r.jsonl")
    svc.log_exchange_digest("11" * 32, "GET", "https://x/", None, b"same")
    svc.log_exchange_digest("22" * 32, "GET", "https://x/", None, b"same")
    lines = [json.loads(x) for x in (tmp_path / "r.jsonl").read_text().splitlines()]
    assert lines[0]["digestNonce"] != lines[1]["digestNonce"]
    assert lines[0]["requestDigest"] != lines[1]["requestDigest"]
    assert all(x["digestScheme"] == DIGEST_SCHEME for x in lines)


def test_digest_is_folded_into_its_receipt_without_the_nonce(tmp_path):
    svc = ReceiptService(wallet_label="NWC", receipts_path=tmp_path / "receipts.jsonl")
    svc.log_payment(kind="l402", amount_sats=10, status="settled", payment_hash=PAYMENT_HASH)
    svc.log_payment(kind="invoice", amount_sats=5, status="settled", payment_hash="cd" * 32)
    assert svc.log_exchange_digest(PAYMENT_HASH, "GET", "https://x/", None, b"{}", nonce=NONCE)

    rows = svc.read_recent(10)
    assert [r["type"] for r in rows] == ["payment_receipt", "payment_receipt"]
    assert rows[0]["requestDigest"] == request_digest(NONCE, "GET", "https://x/", None)
    assert rows[0]["responseDigest"] == response_digest(NONCE, b"{}")
    assert "digestNonce" not in json.dumps(rows)
    assert "requestDigest" not in rows[1]
    raw = (tmp_path / "receipts.jsonl").read_text(encoding="utf-8").splitlines()
    assert json.loads(raw[-1])["type"] == EXCHANGE_DIGEST_TYPE


def test_read_recent_limit_still_counts_receipts(tmp_path):
    svc = ReceiptService(receipts_path=tmp_path / "r.jsonl")
    for i in range(5):
        h = f"{i:02x}" * 32
        svc.log_payment(kind="l402", amount_sats=i + 1, payment_hash=h)
        svc.log_exchange_digest(h, "GET", "https://x/", None, b"")
    rows = svc.read_recent(3)
    assert [r["amountSats"] for r in rows] == [3, 4, 5]
    assert all(r.get("requestDigest") for r in rows)


def test_lookup_payment(tmp_path):
    svc = ReceiptService(receipts_path=tmp_path / "r.jsonl")
    svc.log_payment(kind="l402", amount_sats=10, payment_hash=PAYMENT_HASH)
    svc.log_payment(kind="invoice", amount_sats=5, payment_hash="cd" * 32)
    svc.log_exchange_digest(PAYMENT_HASH, "GET", "https://x/", None, b"{}", nonce=NONCE)

    assert svc.lookup_payment(PAYMENT_HASH.upper()) == {
        "paymentHash": PAYMENT_HASH,
        "requestDigest": request_digest(NONCE, "GET", "https://x/", None),
        "responseDigest": response_digest(NONCE, b"{}"),
    }
    assert svc.lookup_payment("cd" * 32) == {"paymentHash": "cd" * 32}
    assert svc.lookup_payment("ef" * 32) is None


def test_unsalted_legacy_digest_line_is_never_used(tmp_path):
    """A digest line written before salting (no nonce) yields the payment hash only."""
    path = tmp_path / "r.jsonl"
    svc = ReceiptService(receipts_path=path)
    svc.log_payment(kind="l402", amount_sats=10, payment_hash=PAYMENT_HASH)
    with open(path, "a", encoding="utf-8") as f:
        legacy = {
            "type": EXCHANGE_DIGEST_TYPE,
            "paymentHash": PAYMENT_HASH,
            "requestSha256": "11" * 32,
            "responseSha256": "22" * 32,
        }
        f.write(json.dumps(legacy) + "\n")
    assert svc.lookup_payment(PAYMENT_HASH) == {"paymentHash": PAYMENT_HASH}
    row = svc.read_recent(5)[0]
    assert "requestSha256" not in row and "requestDigest" not in row


def test_bodies_are_never_written(tmp_path):
    svc = ReceiptService(receipts_path=tmp_path / "r.jsonl")
    svc.log_exchange_digest(PAYMENT_HASH, "POST", "https://x/", b"REQUEST-BODY", b"SECRET-BODY")
    text = (tmp_path / "r.jsonl").read_text(encoding="utf-8")
    assert "SECRET-BODY" not in text and "REQUEST-BODY" not in text


@pytest.mark.asyncio
async def test_paid_l402_fetch_records_the_salted_digest(tmp_path):
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
    line = json.loads((tmp_path / "r.jsonl").read_text(encoding="utf-8").splitlines()[-1])
    nonce = bytes.fromhex(line["digestNonce"])
    assert svc.lookup_payment(PAYMENT_HASH) == {
        "paymentHash": PAYMENT_HASH,
        "requestDigest": request_digest(nonce, "GET", url, None),
        "responseDigest": response_digest(nonce, b'{"forecast":"sun"}'),
    }
    assert "sun" not in (tmp_path / "r.jsonl").read_text(encoding="utf-8")
    await client.close()


@pytest.mark.asyncio
async def test_compressed_response_is_digested_after_content_decoding(tmp_path):
    """Parity note for the .NET port: the response digest covers the body after
    content-encoding is removed, even when a server compresses despite identity."""
    import gzip

    url = "https://api.provider.com/premium"
    req = httpx.Request("GET", url)
    challenge = httpx.Response(
        402,
        headers=[("WWW-Authenticate", 'L402 macaroon="YWJjZGVm", invoice="lnbc100n1pjtest"')],
        request=req,
    )
    ok = httpx.Response(
        200, request=req, content=gzip.compress(b"plain body"),
        headers={"Content-Encoding": "gzip"},
    )
    wallet = AsyncMock()
    wallet.pay_invoice = AsyncMock(return_value=PREIMAGE)
    svc = ReceiptService(receipts_path=tmp_path / "r.jsonl")
    client = L402Client(wallet=wallet, budget_service=None, receipt_service=svc)
    client._http_client.request = AsyncMock(side_effect=[challenge, ok])
    with patch.object(client, "_get_invoice_amount_msat", return_value=10_000):
        await client.fetch(url, max_sats=1000)
    line = json.loads((tmp_path / "r.jsonl").read_text(encoding="utf-8").splitlines()[-1])
    nonce = bytes.fromhex(line["digestNonce"])
    assert line["responseDigest"] == response_digest(nonce, b"plain body")
    await client.close()
