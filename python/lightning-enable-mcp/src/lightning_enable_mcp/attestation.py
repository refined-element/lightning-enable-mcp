"""
Client-signed provider attestations (kind 38403, shape version 2).

An agent that paid for an L402 call signs, with its OWN reviewer key, a small Nostr
event saying how the provider performed. Mirrors the .NET ``Attestations`` class;
``shared/attestation-golden-vector.json`` pins both ports to the same event bytes.

Event shape (tags in this order; optional ones omitted, never sent empty)::

    ["d", <dedup key>]                       addressable-event key (see below)
    ["v", "2"]                               shape version
    ["p", <subject pubkey>] | ["u", <normalized endpoint URL>]
    ["agreement", <event id>]                optional, agent-to-agent case
    ["receipt", <payment_hash>, <request_sha256>, <response_sha256>]   optional
    ["mech", "responded"|"on_time"|"well_formed"|"price_honored", "1"|"0"]  one per check
    ["judged", "quality", "1".."5"]          optional

Kind 38403 is in the addressable range, so a relay keeps one event per
(reviewer, kind, d). ``d`` is ``receipt:<payment_hash>`` for a receipt-backed review
and ``p:<pubkey>`` / ``u:<url>`` otherwise — re-reviewing the same purchase (or, without
a receipt, the same subject) replaces the earlier review instead of piling up.

Two score families stay separate everywhere: mechanical checks (facts a client can
test) and judged quality (an opinion). Nothing here blends them into one number.

A payment hash in a receipt tag is a REFERENCE, not a verified payment: anyone can
write any hash. Proving it needs the invoice (or preimage) disclosed, which is out of
scope; reads say so.
"""

from __future__ import annotations

import re
import time
from dataclasses import dataclass, field
from typing import Any

from . import nostr_event

ATTESTATION_KIND = 38403
SHAPE_VERSION = "2"

MECH_CHECKS: tuple[str, ...] = ("responded", "on_time", "well_formed", "price_honored")

#: Upper bound on the optional free-text note. It is a short remark, not a transcript.
MAX_CONTENT_CHARS = 1000

PAYMENT_HASH_IS_A_REFERENCE = (
    "A payment hash in a receipt tag is a reference, not a verified payment: any "
    "reviewer can write any hash. Verifying it needs the invoice or preimage disclosed, "
    "which this tool does not do. 'proofBacked' means the review carries a well-formed "
    "receipt reference, nothing more."
)

NOT_A_TRUST_SCORE = (
    "Mechanical pass rates and the judged-quality mean are unweighted counts over the "
    "verified events below. They are not a trust score: reviewers are not weighted by "
    "identity, history or stake, and one operator can run many reviewer keys."
)


class AttestationError(ValueError):
    """Invalid attestation input. The message is safe to show to the caller."""


# ── endpoint URL normalization ───────────────────────────────────────────────

_URL_RE = re.compile(
    r"^(?P<scheme>[A-Za-z][A-Za-z0-9+.\-]*)://(?P<authority>[^/?#]*)"
    r"(?P<path>[^?#]*)(?:\?(?P<query>[^#]*))?(?:#.*)?$",
    re.DOTALL,
)
_PORT_RE = re.compile(r"^[0-9]{1,5}$")
_DEFAULT_PORTS = {"http": "80", "https": "443"}


def normalize_endpoint_url(url: str, include_query: bool = False) -> str:
    """The subject identity for a seller that has no Nostr pubkey.

    Rule (identical in both ports):

    1. The URL must be absolute ``http`` or ``https``.
    2. Scheme and host are lowercased; the host must be ASCII (send IDNs as punycode).
    3. A port equal to the scheme default (80 / 443) is dropped; any other is kept.
    4. User info (``user:pass@``) is rejected — it would publish a credential.
    5. The path is kept exactly as given (case and percent-encoding untouched);
       an empty path becomes ``/``.
    6. The fragment is always dropped.
    7. The query is dropped unless ``include_query`` is true — set it only when the
       query is part of the resource's identity (for example ``?model=x``), and never
       when it carries a credential. When kept, it is kept verbatim; an empty query is
       dropped.
    """
    text = (url or "").strip()
    m = _URL_RE.match(text)
    if not m:
        raise AttestationError("subject_url must be an absolute http(s) URL.")

    scheme = m.group("scheme").lower()
    if scheme not in _DEFAULT_PORTS:
        raise AttestationError("subject_url must use http or https.")

    authority = m.group("authority")
    if "@" in authority:
        raise AttestationError(
            "subject_url must not contain user info (user:pass@) - it would be published."
        )

    if authority.startswith("["):
        close = authority.find("]")
        if close < 0:
            raise AttestationError("subject_url has a malformed IPv6 host.")
        host, rest = authority[: close + 1], authority[close + 1 :]
    else:
        host, sep, port_part = authority.partition(":")
        rest = sep + port_part
    port = ""
    if rest:
        if not rest.startswith(":") or not _PORT_RE.match(rest[1:]):
            raise AttestationError("subject_url has an invalid port.")
        port = rest[1:]

    if not host or any(ord(c) >= 0x7F or ord(c) < 0x21 for c in host):
        raise AttestationError(
            "subject_url needs an ASCII host (send internationalized names as punycode)."
        )
    host = host.lower()

    path = m.group("path") or "/"
    if any(c.isspace() or ord(c) < 0x20 or ord(c) == 0x7F for c in path):
        raise AttestationError("subject_url path must not contain whitespace or control characters.")

    out = f"{scheme}://{host}"
    if port and port != _DEFAULT_PORTS[scheme]:
        out += f":{port}"
    out += path

    query = m.group("query")
    if include_query and query:
        if any(c.isspace() or ord(c) < 0x20 or ord(c) == 0x7F for c in query):
            raise AttestationError("subject_url query must not contain whitespace or control characters.")
        out += "?" + query
    return out


