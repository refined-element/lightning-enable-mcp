"""Ownership verification on ``l402_producer``: ``verify`` with ``proxy_id``, and
``verification_token``, against a mocked ``ProxyVerificationController``.

The method table, error codes, polling bounds and reason hints are pinned against
``shared/l402-producer-verify.json``. Mirrors
``dotnet/tests/LightningEnable.Mcp.Tests/Tools/ProducerVerifyTests.cs``.
"""

import copy
import json
import os
import re
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, patch

import httpx
import pytest

from lightning_enable_mcp.lightning_enable_api import LightningEnableApiClient
from lightning_enable_mcp.tools import producer_verify as pv
from lightning_enable_mcp.tools.producer_setup import publish
from lightning_enable_mcp.tools.publish_agent_capability import publish_agent_capability

API_BASE = "https://api.example.test"
PROXY_ID = "forecast-api-a1b2"
STATE_PATH = f"/api/proxy/{PROXY_ID}/verification"
TOKEN_PATH = f"/api/proxy/{PROXY_ID}/verification/token"
VERIFY_PATH = f"/api/proxy/{PROXY_ID}/verify"

# Shaped like the server's token: 43 url-safe characters. Fake, and named so the gitleaks
# generic-api-key rule does not read it as a credential.
FIXTURE_VERIFICATION_VALUE = "fixture-verification-value-0000000000000000"


def _find_shared(name: str) -> Path:
    for parent in Path(__file__).resolve().parents:
        candidate = parent / "shared" / name
        if candidate.exists():
            return candidate
    raise FileNotFoundError(f"shared/{name} not found above {__file__}")


FIXTURE = json.loads(_find_shared("l402-producer-verify.json").read_text(encoding="utf-8"))

INSTRUCTIONS = {
    "tokenPlaceholder": "<token>",
    "methods": [
        {
            "method": "well-known",
            "order": 1,
            "summary": "Serve this JSON.",
            "url": "https://api.example.com/.well-known/lightning-enable.json",
            "recordName": None,
            "recordType": None,
            "headerName": None,
            "body": '{"verify":"<token>"}',
            "value": "<token>",
        },
        {
            "method": "dns-txt",
            "order": 2,
            "summary": "Add a DNS TXT record.",
            "url": None,
            "recordName": "_lightning-enable.api.example.com",
            "recordType": "TXT",
            "headerName": None,
            "body": None,
            "value": "<token>",
        },
        {
            "method": "header",
            "order": 3,
            "summary": "Return the header.",
            "url": "https://api.example.com/v1/",
            "recordName": None,
            "recordType": None,
            "headerName": "X-Lightning-Enable-Verify",
            "body": None,
            "value": "<token>",
        },
    ],
    "tokenEndpoint": TOKEN_PATH,
    "verifyEndpoint": VERIFY_PATH,
    "note": "Publish the token by any one method.",
}


def state(
    verified=False,
    method=None,
    token_issued=True,
    listed=False,
    reason=None,
    pending=None,
    retry_after_seconds=None,
    token=None,
):
    return {
        "proxyId": PROXY_ID,
        "verified": verified,
        "method": method,
        "verifiedAt": "2026-11-08T18:02:11Z" if verified else None,
        "lastCheckedAt": "2026-11-08T18:02:11Z",
        "failures": 0 if verified else 1,
        "tokenIssued": token_issued,
        "token": token,
        "listed": listed,
        "nextCheckAt": "2026-11-09T04:00:00Z" if verified else None,
        "checkedAt": "2026-11-08T18:02:11Z",
        "reason": reason,
        "pending": pending,
        "retryAfterSeconds": retry_after_seconds,
        "instructions": copy.deepcopy(INSTRUCTIONS),
    }


class Reply:
    def __init__(self, status=200, body=None, retry_after=None, raw=None):
        self.status = status
        self.body = body
        self.retry_after = retry_after
        self.raw = raw


def ok(body, retry_after=None):
    return Reply(200, body, retry_after)


