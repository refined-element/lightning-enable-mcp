"""``l402_producer(action="sell_url")``: a URL plus a Lightning Address becomes a paid endpoint.

One call against the producer claim endpoint (``GET``/``POST {api}/api/producer/claim``):

1. **Quote** (free). Without ``accept_terms`` equal to the quote's ``terms.version`` the tool
   returns the quote and stops; nothing is posted.
2. **Phase 1**: POST the body, get a 402 with an invoice and a macaroon.
3. **Pay** through ``pay_l402_challenge`` itself, so the budget check, the out-of-band
   confirmation (``BudgetService.request_confirmation``), the spend reservation, receipts and
   history are exactly what every other paying tool runs. The 402's ``priceSats`` is the
   ceiling.
4. **Phase 2**: POST the byte-identical body with ``Authorization: L402 <macaroon>:<preimage>``,
   retrying transient failures a bounded number of times.

State: one file per exact body, ``<config dir>/claim/sell-<sha256(body)[:16]>.json`` (0600),
whose phase only moves forward: ``challenge`` (saved before paying) -> ``paying`` (the wallet
may be asked) -> ``paid`` (preimage saved) -> ``redeemed`` (a secret-free marker). The only
step back is ``paying`` -> ``challenge``, and only when the pay path refused before the wallet
was asked and the operation ledger has no record of the invoice. Only a ``challenge`` file may
expire. Calls for the same body are serialized by a per-file lock, and every save re-reads the
file and refuses to move it backwards.

Money safety: "Nothing was charged" is said only for refusals known to happen before the
wallet. Any other failure after the wallet may have been asked is ``payment_state_unknown``
with ``paid: "unknown"``, and this tool never pays that claim again.

Secrets: the macaroon and preimage never enter a tool result; the API key enters exactly one
result per issue, after it has been stored.

Mirrors ``dotnet/src/LightningEnable.Mcp/Tools/ProducerSellUrl.cs``; both are pinned to
``shared/l402-producer-sell-url.json``, including one canonical body serialization.
"""

from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import os
import re
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import TYPE_CHECKING, Any, Callable, Optional
from urllib.parse import urlsplit

import httpx

from .._version import USER_AGENT_PYTHON
from ..idempotent_wallet import _derive_operation_id
from . import sanitize_error
from .create_account import _merge_api_key_into_config
from .pay_challenge import pay_l402_challenge

if TYPE_CHECKING:
    from ..budget_service import BudgetService
    from ..operation_ledger import OperationLedger
    from ..payment_history_service import PaymentHistoryService

logger = logging.getLogger("lightning-enable-mcp.tools.sell_url")

_DEFAULT_BASE_URL = "https://api.lightningenable.com"
_CLAIM_PATH = "/api/producer/claim"
_CONFIG_KEY = "lightningEnableApiKey"

#: Phase-2 attempts before giving up (the first try plus three retries).
MAX_REDEEM_ATTEMPTS = 4
_MAX_RETRY_DELAY_SECONDS = 10

#: Waits between phase-2 attempts. Module level so tests can replace it.
_sleep: Callable[[float], Any] = asyncio.sleep

FEATURE_NOT_ENABLED = "feature_not_enabled"
RATE_LIMITED = "rate_limited"
NETWORK_ERROR = "network_error"

#: code -> (operator-facing message, retryable). Pinned against the shared fixture.
ERROR_TABLE: dict[str, tuple[str, bool]] = {
    "invalid_target_url": (
        "The target URL must be https on a public hostname, at most 500 characters, with no IP "
        "address, credentials, or #fragment. A private or internal address is refused on purpose.",
        False,
    ),
    "invalid_price": (
        "The price per call is outside the allowed range: see minProxyPriceSats and maxProxyPriceSats.",
        False,
    ),
    "invalid_lightning_address": (
        "The Lightning Address did not resolve, or the price per call is outside what it accepts "
        "(minSendableSats to maxSendableSats). Check the spelling and that the wallet provider "
        "supports Lightning Addresses.",
        False,
    ),
    "invalid_recovery": (
        "Recovery must be an email or a 64-hex-character Nostr public key, not both. Disposable "
        "email, and a Nostr key already bound to another account, are refused.",
        False,
    ),
    "terms_required": (
        "acceptTerms must equal the quote's terms.version exactly. Show the operator the terms and "
        "ask again.",
        False,
    ),
    "invalid_name": ("The name is required, 1 to 100 characters.", False),
    "invalid_description": ("The description can be at most 500 characters.", False),
    "invalid_receive": ("Only a Lightning Address receive lane is accepted here.", False),
    "connect_not_available": (
        "The wallet-connect hand-off for Strike, OpenNode, and NWC is not available on this server. "
        "Use a Lightning Address, or l402_producer configure_receive on an existing account.",
        False,
    ),
    "claim_body_mismatch": (
        "The payment was made for a different claim body. This tool always resends the saved body, "
        "so contact support with the payment hash.",
        False,
    ),
    "invalid_l402_token": (
        "The payment token is invalid, expired, or not for a claim. A new claim is a new payment.",
        False,
    ),
    "invalid_authorization": ("The Authorization header was malformed.", False),
    "plan_limit_reached": (
        "That account's plan is at its proxy limit. Claim without an API key, or upgrade the plan.",
        False,
    ),
    "receive_lane_already_configured": (
        "That account already has a different receive wallet, and a claim never replaces it.",
        False,
    ),
    "merchant_exists": (
        "An account already uses that recovery email. Sign in to the dashboard, then claim with "
        "that account's API key, or use a different recovery.",
        False,
    ),
    "payment_provider_busy": (
        "The platform's payment provider did not return an invoice in time. Nothing was invoiced "
        "or charged.",
        True,
    ),
    "payment_provider_rejected": (
        "The platform's payment provider refused to create the invoice. Nothing was charged; a "
        "retry repeats the refusal until the provider side changes.",
        False,
    ),
    "upstream_unavailable": ("A transient upstream failure. Nothing was invoiced.", True),
    "challenge_persist_failed": ("The challenge could not be recorded. Nothing was invoiced.", True),
    "claim_contended": ("A unique proxy id could not be allocated. Nothing was created.", True),
    "claim_unavailable": (
        "Claims are not available on this deployment right now. Check the quote again later.",
        False,
    ),
    "claim_failed": ("The server hit an unexpected claim state.", True),
    FEATURE_NOT_ENABLED: ("Claims are not enabled on this Lightning Enable server.", False),
    RATE_LIMITED: ("Too many claim requests from this address. Wait, then try again.", True),
    NETWORK_ERROR: ("The Lightning Enable API could not be reached.", True),
}

