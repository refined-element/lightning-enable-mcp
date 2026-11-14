"""Client-signed provider attestations: event bytes, URL rule, verification, aggregation.

The golden vector in ``shared/attestation-golden-vector.json`` is checked by BOTH ports
(.NET: ``AttestationGoldenVectorTests``) — same inputs must give the same event bytes,
the same id and, with the fixed auxiliary randomness, the same BIP-340 signature.
"""

import copy
import hashlib
import json
from pathlib import Path

import pytest

from lightning_enable_mcp import attestation as att
from lightning_enable_mcp import nostr_event


def _find_shared(name: str) -> Path:
    for parent in Path(__file__).resolve().parents:
        candidate = parent / "shared" / name
        if candidate.exists():
            return candidate
    raise FileNotFoundError(f"shared/{name} not found above {__file__}")


VECTOR = json.loads(_find_shared("attestation-golden-vector.json").read_text(encoding="utf-8"))
SK = bytes.fromhex(VECTOR["secretKeyHex"])  # BIP-340 test scalar 3 — test-only
AUX = bytes.fromhex(VECTOR["auxRandHex"])

# Second test-only reviewer: BIP-340 scalar 1 (the generator point). Never a real key.
SK_OTHER = (1).to_bytes(32, "big")
SK_THIRD = (2).to_bytes(32, "big")


def _build_from_input(inp: dict, created_at: int) -> dict:
    subject = att.resolve_subject(
        inp.get("subjectPubkey"), inp.get("subjectUrl"), inp.get("includeQuery", False)
    )
    receipt = att.make_receipt(
        inp.get("paymentHash"), inp.get("requestSha256"), inp.get("responseSha256")
    )
    tags = att.build_tags(
        subject,
        receipt=receipt,
        mech=inp.get("mechanical"),
        quality=inp.get("quality"),
        agreement_id=inp.get("agreementId"),
    )
    return att.sign_attestation(SK, tags, inp.get("content", ""), created_at, AUX)


# ── golden vector (parity with .NET) ─────────────────────────────────────────


def test_vector_key_derivation():
    assert nostr_event.xonly_pubkey_hex(SK) == VECTOR["pubkeyHex"]
    assert nostr_event.npub_encode(VECTOR["pubkeyHex"]) == VECTOR["npub"]


@pytest.mark.parametrize("case", VECTOR["events"], ids=lambda c: c["name"])
def test_golden_vector_event_bytes_id_and_signature(case):
    ev = _build_from_input(case["input"], case["createdAt"])
    exp = case["expected"]
    assert ev["tags"] == exp["tags"]
    serialized = nostr_event.serialize_event(
        ev["pubkey"], ev["created_at"], ev["kind"], ev["tags"], ev["content"]
    )
    assert serialized == exp["serialized"]
    assert hashlib.sha256(serialized.encode("utf-8")).hexdigest() == exp["serializedUtf8Sha256"]
    assert ev["id"] == exp["id"]
    # Deterministic with the fixed aux randomness: the .NET port must produce this too.
    assert ev["sig"] == exp["sig"]
    assert nostr_event.verify_event(ev)


@pytest.mark.parametrize("case", VECTOR["events"], ids=lambda c: c["name"])
def test_random_aux_signature_differs_but_verifies(case):
    tags = case["expected"]["tags"]
    ev = att.sign_attestation(SK, tags, case["input"].get("content", ""), case["createdAt"])
    assert ev["id"] == case["expected"]["id"]
    assert nostr_event.verify_event(ev)


@pytest.mark.parametrize(
    "case", VECTOR["urlNormalization"], ids=lambda c: f"{c['input']!r}-{c['includeQuery']}"
)
def test_url_normalization_rule(case):
    if case["expected"] is None:
        with pytest.raises(att.AttestationError):
            att.normalize_endpoint_url(case["input"], case["includeQuery"])
    else:
        assert att.normalize_endpoint_url(case["input"], case["includeQuery"]) == case["expected"]


# ── construction rules ───────────────────────────────────────────────────────


def test_receipt_tag_shape_and_never_a_preimage():
    subject = att.resolve_subject(None, "https://api.example.com/x")
    receipt = att.make_receipt("AB" * 32, None, "cd" * 32)
    tags = att.build_tags(subject, receipt=receipt, mech={"responded": True})
    assert ["receipt", "ab" * 32, "", "cd" * 32] in tags
    assert tags[0] == ["d", "receipt:" + "ab" * 32]
    assert not any(t[0] in ("preimage", "proof") for t in tags)


def test_request_hash_without_payment_hash_rejected():
    with pytest.raises(att.AttestationError):
        att.make_receipt(None, "22" * 32, None)


def test_bad_hashes_rejected():
    with pytest.raises(att.AttestationError):
        att.make_receipt("zz" * 32)
    with pytest.raises(att.AttestationError):
        att.make_receipt("11" * 31)


def test_mechanical_and_judged_are_separate_tags():
    subject = att.resolve_subject("aa" * 32, None)
    tags = att.build_tags(subject, mech={"on_time": False, "responded": True}, quality=2)
    assert tags == [
        ["d", "p:" + "aa" * 32],
        ["v", "2"],
        ["p", "aa" * 32],
        ["mech", "responded", "1"],
        ["mech", "on_time", "0"],
        ["judged", "quality", "2"],
    ]


def test_nothing_to_attest_rejected():
    subject = att.resolve_subject("aa" * 32, None)
    with pytest.raises(att.AttestationError):
        att.build_tags(subject)