class ScriptedApi:
    """Answers each (method, path) from a queue; the last reply repeats. Unknown routes
    answer what a server without them does: a bare 404."""

    def __init__(self):
        self.routes: dict[tuple[str, str], list[Reply]] = {}
        self.requests: list[tuple[str, str]] = []
        self.raises: Exception | None = None

    def on(self, method, path, *replies):
        self.routes[(method, path)] = list(replies)
        return self

    def handler(self, request: httpx.Request) -> httpx.Response:
        key = (request.method, request.url.path)
        self.requests.append(key)
        if self.raises is not None:
            raise self.raises
        queue = self.routes.get(key)
        if not queue:
            return httpx.Response(404, content=b"")
        reply = queue.pop(0) if len(queue) > 1 else queue[0]
        headers = {"Retry-After": str(reply.retry_after)} if reply.retry_after is not None else {}
        if reply.raw is not None:
            return httpx.Response(reply.status, content=reply.raw.encode(), headers=headers)
        return httpx.Response(reply.status, json=reply.body, headers=headers)

    def count(self, method, path):
        return sum(1 for r in self.requests if r == (method, path))


def client_for(api: ScriptedApi) -> LightningEnableApiClient:
    env = {"LIGHTNING_ENABLE_API_URL": API_BASE, "LIGHTNING_ENABLE_API_KEY": "fixture-key-1"}
    with patch.dict(os.environ, env, clear=True):
        with patch("lightning_enable_mcp.lightning_enable_api.get_config_service") as cfg:
            cfg.return_value.configuration = MagicMock(lightning_enable_api_key=None)
            client = LightningEnableApiClient()
    client._client = httpx.AsyncClient(
        transport=httpx.MockTransport(api.handler), headers=dict(client._client.headers)
    )
    return client


async def verify(api, method=None, waits=None):
    waits = [] if waits is None else waits

    async def sleep(seconds):
        waits.append(seconds)

    return json.loads(
        await pv.verify_ownership(PROXY_ID, method, client_for(api), sleep=sleep)
    )


async def token(api, reissue=False, method=None):
    raw = await pv.verification_token(PROXY_ID, method, reissue, client_for(api))
    return raw, json.loads(raw)


@pytest.fixture(autouse=True)
def config_dir(tmp_path, monkeypatch):
    """Token files go to a temp config dir, never the real home directory."""
    config = tmp_path / "config.json"
    monkeypatch.setattr(
        "lightning_enable_mcp.tools.producer_sell_url._default_config_path", lambda: str(config)
    )
    return tmp_path


# ── Shared fixture parity ──────────────────────────────────────────────────