# ── building ─────────────────────────────────────────────────────────────────


@dataclass(frozen=True)
class Subject:
    """Who is reviewed: a Nostr pubkey (``p``) or a normalized endpoint URL (``u``)."""

    type: str  # "p" | "u"
    value: str

    def to_dict(self) -> dict[str, str]:
        return {"type": "pubkey" if self.type == "p" else "url", "value": self.value}


def resolve_subject(
    subject_pubkey: str | None, subject_url: str | None, include_query: bool = False
) -> Subject:
    """Exactly one of ``subject_pubkey`` (64 hex) or ``subject_url`` names the subject."""
    pk = (subject_pubkey or "").strip()
    url = (subject_url or "").strip()
    if pk and url:
        raise AttestationError("Pass subject_pubkey OR subject_url, not both.")
    if pk:
        if not nostr_event.is_hex(pk, 64):
            raise AttestationError("subject_pubkey must be a 64-character hex Nostr pubkey.")
        return Subject("p", pk.lower())
    if url:
        return Subject("u", normalize_endpoint_url(url, include_query))
    raise AttestationError(
        "A subject is required: subject_pubkey (the seller's Nostr pubkey) or, when the "
        "seller has none, subject_url (the L402 endpoint)."
    )


@dataclass(frozen=True)
class ReceiptRef:
    payment_hash: str
    request_sha256: str = ""
    response_sha256: str = ""


def _hash_or_empty(name: str, value: str | None) -> str:
    text = (value or "").strip().lower()
    if text and not nostr_event.is_hex(text, 64):
        raise AttestationError(f"{name} must be 64 hex characters (a SHA-256).")
    return text


def make_receipt(
    payment_hash: str | None, request_sha256: str | None = None, response_sha256: str | None = None
) -> ReceiptRef | None:
    """Validate a receipt reference. None when no payment hash is given."""
    ph = _hash_or_empty("payment_hash", payment_hash)
    req = _hash_or_empty("request_sha256", request_sha256)
    resp = _hash_or_empty("response_sha256", response_sha256)
    if not ph:
        if req or resp:
            raise AttestationError("request_sha256/response_sha256 need a payment_hash.")
        return None
    return ReceiptRef(ph, req, resp)


def build_tags(
    subject: Subject,
    *,
    receipt: ReceiptRef | None = None,
    mech: dict[str, bool] | None = None,
    quality: int | None = None,
    agreement_id: str | None = None,
) -> list[list[str]]:
    """The tag list for a version-2 attestation, in canonical order."""
    mech = {k: v for k, v in (mech or {}).items() if v is not None}
    unknown = sorted(set(mech) - set(MECH_CHECKS))
    if unknown:
        raise AttestationError(f"Unknown mechanical check(s): {', '.join(unknown)}.")
    if quality is not None and (isinstance(quality, bool) or not 1 <= int(quality) <= 5):
        raise AttestationError("quality must be an integer from 1 to 5.")
    if not mech and quality is None:
        raise AttestationError(
            "Nothing to attest: give at least one mechanical check (responded, on_time, "
            "well_formed, price_honored) or a judged quality from 1 to 5."
        )
    agreement = (agreement_id or "").strip().lower()
    if agreement and not nostr_event.is_hex(agreement, 64):
        raise AttestationError("agreement_id must be a 64-character hex event id.")

    d = f"receipt:{receipt.payment_hash}" if receipt else f"{subject.type}:{subject.value}"
    tags: list[list[str]] = [["d", d], ["v", SHAPE_VERSION], [subject.type, subject.value]]
    if agreement:
        tags.append(["agreement", agreement])
    if receipt:
        tags.append(
            ["receipt", receipt.payment_hash, receipt.request_sha256, receipt.response_sha256]
        )
    for check in MECH_CHECKS:
        if check in mech:
            tags.append(["mech", check, "1" if mech[check] else "0"])
    if quality is not None:
        tags.append(["judged", "quality", str(int(quality))])
    return tags


