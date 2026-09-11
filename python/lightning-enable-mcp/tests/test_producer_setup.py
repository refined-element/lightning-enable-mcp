"""The seller-side ``l402_producer`` actions: from an API key to a paid endpoint.

``create`` / ``verify`` mint and check ONE challenge. These six actions are the setup
that has to happen before either is worth calling — pointing the merchant's payouts at
their own wallet, registering an API, pricing its endpoints, listing it, and reading
back what was minted — and every one of them used to require raw REST.

What these tests hold:

1. **Request shape.** Each action hits the exact route and body the Lightning Enable API
   defines (``F:\\lightning-enable-simplify`` ``MerchantSettingsController`` /
   ``ProxyManagementController`` / ``ManifestController``). A stub transport records every
   request, so a renamed field fails here rather than in production.
2. **The NWC connection string never comes back.** It authorises live calls against the
   merchant's wallet. ``configure_receive`` reports ``<set>`` and nothing else — including
   when the API's own error body quotes it.
3. **The MCP wallet is only reused when it IS an NWC wallet.** An LND / Strike / OpenNode
   server refuses with a message naming its wallet, rather than sending something that
   cannot be a receiving connection.
4. **Errors are surfaced, not swallowed.** The API's RFC 9457 members (``type``,
   ``detail``) and its legacy ``error`` / ``message`` both reach the agent.
"""

import json
import os
from unittest.mock import MagicMock, patch

import httpx
import pytest

from lightning_enable_mcp.lightning_enable_api import LightningEnableApiClient
from lightning_enable_mcp.tools.producer_setup import (
    add_endpoint,
    configure_receive,
    create_proxy,
    list_challenges,
    producer_status,
    publish,
    resolve_mcp_nwc_connection,
)

API_BASE = "https://api.example.test"

# A syntactically valid NWC string with obviously fake key material. Named so it cannot
# trip the gitleaks generic-api-key rule.
FIXTURE_NWC = (
    "nostr+walletconnect://"
    + "ab" * 32
    + "?relay=wss://relay.example.test&secret="
    + "cd" * 32
)