class TestFixtureParity:
    def test_tables_match_the_shared_fixture(self):
        assert FIXTURE["errorCodes"] == pv.ERROR_CODES
        assert FIXTURE["methods"] == pv.METHODS
        assert FIXTURE["liveMethods"] == pv.LIVE_METHODS
        assert FIXTURE["maxPendingAttempts"] == pv.MAX_PENDING_ATTEMPTS
        assert FIXTURE["maxRetryAfterSeconds"] == pv.MAX_RETRY_AFTER_SECONDS
        assert FIXTURE["reasonHints"] == pv.REASON_HINTS
        assert FIXTURE["reasonHintFallback"] == pv.REASON_HINT_FALLBACK
        steps = FIXTURE["storedTokenSteps"]
        assert steps["staleWhen"] == pv.STALE_WHEN
        assert steps["default"] == pv.STORED_TOKEN_STEP
        assert steps["stale"] == pv.STALE_STORED_TOKEN_STEP
        unproven = {k: v for k, v in FIXTURE["unprovenMethodSteps"].items() if k != "_comment"}
        assert unproven == pv.UNPROVEN_METHOD_STEPS
        from lightning_enable_mcp.tools.producer_sell_url import NO_VERIFICATION_TOKEN_NOTE

        assert FIXTURE["sellUrlVerification"]["noTokenNote"] == NO_VERIFICATION_TOKEN_NOTE

    def test_exact_server_reasons_select_their_hint(self):
        for reason, hint in FIXTURE["serverReasonSamples"].items():
            if reason == "_comment":
                continue
            assert pv.hints_for(reason, None)[0] == hint, reason
        store = FIXTURE["tokenStore"]
        assert store["filePrefix"] == pv.TOKEN_FILE_PREFIX
        for example in store["examples"]:
            path = pv.token_file_path("/tmp/x/config.json", example["proxyId"])
            assert path.name == example["fileName"]

    def test_stored_token_file_has_the_fixture_fields(self, config_dir):
        storage = pv.store_token(None, PROXY_ID, FIXTURE_VERIFICATION_VALUE, "verification_token")
        assert storage["written"] is True
        data = json.loads(Path(storage["path"]).read_text(encoding="utf-8"))
        assert sorted(data) == sorted(FIXTURE["tokenStore"]["fields"])
        assert Path(storage["path"]).parent == config_dir

    def test_schema_tags_the_verification_arguments_and_the_description_is_findable(self):
        from lightning_enable_mcp.tools.consolidated import L402_PRODUCER_TOOL

        properties = L402_PRODUCER_TOOL.inputSchema["properties"]
        for argument in FIXTURE["verifyArguments"]:
            assert "verify" in properties[argument]["description"], argument
        for argument in FIXTURE["tokenArguments"]:
            description = properties[argument]["description"]
            assert "verification_token" in description or "verify*" in description, argument

        action = properties["action"]["description"]
        assert "verification_token" in action and "proxy_id" in action

        text = L402_PRODUCER_TOOL.description
        for word in ("ownership", "domain", "listed"):
            assert word in text
        assert not re.search(r"\d+\s*(sats?|USD|\$)", text)
        for word in ("non-custodial", "self-custody", "trustless", "decentralized"):
            assert word not in text.lower()
            assert word not in action.lower()


# ── verify ─────────────────────────────────────────────────────────────────


@pytest.mark.asyncio
class TestVerify:
    async def test_returns_the_state_what_each_method_proves_and_points_at_publish(self):
        api = ScriptedApi().on("POST", VERIFY_PATH, ok(state(verified=True, method="dns-txt")))

        result = await verify(api)

        assert result["success"] is True
        assert result["verified"] is True
        assert result["method"] == "dns-txt"
        assert result["attempts"] == 1
        assert "token" not in result
        assert [m["proves"] for m in result["methods"]] == [m["proves"] for m in pv.METHODS]
        steps = " ".join(result["nextSteps"])
        assert "DNS zone" in steps
        assert f"action=publish proxy_id={PROXY_ID}" in steps
        assert api.requests == [("POST", VERIFY_PATH)]

    async def test_when_already_listed_says_so_instead_of_pointing_at_publish(self):
        api = ScriptedApi().on(
            "POST", VERIFY_PATH, ok(state(verified=True, method="header", listed=True))
        )

        steps = " ".join((await verify(api))["nextSteps"])

        assert "listed publicly" in steps
        assert "action=publish" not in steps

    async def test_method_only_filters_the_instructions_shown(self):
        api = ScriptedApi().on("POST", VERIFY_PATH, ok(state()))

        result = await verify(api, method="DNS_TXT")

        assert len(result["methods"]) == 1
        assert result["methods"][0]["recordName"] == "_lightning-enable.api.example.com"
        assert result["methods"][0]["value"] == "<token>"

    async def test_polls_pending_until_verified_honoring_retry_after_capped_at_ten(self):
        api = ScriptedApi().on(
            "POST",
            VERIFY_PATH,
            ok(state(pending=True, retry_after_seconds=30, reason="the check did not finish within 15s"), 30),
            ok(state(pending=True, retry_after_seconds=4), 4),
            ok(state(verified=True, method="well-known")),
        )
        waits: list[float] = []

        result = await verify(api, waits=waits)

        assert result["verified"] is True
        assert result["attempts"] == 3
        assert waits == [10, 4]
        assert api.count("POST", VERIFY_PATH) == 3

    async def test_stops_after_four_pending_checks_and_says_when_to_call_again(self):
        api = ScriptedApi().on(
            "POST", VERIFY_PATH, ok(state(pending=True, retry_after_seconds=3), 3)
        )
        waits: list[float] = []

        result = await verify(api, waits=waits)

        assert api.count("POST", VERIFY_PATH) == pv.MAX_PENDING_ATTEMPTS
        assert waits == [3] * (pv.MAX_PENDING_ATTEMPTS - 1)
        assert result["pending"] is True
        assert "Call verify again in 3 seconds" in " ".join(result["nextSteps"])

    async def test_failed_check_turns_the_reason_into_next_steps(self):
        reason = (
            "well-known: /.well-known/lightning-enable.json answered HTTP 404; dns-txt: no TXT "
            "record at _lightning-enable.api.example.com; header: HEAD answered HTTP 401 without "
            "X-Lightning-Enable-Verify; GET answered HTTP 401 without X-Lightning-Enable-Verify"
        )
        api = ScriptedApi().on("POST", VERIFY_PATH, ok(state(reason=reason)))

        every = (await verify(api))["nextSteps"]
        assert len(every) == 4
        assert every[-1] == pv.REASON_HINT_FALLBACK

        dns_only = (await verify(api, method="dns-txt"))["nextSteps"]
        assert len(dns_only) == 2
        assert "dig +short TXT" in dns_only[0]


