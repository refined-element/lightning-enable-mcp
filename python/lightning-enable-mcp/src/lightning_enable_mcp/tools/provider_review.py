"""
Client-signed provider reviews: ``agent_services`` actions ``review``, ``reviews`` and
``reviewer_key``.

* ``review`` signs a kind-38403 (shape v2) attestation with this installation's own
  reviewer key and publishes it straight to the relay. Needs no API key and no wallet.
* ``reviews`` reads a subject's attestations from the relay, verifies every signature,
  and reports proof-backed and unbacked reviews separately, mechanical checks and judged
  quality separately. No blended score.
* ``reviewer_key`` shows the reviewer public key (hex and npub). Never the secret.

The deprecated ``attest`` / ``reputation`` actions (and the ``publish_agent_attestation``
/ ``get_agent_reputation`` aliases) are unchanged.

Mirrors the .NET ``ProviderReviewTool``.
"""

from __future__ import annotations

import json
import logging
import time
from collections.abc import Mapping
from typing import TYPE_CHECKING, Any

from .. import attestation as att
from .. import nostr_relay
from ..attestation_key import ROTATION_NOTE, AttestationKeyError
from . import sanitize_error

if TYPE_CHECKING:
    from ..attestation_key import AttestationKeyStore
    from ..receipt_service import ReceiptService

logger = logging.getLogger("lightning-enable-mcp.tools.provider_review")

DEFAULT_REVIEWS_LIMIT = 20
MAX_REVIEWS_LIMIT = 500


def _error(message: str, error_type: str, **extra: Any) -> str:
    return json.dumps({"success": False, "error": message, "errorType": error_type, **extra}, indent=2)


def _mech(checks: Mapping[str, Any] | None) -> dict[str, bool]:
    """Validate the ``checks`` argument: known check names mapped to true/false."""
    if checks is None:
        return {}
    if not isinstance(checks, Mapping):
        raise att.AttestationError(
            "checks must be an object such as {\"responded\": true, \"on_time\": false}."
        )
    out: dict[str, bool] = {}
    for name, value in checks.items():
        if name not in att.MECH_CHECKS:
            raise att.AttestationError(
                f"Unknown check '{name}'. Use: {', '.join(att.MECH_CHECKS)}."
            )
        if not isinstance(value, bool):
            raise att.AttestationError(f"checks.{name} must be true or false.")
        out[name] = value
    return out


async def publish_review(
    *,
    subject_pubkey: str | None = None,
    subject_url: str | None = None,
    include_query: bool = False,
    payment_hash: str | None = None,
    allow_unknown_payment: bool = False,
    checks: Mapping[str, Any] | None = None,
    quality: int | None = None,
    agreement_id: str | None = None,
    content: str | None = None,
    key_store: AttestationKeyStore | None = None,
    receipt_service: ReceiptService | None = None,
    relay_url: str | None = None,
    timeout: float = nostr_relay.DEFAULT_TIMEOUT_SECONDS,
) -> str:
    """Sign and publish one provider review. Returns a JSON string.

    A payment hash the local receipt log knows becomes the NIP ``proof`` tag plus the
    ``receipt`` reference with the salted digests recorded for that call. A hash the log
    does not know is refused (``errorType: unknown_payment``) unless
    ``allow_unknown_payment`` is set; then only the ``receipt`` reference is published,
    WITHOUT ``proof``, and the review counts as unbacked.
    """
    try:
        subject = att.resolve_subject(subject_pubkey, subject_url, include_query)
        receipt = att.make_receipt(payment_hash)
        mech = _mech(checks)
        if quality is not None and (isinstance(quality, bool) or not isinstance(quality, int)):
            raise att.AttestationError("quality must be an integer from 1 to 5.")
        text = att.check_content(content)

        known_locally = False
        if receipt is not None:
            local = receipt_service.lookup_payment(receipt.payment_hash) if receipt_service else None
            if local is not None:
                known_locally = True
                receipt = att.ReceiptRef(
                    receipt.payment_hash,
                    (local.get("requestDigest") or "").lower(),
                    (local.get("responseDigest") or "").lower(),
                )
            elif not allow_unknown_payment:
                return _error(
                    "This payment hash is not in the local receipt log, so this server cannot "
                    "vouch that the payment happened. Readers that follow the NIP weight a "
                    "'proof' tag as a verified payment, so it is refused. Pass the hash of a "
                    "payment this server made, omit it, or set allow_unknown_payment=true to "
                    "publish it as an unbacked receipt reference without the proof tag.",
                    "unknown_payment",
                )

        tags = att.build_tags(
            subject,
            receipt=receipt,
            mech=mech,
            quality=quality,
            agreement_id=agreement_id,
            proof=known_locally,
        )
    except att.AttestationError as e:
        return _error(str(e), "invalid_input")

    if key_store is None:
        return _error("Reviewer key store not available.", "key_unavailable")
    try:
        secret = key_store.secret_key()
        identity = key_store.identity()
    except AttestationKeyError as e:
        return _error(str(e), "key_unavailable")

    event = att.sign_attestation(secret, tags, text, int(time.time()))
    relay = relay_url or nostr_relay.resolve_relay_url()
    try:
        outcome = await nostr_relay.publish_event(event, relay, timeout)
    except Exception as e:  # pragma: no cover - publish_event already maps errors
        logger.exception("Relay publish failed")
        return _error(f"Relay publish failed: {sanitize_error(str(e))}", nostr_relay.PROTOCOL_ERROR)

    summary = {
        "eventId": event["id"],
        "reviewer": {"pubkey": identity.pubkey_hex, "npub": identity.npub},
        "relay": relay,
        "subject": subject.to_dict(),
        "proofBacked": known_locally,
        "receipt": (
            {
                "paymentHash": receipt.payment_hash,
                "requestDigest": receipt.request_sha256 or None,
                "responseDigest": receipt.response_sha256 or None,
                "knownToLocalReceiptLog": known_locally,
            }
            if receipt
            else None
        ),
        "mechanical": mech,
        "judgedQuality": quality,
    }
    if not outcome.ok:
        return _error(
            f"The relay did not accept the review ({outcome.status}): {outcome.message}",
            outcome.status,
            event=event,
            **summary,
        )
    return json.dumps(
        {
            "success": True,
            **summary,
            "relayMessage": outcome.message or None,
            "event": event,
            "message": (
                "Review signed with this installation's reviewer key and accepted by the relay "
                f"as kind {att.ATTESTATION_KIND} (shape v{att.SHAPE_VERSION})."
            ),
            "note": att.PAYMENT_HASH_IS_A_REFERENCE,
        },
        indent=2,
        ensure_ascii=False,
    )


