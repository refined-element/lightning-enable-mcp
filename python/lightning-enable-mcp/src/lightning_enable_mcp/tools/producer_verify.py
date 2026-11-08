"""Ownership verification for ``l402_producer``: ``verify`` with ``proxy_id``, and
``verification_token``.

A proxy owner proves they control the API behind a proxy by publishing a token in a
well-known file, a DNS TXT record, or a response header, and asking Lightning Enable to
look for it. Only a verified proxy can be listed publicly; an unverified one keeps working.
The server side is ``ProxyVerificationController``: ``GET /api/proxy/{id}/verification``,
``POST .../verification/token`` and ``POST /api/proxy/{id}/verify``, all on the merchant
API key the other producer actions use.

**The token.** The server returns it once and stores only a hash. This returns it to the
caller once, in its own field, and never logs it. Every instruction value keeps the server's
``<token>`` placeholder.

The method table, error codes, polling bounds and reason hints are pinned against
``shared/l402-producer-verify.json`` so both ports agree. Mirrors
``dotnet/.../Tools/ProducerVerification.cs``.
"""

import asyncio
import json
import re
from collections.abc import Awaitable, Callable
from datetime import datetime, timezone
from pathlib import Path
from typing import TYPE_CHECKING, Any

from . import sanitize_error

if TYPE_CHECKING:
    from ..lightning_enable_api import LightningEnableApiClient

MAX_PENDING_ATTEMPTS = 4
MAX_RETRY_AFTER_SECONDS = 10

PROXY_ID_REQUIRED = "proxy_id_required"
INVALID_METHOD = "invalid_method"
AMBIGUOUS_VERIFY = "ambiguous_verify"
REISSUE_REQUIRED = "reissue_required"
TOKEN_REQUIRED = "verification_token_required"
UNAUTHORIZED = "unauthorized"
PROXY_NOT_FOUND = "proxy_not_found"
FEATURE_NOT_ENABLED = "feature_not_enabled"
RATE_LIMITED = "rate_limited"
REQUEST_REJECTED = "request_rejected"
UPSTREAM_ERROR = "upstream_error"

#: Every error code this surface returns, and whether retrying can help.
ERROR_CODES: dict[str, bool] = {
    PROXY_ID_REQUIRED: False,
    INVALID_METHOD: False,
    AMBIGUOUS_VERIFY: False,
    REISSUE_REQUIRED: False,
    TOKEN_REQUIRED: False,
    UNAUTHORIZED: False,
    PROXY_NOT_FOUND: False,
    FEATURE_NOT_ENABLED: False,
    RATE_LIMITED: True,
    REQUEST_REJECTED: False,
    UPSTREAM_ERROR: True,
}

#: The three methods, in the order the server tries them.
METHODS: list[dict[str, str]] = [
    {
        "method": "well-known",
        "proves": "that you control the web root of the target host",
        "chooseWhen": (
            "you control the web server for the target's hostname and can add a file at its root"
        ),
    },
    {
        "method": "dns-txt",
        "proves": "that you control the DNS zone for the target's hostname",
        "chooseWhen": "someone else hosts the API but you manage DNS for its hostname",
    },
    {
        "method": "header",
        "proves": "only that you control the API's responses at the target base URL",
        "chooseWhen": "you own the API's code but not the web root or DNS",
    },
]

#: Methods a live check produces. ``grandfathered`` and ``platform`` are not: a new token
#: does not unlist those, so only these need ``reissue``.
LIVE_METHODS: list[str] = [m["method"] for m in METHODS]

_SLOW_DNS = (
    "The target host could not be looked up in time. Call verify again; if it persists, "
    "check the hostname's DNS."
)