def test_reason_hints_give_each_method_its_narrowest_match():
    hints = pv.hints_for(
        "dns-txt: no TXT record at _lightning-enable.api.example.com contains this proxy's "
        "current token",
        None,
    )
    assert len(hints) == 2
    assert "Remove quotes" in hints[0]


# ── verification_token ─────────────────────────────────────────────────────


@pytest.mark.asyncio
class TestToken:
    async def test_is_returned_once_with_the_storage_note_and_placeholder_instructions(self):
        api = (
            ScriptedApi()
            .on("GET", STATE_PATH, ok(state(token_issued=False)))
            .on("POST", TOKEN_PATH, ok(state(token=FIXTURE_VERIFICATION_VALUE)))
        )

        raw, result = await token(api)

        assert result["token"] == FIXTURE_VERIFICATION_VALUE
        assert raw.count(FIXTURE_VERIFICATION_VALUE) == 1
        assert "shown only this once" in result["tokenNote"]
        assert all(m["value"] == "<token>" for m in result["methods"])
        assert "warnings" not in result
        assert api.requests == [("GET", STATE_PATH), ("POST", TOKEN_PATH)]
        assert result["tokenStorage"]["written"] is True
        assert result["tokenStorage"]["path"] in result["tokenNote"]
        assert pv.load_token(None, PROXY_ID)["token"] == FIXTURE_VERIFICATION_VALUE

    async def test_verify_uses_a_stored_token_before_pointing_at_verification_token(self):
        pv.store_token(None, PROXY_ID, FIXTURE_VERIFICATION_VALUE, "sell_url")
        api = ScriptedApi().on("POST", VERIFY_PATH, ok(state(reason="dns-txt: no TXT record at x")))

        result = await verify(api)

        assert result["storedToken"]["token"] == FIXTURE_VERIFICATION_VALUE
        assert result["storedToken"]["source"] == "sell_url"
        assert result["nextSteps"][0] == pv.STORED_TOKEN_STEP.replace(
            "{path}", pv.load_token(None, PROXY_ID)["path"]
        )

    async def test_verify_when_the_reason_says_not_current_calls_the_stored_token_stale(self):
        pv.store_token(None, PROXY_ID, FIXTURE_VERIFICATION_VALUE, "sell_url")
        api = ScriptedApi().on(
            "POST",
            VERIFY_PATH,
            ok(state(reason="well-known: \"verify\" does not contain this proxy's current token")),
        )

        first = (await verify(api))["nextSteps"][0]

        assert "stale" in first
        assert f"action=verification_token proxy_id={PROXY_ID}" in first
        assert "Publish it unless" not in first

    async def test_grandfathered_proxy_reported_unverified_leads_with_its_standing(self):
        api = ScriptedApi().on(
            "POST", VERIFY_PATH, ok(state(verified=False, method="grandfathered", listed=True))
        )

        steps = (await verify(api))["nextSteps"]

        assert steps[0] == pv.UNPROVEN_METHOD_STEPS["grandfathered"]

    async def test_verify_does_not_echo_a_stored_token_once_verified(self):
        pv.store_token(None, PROXY_ID, FIXTURE_VERIFICATION_VALUE, "sell_url")
        api = ScriptedApi().on("POST", VERIFY_PATH, ok(state(verified=True, method="header")))

        raw = await pv.verify_ownership(PROXY_ID, None, client_for(api))

        assert FIXTURE_VERIFICATION_VALUE not in raw

    async def test_refuses_to_unverify_a_live_verification_unless_reissue(self):
        api = (
            ScriptedApi()
            .on("GET", STATE_PATH, ok(state(verified=True, method="well-known", listed=True)))
            .on("POST", TOKEN_PATH, ok(state(token=FIXTURE_VERIFICATION_VALUE)))
        )

        _, refused = await token(api)
        assert refused["errorCode"] == pv.REISSUE_REQUIRED
        assert "unlisted" in refused["error"] and "reissue=true" in refused["error"]
        assert api.count("POST", TOKEN_PATH) == 0

        _, rotated = await token(api, reissue=True)
        assert rotated["token"] == FIXTURE_VERIFICATION_VALUE
        assert "unverified and unlisted" in " ".join(rotated["warnings"])

    async def test_for_a_grandfathered_proxy_needs_no_reissue(self):
        api = (
            ScriptedApi()
            .on("GET", STATE_PATH, ok(state(verified=True, method="grandfathered", listed=True)))
            .on(
                "POST",
                TOKEN_PATH,
                ok(state(verified=True, method="grandfathered", token=FIXTURE_VERIFICATION_VALUE)),
            )
        )

        _, result = await token(api)

        assert result["success"] is True
        assert "grandfathered standing" in " ".join(result["warnings"])


