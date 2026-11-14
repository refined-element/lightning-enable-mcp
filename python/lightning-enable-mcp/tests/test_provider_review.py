"""agent_services review / reviews / reviewer_key, end to end against a fake relay."""

import json

import pytest

from lightning_enable_mcp import attestation as att
from lightning_enable_mcp import nostr_event
from lightning_enable_mcp.attestation_key import AttestationKeyStore
from lightning_enable_mcp.receipt_service import ReceiptService
from lightning_enable_mcp.tools.provider_review import publish_review, read_reviews, reviewer_key

from .fake_nostr_relay import FakeRelay

URL = "https://API.Example.com/v1/forecast?city=Paris"
NORMALIZED = "https://api.example.com/v1/forecast"
PH = "ab" * 32


@pytest.fixture
def keys(tmp_path):
    return AttestationKeyStore(config_dir=tmp_path / "cfg", env={})


@pytest.fixture
def receipts(tmp_path):
    svc = ReceiptService(receipts_path=tmp_path / "receipts.jsonl")
    svc.log_payment(kind="l402", amount_sats=10, status="settled", payment_hash=PH)
    svc.log_exchange_digest(PH, "11" * 32, "22" * 32)
    return svc


@pytest.mark.asyncio
async def test_review_signs_with_reviewer_key_and_attaches_local_receipt_hashes(keys, receipts):
    async with FakeRelay("accept") as relay:
        out = json.loads(
            await publish_review(
                subject_url=URL,
                payment_hash=PH,
                checks={"responded": True, "on_time": False},
                quality=4,
                content="slow but right",
                key_store=keys,
                receipt_service=receipts,
                relay_url=relay.url,
            )
        )
        stored = relay.stored
    assert out["success"] is True, out
    ev = stored[0]
    assert nostr_event.verify_event(ev)
    assert ev["pubkey"] == keys.identity().pubkey_hex == out["reviewer"]["pubkey"]
    assert ["u", NORMALIZED] in ev["tags"]
    assert ["receipt", PH, "11" * 32, "22" * 32] in ev["tags"]
    assert ["mech", "responded", "1"] in ev["tags"]
    assert ["mech", "on_time", "0"] in ev["tags"]
    assert ["judged", "quality", "4"] in ev["tags"]
    assert out["receipt"]["source"] == "local_receipt"
    assert out["receipt"]["knownToLocalReceiptLog"] is True
    # The secret never appears in the result.
    assert keys.secret_key().hex() not in json.dumps(out)


@pytest.mark.asyncio
async def test_review_with_unknown_payment_hash_uses_caller_values(keys, receipts):
    async with FakeRelay("accept") as relay:
        out = json.loads(
            await publish_review(
                subject_pubkey="cd" * 32,
                payment_hash="ef" * 32,
                response_sha256="33" * 32,
                quality=5,
                key_store=keys,
                receipt_service=receipts,
                relay_url=relay.url,
            )
        )
    assert out["success"] is True
    assert out["receipt"] == {
        "paymentHash": "ef" * 32,
        "requestSha256": None,
        "responseSha256": "33" * 32,
        "source": "caller",
        "knownToLocalReceiptLog": False,
    }


@pytest.mark.asyncio
async def test_review_conflicting_hash_with_local_receipt_is_refused(keys, receipts):
    out = json.loads(
        await publish_review(
            subject_url=URL, payment_hash=PH, request_sha256="99" * 32, checks={"responded": True},
            key_store=keys, receipt_service=receipts, relay_url="ws://127.0.0.1:9",
        )
    )
    assert out["success"] is False and out["errorType"] == "invalid_input"
    assert "local receipt" in out["error"]


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "kwargs",
    [
        {},  # nothing to attest
        {"quality": 9},
        {"checks": {"responded": "yes"}},
        {"checks": {"fast": True}},
        {"checks": ["responded"]},
        {"quality": 3, "payment_hash": "zz"},
        {"quality": 3, "content": "x" * 1001},
    ],
)
async def test_review_input_errors(keys, kwargs):
    out = json.loads(await publish_review(subject_url=URL, key_store=keys, relay_url="ws://127.0.0.1:9", **kwargs))
    assert out == {**out, "success": False, "errorType": "invalid_input"}


@pytest.mark.asyncio
async def test_review_relay_rejection_and_auth_are_reported(keys):
    async with FakeRelay("reject") as relay:
        rejected = json.loads(
            await publish_review(subject_url=URL, quality=3, key_store=keys, relay_url=relay.url)
        )
    async with FakeRelay("auth") as relay:
        auth = json.loads(
            await publish_review(subject_url=URL, quality=3, key_store=keys, relay_url=relay.url)
        )
    assert rejected["success"] is False and rejected["errorType"] == "rejected"
    assert "blocked" in rejected["error"]
    assert auth["success"] is False and auth["errorType"] == "auth_required"
    assert "NIP-42" in auth["error"]


@pytest.mark.asyncio
async def test_reviews_round_trip_verifies_and_separates_families(keys, tmp_path):
    other = AttestationKeyStore(config_dir=tmp_path / "other", env={})
    async with FakeRelay("accept") as relay:
        await publish_review(subject_url=URL, payment_hash=PH, checks={"responded": True}, quality=5,
                             key_store=keys, relay_url=relay.url)
        await publish_review(subject_url=URL, checks={"responded": False}, quality=1,
                             key_store=other, relay_url=relay.url)
        # A tampered copy of a real event and an event for another subject.
        forged = dict(relay.stored[0], content="edited")
        relay.extra_events.append(forged)
        out = json.loads(await read_reviews(subject_url=URL, relay_url=relay.url))

    assert out["success"] is True and out["complete"] is True
    assert out["subject"] == {"type": "url", "value": NORMALIZED}
    assert out["rejected"]["invalidSignature"] == 1
    assert out["proofBacked"]["reviews"] == 1 and out["unbacked"]["reviews"] == 1
    assert out["proofBacked"]["judgedQuality"]["mean"] == 5.0
    assert out["unbacked"]["mechanical"]["responded"]["passRate"] == 0.0
    assert att.PAYMENT_HASH_IS_A_REFERENCE in out["notes"]
    assert "averageRating" not in out


@pytest.mark.asyncio
async def test_reviews_auth_required_is_typed():
    async with FakeRelay("auth") as relay:
        out = json.loads(await read_reviews(subject_pubkey="cd" * 32, relay_url=relay.url))
    assert out["success"] is False and out["errorType"] == "auth_required"


@pytest.mark.asyncio
async def test_reviewer_key_returns_only_public_material(keys):
    out = json.loads(await reviewer_key(key_store=keys))
    assert out["success"] is True
    assert out["pubkey"] == keys.identity().pubkey_hex
    assert out["npub"].startswith("npub1")
    assert "new reviewer identity" in out["note"]
    assert keys.secret_key().hex() not in json.dumps(out)