#: ``merchant_exists`` after payment: the server checks the recovery email only in phase 2
#: (spec §17.3). Changing the arguments would be a new claim and a new payment.
MERCHANT_EXISTS_AFTER_PAYMENT = (
    "The claim payment is spent, and an account already uses that recovery email. Do not change "
    "the arguments or claim again: a different body is a new claim and a new payment. Contact "
    "support@lightningenable.com with the payment hash."
)

#: Permanent before payment, but can clear on a phase-2 retry: phase 2 re-runs the DNS check
#: and the Lightning Address lookup, so a briefly-down provider or resolver recovers.
RETRY_AFTER_PAYMENT_ALSO = frozenset({"invalid_lightning_address", "invalid_target_url"})

#: Used only when the server's quote carries no ``bodyRule`` of its own.
_FALLBACK_BODY_RULE = (
    "The claim body is written once and sent twice: the payment is bound to the target URL, the "
    "Lightning Address, and the recovery binding. Changing any of them after paying makes the "
    "payment unusable, so call sell_url again with exactly the same arguments."
)

RECOVERY_EMAIL_WARNING = (
    "An email that already belongs to a Lightning Enable account is refused only after payment "
    "(merchant_exists), and the paid claim cannot be reused for a different body. Use an email "
    "that is not registered yet, a Nostr public key, or no recovery."
)

REPLAY_ROTATED_NOTE = (
    "This claim was replayed, so the server rotated the account's API key: the key returned here "
    "is now the only valid one, and any earlier key for this account no longer works."
)

REPLAY_WITHOUT_KEY_NOTE = (
    "The server answered this replay without an API key. A replay normally rotates the key and "
    "returns the new one; if the account's key is lost, recover it with the recovery binding, or "
    "contact support with the payment hash."
)

_PROBLEM_CORE = frozenset(
    {
        "type", "title", "status", "detail", "error", "message", "instance", "traceId",
        # Never echo a challenge's secrets, should a 402 body ever reach this path.
        "macaroon", "invoice", "preimage", "apiKey",
    }
)


def describe(code: str, status: int) -> tuple[str, bool]:
    """Message and retry flag for a claim code (unknown codes: retryable when 5xx)."""
    if code in ERROR_TABLE:
        return ERROR_TABLE[code]
    return f"The claim endpoint answered HTTP {status}.", status >= 500


# ── HTTP ─────────────────────────────────────────────────────────────────────────────


@dataclass
class ClaimHttpResponse:
    status: int
    body: str
    retry_after_seconds: Optional[int] = None


def _api_base_url() -> str:
    base = (os.getenv("LIGHTNING_ENABLE_API_URL") or "").strip()
    if not base or base.startswith("${"):
        base = _DEFAULT_BASE_URL
    return base.rstrip("/")


class ProducerClaimClient:
    """The claim endpoint on the operator-configured Lightning Enable origin.

    Deliberately not ``L402Client`` (it pays a 402 on its own, and the claim needs the 402 in
    hand to save the body and macaroon before paying) and not ``LightningEnableApiClient``
    (it sends the configured merchant key, and an unauthenticated claim must create a NEW
    account). Redirects are not followed, so the paid Authorization header never leaves
    this origin.
    """

    def __init__(self) -> None:
        self.claim_url = _api_base_url() + _CLAIM_PATH
        self._client = httpx.AsyncClient(
            timeout=30.0,
            follow_redirects=False,
            headers={"Accept": "application/json", "User-Agent": USER_AGENT_PYTHON},
        )

    async def get_quote(self) -> ClaimHttpResponse:
        return self._wrap(await self._client.get(self.claim_url))

    async def post(self, body: str, authorization: Optional[str]) -> ClaimHttpResponse:
        headers = {"Content-Type": "application/json"}
        if authorization:
            headers["Authorization"] = authorization
        # Bytes, not a re-serialized object: the paid token is bound to this body.
        return self._wrap(
            await self._client.post(self.claim_url, content=body.encode("utf-8"), headers=headers)
        )

    @staticmethod
    def _wrap(response: httpx.Response) -> ClaimHttpResponse:
        retry_after = None
        raw = response.headers.get("Retry-After")
        if raw and raw.strip().isdigit():
            retry_after = int(raw.strip())
        return ClaimHttpResponse(response.status_code, response.text, retry_after)

    async def close(self) -> None:
        await self._client.aclose()


# ── Scratch state ────────────────────────────────────────────────────────────────────

CHALLENGE = "challenge"
PAYING = "paying"
PAID = "paid"
REDEEMED = "redeemed"
_RANK = {CHALLENGE: 0, PAYING: 1, PAID: 2, REDEEMED: 3}


class ClaimStateConflict(RuntimeError):
    """A save would move the state file backwards."""


def _restrict(path: Path) -> None:
    try:
        from ..config import _restrict_file_permissions

        _restrict_file_permissions(path)
    except Exception:  # noqa: BLE001 — best effort
        pass


def _write_restricted(path: Path, contents: str) -> None:
    """Create the file empty, restrict it to the owner, THEN write the secret."""
    path.write_text("", encoding="utf-8")
    _restrict(path)
    path.write_text(contents, encoding="utf-8")