# ── Errors ─────────────────────────────────────────────────────────────────

TOKEN_REQUIRED_BODY = {
    "type": "https://lightningenable.com/problems/verification_token_required",
    "title": "No verification token",
    "status": 409,
    "detail": "This proxy has no verification token yet.",
    "error": "verification_token_required",
    "message": "This proxy has no verification token yet.",
    "tokenEndpoint": TOKEN_PATH,
}

ERROR_CASES = [
    (401, {"error": "Merchant authentication required"}, None, None, pv.UNAUTHORIZED),
    (404, {"error": "Proxy not found"}, None, None, pv.PROXY_NOT_FOUND),
    (404, None, "", None, pv.FEATURE_NOT_ENABLED),
    (409, TOKEN_REQUIRED_BODY, None, None, pv.TOKEN_REQUIRED),
    (
        429,
        {"error": "Too many requests", "message": "Rate limit exceeded.", "retryAfter": 42.0},
        None,
        42,
        pv.RATE_LIMITED,
    ),
    (500, {}, None, None, pv.UPSTREAM_ERROR),
    (400, {"error": "bad"}, None, None, pv.REQUEST_REJECTED),
]


@pytest.mark.asyncio
@pytest.mark.parametrize("status,body,raw,retry_after,code", ERROR_CASES)
async def test_verify_maps_each_server_error_to_its_code(status, body, raw, retry_after, code):
    api = ScriptedApi().on("POST", VERIFY_PATH, Reply(status, body, retry_after, raw))

    result = await verify(api)

    assert result["success"] is False
    assert result["errorCode"] == code
    assert result["retryable"] is pv.ERROR_CODES[code]
    assert result["httpStatus"] == status
    if retry_after is not None:
        assert result["retryAfterSeconds"] == retry_after
    if code == pv.TOKEN_REQUIRED:
        assert "action=verification_token" in " ".join(result["nextSteps"])
    assert api.count("POST", VERIFY_PATH) == 1, "an error is never polled"