#: Reason substrings (from ``ProxyVerificationService``) and what to do about each. Order
#: matters: a method gets the FIRST hint that matches it, so the narrower TXT text sits
#: before the broader one.
REASON_HINTS: list[dict[str, Any]] = [
    {
        "contains": "The target host was refused before any request",
        "method": None,
        "hint": (
            "No method ran: the target host resolves to a private or internal address, or does "
            "not resolve. Point the proxy at the API's public HTTPS address."
        ),
    },
    {
        "contains": "The target URL cannot be verified",
        "method": None,
        "hint": (
            "No method ran: the target base URL uses an IP address, localhost or an internal "
            "domain, a port other than 80 or 443, or a scheme other than http or https. Update "
            "the proxy's target to the API's public hostname, then issue a token and verify again."
        ),
    },
    {
        "contains": "was re-issued while this check ran",
        "method": None,
        "hint": (
            "The token was replaced while the check ran. Publish the newest token and call "
            "verify again."
        ),
    },
    {
        "contains": "which is not followed",
        "method": None,
        "hint": (
            "Redirects are never followed. Serve the file or header on the target host itself, "
            "make the base URL answer without a trailing-slash redirect, and exempt the path "
            "from any sign-in redirect."
        ),
    },
    {"contains": "the host check did not finish", "method": None, "hint": _SLOW_DNS},
    {"contains": "the host check failed", "method": None, "hint": _SLOW_DNS},
    {
        "contains": "no answer within",
        "method": None,
        "hint": (
            "The target answered too slowly. Make the file or base URL answer quickly without "
            "waking a cold-start backend, or use dns-txt, which does not depend on your "
            "server's response time."
        ),
    },
    {
        "contains": "could not be reached",
        "method": None,
        "hint": (
            "The request did not complete. Check that the host answers HTTPS on port 443 with a "
            "valid, publicly trusted certificate and that a firewall does not block outside "
            "requests."
        ),
    },
    {
        "contains": "lightning-enable.json answered HTTP",
        "method": "well-known",
        "hint": (
            "The file is not served at the target host's /.well-known/lightning-enable.json, or "
            "a CDN still serves a cached 404. Check the path on the target host itself and purge "
            "the cache."
        ),
    },
    {
        "contains": "is not valid JSON",
        "method": "well-known",
        "hint": 'Serve exactly {"verify":"<token>"}, with straight quotes and no comments.',
    },
    {
        "contains": 'has no "verify" field',
        "method": "well-known",
        "hint": "The key must be verify, in lowercase.",
    },
    {
        "contains": "must be a string or an array of strings",
        "method": "well-known",
        "hint": "Put the token in quotes, or in an array of quoted tokens.",
    },
    {
        "contains": "is larger than",
        "method": "well-known",
        "hint": "Serve only the verify field; the file must stay under 4 KB.",
    },
    {
        "contains": "\"verify\" does not contain this proxy's current token",
        "method": "well-known",
        "hint": (
            "The file's token is old, mistyped, or cached. Publish the token you most recently "
            "issued, or issue a new one."
        ),
    },
    {
        "contains": "contains this proxy's current token",
        "method": "dns-txt",
        "hint": (
            "A TXT record exists, but none of its values is exactly the current token. Remove "
            "quotes, prefixes and old tokens, or issue a new token."
        ),
    },
    {
        "contains": "no TXT record at",
        "method": "dns-txt",
        "hint": (
            "No TXT record is visible yet. A new record can take minutes to propagate; check "
            "with dig +short TXT on the record name. Many DNS providers append your zone, so "
            "enter only the part before it."
        ),
    },
    {
        "contains": "does not carry this proxy's current token",
        "method": "header",
        "hint": (
            "The header is there, but its value is not the current token. Publish the token you "
            "most recently issued."
        ),
    },
    {
        "contains": "without X-Lightning-Enable-Verify",
        "method": "header",
        "hint": (
            "The base URL answered without the header. Add it to every response at the base "
            "URL, including error responses and HEAD."
        ),
    },
]

REASON_HINT_FALLBACK = (
    "Fix what the reason names for the method you published, then call verify again. Only "
    "that method needs to pass; the others failing is expected."
)

_ABOUT = (
    "Ownership verification proves you control the API behind the proxy. Only a verified "
    "proxy can be listed publicly (l402_producer action=publish); an unverified one keeps "
    "working and taking payments by its direct URL."
)

#: State fields copied from the server's answer. An allowlist: never ``token``.
_STATE_FIELDS = (
    "proxyId",
    "verified",
    "method",
    "verifiedAt",
    "lastCheckedAt",
    "failures",
    "tokenIssued",
    "listed",
    "nextCheckAt",
    "checkedAt",
    "reason",
    "pending",
    "retryAfterSeconds",
)

_INSTRUCTION_FIELDS = ("summary", "url", "recordName", "recordType", "headerName", "body", "value")

Sleep = Callable[[float], Awaitable[Any]]


# ── Token store ───────────────────────────────────────────────────────────────────────
#
# The server shows a verification token once. Like sell_url's API key, it is kept in an
# owner-only file beside config.json, ``verification-<proxyId>.json``, so ``verify`` can
# point at it later instead of issuing a new one (which would replace the published token).