def state_file_name(body: str) -> str:
    return f"sell-{hashlib.sha256(body.encode('utf-8')).hexdigest()[:16]}.json"


_STATE_FIELDS = {
    "claimUrl": "claim_url",
    "invoice": "invoice",
    "macaroon": "macaroon",
    "paymentHash": "payment_hash",
    "expiresAt": "expires_at",
    "preimage": "preimage",
    "proxyId": "proxy_id",
    "proxyUrl": "proxy_url",
    "manifestUrl": "manifest_url",
    "keyLocation": "key_location",
    "keyPath": "key_path",
    "redeemedAt": "redeemed_at",
}


@dataclass
class ClaimState:
    """The claim, one 0600 file per exact body. The phase only moves forward."""

    directory: Path
    body: str
    claim_url: Optional[str] = None
    invoice: Optional[str] = None
    macaroon: Optional[str] = None
    payment_hash: Optional[str] = None
    expires_at: Optional[str] = None
    price_sats: int = 0
    phase: str = CHALLENGE
    preimage: Optional[str] = None
    proxy_id: Optional[str] = None
    proxy_url: Optional[str] = None
    manifest_url: Optional[str] = None
    key_location: Optional[str] = None
    key_path: Optional[str] = None
    redeemed_at: Optional[str] = None
    _file: Path = field(init=False)

    def __post_init__(self) -> None:
        self._file = Path(self.directory) / state_file_name(self.body)

    @property
    def file_path(self) -> str:
        return str(self._file)

    def is_expired(self, now: datetime) -> bool:
        """Only a ``challenge`` the wallet never saw can expire."""
        if self.phase != CHALLENGE or not self.expires_at:
            return False
        try:
            expires = datetime.fromisoformat(self.expires_at.replace("Z", "+00:00"))
        except ValueError:
            # .NET round-trip timestamps carry 7 fractional digits; trim to microseconds.
            m = re.match(r"^(.*?\.\d{6})\d*(.*)$", self.expires_at)
            if not m:
                return False
            try:
                expires = datetime.fromisoformat((m.group(1) + m.group(2)).replace("Z", "+00:00"))
            except ValueError:
                return False
        if expires.tzinfo is None:
            expires = expires.replace(tzinfo=timezone.utc)
        return expires <= now

    @classmethod
    def load(cls, directory: Path, body: str) -> Optional["ClaimState"]:
        path = Path(directory) / state_file_name(body)
        if not path.exists():
            return None
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except Exception:  # noqa: BLE001
            return None
        # Same file name but a different body would be a hash collision; refuse it.
        if not isinstance(data, dict) or data.get("body") != body:
            return None
        state = cls(directory=Path(directory), body=body)
        for key, attr in _STATE_FIELDS.items():
            setattr(state, attr, data.get(key))
        state.price_sats = int(data.get("priceSats") or 0)
        state.phase = data.get("phase") or CHALLENGE
        return state

    def mark_redeemed(
        self,
        proxy_id: str,
        proxy_url: Optional[str],
        manifest_url: Optional[str],
        key_location: Optional[str],
        key_path: Optional[str],
        now: datetime,
    ) -> None:
        """Replace the token with a secret-free marker of the finished claim."""
        self.phase = REDEEMED
        self.invoice = self.macaroon = self.preimage = None
        self.proxy_id, self.proxy_url, self.manifest_url = proxy_id, proxy_url, manifest_url
        self.key_location, self.key_path = key_location, key_path
        self.redeemed_at = now.astimezone(timezone.utc).isoformat()
        self.save()

    def save(self, allow_downgrade_from_paying: bool = False) -> None:
        """Write the state, re-reading the file first: never move the phase backwards, except
        ``paying`` -> ``challenge`` when allowed AND the file still says ``paying``."""
        directory = self._file.parent
        if not directory.exists():
            directory.mkdir(parents=True, exist_ok=True)
            try:
                os.chmod(directory, 0o700)
            except OSError:
                pass

        on_disk = ClaimState.load(directory, self.body)
        if on_disk is not None and _RANK.get(on_disk.phase, 0) > _RANK.get(self.phase, 0):
            permitted = (
                allow_downgrade_from_paying and on_disk.phase == PAYING and self.phase == CHALLENGE
            )
            if not permitted:
                raise ClaimStateConflict(
                    f"The claim state moved to '{on_disk.phase}' elsewhere; not overwriting it "
                    f"with '{self.phase}'."
                )

        data: dict[str, Any] = {
            "version": 1,
            "claimUrl": self.claim_url,
            "body": self.body,
            "paymentHash": self.payment_hash,
            "expiresAt": self.expires_at,
            "priceSats": self.price_sats,
            "phase": self.phase,
        }
        for key, attr in _STATE_FIELDS.items():
            value = getattr(self, attr)
            if key not in data and value is not None:
                data[key] = value
        # Write-then-rename; the temp file is restricted before the secrets go in.
        tmp = self._file.with_name(self._file.name + ".tmp")
        _write_restricted(tmp, json.dumps(data, indent=2))
        os.replace(tmp, self._file)

    def delete(self) -> None:
        try:
            self._file.unlink()
        except OSError:
            pass


# ── Body: one canonical serialization, shared with the .NET port ─────────────────────


