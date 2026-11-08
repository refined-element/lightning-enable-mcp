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

Crash safety: the exact body and the challenge are written to
``<config dir>/claim/sell-<sha256(body)[:16]>.json`` (0600) BEFORE paying, and the preimage
right after, so calling again with the same arguments skips phase 1 and redeems the token
already paid for. The file is deleted once the claim answers 200.

Secrets: the macaroon and preimage never enter a tool result; the API key enters exactly one
(the first 200), after it has been stored.

Mirrors ``dotnet/src/LightningEnable.Mcp/Tools/ProducerSellUrl.cs``; both are pinned to
``shared/l402-producer-sell-url.json``.
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
from . import sanitize_error
from .create_account import _merge_api_key_into_config
from .pay_challenge import pay_l402_challenge

if TYPE_CHECKING:
    from ..budget_service import BudgetService
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

#: Permanent before payment, but can clear on a phase-2 retry: phase 2 re-runs the DNS check
#: and the Lightning Address lookup, so a briefly-down provider or resolver recovers.
RETRY_AFTER_PAYMENT_ALSO = frozenset({"invalid_lightning_address", "invalid_target_url"})

_BODY_RULE = (
    "The claim body is written once and sent twice: the payment is bound to the target URL, the "
    "Lightning Address, and the recovery binding. Changing any of them after paying makes the "
    "payment unusable, so call sell_url again with exactly the same arguments."
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
    base = os.getenv("LIGHTNING_ENABLE_API_URL")
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


def _restrict(path: Path) -> None:
    try:
        from ..config import _restrict_file_permissions

        _restrict_file_permissions(path)
    except Exception:  # noqa: BLE001 — best effort
        pass


def state_file_name(body: str) -> str:
    return f"sell-{hashlib.sha256(body.encode('utf-8')).hexdigest()[:16]}.json"


@dataclass
class ClaimState:
    """The claim in flight, one 0600 file per exact body."""

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
    _file: Path = field(init=False)

    def __post_init__(self) -> None:
        self._file = Path(self.directory) / state_file_name(self.body)

    @property
    def file_path(self) -> str:
        return str(self._file)

    def is_expired(self, now: datetime) -> bool:
        if not self.expires_at:
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
        return cls(
            directory=Path(directory),
            body=body,
            claim_url=data.get("claimUrl"),
            invoice=data.get("invoice"),
            macaroon=data.get("macaroon"),
            payment_hash=data.get("paymentHash"),
            expires_at=data.get("expiresAt"),
            price_sats=int(data.get("priceSats") or 0),
            phase=data.get("phase") or CHALLENGE,
            preimage=data.get("preimage"),
        )

    def save(self) -> None:
        directory = self._file.parent
        if not directory.exists():
            directory.mkdir(parents=True, exist_ok=True)
            try:
                os.chmod(directory, 0o700)
            except OSError:
                pass
        data: dict[str, Any] = {
            "version": 1,
            "claimUrl": self.claim_url,
            "body": self.body,
            "invoice": self.invoice,
            "macaroon": self.macaroon,
            "paymentHash": self.payment_hash,
            "expiresAt": self.expires_at,
            "priceSats": self.price_sats,
            "phase": self.phase,
        }
        if self.preimage is not None:
            data["preimage"] = self.preimage
        # Create and restrict the temp file before the secrets go in, then rename over.
        tmp = self._file.with_name(self._file.name + ".tmp")
        tmp.write_text("", encoding="utf-8")
        _restrict(tmp)
        tmp.write_text(json.dumps(data, indent=2), encoding="utf-8")
        os.replace(tmp, self._file)

    def delete(self) -> None:
        try:
            self._file.unlink()
        except OSError:
            pass


# ── Body ─────────────────────────────────────────────────────────────────────────────


def build_body(
    target_url: str,
    name: str,
    description: Optional[str],
    price_sats: Optional[int],
    lightning_address: str,
    email: Optional[str],
    pubkey: Optional[str],
    list_publicly: bool,
    accept_terms: str,
) -> str:
    """The claim body, field order as the skill writes it. Serialized ONCE."""
    body: dict[str, Any] = {"targetUrl": target_url, "name": name}
    if description and description.strip():
        body["description"] = description.strip()
    if price_sats and price_sats > 0:
        body["defaultPriceSats"] = int(price_sats)
    body["receive"] = {"lightningAddress": lightning_address}
    if email:
        body["recovery"] = {"email": email}
    elif pubkey:
        body["recovery"] = {"nostrPubkey": pubkey}
    body["listPublicly"] = bool(list_publicly)
    body["acceptTerms"] = accept_terms
    return json.dumps(body, separators=(",", ":"))


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


def _recovery_block(state: ClaimState) -> dict:
    return {
        "paymentSpent": True,
        "paymentHash": state.payment_hash,
        "stateFile": state.file_path,
        "tokenExpiresAt": state.expires_at,
        "instructions": (
            "The claim payment is spent; do not pay again. The saved file holds the exact claim "
            "body and the paid token (macaroon and preimage). Do not edit it and never paste its "
            "contents into chat. To retry, call l402_producer(action=\"sell_url\") again with "
            "exactly the same arguments before the token expires; the tool skips the payment and "
            "redeems the saved token. By hand: POST the file's \"body\" byte for byte to "
            f"{state.claim_url} with Content-Type: application/json and Authorization: L402 "
            "<macaroon>:<preimage> taken from the file. If it keeps failing, contact "
            "support@lightningenable.com with the payment hash only."
        ),
    }


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


def _quote_result(quote: dict, terms_version: str, accept_terms: Optional[str], name: str) -> str:
    mismatch = bool(accept_terms and accept_terms.strip())
    prefix = (
        f'acceptTerms "{accept_terms.strip()}" is not the current terms version. ' if mismatch else ""
    )
    return _dumps(
        {
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
            "name": name,
            "bodyRule": _BODY_RULE,
            "message": prefix
            + "Nothing was sent or charged. Show the operator the claim price, what it covers, the "
            "renewal, and the terms link, and get an explicit yes before paying.",
            "nextStep": (
                'Call l402_producer(action="sell_url") again with the same arguments plus '
                f'accept_terms="{terms_version}".'
            ),
        }
    )


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


def store_api_key(config_path: str, proxy_id: str, api_key: str) -> dict:
    """Empty config slot (and no key in the environment): merge into config.json the way
    ``create_lightning_enable_account`` does. Otherwise another account owns the slot, so
    write ``claim-<proxyId>.key`` beside it, 0600."""
    path = Path(config_path)
    existing = os.getenv("LIGHTNING_ENABLE_API_KEY")
    if not existing or existing.startswith("${"):
        existing = _read_config_key(path)

    if not existing or existing == api_key:
        ok, written_path, _error = _merge_api_key_into_config(api_key, str(path))
        if ok:
            return {"written": True, "location": "config", "path": written_path, "error": None}
        # An unparseable config is never overwritten; fall through to the key file.

    safe_id = re.sub(r"[^a-z0-9-]", "-", proxy_id.lower())
    key_path = path.parent / f"claim-{safe_id}.key"
    try:
        key_path.parent.mkdir(parents=True, exist_ok=True)
        key_path.write_text(api_key + "\n", encoding="utf-8")
        _restrict(key_path)
        return {"written": True, "location": "key_file", "path": str(key_path), "error": None}
    except Exception as e:  # noqa: BLE001
        return {
            "written": False,
            "location": "none",
            "path": str(key_path),
            "error": sanitize_error(str(e)),
        }


def _success_result(claimed: dict, state: ClaimState, config_path: str, resumed: bool) -> str:
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

    if isinstance(api_key, str) and api_key:
        stored = store_api_key(config_path, proxy_id, api_key)
        result["apiKey"] = api_key
        result["apiKeyStorage"] = stored
        only = (
            " With no recovery binding it is the account's only credential."
            if recovery == "none"
            else ""
        )
        rotated = (
            " This is a fresh key: any earlier key for this account no longer works."
            if key_rotated
            else ""
        )
        if not stored["written"]:
            note = (
                f"This API key is shown once, here, and could not be saved ({stored['error']}). "
                f"Save it now.{only}{rotated}"
            )
        elif stored["location"] == "config":
            note = (
                f"This API key is shown once, here. It is stored in {stored['path']} as "
                f"{_CONFIG_KEY}; restart the MCP server so l402_producer uses it.{only}{rotated}"
            )
        else:
            note = (
                "This API key is shown once, here. Another account's key is already configured, "
                f"so this one was stored in {stored['path']} (readable only by you) instead of "
                "config.json. Set LIGHTNING_ENABLE_API_KEY to it to manage this proxy."
                f"{only}{rotated}"
            )
        result["apiKeyNote"] = note
    elif replayed:
        result["apiKeyNote"] = (
            "This claim had already been redeemed, so the server does not return the API key "
            "again. If the first answer was lost, recover the account with the recovery binding, "
            "or contact support with the payment hash."
            if resumed
            else "The server returned the stored claim without the API key: only the first "
            "answer carries it."
        )
        result["paymentHash"] = state.payment_hash

    state.delete()
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


async def _redeem(
    state: ClaimState, claim_client: ProducerClaimClient, config_path: str, resumed: bool
) -> str:
    authorization = f"L402 {state.macaroon}:{state.preimage}"
    code, status, problem, retry_after = NETWORK_ERROR, 0, None, None

    for attempt in range(1, MAX_REDEEM_ATTEMPTS + 1):
        response: Optional[ClaimHttpResponse] = None
        try:
            response = await claim_client.post(state.body, authorization)
        except (httpx.HTTPError, asyncio.TimeoutError):
            code, status, problem, retry_after = NETWORK_ERROR, 0, None, None

        if response is not None:
            if response.status == 200:
                claimed = _parse_object(response.body)
                if claimed is not None:
                    return _success_result(claimed, state, config_path, resumed)
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


async def _pay_then_redeem(
    state: ClaimState,
    confirmation_nonce: Optional[str],
    claim_client: ProducerClaimClient,
    wallet: Any,
    budget_service: "BudgetService | None",
    payment_history_service: "PaymentHistoryService | None",
    config_path: str,
) -> str:
    if not wallet:
        return _failure(
            "no_wallet",
            "No spending wallet is configured, so the claim cannot be paid. Configure LND, NWC, "
            "or Strike (setup_wallet), then call sell_url again with the same arguments.",
            stage="payment",
        )

    was_paying = state.phase == PAYING
    state.phase = PAYING
    state.save()

    # The SAME path pay_l402_challenge runs: budget check, out-of-band confirmation through
    # request_confirmation, the spend reservation, receipts and history. The 402's priceSats
    # is the ceiling, so the budget is checked against it.
    pay_json = await pay_l402_challenge(
        invoice=state.invoice or "",
        macaroon=state.macaroon,
        max_sats=state.price_sats,
        confirmation_nonce=confirmation_nonce,
        wallet=wallet,
        budget_service=budget_service,
        payment_history_service=payment_history_service,
    )
    pay = _parse_object(pay_json) or {}

    if pay.get("success") is True and pay.get("preimage"):
        state.preimage = str(pay["preimage"])
        state.phase = PAID
        state.save()  # the preimage first, before anything else can fail
        return await _redeem(state, claim_client, config_path, resumed=False)

    if pay.get("requiresConfirmation") is True:
        state.phase = CHALLENGE
        state.save()
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
                    'Ask the human operator for the confirmation code, then call '
                    'l402_producer(action="sell_url") again with exactly the same arguments '
                    'plus confirmation_nonce="<code-from-human>".'
                ),
                "expiresInSeconds": pay.get("expiresInSeconds"),
                "amount": pay.get("amount"),
                "paid": False,
            }
        )

    # Did money move? A pending or proof-less settlement says so outright; receipt_written is
    # None when nothing was paid and True/False once the wallet seam saw a payment.
    money_moved = pay.get("status") in ("pending", "paid_no_preimage") or (
        "receipt_written" in pay and pay["receipt_written"] is not None
    )
    if money_moved or was_paying:
        return _dumps(
            {
                "success": False,
                "action": "sell_url",
                "stage": "payment",
                "error": "payment_unproven" if money_moved else "payment_state_unknown",
                "message": (
                    "The claim payment left the wallet (or is in flight), but the wallet returned "
                    "no proof of payment, so the claim cannot be redeemed yet. Do NOT pay again."
                    if money_moved
                    else "An earlier sell_url call started paying this claim and did not finish, "
                    "and the wallet now refuses the invoice. It may have been paid. Do NOT pay "
                    "again."
                ),
                "detail": sanitize_error(str(pay.get("error") or ""), 300),
                "retryable": False,
                "paid": True,
                "recovery": _recovery_block(state),
            }
        )

    # Refused before the wallet was asked: budget, ceiling, approval channel. Nothing spent.
    state.phase = CHALLENGE
    state.save()
    refused: dict[str, Any] = {
        "success": False,
        "action": "sell_url",
        "stage": "payment",
        "error": "payment_refused",
        "message": "The claim was not paid: "
        + sanitize_error(str(pay.get("error") or "unknown error"), 300)
        + " Nothing was charged.",
        "retryable": False,
        "paid": False,
        "priceSats": state.price_sats,
    }
    if "confirmationChannel" in pay:
        refused["confirmationChannel"] = pay["confirmationChannel"]
    return _dumps(refused)