TOKEN_FILE_PREFIX = "verification-"


def token_file_path(config_path: str, proxy_id: str) -> Path:
    """``verification-<proxyId>.json`` beside config.json; the id lowercased, anything but
    ``a-z0-9-`` replaced by ``-`` (the same rule as ``claim-<proxyId>.key``)."""
    safe_id = re.sub(r"[^a-z0-9-]", "-", proxy_id.lower())
    return Path(config_path).parent / f"{TOKEN_FILE_PREFIX}{safe_id}.json"


def _config_path(config_path: str | None) -> str:
    if config_path:
        return config_path
    from .producer_sell_url import _default_config_path

    return _default_config_path()


def store_token(config_path: str | None, proxy_id: str, token: str, source: str) -> dict[str, Any]:
    """Write the token owner-only. Never raises; reports where it went or why it did not."""
    from .producer_sell_url import _write_restricted

    path = token_file_path(_config_path(config_path), proxy_id)
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        _write_restricted(
            path,
            json.dumps(
                {
                    "proxyId": proxy_id,
                    "token": token,
                    "source": source,
                    "storedAt": datetime.now(timezone.utc).isoformat(),
                },
                indent=2,
            ),
        )
        return {"written": True, "path": str(path), "error": None}
    except Exception as e:  # noqa: BLE001
        return {"written": False, "path": str(path), "error": sanitize_error(str(e))}


def load_token(config_path: str | None, proxy_id: str) -> dict[str, Any] | None:
    """The stored token for ``proxy_id``, or None."""
    path = token_file_path(_config_path(config_path), proxy_id)
    try:
        if not path.exists():
            return None
        data = json.loads(path.read_text(encoding="utf-8"))
    except Exception:  # noqa: BLE001
        return None
    if not isinstance(data, dict) or not isinstance(data.get("token"), str) or not data["token"]:
        return None
    return {"token": data["token"], "path": str(path), "source": data.get("source")}


def storage_note(storage: dict[str, Any]) -> str:
    """The sentence that says where the token went, sell_url-key style."""
    if storage.get("written"):
        return (
            f"It is stored in {storage['path']} (readable only by you), where l402_producer "
            "action=verify finds it."
        )
    return f"It could not be saved ({storage.get('error')}). Save it now."


def _fail(code: str, error: str) -> str:
    return json.dumps(
        {"success": False, "errorCode": code, "error": error, "retryable": ERROR_CODES[code]},
        indent=2,
    )


def _int(value: Any) -> int | None:
    return value if isinstance(value, int) and not isinstance(value, bool) else None


def normalize_method(method: str | None) -> str | None:
    """The advisory method filter, normalized. None for none; ``""`` for invalid."""
    if method is None or not str(method).strip():
        return None
    value = str(method).strip().lower().replace("_", "-")
    if value in ("dns", "txt", "dns-txt-record"):
        value = "dns-txt"
    return value if value in LIVE_METHODS else ""


def is_ownership_verify(proxy_id: Any, macaroon: Any, preimage: Any) -> bool:
    """``verify`` with a ``proxy_id`` and no payment arguments checks ownership."""
    return bool(proxy_id and str(proxy_id).strip()) and not (
        (macaroon and str(macaroon).strip()) or (preimage and str(preimage).strip())
    )


def is_ambiguous_verify(proxy_id: Any, macaroon: Any, preimage: Any) -> bool:
    """``verify`` naming both a proxy and a payment is refused."""
    return bool(proxy_id and str(proxy_id).strip()) and bool(
        (macaroon and str(macaroon).strip()) or (preimage and str(preimage).strip())
    )


def ambiguous_verify_result() -> str:
    return _fail(
        AMBIGUOUS_VERIFY,
        "verify takes either macaroon and preimage (check a payment) or proxy_id (check "
        "ownership of your API), not both. Call it once for each.",
    )


def hints_for(reason: str | None, method: str | None) -> list[str]:
    """Plain-language next steps for a failed check's reason.

    Each method gets the first hint that matches it; a ``method`` filter drops the other
    methods' hints.
    """
    hints: list[str] = []
    if not reason or not reason.strip():
        return hints
    covered: set[str] = set()
    for entry in REASON_HINTS:
        if entry["contains"] not in reason:
            continue
        entry_method = entry["method"]
        if entry_method is not None:
            if (method is not None and entry_method != method) or entry_method in covered:
                continue
            covered.add(entry_method)
        if entry["hint"] not in hints:
            hints.append(entry["hint"])
    hints.append(REASON_HINT_FALLBACK)
    return hints