def build_body(
    target_url: str,
    name: str,
    description: Optional[str],
    default_price_sats: Optional[int],
    lightning_address: str,
    email: Optional[str],
    pubkey: Optional[str],
    list_publicly: bool,
    accept_terms: str,
) -> str:
    """The claim body, serialized ONCE, canonically (``shared/l402-producer-sell-url.json``
    ``canonicalBody``): fixed key order, compact separators, UTF-8, no HTML escaping, only the
    quote, backslash and control characters escaped. Both ports produce identical bytes, so the
    body sent and the state file name agree across ports."""
    body: dict[str, Any] = {"targetUrl": target_url, "name": name}
    if description and description.strip():
        body["description"] = description.strip()
    if default_price_sats and default_price_sats > 0:
        body["defaultPriceSats"] = int(default_price_sats)
    body["receive"] = {"lightningAddress": lightning_address}
    if email:
        body["recovery"] = {"email": email}
    elif pubkey:
        body["recovery"] = {"nostrPubkey": pubkey}
    body["listPublicly"] = bool(list_publicly)
    body["acceptTerms"] = accept_terms
    return json.dumps(body, separators=(",", ":"), ensure_ascii=False)


def _parse_object(text: Optional[str]) -> Optional[dict]:
    if not text or not text.strip():
        return None
    try:
        value = json.loads(text)
    except ValueError:
        return None
    return value if isinstance(value, dict) else None


def _as_int(value: Any) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return 0


def _dumps(obj: dict) -> str:
    return json.dumps(obj, indent=2)


# ── Results ──────────────────────────────────────────────────────────────────────────


def _local_refusal(code: str, message: str) -> str:
    return _dumps(
        {
            "success": False,
            "action": "sell_url",
            "stage": "input",
            "error": code,
            "message": message + " Nothing was sent or charged.",
            "retryable": False,
            "paid": False,
        }
    )


def _failure(code: str, message: str, stage: str, **extra: Any) -> str:
    result = {
        "success": False,
        "action": "sell_url",
        "stage": stage,
        "error": code,
        "message": message,
        "retryable": False,
        "paid": False,
    }
    result.update(extra)
    return _dumps(result)


def _no_wallet(stage: str) -> str:
    return _failure(
        "no_wallet",
        "No spending wallet is configured, so the claim cannot be paid. Configure LND, NWC, or "
        "Strike (setup_wallet), then call sell_url again with the same arguments. Nothing was "
        "minted or charged.",
        stage=stage,
    )


def _recovery_block(state: ClaimState) -> dict:
    if state.preimage:
        instructions = (
            "The claim payment is spent; do not pay again. The saved file holds the exact claim "
            "body and the paid token (macaroon and preimage). Do not edit it and never paste its "
            "contents into chat. To retry, call l402_producer(action=\"sell_url\") again with "
            "exactly the same arguments before the token expires; the tool skips the payment and "
            "redeems the saved token. By hand: POST the file's \"body\" byte for byte to "
            f"{state.claim_url} with Content-Type: application/json and Authorization: L402 "
            "<macaroon>:<preimage> taken from the file. If it keeps failing, contact "
            "support@lightningenable.com with the payment hash only."
        )
    else:
        instructions = (
            "No proof of payment was saved, and the wallet may have paid. Do not pay again and do "
            "not change the arguments; calling sell_url again with the same arguments answers "
            "from the saved file and never pays this claim again. Check the wallet's payment "
            "history for the payment hash, then contact support@lightningenable.com with the "
            "payment hash only. Keep the saved file and never paste its contents into chat."
        )
    return {
        "paymentSpent": True if state.preimage else "unknown",
        "paymentHash": state.payment_hash,
        "stateFile": state.file_path,
        "tokenExpiresAt": state.expires_at,
        "instructions": instructions,
    }


def _unknown(state: ClaimState, detail: Optional[str]) -> str:
    result: dict[str, Any] = {
        "success": False,
        "action": "sell_url",
        "stage": "payment",
        "error": "payment_state_unknown",
        "message": (
            "The claim payment may have been made: the attempt did not finish cleanly after the "
            "wallet may have been asked. Do NOT pay again and do not change the arguments. Check "
            "the wallet's payment history for the payment hash, then contact "
            "support@lightningenable.com with it."
        ),
        "retryable": False,
        "paid": "unknown",
        "recovery": _recovery_block(state),
    }
    if detail:
        result["detail"] = detail
    return _dumps(result)


def _paid(state: ClaimState, code: str, message: str, detail: Optional[str] = None) -> str:
    result: dict[str, Any] = {
        "success": False,
        "action": "sell_url",
        "stage": "phase2" if state.phase == PAID else "payment",
        "error": code,
        "message": message,
        "retryable": False,
        "paid": True,
        "recovery": _recovery_block(state),
    }
    if detail:
        result["detail"] = detail
    return _dumps(result)


def _already_claimed(s: ClaimState) -> str:
    key_sentence = (
        f"The API key was stored in {s.key_path} and is not shown again."
        if s.key_path
        else "The API key was shown only in the first result."
    )
    return _dumps(
        {
            "success": True,
            "action": "sell_url",
            "stage": "already_claimed",
            "alreadyClaimed": True,
            "paidThisCall": False,
            "proxyId": s.proxy_id,
            "proxyUrl": s.proxy_url,
            "manifestUrl": s.manifest_url,
            "paymentHash": s.payment_hash,
            "apiKeyStorage": {"location": s.key_location, "path": s.key_path},
            "redeemedAt": s.redeemed_at,
            "message": (
                f"This exact claim was already completed at {s.redeemed_at}. Nothing was paid "
                f"again. {key_sentence} Selling a different URL or changing the details is a new "
                "claim and a new payment."
            ),
        }
    )


def _code_for(status: int, problem: Optional[dict]) -> str:
    code = problem.get("error") if problem else None
    if isinstance(code, str) and code:
        return code
    if status == 404:
        return FEATURE_NOT_ENABLED
    if status == 429:
        return RATE_LIMITED
    if status == 0:
        return NETWORK_ERROR
    return f"http_{status}"