@pytest.mark.parametrize("quality", [0, 6, True])
def test_quality_out_of_range_rejected(quality):
    subject = att.resolve_subject("aa" * 32, None)
    with pytest.raises(att.AttestationError):
        att.build_tags(subject, quality=quality)


def test_unknown_mech_check_rejected():
    subject = att.resolve_subject("aa" * 32, None)
    with pytest.raises(att.AttestationError):
        att.build_tags(subject, mech={"fast": True})


def test_subject_rules():
    with pytest.raises(att.AttestationError):
        att.resolve_subject(None, None)
    with pytest.raises(att.AttestationError):
        att.resolve_subject("aa" * 32, "https://x.example/")
    with pytest.raises(att.AttestationError):
        att.resolve_subject("npub1xyz", None)


def test_content_length_limit():
    with pytest.raises(att.AttestationError):
        att.check_content("x" * (att.MAX_CONTENT_CHARS + 1))


# ── reading / aggregation ────────────────────────────────────────────────────

URL = "https://api.example.com/v1/forecast"
SUBJECT = att.Subject("u", URL)


def _ev(sk, created_at, *, receipt=None, mech=None, quality=None, subject=SUBJECT, content=""):
    tags = att.build_tags(
        subject,
        receipt=att.make_receipt(*receipt) if receipt else None,
        mech=mech,
        quality=quality,
    )
    return att.sign_attestation(sk, tags, content, created_at)


def test_parse_round_trip():
    ev = _ev(SK, 100, receipt=("11" * 32, "22" * 32, "33" * 32), mech={"responded": True}, quality=3)
    parsed = att.parse_attestation(ev)
    assert parsed is not None
    assert parsed.proof_backed
    assert parsed.receipt == att.ReceiptRef("11" * 32, "22" * 32, "33" * 32)
    assert parsed.mech == {"responded": True}
    assert parsed.quality == 3


def test_aggregate_separates_backed_and_unbacked_and_families():
    events = [
        _ev(SK, 100, receipt=("11" * 32, "", ""), mech={"responded": True, "on_time": False}, quality=5),
        _ev(SK_OTHER, 101, receipt=("12" * 32, "", ""), mech={"responded": True, "on_time": True}),
        _ev(SK_THIRD, 102, mech={"responded": False}, quality=1),
    ]
    out = att.aggregate(events, SUBJECT)
    assert out["verifiedEvents"] == 3
    backed, unbacked = out["proofBacked"], out["unbacked"]
    assert backed["reviews"] == 2 and unbacked["reviews"] == 1
    assert backed["mechanical"]["responded"] == {"passed": 2, "reported": 2, "passRate": 1.0}
    assert backed["mechanical"]["on_time"] == {"passed": 1, "reported": 2, "passRate": 0.5}
    assert backed["mechanical"]["well_formed"] == {"passed": 0, "reported": 0, "passRate": None}
    assert backed["judgedQuality"] == {"ratings": 1, "mean": 5.0}
    assert unbacked["mechanical"]["responded"]["passRate"] == 0.0
    assert unbacked["judgedQuality"] == {"ratings": 1, "mean": 1.0}
    # No single blended number anywhere.
    for key in ("averageRating", "score", "rating", "trustScore"):
        assert key not in out
    assert att.PAYMENT_HASH_IS_A_REFERENCE in out["notes"]


def test_aggregate_dedups_same_reviewer_same_receipt_latest_wins():
    old = _ev(SK, 100, receipt=("11" * 32, "", ""), quality=1)
    new = _ev(SK, 200, receipt=("11" * 32, "", ""), quality=5)
    other_receipt = _ev(SK, 150, receipt=("12" * 32, "", ""), quality=3)
    out = att.aggregate([new, old, other_receipt, new], SUBJECT)
    assert out["duplicatesCollapsed"] == 1
    assert out["proofBacked"]["reviews"] == 2
    assert {r["judgedQuality"] for r in out["reviews"]} == {5, 3}
    assert out["distinctReviewers"] == 1


def test_aggregate_dedups_unbacked_by_reviewer_and_subject():
    out = att.aggregate([_ev(SK, 1, quality=2), _ev(SK, 2, quality=4)], SUBJECT)
    assert out["unbacked"]["reviews"] == 1
    assert out["reviews"][0]["judgedQuality"] == 4


def test_aggregate_rejects_tampered_event():
    good = _ev(SK, 100, mech={"responded": True})
    tampered = copy.deepcopy(good)
    tampered["tags"][-1] = ["mech", "responded", "0"]  # flip the verdict, keep id/sig
    forged_sig = copy.deepcopy(_ev(SK_OTHER, 100, mech={"responded": True}))
    forged_sig["sig"] = good["sig"]
    out = att.aggregate([tampered, forged_sig], SUBJECT)
    assert out["rejected"]["invalidSignature"] == 2
    assert out["reviews"] == []


def test_aggregate_counts_legacy_and_wrong_subject():
    legacy_tags = [["d", "att-1"], ["p", "aa" * 32, "", "subject"], ["rating", "5"]]
    legacy = att.sign_attestation(SK, legacy_tags, "great")
    elsewhere = _ev(SK, 5, quality=4, subject=att.Subject("u", "https://other.example/"))
    out = att.aggregate([legacy, elsewhere], SUBJECT)
    assert out["legacyIgnored"] == 1
    assert out["rejected"]["wrongSubject"] == 1
    assert out["reviews"] == []


def test_aggregate_rejects_malformed_v2():
    tags = [["d", "x"], ["v", "2"], ["u", URL], ["judged", "quality", "9"]]
    ev = att.sign_attestation(SK, tags, "")
    out = att.aggregate([ev, "not-an-event"], SUBJECT)
    assert out["rejected"]["malformed"] == 2