def methods_from(instructions: Any) -> list[dict[str, Any]]:
    """The server's per-method instructions, each with what the method proves.

    Falls back to the static table when the server sent none, so the agent still learns
    what each method proves.
    """
    raw = instructions.get("methods") if isinstance(instructions, dict) else None
    shown: list[dict[str, Any]] = []
    for source in raw if isinstance(raw, list) else []:
        if not isinstance(source, dict):
            continue
        name = source.get("method")
        instruction: dict[str, Any] = {"method": name}
        info = next((m for m in METHODS if m["method"] == name), None)
        if info:
            instruction["proves"] = info["proves"]
            instruction["chooseWhen"] = info["chooseWhen"]
        for field in _INSTRUCTION_FIELDS:
            value = source.get(field)
            if isinstance(value, str):
                instruction[field] = value
        shown.append(instruction)
    if not shown:
        shown = [dict(m) for m in METHODS]
    return shown


def _state_payload(state: dict[str, Any], slug: str, method: str | None) -> dict[str, Any]:
    payload: dict[str, Any] = {"success": True}
    for field in _STATE_FIELDS:
        if field in state:
            payload[field] = state[field]
    payload.setdefault("proxyId", slug)
    payload["about"] = _ABOUT

    instructions = state.get("instructions")
    every = methods_from(instructions)
    shown = every if method is None else [m for m in every if m.get("method") == method]
    if method is not None and not shown:
        payload["methodNote"] = (
            f"{method} is not available for this proxy's target; every available method is shown."
        )
        shown = every
    payload["methods"] = shown
    if isinstance(instructions, dict) and isinstance(instructions.get("tokenPlaceholder"), str):
        payload["tokenPlaceholder"] = instructions["tokenPlaceholder"]
    return payload


def _next_steps(state: dict[str, Any], slug: str, method: str | None, attempts: int) -> list[str]:
    steps: list[str] = []
    verified_by = state.get("method")

    if state.get("pending") is True:
        retry = _int(state.get("retryAfterSeconds")) or MAX_RETRY_AFTER_SECONDS
        steps.append(
            f"The check ran past the server's time limit {attempts} times and recorded "
            f"nothing, so it does not count as a failure. Call verify again in {retry} seconds."
        )
        steps.extend(h for h in hints_for(state.get("reason"), method) if h != REASON_HINT_FALLBACK)
        steps.append(
            "If the target is slow, a dns-txt record does not depend on your server's response "
            "time."
        )
        return steps

    if state.get("verified") is True:
        info = next((m for m in METHODS if m["method"] == verified_by), None)
        if verified_by == "grandfathered":
            steps.append(
                "This proxy was listed before ownership verification existed, so it stays "
                "verified (grandfathered) and the daily re-check skips it. To replace that with "
                "a real method, call verification_token, publish the token, and call verify."
            )
        elif verified_by == "platform":
            steps.append(
                "Lightning Enable vouches for this proxy (platform); there is nothing to publish."
            )
        else:
            steps.append(
                f"Verified by {verified_by}."
                if info is None
                else f"Verified by {verified_by}, which proves {info['proves']}."
            )
            steps.append(
                "Leave the token in place: verified proxies are re-checked daily, and three "
                "failed re-checks in a row make the proxy unverified and unlisted."
            )
        steps.append(
            "It is listed publicly."
            if state.get("listed") is True
            else f"Listing is not on yet. To list it, call l402_producer action=publish proxy_id={slug}."
        )
        return steps

    if state.get("tokenIssued") is False:
        return [f"No token exists yet. Call l402_producer action=verification_token proxy_id={slug}."]

    steps.extend(hints_for(state.get("reason"), method))
    return steps or [REASON_HINT_FALLBACK]