def _problem_result(
    code: str,
    status: int,
    problem: Optional[dict],
    retry_after: Optional[int],
    state: Optional[ClaimState],
    stage: str,
    paid: bool,
    attempts: Optional[int] = None,
) -> str:
    message, retryable = describe(code, status)
    if paid and code == "merchant_exists":
        message = MERCHANT_EXISTS_AFTER_PAYMENT
    result: dict[str, Any] = {
        "success": False,
        "action": "sell_url",
        "stage": stage,
        "error": code,
        "message": message,
        "retryable": retryable,
        "paid": paid,
    }
    if status > 0:
        result["httpStatus"] = status
    detail = None
    if problem:
        detail = problem.get("detail") or problem.get("message")
    if isinstance(detail, str) and detail:
        result["detail"] = sanitize_error(detail, 300)
    if retry_after is not None:
        result["retryAfterSeconds"] = retry_after
    if attempts is not None:
        result["redeemAttempts"] = attempts
    if problem:
        extras = {k: v for k, v in problem.items() if k not in _PROBLEM_CORE and v is not None}
        if extras:
            result["problem"] = extras
    if paid and state is not None:
        result["recovery"] = _recovery_block(state)
    elif not paid:
        result["note"] = (
            "A refusal before payment costs nothing. Fix the named field and call sell_url again."
        )
    return _dumps(result)


def _http_problem(response: ClaimHttpResponse, stage: str) -> str:
    problem = _parse_object(response.body)
    return _problem_result(
        _code_for(response.status, problem),
        response.status,
        problem,
        response.retry_after_seconds,
        None,
        stage,
        paid=False,
    )


def _quote_result(
    quote: dict, terms_version: str, accept_terms: Optional[str], name: str, has_recovery_email: bool
) -> str:
    mismatch = bool(accept_terms and accept_terms.strip())
    prefix = (
        f'acceptTerms "{accept_terms.strip()}" is not the current terms version. ' if mismatch else ""
    )
    result: dict[str, Any] = {
        "success": True,
        "action": "sell_url",
        "stage": "quote",
        "requiresTermsAcceptance": True,
        "paid": False,
        "price": quote.get("price"),
        "covers": quote.get("covers"),
        "renewal": quote.get("renewal"),
        "limits": quote.get("limits"),
        "terms": quote.get("terms"),
        "listing": quote.get("listing"),
        # The server's own wording, verbatim, so the agent can show it without another call.
        "pricingNote": quote.get("pricingNote"),
        "bodyRule": quote.get("bodyRule") or _FALLBACK_BODY_RULE,
        "name": name,
        "message": prefix
        + "Nothing was sent or charged. Show the operator the claim price, what it covers, the "
        "renewal, and the terms link, and get an explicit yes before paying.",
        "nextStep": (
            'Call l402_producer(action="sell_url") again with the same arguments plus '
            f'accept_terms="{terms_version}".'
        ),
    }
    if has_recovery_email:
        result["recoveryEmailWarning"] = RECOVERY_EMAIL_WARNING
    return _dumps(result)


# ── API key storage ──────────────────────────────────────────────────────────────────


def _read_config_key(config_path: Path) -> Optional[str]:
    try:
        if not config_path.exists():
            return None
        data = json.loads(config_path.read_text(encoding="utf-8"))
        value = data.get(_CONFIG_KEY) if isinstance(data, dict) else None
        return value if isinstance(value, str) else None
    except Exception:  # noqa: BLE001
        return None


def _key_file_path(config_path: str, proxy_id: str) -> Path:
    safe_id = re.sub(r"[^a-z0-9-]", "-", proxy_id.lower())
    return Path(config_path).parent / f"claim-{safe_id}.key"


def plan_key_target(
    config_path: str, proxy_id: str, api_key: str, replace_config: bool
) -> tuple[str, str]:
    """Where the key goes: the config slot when it is empty (and no key is in the environment),
    already holds this key, or holds this claim's own key that a replay just rotated.
    Otherwise another account owns the slot, so ``claim-<proxyId>.key`` beside it."""
    if replace_config:
        return "config", config_path
    existing = os.getenv("LIGHTNING_ENABLE_API_KEY")
    if not existing or existing.startswith("${"):
        existing = _read_config_key(Path(config_path))
    if not existing or existing == api_key:
        return "config", config_path
    return "key_file", str(_key_file_path(config_path, proxy_id))


def write_key(location: str, config_path: str, proxy_id: str, api_key: str) -> dict:
    if location == "config":
        ok, written_path, _error = _merge_api_key_into_config(api_key, config_path)
        if ok:
            return {"written": True, "location": "config", "path": written_path, "error": None}
        # An unparseable config is never overwritten; fall back to the key file.
    key_path = _key_file_path(config_path, proxy_id)
    try:
        key_path.parent.mkdir(parents=True, exist_ok=True)
        _write_restricted(key_path, api_key + "\n")
        return {"written": True, "location": "key_file", "path": str(key_path), "error": None}
    except Exception as e:  # noqa: BLE001
        return {
            "written": False,
            "location": "none",
            "path": str(key_path),
            "error": sanitize_error(str(e)),
        }


def store_api_key(config_path: str, proxy_id: str, api_key: str) -> dict:
    """Plan, then write (kept for callers that do not track a claim state)."""
    location, _ = plan_key_target(config_path, proxy_id, api_key, replace_config=False)
    return write_key(location, config_path, proxy_id, api_key)