def check_content(content: str | None) -> str:
    text = content or ""
    if len(text) > MAX_CONTENT_CHARS:
        raise AttestationError(
            f"content is limited to {MAX_CONTENT_CHARS} characters; it is a short note, "
            "never the prompt or the response."
        )
    return text


def sign_attestation(
    secret_key: bytes,
    tags: list[list[str]],
    content: str = "",
    created_at: int | None = None,
    aux_rand: bytes | None = b"",
) -> dict[str, Any]:
    """A complete signed kind-38403 event. ``aux_rand`` b"" draws fresh randomness."""
    event: dict[str, Any] = {
        "pubkey": nostr_event.xonly_pubkey_hex(secret_key),
        "created_at": int(time.time()) if created_at is None else int(created_at),
        "kind": ATTESTATION_KIND,
        "tags": tags,
        "content": content,
    }
    event["id"] = nostr_event.compute_event_id(event)
    event["sig"] = nostr_event.sign_event_id(event["id"], secret_key, aux_rand)
    return {k: event[k] for k in ("id", "pubkey", "created_at", "kind", "tags", "content", "sig")}


# ── reading ──────────────────────────────────────────────────────────────────


@dataclass
class ParsedAttestation:
    event_id: str
    reviewer: str
    created_at: int
    subject: Subject
    receipt: ReceiptRef | None
    mech: dict[str, bool]
    quality: int | None
    agreement_id: str | None
    content: str

    @property
    def proof_backed(self) -> bool:
        return self.receipt is not None

    def dedup_key(self) -> tuple[str, str]:
        ref = (
            f"receipt:{self.receipt.payment_hash}"
            if self.receipt
            else f"{self.subject.type}:{self.subject.value}"
        )
        return (self.reviewer, ref)

    def to_dict(self) -> dict[str, Any]:
        return {
            "eventId": self.event_id,
            "reviewerPubkey": self.reviewer,
            "createdAt": self.created_at,
            "proofBacked": self.proof_backed,
            "receipt": (
                {
                    "paymentHash": self.receipt.payment_hash,
                    "requestSha256": self.receipt.request_sha256 or None,
                    "responseSha256": self.receipt.response_sha256 or None,
                }
                if self.receipt
                else None
            ),
            "mechanical": {c: self.mech[c] for c in MECH_CHECKS if c in self.mech},
            "judgedQuality": self.quality,
            "agreementId": self.agreement_id,
            "content": self.content,
        }


def is_version2(event: dict[str, Any]) -> bool:
    tags = event.get("tags")
    return isinstance(tags, list) and any(
        isinstance(t, list) and len(t) >= 2 and t[0] == "v" and t[1] == SHAPE_VERSION
        for t in tags
    )


def parse_attestation(event: dict[str, Any]) -> ParsedAttestation | None:
    """Parse a version-2 attestation's tags. None when malformed. Does NOT verify the
    signature — call :func:`nostr_event.verify_event` first."""
    try:
        if event.get("kind") != ATTESTATION_KIND or not is_version2(event):
            return None
        tags = [t for t in event["tags"] if isinstance(t, list) and t and isinstance(t[0], str)]
        subject: Subject | None = None
        receipt: ReceiptRef | None = None
        mech: dict[str, bool] = {}
        quality: int | None = None
        agreement: str | None = None
        for tag in tags:
            name, vals = tag[0], tag[1:]
            if not all(isinstance(v, str) for v in vals):
                return None
            if name in ("p", "u") and subject is None and vals:
                if name == "p" and not nostr_event.is_hex(vals[0], 64):
                    return None
                subject = Subject(name, vals[0].lower() if name == "p" else vals[0])
            elif name == "receipt" and receipt is None:
                if len(vals) != 3 or not nostr_event.is_hex(vals[0], 64):
                    return None
                for h in vals[1:]:
                    if h and not nostr_event.is_hex(h, 64):
                        return None
                receipt = ReceiptRef(vals[0].lower(), vals[1].lower(), vals[2].lower())
            elif name == "mech" and len(vals) == 2 and vals[0] in MECH_CHECKS:
                if vals[1] not in ("0", "1"):
                    return None
                mech.setdefault(vals[0], vals[1] == "1")
            elif name == "judged" and len(vals) == 2 and vals[0] == "quality":
                if vals[1] not in ("1", "2", "3", "4", "5") or quality is not None:
                    return None
                quality = int(vals[1])
            elif name == "agreement" and vals and agreement is None:
                agreement = vals[0]
        if subject is None or (not mech and quality is None):
            return None
        return ParsedAttestation(
            event_id=event["id"],
            reviewer=event["pubkey"].lower(),
            created_at=int(event["created_at"]),
            subject=subject,
            receipt=receipt,
            mech=mech,
            quality=quality,
            agreement_id=agreement,
            content=event.get("content") or "",
        )
    except Exception:
        return None