def _error(result: dict[str, Any], slug: str, api_client: "LightningEnableApiClient") -> str:
    """Map a failed verification call to an error code an agent can branch on."""
    status = result.get("httpStatus")
    api_message = result.get("error")
    steps: list[str] | None = None

    if status is None:
        code = UPSTREAM_ERROR
        message = f"The Lightning Enable API could not be reached: {api_message}"
    elif status in (401, 403):
        code = UNAUTHORIZED
        message = (
            "The Lightning Enable API refused the merchant API key. Check "
            "LIGHTNING_ENABLE_API_KEY."
        )
    elif status == 404 and "proxy not found" in str(api_message or "").lower():
        code = PROXY_NOT_FOUND
        message = (
            f"This account has no proxy {slug}. Use the proxy id create_proxy or sell_url returned."
        )
    elif status == 404:
        code = FEATURE_NOT_ENABLED
        message = (
            f"The Lightning Enable API at {api_client.base_url} answered 404 without naming the "
            "proxy, so it has no ownership-verification routes yet. Try again after it is "
            "updated, or check that LIGHTNING_ENABLE_API_URL points at the right server."
        )
    elif status == 409 and result.get("errorCode") == TOKEN_REQUIRED:
        code = TOKEN_REQUIRED
        message = f"Proxy {slug} has no verification token yet."
        steps = [
            f"Call l402_producer action=verification_token proxy_id={slug}, publish the token "
            "by one method, then call verify again."
        ]
    elif status == 429:
        code = RATE_LIMITED
        wait = result.get("retryAfterSeconds")
        message = "The verification rate limit for this API key was reached." + (
            f" Wait {wait} seconds and try again." if wait is not None else " Wait a minute and try again."
        )
    elif status >= 500:
        code = UPSTREAM_ERROR
        message = f"The Lightning Enable API failed ({status}). Try again shortly."
    else:
        code = REQUEST_REJECTED
        message = f"The Lightning Enable API rejected the request ({status})."

    payload: dict[str, Any] = {
        "success": False,
        "errorCode": code,
        "error": message,
        "retryable": ERROR_CODES[code],
    }
    if api_message and str(api_message).strip() and api_message != message:
        payload["apiError"] = api_message
    if result.get("errorType"):
        payload["errorType"] = result["errorType"]
    if status is not None:
        payload["httpStatus"] = status
    if result.get("retryAfterSeconds") is not None:
        payload["retryAfterSeconds"] = result["retryAfterSeconds"]
    if steps is not None:
        payload["nextSteps"] = steps
    return json.dumps(payload, indent=2)


def _gate(
    api_client: "LightningEnableApiClient | None", proxy_id: Any, method: Any
) -> tuple[str | None, str, str | None]:
    if api_client is None:
        return _fail(UPSTREAM_ERROR, "Lightning Enable API service not available"), "", None
    if not api_client.is_configured:
        from ..lightning_enable_api import PRODUCER_API_KEY_REQUIRED

        return json.dumps({"success": False, "error": PRODUCER_API_KEY_REQUIRED}, indent=2), "", None
    if not proxy_id or not str(proxy_id).strip():
        return (
            _fail(
                PROXY_ID_REQUIRED,
                "proxy_id is required: the id create_proxy or sell_url returned for the proxy "
                "whose API you own.",
            ),
            "",
            None,
        )
    normalized = normalize_method(method)
    if normalized == "":
        return (
            _fail(
                INVALID_METHOD,
                f"method must be one of: {', '.join(LIVE_METHODS)}. It only filters the "
                "instructions shown; omit it to see all three.",
            ),
            "",
            None,
        )
    return None, str(proxy_id).strip(), normalized


async def verify_ownership(
    proxy_id: str | None = None,
    method: str | None = None,
    api_client: "LightningEnableApiClient | None" = None,
    sleep: Sleep | None = None,
    config_path: str | None = None,
) -> str:
    """Run the ownership check, polling ``pending`` up to ``MAX_PENDING_ATTEMPTS`` checks.

    Each wait honors ``retryAfterSeconds`` capped at ``MAX_RETRY_AFTER_SECONDS``.
    """
    error, slug, normalized = _gate(api_client, proxy_id, method)
    if error:
        return error
    assert api_client is not None  # noqa: S101 — narrowed by _gate
    sleep = sleep or asyncio.sleep

    try:
        attempts = 0
        while True:
            attempts += 1
            result = await api_client.verify_proxy_ownership(slug)
            if not result.get("success"):
                return _error(result, slug, api_client)
            state = result.get("data") or {}
            if state.get("pending") is not True or attempts >= MAX_PENDING_ATTEMPTS:
                break
            wait = _int(state.get("retryAfterSeconds")) or result.get("retryAfterSeconds") or 1
            await sleep(max(1, min(int(wait), MAX_RETRY_AFTER_SECONDS)))

        payload = _state_payload(state, slug, normalized)
        payload["attempts"] = attempts
        steps = _next_steps(state, slug, normalized, attempts)
        stored = load_token(config_path, slug) if state.get("verified") is not True else None
        if stored is not None:
            # Use the token already issued (by sell_url or verification_token) rather than
            # sending the agent to verification_token, which would replace it.
            payload["storedToken"] = stored
            steps.insert(
                0,
                f"Publish the token stored in {stored['path']} (token above); you do not need "
                "a new one. A new token from verification_token would replace it.",
            )
        payload["nextSteps"] = steps
        return json.dumps(payload, indent=2)
    except asyncio.CancelledError:
        raise
    except Exception as e:  # noqa: BLE001 — a tool must never raise at the agent
        return _fail(
            UPSTREAM_ERROR, f"The verification check could not be completed: {sanitize_error(str(e))}"
        )