def _success_result(claimed: dict, state: ClaimState, config_path: str, now: datetime) -> str:
    proxy = claimed.get("proxy") if isinstance(claimed.get("proxy"), dict) else {}
    proxy_id = proxy.get("proxyId") or "claim"
    replayed = claimed.get("replayed") is True
    key_rotated = claimed.get("keyRotated") is True
    api_key = claimed.get("apiKey")
    recovery = claimed.get("recovery")

    result: dict[str, Any] = {
        "success": True,
        "action": "sell_url",
        "replayed": replayed,
        "keyRotated": key_rotated,
        "proxyUrl": proxy.get("url"),
        "manifestUrl": proxy.get("manifest"),
        "openapiUrl": proxy.get("openapi"),
        "proxyId": proxy_id,
        "proxyActive": proxy.get("active"),
        "receive": claimed.get("receive"),
        "paidThrough": claimed.get("paidThrough"),
        "renew": claimed.get("renew"),
        "recovery": recovery,
        "listing": claimed.get("listing"),
        "warnings": claimed.get("warnings") or [],
        "claimPaidSats": state.price_sats,
    }

    stored: Optional[dict] = None
    if isinstance(api_key, str) and api_key:
        # A rotated key replaces this claim's earlier key where that one was stored: the old key
        # is dead. The target is recorded in the state file BEFORE the key is written.
        replace_config = key_rotated and state.key_location == "config"
        location, path = plan_key_target(config_path, proxy_id, api_key, replace_config)
        state.key_location, state.key_path = location, path
        state.save()
        stored = write_key(location, config_path, proxy_id, api_key)
        if stored["location"] != location:
            state.key_location, state.key_path = stored["location"], stored["path"]
            state.save()

        result["apiKey"] = api_key
        result["apiKeyStorage"] = dict(
            stored, replacedRotatedKey=bool(replace_config and stored["location"] == "config")
        )
        only = (
            " With no recovery binding it is the account's only credential."
            if recovery == "none"
            else ""
        )
        lead = REPLAY_ROTATED_NOTE if (replayed or key_rotated) else "This API key is shown once, here."
        if not stored["written"]:
            where = f" It could not be saved ({stored['error']}). Save it now."
        elif stored["location"] == "config":
            where = (
                f" It replaced this claim's earlier key in {stored['path']} ({_CONFIG_KEY}); "
                "restart the MCP server so l402_producer uses it."
                if replace_config
                else f" It is stored in {stored['path']} as {_CONFIG_KEY}; restart the MCP server "
                "so l402_producer uses it."
            )
        else:
            where = (
                " Another account's key is already configured, so this one was stored in "
                f"{stored['path']} (readable only by you) instead of config.json. Set "
                "LIGHTNING_ENABLE_API_KEY to it to manage this proxy."
            )
        result["apiKeyNote"] = lead + where + only
    elif replayed:
        result["apiKeyNote"] = REPLAY_WITHOUT_KEY_NOTE
        result["paymentHash"] = state.payment_hash

    # Replace the token with a secret-free marker, so a re-call answers "already claimed"
    # instead of buying a second claim.
    state.mark_redeemed(
        proxy_id,
        proxy.get("url"),
        proxy.get("manifest"),
        stored["location"] if stored and stored["written"] else state.key_location,
        stored["path"] if stored and stored["written"] else state.key_path,
        now,
    )
    result["message"] = (
        "The URL is now a paid L402 endpoint. Share proxyUrl; buyers pay per call to the "
        "Lightning Address, and agents find the price at manifestUrl."
    )
    return _dumps(result)


# ── Phase 2 ──────────────────────────────────────────────────────────────────────────


def _backoff(attempt: int, retry_after: Optional[int]) -> float:
    if retry_after is not None:
        return float(min(max(retry_after, 1), _MAX_RETRY_DELAY_SECONDS))
    return float(min(1 << (attempt - 1), _MAX_RETRY_DELAY_SECONDS))


async def _redeem(state: ClaimState, ctx: "_Ctx") -> str:
    authorization = f"L402 {state.macaroon}:{state.preimage}"
    code, status, problem, retry_after = NETWORK_ERROR, 0, None, None

    for attempt in range(1, MAX_REDEEM_ATTEMPTS + 1):
        response: Optional[ClaimHttpResponse] = None
        try:
            response = await ctx.claim_client.post(state.body, authorization)
        except (httpx.HTTPError, asyncio.TimeoutError):
            code, status, problem, retry_after = NETWORK_ERROR, 0, None, None

        if response is not None:
            if response.status == 200:
                claimed = _parse_object(response.body)
                if claimed is not None:
                    return _success_result(claimed, state, ctx.config_path, ctx.clock())
            status = response.status
            problem = _parse_object(response.body)
            code = "claim_failed" if response.status == 200 else _code_for(response.status, problem)
            retry_after = response.retry_after_seconds

        retry = describe(code, status)[1] or code in RETRY_AFTER_PAYMENT_ALSO
        if not retry or attempt == MAX_REDEEM_ATTEMPTS:
            return _problem_result(
                code, status, problem, retry_after, state, "phase2", paid=True, attempts=attempt
            )
        await _sleep(_backoff(attempt, retry_after))

    return _problem_result(  # pragma: no cover — the loop always returns
        code, status, problem, retry_after, state, "phase2", paid=True, attempts=MAX_REDEEM_ATTEMPTS
    )


# ── Pay ──────────────────────────────────────────────────────────────────────────────

#: Error prefixes ``pay_l402_challenge`` returns only BEFORE the wallet is asked.
_PRE_WALLET_PREFIXES = (
    "Payment denied by budget policy",  # budget deny and the reservation refusal
    "Confirmation code is invalid",
    "Invoice has no amount",
    "Invoice is required",
    "Wallet not initialized",
)


def is_known_pre_wallet_refusal(pay: dict) -> bool:
    """The pay results returned only before the wallet is asked: budget deny or reservation
    refusal, confirmation required or refused by the channel, an invalid or expired code, an
    amountless or invalid invoice, the ``max_sats`` ceiling, and no wallet."""
    if "requiresConfirmation" in pay:
        return True
    error = str(pay.get("error") or "")
    if error.startswith(_PRE_WALLET_PREFIXES):
        return True
    return error.startswith("Invoice amount ") and " exceeds maximum " in error


def _ledger_knows(ledger: "OperationLedger | None", invoice: Optional[str]) -> bool:
    return bool(ledger is not None and invoice and ledger.lookup(_derive_operation_id(invoice)))