class StubApi:
    """Records every request and answers from a ``(method, path)`` table."""

    def __init__(self, routes: dict[tuple[str, str], tuple[int, dict]]):
        self.routes = routes
        self.requests: list[httpx.Request] = []

    def handler(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        key = (request.method, request.url.path)
        if key not in self.routes:
            return httpx.Response(404, json={"error": f"stub has no route for {key}"})
        status, body = self.routes[key]
        return httpx.Response(status, json=body)

    def request_for(self, method: str, path: str) -> httpx.Request:
        for request in self.requests:
            if request.method == method and request.url.path == path:
                return request
        raise AssertionError(
            f"{method} {path} was never called; saw "
            f"{[(r.method, r.url.path) for r in self.requests]}"
        )

    def body_of(self, method: str, path: str) -> dict:
        return json.loads(self.request_for(method, path).content)

    @property
    def paths(self) -> list[tuple[str, str]]:
        return [(r.method, r.url.path) for r in self.requests]


def make_client(routes: dict[tuple[str, str], tuple[int, dict]], api_key="fixture-key-1"):
    """A client whose HTTP layer is the stub, keyed like the real API."""
    stub = StubApi(routes)
    env = {"LIGHTNING_ENABLE_API_URL": API_BASE}
    if api_key:
        env["LIGHTNING_ENABLE_API_KEY"] = api_key
    with patch.dict(os.environ, env, clear=True):
        with patch("lightning_enable_mcp.lightning_enable_api.get_config_service") as cfg:
            cfg.return_value.configuration = MagicMock(lightning_enable_api_key=None)
            client = LightningEnableApiClient()
    client._client = httpx.AsyncClient(
        transport=httpx.MockTransport(stub.handler),
        headers=dict(client._client.headers),
    )
    return client, stub


def unconfigured_client():
    with patch.dict(os.environ, {}, clear=True):
        with patch("lightning_enable_mcp.lightning_enable_api.get_config_service") as cfg:
            cfg.return_value.configuration = MagicMock(lightning_enable_api_key=None)
            return LightningEnableApiClient()


def wallet_config(**wallets):
    """A config service whose wallets carry only what the test names."""
    defaults = {
        "nwc_connection_string": None,
        "strike_api_key": None,
        "opennode_api_key": None,
        "lnd_rest_host": None,
        "lnd_macaroon_hex": None,
        "priority": None,
    }
    defaults.update(wallets)
    service = MagicMock()
    service.config_file_path = "/tmp/fixture-config.json"
    service.configuration.wallets = MagicMock(**defaults)
    return service


# ══ configure_receive ═════════════════════════════════════════════════════════


class TestConfigureReceive:
    @pytest.mark.asyncio
    async def test_saves_the_connection_then_switches_the_provider(self):
        client, stub = make_client(
            {
                ("PUT", "/api/merchant/nwc-connection"): (200, {"success": True, "message": "saved"}),
                ("PUT", "/api/merchant/payment-provider"): (200, {"success": True, "message": "ok"}),
            }
        )
        result = json.loads(
            await configure_receive(nwc_connection_string=FIXTURE_NWC, api_client=client)
        )

        assert result["success"] is True
        assert stub.paths == [
            ("PUT", "/api/merchant/nwc-connection"),
            ("PUT", "/api/merchant/payment-provider"),
        ]
        assert stub.body_of("PUT", "/api/merchant/nwc-connection") == {
            "nwcConnectionString": FIXTURE_NWC
        }
        assert stub.body_of("PUT", "/api/merchant/payment-provider") == {"provider": "nwc"}
        assert result["provider"] == "nwc"
        assert result["nwcConnectionString"] == "<set>"

    @pytest.mark.asyncio
    async def test_never_echoes_the_connection_string(self):
        client, _ = make_client(
            {
                ("PUT", "/api/merchant/nwc-connection"): (200, {"success": True}),
                ("PUT", "/api/merchant/payment-provider"): (200, {"success": True}),
            }
        )
        raw = await configure_receive(nwc_connection_string=FIXTURE_NWC, api_client=client)

        assert FIXTURE_NWC not in raw
        assert "cd" * 32 not in raw

    @pytest.mark.asyncio
    async def test_scrubs_the_string_out_of_an_api_error_body(self):
        """Defence in depth: the API promises not to quote it, we make sure anyway."""
        client, _ = make_client(
            {
                ("PUT", "/api/merchant/nwc-connection"): (
                    400,
                    {"error": f"could not parse {FIXTURE_NWC}"},
                ),
            }
        )
        raw = await configure_receive(nwc_connection_string=FIXTURE_NWC, api_client=client)

        assert FIXTURE_NWC not in raw
        assert json.loads(raw)["success"] is False

    @pytest.mark.asyncio
    async def test_does_not_switch_provider_when_the_save_fails(self):
        client, stub = make_client(
            {
                ("PUT", "/api/merchant/nwc-connection"): (
                    400,
                    {"error": "The 'secret' parameter must be 64 hex characters."},
                ),
                ("PUT", "/api/merchant/payment-provider"): (200, {"success": True}),
            }
        )
        result = json.loads(
            await configure_receive(nwc_connection_string=FIXTURE_NWC, api_client=client)
        )

        assert result["success"] is False
        assert "secret" in result["error"]
        assert ("PUT", "/api/merchant/payment-provider") not in stub.paths

    @pytest.mark.asyncio
    async def test_rejects_a_malformed_string_before_sending_it(self):
        client, stub = make_client({})
        result = json.loads(
            await configure_receive(nwc_connection_string="not-a-connection", api_client=client)
        )

        assert result["success"] is False
        assert "nostr+walletconnect://" in result["error"]
        assert stub.requests == []

    @pytest.mark.asyncio
    async def test_falls_back_to_the_mcp_wallet_when_it_is_nwc(self):
        client, stub = make_client(
            {
                ("PUT", "/api/merchant/nwc-connection"): (200, {"success": True}),
                ("PUT", "/api/merchant/payment-provider"): (200, {"success": True}),
            }
        )
        config = wallet_config(nwc_connection_string=FIXTURE_NWC)

        with patch.dict(os.environ, {}, clear=True):
            result = json.loads(
                await configure_receive(api_client=client, config_service=config)
            )

        assert result["success"] is True
        assert result["usedMcpWallet"] is True
        assert "NWC" in result["source"]
        assert stub.body_of("PUT", "/api/merchant/nwc-connection") == {
            "nwcConnectionString": FIXTURE_NWC
        }

    @pytest.mark.asyncio
    async def test_env_var_wins_over_the_config_file_for_the_fallback(self):
        env_string = FIXTURE_NWC.replace("relay.example.test", "relay2.example.test")
        client, stub = make_client(
            {
                ("PUT", "/api/merchant/nwc-connection"): (200, {"success": True}),
                ("PUT", "/api/merchant/payment-provider"): (200, {"success": True}),
            }
        )
        config = wallet_config(nwc_connection_string=FIXTURE_NWC)

        with patch.dict(os.environ, {"NWC_CONNECTION_STRING": env_string}, clear=True):
            await configure_receive(api_client=client, config_service=config)

        assert stub.body_of("PUT", "/api/merchant/nwc-connection") == {
            "nwcConnectionString": env_string
        }

    @pytest.mark.asyncio
    @pytest.mark.parametrize(
        "wallets,expected",
        [
            ({"lnd_rest_host": "localhost:8080", "lnd_macaroon_hex": "ab" * 32}, "LND"),
            ({"strike_api_key": "fixture-strike"}, "Strike"),
            ({"opennode_api_key": "fixture-opennode"}, "OpenNode"),
        ],
    )
    async def test_refuses_when_the_mcp_wallet_is_not_nwc(self, wallets, expected):
        client, stub = make_client({})
        config = wallet_config(**wallets)

        with patch.dict(os.environ, {}, clear=True):
            result = json.loads(
                await configure_receive(api_client=client, config_service=config)
            )

        assert result["success"] is False
        assert expected in result["error"]
        assert "nwc_connection_string" in result["error"]
        assert stub.requests == []

    @pytest.mark.asyncio
    async def test_refuses_when_no_wallet_is_configured_at_all(self):
        client, stub = make_client({})
        config = wallet_config()

        with patch.dict(os.environ, {}, clear=True):
            result = json.loads(
                await configure_receive(api_client=client, config_service=config)
            )

        assert result["success"] is False
        assert "no wallet" in result["error"].lower()
        assert stub.requests == []

    @pytest.mark.asyncio
    async def test_requires_an_api_key(self):
        result = json.loads(
            await configure_receive(
                nwc_connection_string=FIXTURE_NWC, api_client=unconfigured_client()
            )
        )
        assert result["success"] is False
        assert "LIGHTNING_ENABLE_API_KEY" in result["error"]


class TestResolveMcpNwcConnection:
    def test_returns_the_string_when_the_wallet_is_nwc(self):
        with patch.dict(os.environ, {}, clear=True):
            value, state = resolve_mcp_nwc_connection(
                wallet_config(nwc_connection_string=FIXTURE_NWC)
            )
        assert value == FIXTURE_NWC
        assert state["provider"] == "NWC"

    def test_returns_none_when_a_higher_priority_wallet_wins(self):
        """LND outranks NWC by default, so the server is not an NWC server."""
        with patch.dict(os.environ, {}, clear=True):
            value, state = resolve_mcp_nwc_connection(
                wallet_config(
                    nwc_connection_string=FIXTURE_NWC,
                    lnd_rest_host="localhost:8080",
                    lnd_macaroon_hex="ab" * 32,
                )
            )
        assert value is None
        assert state["provider"] == "LND"


# ══ status ════════════════════════════════════════════════════════════════════


MERCHANT_ME = {
    "merchantId": 42,
    "name": "Fixture Merchant",
    "email": "merchant@example.test",
    "planTier": "individual",
    "subscriptionStatus": "active",
    "isActive": True,
    "features": {"l402Enabled": True},
    "onboarding": {
        "hasOpenNodeKey": False,
        "hasStrikeKey": False,
        "hasNwcConnection": True,
        "hasWebhookUrl": False,
        "hasActiveProxy": True,
        "proxyCount": 2,
        "isFullyConfigured": True,
    },
}

QUICKSTART = {
    "merchantId": 42,
    "merchantName": "Fixture Merchant",
    "completedSteps": 2,
    "totalSteps": 4,
    "requiredStepsCompleted": 2,
    "requiredStepsTotal": 3,
    "isReadyForProduction": False,
    "steps": [
        {"stepNumber": 1, "title": "Connect a wallet", "isCompleted": True, "isRequired": True},
        {"stepNumber": 2, "title": "Create a proxy", "isCompleted": False, "isRequired": True},
    ],
}

CHALLENGES = {
    "challenges": [
        {
            "paymentHash": "aa11",
            "resource": "/weather",
            "amountSats": 25,
            "status": "paid",
            "createdAt": "2026-09-01T10:00:00Z",
            "paidAt": "2026-09-01T10:01:00Z",
            "expiresAt": "2026-09-01T11:00:00Z",
            "idempotencyKey": None,
        }
    ],
    "total": 1,
    "limit": 5,
    "offset": 0,
    "status": None,
}


class TestStatus:
    @pytest.mark.asyncio
    async def test_summarises_account_checklist_and_recent_challenges(self):
        client, stub = make_client(
            {
                ("GET", "/api/merchant/me"): (200, MERCHANT_ME),
                ("GET", "/api/merchant/quickstart"): (200, QUICKSTART),
                ("GET", "/api/l402/challenges"): (200, CHALLENGES),
            }
        )
        result = json.loads(await producer_status(limit=5, api_client=client))

        assert result["success"] is True
        assert result["merchant"]["planTier"] == "individual"
        assert result["receive"]["provider"] == "nwc"
        assert result["receive"]["configured"] is True
        assert result["receive"]["nwcConnectionString"] == "<set>"
        assert result["proxies"]["count"] == 2
        assert result["checklist"]["completedSteps"] == 2
        assert result["recentChallenges"][0]["paymentHash"] == "aa11"
        assert result["recentChallenges"][0]["amountSats"] == 25
        assert stub.request_for("GET", "/api/l402/challenges").url.params["limit"] == "5"

    @pytest.mark.asyncio
    async def test_never_reports_a_macaroon_or_preimage(self):
        leaky = {
            "challenges": [
                dict(
                    CHALLENGES["challenges"][0],
                    macaroon="fixture-macaroon-value",
                    preimage="ff" * 32,
                )
            ],
            "total": 1,
        }
        client, _ = make_client(
            {
                ("GET", "/api/merchant/me"): (200, MERCHANT_ME),
                ("GET", "/api/merchant/quickstart"): (200, QUICKSTART),
                ("GET", "/api/l402/challenges"): (200, leaky),
            }
        )
        raw = await producer_status(api_client=client)

        assert "macaroon" not in raw
        assert "preimage" not in raw
        assert "ff" * 32 not in raw

    @pytest.mark.asyncio
    async def test_still_answers_when_the_checklist_is_unavailable(self):
        client, _ = make_client(
            {
                ("GET", "/api/merchant/me"): (200, MERCHANT_ME),
                ("GET", "/api/merchant/quickstart"): (404, {"error": "Merchant not found"}),
                ("GET", "/api/l402/challenges"): (200, CHALLENGES),
            }
        )
        result = json.loads(await producer_status(api_client=client))

        assert result["success"] is True
        assert result["checklist"] is None
        assert "Merchant not found" in result["checklistError"]

    @pytest.mark.asyncio
    async def test_fails_when_the_account_lookup_fails(self):
        client, _ = make_client(
            {("GET", "/api/merchant/me"): (401, {"error": "Authentication required"})}
        )
        result = json.loads(await producer_status(api_client=client))

        assert result["success"] is False
        assert "Authentication required" in result["error"]

    @pytest.mark.asyncio
    async def test_derives_the_provider_from_the_onboarding_flags(self):
        strike_only = dict(
            MERCHANT_ME,
            onboarding=dict(
                MERCHANT_ME["onboarding"], hasNwcConnection=False, hasStrikeKey=True
            ),
        )
        client, _ = make_client(
            {
                ("GET", "/api/merchant/me"): (200, strike_only),
                ("GET", "/api/merchant/quickstart"): (200, QUICKSTART),
                ("GET", "/api/l402/challenges"): (200, CHALLENGES),
            }
        )
        result = json.loads(await producer_status(api_client=client))

        assert result["receive"]["provider"] == "strike"
        assert result["receive"]["nwcConnectionString"] == "<unset>"

    @pytest.mark.asyncio
    async def test_prefers_an_explicit_payment_provider_field_when_the_api_adds_one(self):
        explicit = dict(MERCHANT_ME, paymentProvider="opennode")
        client, _ = make_client(
            {
                ("GET", "/api/merchant/me"): (200, explicit),
                ("GET", "/api/merchant/quickstart"): (200, QUICKSTART),
                ("GET", "/api/l402/challenges"): (200, CHALLENGES),
            }
        )
        result = json.loads(await producer_status(api_client=client))

        assert result["receive"]["provider"] == "opennode"


# ══ create_proxy ══════════════════════════════════════════════════════════════


PROXY_CREATED = {
    "id": 7,
    "proxyId": "weather-api",
    "name": "Weather API",
    "description": "Forecasts",
    "targetBaseUrl": "https://upstream.example.test",
    "defaultPriceSats": 25,
    "isActive": True,
    "proxyUrl": "/l402/proxy/weather-api",
}


class TestCreateProxy:
    @pytest.mark.asyncio
    async def test_posts_the_create_proxy_dto_and_returns_the_public_base_url(self):
        client, stub = make_client({("POST", "/api/proxy"): (201, PROXY_CREATED)})
        result = json.loads(
            await create_proxy(
                name="Weather API",
                target_base_url="https://upstream.example.test",
                description="Forecasts",
                default_price_sats=25,
                api_client=client,
            )
        )

        assert stub.body_of("POST", "/api/proxy") == {
            "name": "Weather API",
            "targetBaseUrl": "https://upstream.example.test",
            "description": "Forecasts",
            "defaultPriceSats": 25,
        }
        assert result["success"] is True
        assert result["proxyId"] == "weather-api"
        assert result["proxyUrl"] == "/l402/proxy/weather-api"
        assert result["publicBaseUrl"] == f"{API_BASE}/l402/proxy/weather-api"

    @pytest.mark.asyncio
    async def test_requires_a_name_and_target(self):
        client, stub = make_client({})
        missing_name = json.loads(
            await create_proxy(name="", target_base_url="https://x.example.test", api_client=client)
        )
        missing_target = json.loads(
            await create_proxy(name="X", target_base_url="  ", api_client=client)
        )

        assert missing_name["success"] is False
        assert missing_target["success"] is False
        assert stub.requests == []

    @pytest.mark.asyncio
    async def test_surfaces_a_problem_json_plan_limit(self):
        client, _ = make_client(
            {
                ("POST", "/api/proxy"): (
                    402,
                    {
                        "type": "https://lightningenable.com/problems/plan_proxy_limit",
                        "title": "Proxy limit reached",
                        "status": 402,
                        "detail": "Plan 'free' caps proxy configs at 1. Upgrade for unlimited.",
                        "error": "plan_proxy_limit",
                        "upgrade_url": "https://api.lightningenable.com/checkout?plan=individual",
                    },
                )
            }
        )
        result = json.loads(
            await create_proxy(
                name="Weather API",
                target_base_url="https://upstream.example.test",
                api_client=client,
            )
        )

        assert result["success"] is False
        assert "caps proxy configs at 1" in result["error"]
        assert result["errorType"].endswith("plan_proxy_limit")
        assert result["errorCode"] == "plan_proxy_limit"
        assert result["httpStatus"] == 402


# ══ add_endpoint ══════════════════════════════════════════════════════════════


ENDPOINT_CREATED = {
    "id": 3,
    "endpointId": "forecast",
    "path": "/forecast",
    "httpMethod": "GET",
    "summary": "Five-day forecast",
    "basePriceSats": 10,
    "isActive": True,
}


class TestAddEndpoint:
    @pytest.mark.asyncio
    async def test_posts_the_manifest_endpoint_dto(self):
        client, stub = make_client(
            {("POST", "/api/proxy/weather-api/manifest/endpoints"): (201, ENDPOINT_CREATED)}
        )
        result = json.loads(
            await add_endpoint(
                proxy_id="weather-api",
                endpoint_id="forecast",
                path="/forecast",
                http_method="get",
                summary="Five-day forecast",
                price_sats=10,
                api_client=client,
            )
        )

        assert stub.body_of("POST", "/api/proxy/weather-api/manifest/endpoints") == {
            "endpointId": "forecast",
            "path": "/forecast",
            "httpMethod": "GET",
            "summary": "Five-day forecast",
            "basePriceSats": 10,
        }
        assert result["success"] is True
        assert result["httpMethod"] == "GET"
        assert result["url"] == f"{API_BASE}/l402/proxy/weather-api/forecast"

    @pytest.mark.asyncio
    async def test_url_encodes_the_proxy_id_in_the_path(self):
        """A slug with a slash must not walk out of its own route segment."""
        client, stub = make_client(
            {("POST", "/api/proxy/a/b/manifest/endpoints"): (201, ENDPOINT_CREATED)}
        )
        await add_endpoint(
            proxy_id="a/b",
            endpoint_id="e",
            path="/x",
            api_client=client,
        )

        assert len(stub.requests) == 1
        assert b"/api/proxy/a%2Fb/manifest/endpoints" in stub.requests[0].url.raw_path

    @pytest.mark.asyncio
    async def test_requires_proxy_endpoint_and_path(self):
        client, stub = make_client({})
        for kwargs in (
            {"proxy_id": "", "endpoint_id": "e", "path": "/x"},
            {"proxy_id": "p", "endpoint_id": "", "path": "/x"},
            {"proxy_id": "p", "endpoint_id": "e", "path": ""},
        ):
            result = json.loads(await add_endpoint(api_client=client, **kwargs))
            assert result["success"] is False
        assert stub.requests == []

    @pytest.mark.asyncio
    async def test_surfaces_a_duplicate_endpoint_error(self):
        client, _ = make_client(
            {
                ("POST", "/api/proxy/weather-api/manifest/endpoints"): (
                    400,
                    {"error": "Endpoint with ID 'forecast' already exists"},
                )
            }
        )
        result = json.loads(
            await add_endpoint(
                proxy_id="weather-api", endpoint_id="forecast", path="/forecast", api_client=client
            )
        )

        assert result["success"] is False
        assert "already exists" in result["error"]


# ══ publish ═══════════════════════════════════════════════════════════════════


MANIFEST_SETTINGS = {
    "manifestEnabled": True,
    "serviceDescription": "Forecasts for agents",
    "categories": ["weather", "data"],
    "manifestPubliclyListed": True,
    "manifestUrl": "/l402/proxy/weather-api/.well-known/l402-manifest.json",
}


class TestPublish:
    @pytest.mark.asyncio
    async def test_enables_the_manifest_and_lists_it_publicly(self):
        client, stub = make_client(
            {("PUT", "/api/proxy/weather-api/manifest/settings"): (200, MANIFEST_SETTINGS)}
        )
        result = json.loads(
            await publish(
                proxy_id="weather-api",
                service_description="Forecasts for agents",
                categories=["weather", "data"],
                api_client=client,
            )
        )

        body = stub.body_of("PUT", "/api/proxy/weather-api/manifest/settings")
        assert body["manifestEnabled"] is True
        assert body["manifestPubliclyListed"] is True
        assert body["serviceDescription"] == "Forecasts for agents"
        assert body["categories"] == ["weather", "data"]
        assert result["success"] is True
        assert result["openapiUrl"] == f"{API_BASE}/l402/proxy/weather-api/openapi.json"
        assert result["manifestUrl"] == (
            f"{API_BASE}/l402/proxy/weather-api/.well-known/l402-manifest.json"
        )

    @pytest.mark.asyncio
    async def test_renames_the_proxy_first_when_a_service_name_is_given(self):
        """``service.name`` in the manifest is the proxy's own name, not a manifest field."""
        client, stub = make_client(
            {
                ("PUT", "/api/proxy/weather-api"): (200, PROXY_CREATED),
                ("PUT", "/api/proxy/weather-api/manifest/settings"): (200, MANIFEST_SETTINGS),
            }
        )
        await publish(
            proxy_id="weather-api",
            service_name="Weather for Agents",
            service_description="Forecasts",
            api_client=client,
        )

        assert stub.paths == [
            ("PUT", "/api/proxy/weather-api"),
            ("PUT", "/api/proxy/weather-api/manifest/settings"),
        ]
        assert stub.body_of("PUT", "/api/proxy/weather-api") == {"name": "Weather for Agents"}

    @pytest.mark.asyncio
    async def test_does_not_publish_when_the_rename_fails(self):
        client, stub = make_client(
            {
                ("PUT", "/api/proxy/weather-api"): (404, {"error": "Proxy not found"}),
                ("PUT", "/api/proxy/weather-api/manifest/settings"): (200, MANIFEST_SETTINGS),
            }
        )
        result = json.loads(
            await publish(
                proxy_id="weather-api", service_name="Renamed", api_client=client
            )
        )

        assert result["success"] is False
        assert "Proxy not found" in result["error"]
        assert ("PUT", "/api/proxy/weather-api/manifest/settings") not in stub.paths

    @pytest.mark.asyncio
    async def test_surfaces_the_plan_gate_on_public_listing(self):
        client, _ = make_client(
            {
                ("PUT", "/api/proxy/weather-api/manifest/settings"): (
                    402,
                    {
                        "error": "plan_feature_disabled",
                        "feature": "registry_listing",
                        "message": "Public registry listing needs a paid plan.",
                        "current_plan": "free",
                    },
                )
            }
        )
        result = json.loads(await publish(proxy_id="weather-api", api_client=client))

        assert result["success"] is False
        assert "paid plan" in result["error"]
        assert result["errorCode"] == "plan_feature_disabled"

    @pytest.mark.asyncio
    async def test_requires_a_proxy_id(self):
        client, stub = make_client({})
        result = json.loads(await publish(proxy_id="", api_client=client))
        assert result["success"] is False
        assert stub.requests == []


# ══ list_challenges ═══════════════════════════════════════════════════════════


class TestListChallenges:
    @pytest.mark.asyncio
    async def test_passes_the_status_filter_and_paging(self):
        client, stub = make_client({("GET", "/api/l402/challenges"): (200, CHALLENGES)})
        result = json.loads(
            await list_challenges(status="paid", limit=10, offset=20, api_client=client)
        )

        params = stub.request_for("GET", "/api/l402/challenges").url.params
        assert params["status"] == "paid"
        assert params["limit"] == "10"
        assert params["offset"] == "20"
        assert result["success"] is True
        assert result["total"] == 1
        assert result["challenges"][0]["resource"] == "/weather"

    @pytest.mark.asyncio
    async def test_omits_the_status_parameter_when_unfiltered(self):
        client, stub = make_client({("GET", "/api/l402/challenges"): (200, CHALLENGES)})
        await list_challenges(api_client=client)

        assert "status" not in stub.request_for("GET", "/api/l402/challenges").url.params

    @pytest.mark.asyncio
    async def test_rejects_an_unknown_status_before_sending(self):
        client, stub = make_client({})
        result = json.loads(await list_challenges(status="settled", api_client=client))

        assert result["success"] is False
        assert "paid" in result["error"]
        assert stub.requests == []

    @pytest.mark.asyncio
    async def test_surfaces_the_apis_invalid_filter_problem(self):
        client, _ = make_client(
            {
                ("GET", "/api/l402/challenges"): (
                    400,
                    {
                        "type": "https://lightningenable.com/problems/invalid_status_filter",
                        "title": "Invalid status filter",
                        "detail": "status must be one of: paid, unpaid, expired.",
                        "error": "invalid_status_filter",
                    },
                )
            }
        )
        result = json.loads(await list_challenges(api_client=client))

        assert result["success"] is False
        assert "paid, unpaid, expired" in result["error"]
        assert result["errorType"].endswith("invalid_status_filter")


# ══ API-key gating, shared ════════════════════════════════════════════════════


class TestApiKeyGating:
    @pytest.mark.asyncio
    @pytest.mark.parametrize(
        "call",
        [
            lambda c: producer_status(api_client=c),
            lambda c: create_proxy(name="n", target_base_url="https://x.example.test", api_client=c),
            lambda c: add_endpoint(proxy_id="p", endpoint_id="e", path="/x", api_client=c),
            lambda c: publish(proxy_id="p", api_client=c),
            lambda c: list_challenges(api_client=c),
        ],
    )
    async def test_every_action_requires_the_merchant_api_key(self, call):
        result = json.loads(await call(unconfigured_client()))
        assert result["success"] is False
        assert "LIGHTNING_ENABLE_API_KEY" in result["error"]

    @pytest.mark.asyncio
    @pytest.mark.parametrize(
        "call",
        [
            lambda: configure_receive(nwc_connection_string=FIXTURE_NWC, api_client=None),
            lambda: producer_status(api_client=None),
            lambda: create_proxy(name="n", target_base_url="https://x.example.test", api_client=None),
            lambda: add_endpoint(proxy_id="p", endpoint_id="e", path="/x", api_client=None),
            lambda: publish(proxy_id="p", api_client=None),
            lambda: list_challenges(api_client=None),
        ],
    )
    async def test_missing_client_is_a_descriptive_error_not_a_crash(self, call):
        result = json.loads(await call())
        assert result["success"] is False
        assert result["error"]

    @pytest.mark.asyncio
    async def test_no_action_ever_echoes_the_api_key(self):
        client, _ = make_client(
            {("GET", "/api/l402/challenges"): (200, CHALLENGES)}, api_key="fixture-key-1"
        )
        raw = await list_challenges(api_client=client)
        assert "fixture-key-1" not in raw


# ══ Advertised surface and dispatch ═══════════════════════════════════════════


NEW_ACTIONS = (
    "configure_receive",
    "status",
    "create_proxy",
    "add_endpoint",
    "publish",
    "list_challenges",
)

#: Action → the ``server`` module attribute it must reach.
ACTION_IMPLEMENTATIONS = {
    "create": "create_l402_challenge",
    "verify": "verify_l402_payment",
    "configure_receive": "configure_receive",
    "status": "producer_status",
    "create_proxy": "create_proxy",
    "add_endpoint": "add_endpoint",
    "publish": "publish_l402_service",
    "list_challenges": "list_l402_challenges",
}

#: Actions that only read. Annotations are per TOOL, so these do not change the tool's
#: own ``readOnlyHint`` — the widest action decides that — but the split is recorded here
#: and reflected in the action description the model reads.
READ_ONLY_ACTIONS = frozenset({"status", "list_challenges"})
WRITE_ACTIONS = frozenset(
    {"create", "verify", "configure_receive", "create_proxy", "add_endpoint", "publish"}
)


class TestAdvertisedSurface:
    def test_schema_advertises_every_action(self):
        from lightning_enable_mcp.tools.consolidated import (
            ACTION_TOOLS,
            L402_PRODUCER_TOOL,
        )

        enum = L402_PRODUCER_TOOL.inputSchema["properties"]["action"]["enum"]
        assert enum == list(ACTION_IMPLEMENTATIONS)
        assert ACTION_TOOLS["l402_producer"] == ("action", tuple(ACTION_IMPLEMENTATIONS))

    def test_every_action_is_classified_as_a_read_or_a_write(self):
        assert READ_ONLY_ACTIONS | WRITE_ACTIONS == set(ACTION_IMPLEMENTATIONS)
        assert not READ_ONLY_ACTIONS & WRITE_ACTIONS

    def test_action_description_marks_the_read_only_actions(self):
        from lightning_enable_mcp.tools.consolidated import L402_PRODUCER_TOOL

        description = L402_PRODUCER_TOOL.inputSchema["properties"]["action"]["description"]
        for action in sorted(ACTION_IMPLEMENTATIONS):
            assert action in description
        assert description.count("read-only") == len(READ_ONLY_ACTIONS)

    def test_create_and_verify_arguments_are_untouched(self):
        from lightning_enable_mcp.tools.consolidated import L402_PRODUCER_TOOL

        properties = L402_PRODUCER_TOOL.inputSchema["properties"]
        for argument in ("resource", "price_sats", "description", "macaroon", "preimage"):
            assert argument in properties
        assert L402_PRODUCER_TOOL.inputSchema["required"] == ["action"]

    def test_every_new_argument_is_advertised(self):
        from lightning_enable_mcp.tools.consolidated import L402_PRODUCER_TOOL

        properties = L402_PRODUCER_TOOL.inputSchema["properties"]
        for argument in (
            "nwc_connection_string",
            "name",
            "target_base_url",
            "default_price_sats",
            "proxy_id",
            "endpoint_id",
            "path",
            "http_method",
            "summary",
            "service_name",
            "service_description",
            "categories",
            "challenge_status",
            "limit",
            "offset",
        ):
            assert argument in properties, argument
        assert properties["categories"]["type"] == "array"
        assert properties["challenge_status"]["enum"] == list(CHALLENGE_STATUS_VALUES)

    def test_the_tool_stays_a_write_annotated_for_its_widest_action(self):
        from lightning_enable_mcp.tools.annotations import TOOL_ANNOTATIONS

        annotations = TOOL_ANNOTATIONS["l402_producer"]
        assert annotations.readOnlyHint is False
        assert annotations.destructiveHint is False
        assert annotations.title == "L402 producer"

    def test_the_tool_count_is_unchanged(self):
        """New actions, not new tools — the advertised inventory must not grow."""
        from lightning_enable_mcp.tools.registry import STANDARD_TOOLS

        assert len(STANDARD_TOOLS) == 16

    def test_no_new_deprecated_alias_was_introduced(self):
        from lightning_enable_mcp.server import DEPRECATED_ALIASES

        producer_aliases = {
            name: target.args
            for name, target in DEPRECATED_ALIASES.items()
            if target.tool == "l402_producer"
        }
        assert producer_aliases == {
            "create_l402_challenge": {"action": "create"},
            "verify_l402_payment": {"action": "verify"},
        }


CHALLENGE_STATUS_VALUES = ("paid", "unpaid", "expired")


class TestDispatch:
    @staticmethod
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
    @pytest.mark.parametrize("action", sorted(ACTION_IMPLEMENTATIONS))
    async def test_each_action_reaches_its_own_handler(self, action):
        from unittest.mock import AsyncMock

        from lightning_enable_mcp.server import LightningEnableServer

        server = LightningEnableServer()
        server.wallet = MagicMock()
        server.l402_client = MagicMock()

        impl = ACTION_IMPLEMENTATIONS[action]
        with patch(
            f"lightning_enable_mcp.server.{impl}",
            new=AsyncMock(return_value='{"success": true}'),
        ) as handler:
            text = await self._call(server, {"action": action})

        handler.assert_awaited_once()
        assert json.loads(text)["success"] is True

    @pytest.mark.asyncio
    async def test_list_challenges_takes_its_filter_from_challenge_status(self):
        """``status`` is an ACTION name, so the filter argument has its own name."""
        from unittest.mock import AsyncMock

        from lightning_enable_mcp.server import LightningEnableServer

        server = LightningEnableServer()
        server.wallet = MagicMock()
        server.l402_client = MagicMock()

        with patch(
            "lightning_enable_mcp.server.list_l402_challenges",
            new=AsyncMock(return_value='{"success": true}'),
        ) as handler:
            await self._call(
                server,
                {
                    "action": "list_challenges",
                    "challenge_status": "paid",
                    "limit": 7,
                    "offset": 3,
                },
            )

        kwargs = handler.await_args.kwargs
        assert kwargs["status"] == "paid"
        assert kwargs["limit"] == 7
        assert kwargs["offset"] == 3

    @pytest.mark.asyncio
    async def test_the_schema_enum_rejects_an_unknown_action(self):
        from lightning_enable_mcp.server import LightningEnableServer

        server = LightningEnableServer()
        server.wallet = MagicMock()
        server.l402_client = MagicMock()

        text = await self._call(server, {"action": "monetise"})

        assert "monetise" in text
        for action in ACTION_IMPLEMENTATIONS:
            assert action in text

    def test_the_handler_fallback_names_every_valid_action(self):
        """Behind the enum: a client that skips schema validation still gets the list."""
        from lightning_enable_mcp.server import _unknown_action

        result = json.loads(_unknown_action("l402_producer", "action", "monetise"))

        assert result["success"] is False
        for action in ACTION_IMPLEMENTATIONS:
            assert action in result["error"]