@dataclass
class _Family:
    reviews: int = 0
    checks: dict[str, list[int]] = field(
        default_factory=lambda: {c: [0, 0] for c in MECH_CHECKS}
    )
    quality: list[int] = field(default_factory=list)

    def add(self, a: ParsedAttestation) -> None:
        self.reviews += 1
        for check, passed in a.mech.items():
            self.checks[check][1] += 1
            if passed:
                self.checks[check][0] += 1
        if a.quality is not None:
            self.quality.append(a.quality)

    def to_dict(self) -> dict[str, Any]:
        return {
            "reviews": self.reviews,
            "mechanical": {
                c: {
                    "passed": p,
                    "reported": n,
                    "passRate": round(p / n, 4) if n else None,
                }
                for c, (p, n) in self.checks.items()
            },
            "judgedQuality": {
                "ratings": len(self.quality),
                "mean": round(sum(self.quality) / len(self.quality), 2) if self.quality else None,
            },
        }


def aggregate(events: list[dict[str, Any]], subject: Subject) -> dict[str, Any]:
    """Verify, parse, deduplicate and summarize attestation events for ``subject``.

    * Every event's id and BIP-340 signature are checked; failures are counted and
      dropped (``rejected.invalidSignature``).
    * Events without the version-2 marker are counted as ``legacyIgnored``.
    * One reviewer's several attestations for the same receipt reference (or, without
      a receipt, the same subject) collapse to the latest (NIP-01 tie-break: lowest id).
    * Proof-backed and unbacked reviews are summarized separately, and within each the
      mechanical checks and the judged quality are reported separately.
    """
    rejected = {"invalidSignature": 0, "malformed": 0, "wrongSubject": 0}
    legacy = 0
    seen_ids: set[str] = set()
    latest: dict[tuple[str, str], ParsedAttestation] = {}
    verified = 0
    accepted = 0
    for ev in events:
        if not isinstance(ev, dict):
            rejected["malformed"] += 1
            continue
        ev_id = ev.get("id")
        if isinstance(ev_id, str) and ev_id in seen_ids:
            continue  # the same event delivered twice
        if not nostr_event.verify_event(ev):
            rejected["invalidSignature"] += 1
            continue
        seen_ids.add(ev_id)  # type: ignore[arg-type]
        verified += 1
        if ev.get("kind") != ATTESTATION_KIND or not is_version2(ev):
            legacy += 1
            continue
        parsed = parse_attestation(ev)
        if parsed is None:
            rejected["malformed"] += 1
            continue
        if parsed.subject != subject:
            rejected["wrongSubject"] += 1
            continue
        accepted += 1
        key = parsed.dedup_key()
        cur = latest.get(key)
        if cur is None or (parsed.created_at, _neg_id(parsed.event_id)) > (
            cur.created_at,
            _neg_id(cur.event_id),
        ):
            latest[key] = parsed

    kept = sorted(latest.values(), key=lambda a: (-a.created_at, a.event_id))
    backed, unbacked = _Family(), _Family()
    for a in kept:
        (backed if a.proof_backed else unbacked).add(a)

    return {
        "subject": subject.to_dict(),
        "eventsReceived": len(events),
        "verifiedEvents": verified,
        "rejected": rejected,
        "legacyIgnored": legacy,
        "duplicatesCollapsed": accepted - len(kept),
        "distinctReviewers": len({a.reviewer for a in kept}),
        "proofBacked": backed.to_dict(),
        "unbacked": unbacked.to_dict(),
        "reviews": [a.to_dict() for a in kept],
        "notes": [PAYMENT_HASH_IS_A_REFERENCE, NOT_A_TRUST_SCORE],
    }


def _neg_id(event_id: str) -> tuple[int, ...]:
    # NIP-01: on equal created_at the LOWEST id wins, so compare on the negated id.
    return tuple(-ord(c) for c in event_id)