async def _pay_then_redeem(state: ClaimState, ctx: "_Ctx") -> str:
    if not ctx.wallet:
        return _no_wallet("payment")

    state.phase = PAYING
    state.save()

    # The SAME path pay_l402_challenge runs: budget check, out-of-band confirmation through
    # request_confirmation, the spend reservation, receipts and history. The 402's priceSats
    # is the ceiling, so the budget is checked against it.
    pay_json = await pay_l402_challenge(
        invoice=state.invoice or "",
        macaroon=state.macaroon,
        max_sats=state.price_sats,
        confirmation_nonce=ctx.confirmation_nonce,
        wallet=ctx.wallet,
        budget_service=ctx.budget_service,
        payment_history_service=ctx.payment_history_service,
    )
    pay = _parse_object(pay_json) or {}

    if pay.get("success") is True and pay.get("preimage"):
        state.preimage = str(pay["preimage"])
        state.phase = PAID
        state.save()  # the preimage first, before anything else can fail
        return await _redeem(state, ctx)

    detail = sanitize_error(str(pay.get("error") or ""), 300)

    # Money left the wallet (or is in flight) without a proof.
    if pay.get("status") in ("pending", "paid_no_preimage") or pay.get("receipt_written") is not None:
        return _paid(
            state,
            "payment_unproven",
            "The claim payment left the wallet (or is in flight), but the wallet returned no "
            "proof of payment, so the claim cannot be redeemed yet. Do NOT pay again.",
            detail,
        )

    # Anything not provably refused before the wallet is ambiguous: keep 'paying'.
    if not is_known_pre_wallet_refusal(pay) or _ledger_knows(ctx.ledger, state.invoice):
        return _unknown(state, detail)

    state.phase = CHALLENGE
    state.save(allow_downgrade_from_paying=True)

    if pay.get("requiresConfirmation") is True:
        return _dumps(
            {
                "success": False,
                "action": "sell_url",
                "stage": "payment",
                "requiresConfirmation": True,
                "confirmationChannel": pay.get("confirmationChannel"),
                "error": "The claim payment requires human confirmation",
                "message": pay.get("message"),
                "howToConfirm": (
                    "Ask the human operator for the confirmation code, then call "
                    'l402_producer(action="sell_url") again with exactly the same arguments '
                    'plus confirmation_nonce="<code-from-human>".'
                ),
                "expiresInSeconds": pay.get("expiresInSeconds"),
                "amount": pay.get("amount"),
                "paid": False,
            }
        )

    refused: dict[str, Any] = {
        "success": False,
        "action": "sell_url",
        "stage": "payment",
        "error": "payment_refused",
        "message": "The claim was not paid: " + (detail or "unknown error") + " Nothing was charged.",
        "retryable": False,
        "paid": False,
        "priceSats": state.price_sats,
        "nextStep": (
            "If a confirmation code was rejected, call sell_url again with the same arguments and "
            "no confirmation_nonce to request a new one."
        ),
    }
    if "confirmationChannel" in pay:
        refused["confirmationChannel"] = pay["confirmationChannel"]
    return _dumps(refused)


# ── Entry point ──────────────────────────────────────────────────────────────────────


@dataclass
class _Ctx:
    claim_client: ProducerClaimClient
    wallet: Any
    ledger: "OperationLedger | None"
    budget_service: "BudgetService | None"
    payment_history_service: "PaymentHistoryService | None"
    config_path: str
    confirmation_nonce: Optional[str]
    has_recovery_email: bool
    clock: Callable[[], datetime]


#: Serializes calls for the same claim body within this process.
_locks: dict[str, asyncio.Lock] = {}


def _default_config_path() -> str:
    try:
        from ..config import get_config_service

        return get_config_service().config_file_path
    except Exception:  # noqa: BLE001
        return str(Path.home() / ".lightning-enable" / "config.json")


async def _run(
    ctx: _Ctx,
    state_dir: Path,
    body: Optional[str],
    accept_terms: Optional[str],
    service_name: str,
) -> str:
    # ── Resume: the saved state for this exact body decides. ─────────────────────────
    if body is not None:
        saved = ClaimState.load(state_dir, body)
        if saved is not None:
            if saved.phase == REDEEMED:
                return _already_claimed(saved)
            if saved.phase == PAID:
                return await _redeem(saved, ctx)
            if saved.phase == PAYING:
                # The wallet may have been asked. Only the ledger can prove it was not.
                if ctx.ledger is not None and not _ledger_knows(ctx.ledger, saved.invoice):
                    saved.phase = CHALLENGE
                    saved.save(allow_downgrade_from_paying=True)
                    return await _pay_then_redeem(saved, ctx)
                return _unknown(saved, None)
            if not saved.is_expired(ctx.clock()):
                return await _pay_then_redeem(saved, ctx)
            saved.delete()  # a challenge the wallet never saw, and expired: worth nothing

    # ── 1. The quote (free). ─────────────────────────────────────────────────────────
    try:
        quote_response = await ctx.claim_client.get_quote()
    except (httpx.HTTPError, asyncio.TimeoutError):
        return _problem_result(NETWORK_ERROR, 0, None, None, None, "quote", paid=False)
    if quote_response.status != 200:
        return _http_problem(quote_response, "quote")

    quote = _parse_object(quote_response.body)
    if quote is None:
        return _failure("invalid_quote", "The claim quote was not valid JSON.", stage="quote")
    if quote.get("enabled") is False:
        return _problem_result("claim_unavailable", 200, None, None, None, "quote", paid=False)

    terms_version = (quote.get("terms") or {}).get("version")
    quoted_sats = _as_int((quote.get("price") or {}).get("sats"))
    if not terms_version or quoted_sats <= 0:
        return _failure(
            "invalid_quote", "The claim quote has no terms version or price.", stage="quote"
        )

    if body is None or not accept_terms or accept_terms.strip() != terms_version:
        return _quote_result(quote, terms_version, accept_terms, service_name, ctx.has_recovery_email)

    # ── 2. Phase 1: the 402. ─────────────────────────────────────────────────────────
    if not ctx.wallet:
        return _no_wallet("phase1")

    try:
        challenge_response = await ctx.claim_client.post(body, None)
    except (httpx.HTTPError, asyncio.TimeoutError):
        return _problem_result(NETWORK_ERROR, 0, None, None, None, "phase1", paid=False)

    challenge = _parse_object(challenge_response.body) if challenge_response.status == 402 else None
    invoice = challenge.get("invoice") if challenge else None
    macaroon = challenge.get("macaroon") if challenge else None
    if challenge_response.status != 402 or not invoice or not macaroon:
        return _http_problem(challenge_response, "phase1")

    challenge_sats = _as_int(challenge.get("priceSats"))
    if challenge_sats <= 0 or challenge_sats > quoted_sats:
        return _failure(
            "price_changed",
            f"The claim invoice asks for {challenge_sats:,} sats but the quote said "
            f"{quoted_sats:,}. Nothing was paid. Show the operator the new figure and call "
            "sell_url again if they agree.",
            stage="phase1",
            quotedSats=quoted_sats,
            invoiceSats=challenge_sats,
        )

    state = ClaimState(
        directory=state_dir,
        body=body,
        claim_url=ctx.claim_client.claim_url,
        invoice=invoice,
        macaroon=macaroon,
        payment_hash=challenge.get("paymentHash"),
        expires_at=str(challenge.get("expiresAt")) if challenge.get("expiresAt") else None,
        price_sats=challenge_sats,
        phase=CHALLENGE,
    )
    state.save()  # before paying: a crash from here on is recoverable
    return await _pay_then_redeem(state, ctx)