async def verification_token(
    proxy_id: str | None = None,
    method: str | None = None,
    reissue: bool = False,
    api_client: "LightningEnableApiClient | None" = None,
    config_path: str | None = None,
) -> str:
    """Issue the verification token and return it once, with the instructions to publish it.

    Reads the state first: re-issuing replaces the token and makes a proxy verified by a live
    method unverified and unlisted, so that needs ``reissue``.
    """
    error, slug, normalized = _gate(api_client, proxy_id, method)
    if error:
        return error
    assert api_client is not None  # noqa: S101 — narrowed by _gate

    try:
        before = await api_client.get_proxy_verification(slug)
        if not before.get("success"):
            return _error(before, slug, api_client)
        prior = before.get("data") or {}
        prior_method = prior.get("method")
        live_verified = prior.get("verified") is True and prior_method in LIVE_METHODS
        if live_verified and not reissue:
            listed = " and listed" if prior.get("listed") is True else ""
            return _fail(
                REISSUE_REQUIRED,
                f"Proxy {slug} is already verified by {prior_method}{listed}, so it needs no new "
                "token. A new token replaces the published one and makes the proxy unverified "
                "and unlisted until you publish the new token and verify again. Call "
                "verification_token with reissue=true only to rotate it.",
            )

        issued = await api_client.issue_proxy_verification_token(slug)
        if not issued.get("success"):
            return _error(issued, slug, api_client)
        state = issued.get("data") or {}
        token = state.get("token")
        if not isinstance(token, str) or not token:
            return _fail(
                UPSTREAM_ERROR,
                "The server answered without a token. Call verification_token again to issue one.",
            )

        storage = store_token(config_path, slug, token, "verification_token")
        payload: dict[str, Any] = {
            "success": True,
            "proxyId": state.get("proxyId") or slug,
            "token": token,
            "tokenNote": (
                "This token is shown only this once here, and Lightning Enable keeps only a hash "
                "of it. " + storage_note(storage) + " Publish it by one method below, replacing "
                f"<token>, then call l402_producer action=verify proxy_id={slug}. Issuing another "
                "token replaces this one."
            ),
            "tokenStorage": storage,
        }
        for key, value in _state_payload(state, slug, normalized).items():
            payload.setdefault(key, value)

        warnings: list[str] = []
        if prior.get("tokenIssued") is True:
            warnings.append(
                "This replaced the earlier token, which no longer verifies. Publish this one in "
                "its place."
            )
        if live_verified:
            warnings.append(
                f"The proxy was verified by {prior_method}; it is now unverified and unlisted "
                "until verify succeeds with this token, and listing must then be turned on again "
                "with publish."
            )
        elif prior_method in ("grandfathered", "platform"):
            warnings.append(
                f"The proxy keeps its {prior_method} standing until a check with this token succeeds."
            )
        if warnings:
            payload["warnings"] = warnings

        payload["nextSteps"] = [
            "Pick one method from methods, publish the token there in place of <token>, then "
            f"call l402_producer action=verify proxy_id={slug}.",
            "Once verified, turn listing on with l402_producer action=publish.",
        ]
        return json.dumps(payload, indent=2)
    except asyncio.CancelledError:
        raise
    except Exception as e:  # noqa: BLE001 — a tool must never raise at the agent
        return _fail(
            UPSTREAM_ERROR,
            f"The verification token could not be issued: {sanitize_error(str(e))}",
        )