# ── Entry point ──────────────────────────────────────────────────────────────────────


def _default_config_path() -> str:
    try:
        from ..config import get_config_service

        return get_config_service().config_file_path
    except Exception:  # noqa: BLE001
        return str(Path.home() / ".lightning-enable" / "config.json")


async def sell_url(
    target_url: Optional[str],
    lightning_address: Optional[str],
    price_sats: Optional[int] = None,
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
    config_path: Optional[str] = None,
    now: Optional[Callable[[], datetime]] = None,
) -> str:
    """Turn ``target_url`` plus ``lightning_address`` into a paid L402 endpoint."""
    try:
        # ── Local checks: nothing is sent, nothing is minted. ────────────────────────
        target_url = (target_url or "").strip()
        lightning_address = (lightning_address or "").strip()
        if not target_url:
            return _local_refusal(
                "invalid_target_url", "target_url is required: the https URL of the API to sell."
            )
        parts = urlsplit(target_url)
        if parts.scheme not in ("https", "http") or not parts.hostname:
            return _local_refusal(
                "invalid_target_url",
                "target_url must be an absolute https URL, like https://api.example.com.",
            )
        if not lightning_address or "@" not in lightning_address:
            return _local_refusal(
                "invalid_lightning_address",
                "lightning_address is required, as name@domain from a wallet provider that "
                "supports Lightning Addresses.",
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
        if price_sats is not None and price_sats < 0:
            return _local_refusal(
                "invalid_price", "price_sats must be positive; omit it for the server default."
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
        clock = now or (lambda: datetime.now(timezone.utc))

        # ── Resume: a saved challenge for this exact body skips phase 1. ─────────────
        body: Optional[str] = None
        if accept_terms and accept_terms.strip():
            body = build_body(
                target_url, service_name, description, price_sats, lightning_address,
                email, pubkey, list_publicly, accept_terms.strip(),
            )
            saved = ClaimState.load(state_dir, body)
            if saved is not None:
                if saved.phase == PAID:
                    return await _redeem(saved, claim_client, config_path, resumed=True)
                if saved.phase == PAYING or not saved.is_expired(clock()):
                    return await _pay_then_redeem(
                        saved, confirmation_nonce, claim_client, wallet, budget_service,
                        payment_history_service, config_path,
                    )
                saved.delete()  # an unpaid, expired challenge is worth nothing

        # ── 1. The quote (free). ─────────────────────────────────────────────────────
        try:
            quote_response = await claim_client.get_quote()
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

        if not accept_terms or accept_terms.strip() != terms_version:
            return _quote_result(quote, terms_version, accept_terms, service_name)

        # ── 2. Phase 1: the 402. ─────────────────────────────────────────────────────
        if not wallet:
            return _failure(
                "no_wallet",
                "No spending wallet is configured, so the claim cannot be paid. Configure LND, "
                "NWC, or Strike (setup_wallet), then call sell_url again. Nothing was minted or "
                "charged.",
                stage="phase1",
            )

        assert body is not None
        try:
            challenge_response = await claim_client.post(body, None)
        except (httpx.HTTPError, asyncio.TimeoutError):
            return _problem_result(NETWORK_ERROR, 0, None, None, None, "phase1", paid=False)

        challenge = (
            _parse_object(challenge_response.body) if challenge_response.status == 402 else None
        )
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
            claim_url=claim_client.claim_url,
            invoice=invoice,
            macaroon=macaroon,
            payment_hash=challenge.get("paymentHash"),
            expires_at=str(challenge.get("expiresAt")) if challenge.get("expiresAt") else None,
            price_sats=challenge_sats,
            phase=CHALLENGE,
        )
        state.save()  # before paying: a crash from here on is recoverable

        return await _pay_then_redeem(
            state, confirmation_nonce, claim_client, wallet, budget_service,
            payment_history_service, config_path,
        )
    except Exception as e:  # noqa: BLE001
        logger.exception("sell_url failed")
        return _failure("sell_url_failed", f"sell_url failed: {sanitize_error(str(e))}", "unknown")