async def sell_url(
    target_url: Optional[str],
    lightning_address: Optional[str],
    default_price_sats: Optional[int] = None,
    name: Optional[str] = None,
    description: Optional[str] = None,
    recovery_email: Optional[str] = None,
    recovery_nostr_pubkey: Optional[str] = None,
    list_publicly: bool = False,
    accept_terms: Optional[str] = None,
    confirmation_nonce: Optional[str] = None,
    claim_client: Optional[ProducerClaimClient] = None,
    wallet: Any = None,
    budget_service: "BudgetService | None" = None,
    payment_history_service: "PaymentHistoryService | None" = None,
    operation_ledger: "OperationLedger | None" = None,
    config_path: Optional[str] = None,
    now: Optional[Callable[[], datetime]] = None,
) -> str:
    """Turn ``target_url`` plus ``lightning_address`` into a paid L402 endpoint."""
    # ── Local checks: nothing is sent, nothing is minted. ────────────────────────────
    target_url = (target_url or "").strip()
    lightning_address = (lightning_address or "").strip()
    if not target_url:
        return _local_refusal(
            "invalid_target_url", "target_url is required: the https URL of the API to sell."
        )
    parts = urlsplit(target_url)
    if parts.scheme != "https" or not parts.hostname:
        return _local_refusal(
            "invalid_target_url",
            "target_url must be an absolute https URL, like https://api.example.com.",
        )
    if not lightning_address or "@" not in lightning_address:
        return _local_refusal(
            "invalid_lightning_address",
            "lightning_address is required, as name@domain from a wallet provider that supports "
            "Lightning Addresses.",
        )
    email = recovery_email.strip() if recovery_email and recovery_email.strip() else None
    pubkey = (
        recovery_nostr_pubkey.strip()
        if recovery_nostr_pubkey and recovery_nostr_pubkey.strip()
        else None
    )
    if email and pubkey:
        return _local_refusal(
            "invalid_recovery", "Give recovery_email or recovery_nostr_pubkey, not both."
        )
    if default_price_sats is not None and default_price_sats < 0:
        return _local_refusal(
            "invalid_price",
            "default_price_sats must be positive; omit it for the server default.",
        )
    if claim_client is None:
        return _failure(
            "not_available",
            "The producer claim client is not available on this server.",
            stage="setup",
        )

    service_name = name.strip() if name and name.strip() else parts.hostname
    config_path = config_path or _default_config_path()
    state_dir = Path(config_path).parent / "claim"
    ctx = _Ctx(
        claim_client=claim_client,
        wallet=wallet,
        ledger=operation_ledger,
        budget_service=budget_service,
        payment_history_service=payment_history_service,
        config_path=config_path,
        confirmation_nonce=confirmation_nonce,
        has_recovery_email=email is not None,
        clock=now or (lambda: datetime.now(timezone.utc)),
    )

    body: Optional[str] = None
    lock: Optional[asyncio.Lock] = None
    try:
        if accept_terms and accept_terms.strip():
            body = build_body(
                target_url, service_name, description, default_price_sats, lightning_address,
                email, pubkey, list_publicly, accept_terms.strip(),
            )
            key = os.path.normcase(os.path.abspath(state_dir / state_file_name(body)))
            lock = _locks.setdefault(key, asyncio.Lock())
            await lock.acquire()
        try:
            return await _run(ctx, state_dir, body, accept_terms, service_name)
        finally:
            if lock is not None and lock.locked():
                lock.release()
    except Exception as e:  # noqa: BLE001
        # Whatever escaped, the file says whether money may have moved.
        logger.warning("sell_url stopped: %s", type(e).__name__)
        saved = ClaimState.load(state_dir, body) if body is not None else None
        if saved is not None and saved.phase == PAID:
            return _paid(
                saved,
                "sell_url_failed",
                f"sell_url stopped after the claim was paid: {sanitize_error(str(e))}",
            )
        if saved is not None and saved.phase == PAYING:
            return _unknown(saved, sanitize_error(str(e), 300))
        if saved is not None and saved.phase == REDEEMED:
            return _already_claimed(saved)
        return _failure("sell_url_failed", f"sell_url failed: {sanitize_error(str(e))}", "unknown")