@pytest.mark.asyncio
async def test_server_without_the_routes_is_reported_as_feature_not_enabled_for_both_actions():
    api = ScriptedApi()

    assert (await verify(api))["errorCode"] == pv.FEATURE_NOT_ENABLED
    _, result = await token(api)
    assert result["errorCode"] == pv.FEATURE_NOT_ENABLED
    assert API_BASE in result["error"]
    assert api.count("POST", TOKEN_PATH) == 0


@pytest.mark.asyncio
async def test_transport_failure_is_a_retryable_upstream_error():
    api = ScriptedApi()
    api.raises = httpx.ConnectError("connection refused")

    result = await verify(api)

    assert result["errorCode"] == pv.UPSTREAM_ERROR
    assert result["retryable"] is True


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "proxy_id,method,code",
    [(None, None, "proxy_id_required"), (PROXY_ID, "smoke-signal", "invalid_method")],
)
async def test_local_validation_happens_before_any_request(proxy_id, method, code):
    api = ScriptedApi()

    verified = json.loads(await pv.verify_ownership(proxy_id, method, client_for(api)))
    issued = json.loads(await pv.verification_token(proxy_id, method, False, client_for(api)))

    assert verified["errorCode"] == code
    assert issued["errorCode"] == code
    assert api.requests == []


# ── Dispatch ───────────────────────────────────────────────────────────────


async def _call(server, arguments):
    from mcp.types import CallToolRequest, CallToolRequestParams

    handler = server.server.request_handlers[CallToolRequest]
    result = await handler(
        CallToolRequest(
            method="tools/call",
            params=CallToolRequestParams(name="l402_producer", arguments=arguments),
        )
    )
    return result.root.content[0].text


@pytest.mark.asyncio
async def test_verify_action_with_proxy_id_checks_ownership_and_with_both_is_refused():
    from lightning_enable_mcp.server import LightningEnableServer

    server = LightningEnableServer()
    server.wallet = MagicMock()
    server.l402_client = MagicMock()
    server.api_client = MagicMock()
    with patch(
        "lightning_enable_mcp.server.verify_ownership",
        new=AsyncMock(return_value='{"success": true}'),
    ) as ownership, patch(
        "lightning_enable_mcp.server.verify_l402_payment",
        new=AsyncMock(return_value='{"success": true}'),
    ) as payment, patch(
        "lightning_enable_mcp.server.verification_token",
        new=AsyncMock(return_value='{"success": true}'),
    ) as issue:
        await _call(server, {"action": "verify", "proxy_id": PROXY_ID, "method": "dns-txt"})
        both = await _call(
            server, {"action": "verify", "proxy_id": PROXY_ID, "macaroon": "AgEL", "preimage": "00"}
        )
        await _call(server, {"action": "verify", "macaroon": "AgEL", "preimage": "00"})
        await _call(server, {"action": "verification_token", "proxy_id": PROXY_ID, "reissue": True})

    assert ownership.await_args.kwargs["proxy_id"] == PROXY_ID
    assert ownership.await_args.kwargs["method"] == "dns-txt"
    assert json.loads(both)["errorCode"] == pv.AMBIGUOUS_VERIFY
    payment.assert_awaited_once()
    assert issue.await_args.kwargs["reissue"] is True


ROUTING_CASES = [(tuple(c["args"]), c["route"]) for c in FIXTURE["verifyRouting"]["cases"]]


@pytest.mark.asyncio
@pytest.mark.parametrize("args,route", ROUTING_CASES)
async def test_verify_routing_matches_the_shared_table(args, route):
    from lightning_enable_mcp.server import LightningEnableServer

    server = LightningEnableServer()
    server.wallet = MagicMock()
    server.l402_client = MagicMock()
    server.api_client = MagicMock()
    values = {"proxy_id": PROXY_ID, "macaroon": "AgEL", "preimage": "00"}
    arguments = {"action": "verify", **{name: values[name] for name in args}}
    with patch(
        "lightning_enable_mcp.server.verify_ownership",
        new=AsyncMock(return_value='{"success": true}'),
    ) as ownership, patch(
        "lightning_enable_mcp.server.verify_l402_payment",
        new=AsyncMock(return_value='{"success": true}'),
    ) as payment:
        text = await _call(server, arguments)

    result = json.loads(text)
    if result.get("errorCode") == pv.AMBIGUOUS_VERIFY:
        actual = "ambiguous"
    elif ownership.await_count:
        actual = "ownership"
    elif payment.await_count:
        actual = "payment"
    else:
        actual = "none"
    assert actual == route, args
    assert ownership.await_count + payment.await_count == (0 if route == "ambiguous" else 1)