async def read_reviews(
    *,
    subject_pubkey: str | None = None,
    subject_url: str | None = None,
    include_query: bool = False,
    limit: int | None = None,
    relay_url: str | None = None,
    timeout: float = nostr_relay.DEFAULT_TIMEOUT_SECONDS,
) -> str:
    """Read, verify and summarize a subject's attestations. Returns a JSON string."""
    try:
        subject = att.resolve_subject(subject_pubkey, subject_url, include_query)
    except att.AttestationError as e:
        return _error(str(e), "invalid_input")

    n = DEFAULT_REVIEWS_LIMIT if limit is None else limit
    if isinstance(n, bool) or not isinstance(n, int):
        return _error("limit must be an integer.", "invalid_input")
    n = max(1, min(MAX_REVIEWS_LIMIT, n))

    relay = relay_url or nostr_relay.resolve_relay_url()
    # Fetch the full window (not just `n`) so one prolific key cannot fill it and hide
    # everyone else; `n` only bounds how many individual reviews are listed.
    fetch = nostr_relay.MAX_QUERY_EVENTS
    flt = {"kinds": [att.ATTESTATION_KIND], f"#{subject.type}": [subject.value], "limit": fetch}
    outcome = await nostr_relay.query_events(flt, relay, timeout, max_events=fetch)
    if not outcome.ok and outcome.status != nostr_relay.TIMEOUT:
        return _error(
            f"Could not read reviews from the relay ({outcome.status}): {outcome.message}",
            outcome.status,
            relay=relay,
        )

    summary = att.aggregate(outcome.events, subject, limit=n)
    return json.dumps(
        {
            "success": True,
            "relay": relay,
            "complete": outcome.ok,
            **({} if outcome.ok else {"warning": outcome.message + " Results are partial."}),
            **summary,
        },
        indent=2,
        ensure_ascii=False,
    )


async def reviewer_key(*, key_store: AttestationKeyStore | None = None) -> str:
    """This installation's reviewer public key. Creates the key on first use."""
    if key_store is None:
        return _error("Reviewer key store not available.", "key_unavailable")
    try:
        identity = key_store.identity()
    except AttestationKeyError as e:
        return _error(str(e), "key_unavailable")
    return json.dumps(
        {
            "success": True,
            **identity.to_dict(),
            "note": (
                "This key signs only reviews. It is not your wallet key and cannot move "
                "money. The secret never leaves this machine. " + ROTATION_NOTE
            ),
        },
        indent=2,
    )