@pytest.mark.asyncio
async def test_publish_refused_for_ownership_points_at_verify():
    api = ScriptedApi().on(
        "PUT",
        f"/api/proxy/{PROXY_ID}/manifest/settings",
        Reply(
            422,
            {
                "error": "ownership_verification_required",
                "message": "Verify ownership first.",
                "verify_endpoint": VERIFY_PATH,
            },
        ),
    )

    result = json.loads(await publish(PROXY_ID, api_client=client_for(api)))

    assert result["errorCode"] == "ownership_verification_required"
    assert f"action=verify proxy_id={PROXY_ID}" in result["hint"]


# ── publish_agent_capability ───────────────────────────────────────────────

UNPUBLISHED_BODY = {
    "serviceId": "forecast",
    "proxyId": PROXY_ID,
    "l402Endpoint": f"{API_BASE}/l402/proxy/{PROXY_ID}",
    "categories": ["weather"],
    "priceSats": 10,
    "published": False,
    "reason": "ownership_verification_required",
    "verifyEndpoint": VERIFY_PATH,
    "verification": {"token": FIXTURE_VERIFICATION_VALUE, "instructions": INSTRUCTIONS},
}


@pytest.mark.asyncio
async def test_capability_not_published_says_so_returns_the_token_once_and_points_at_verify():
    api = ScriptedApi().on("POST", "/api/agents/capabilities", Reply(201, UNPUBLISHED_BODY))

    raw = await publish_agent_capability(
        "forecast",
        ["weather"],
        "Forecasts",
        10,
        target_url="https://api.example.com/v1",
        api_client=client_for(api),
    )
    result = json.loads(raw)

    assert result["published"] is False
    assert result["reason"] == "ownership_verification_required"
    assert result["proxyId"] == PROXY_ID
    assert "NOT published" in result["message"]
    assert "successfully" not in result["message"]
    assert raw.count(FIXTURE_VERIFICATION_VALUE) == 1
    assert "shown only this once" in result["verification"]["tokenNote"]
    assert len(result["verification"]["methods"]) == 3
    assert result["verification"]["tokenStorage"]["written"] is True
    assert pv.load_token(None, PROXY_ID)["source"] == "publish_agent_capability"
    assert f'l402_producer(action="verify", proxy_id="{PROXY_ID}")' in " ".join(
        result["nextSteps"]
    )


@pytest.mark.asyncio
async def test_capability_explicitly_published_keeps_the_old_success_path():
    api = ScriptedApi().on(
        "POST",
        "/api/agents/capabilities",
        Reply(
            201,
            {
                "eventId": "evt1",
                "l402Endpoint": f"{API_BASE}/l402/proxy/{PROXY_ID}",
                "published": True,
            },
        ),
    )

    result = json.loads(
        await publish_agent_capability(
            "forecast", ["weather"], "Forecasts", 10, api_client=client_for(api)
        )
    )

    assert result["eventId"] == "evt1"
    assert "published successfully" in result["message"]


@pytest.mark.asyncio
async def test_capability_published_keeps_the_old_success_path():
    api = ScriptedApi().on(
        "POST",
        "/api/agents/capabilities",
        Reply(201, {"eventId": "evt1", "l402Endpoint": f"{API_BASE}/l402/proxy/{PROXY_ID}"}),
    )

    result = json.loads(
        await publish_agent_capability(
            "forecast", ["weather"], "Forecasts", 10, api_client=client_for(api)
        )
    )

    assert result["eventId"] == "evt1"
    assert "published successfully" in result["message"]
