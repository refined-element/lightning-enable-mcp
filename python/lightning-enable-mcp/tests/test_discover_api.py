"""
Tests for discover_api tool
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock, patch

# Reach the SUBMODULE explicitly via importlib, NOT via attribute access on
# the parent package. The package's __init__.py does
# `from .discover_api import discover_api`, which binds the FUNCTION to the
# `tools.discover_api` attribute — shadowing the submodule. So both
# `patch("lightning_enable_mcp.tools.discover_api.httpx")` and
# `import lightning_enable_mcp.tools.discover_api as alias` resolve to the
# function (which has no `httpx` attribute → AttributeError on Python 3.10;
# 3.12's mock.patch handles it differently and happens to pass).
# `importlib.import_module` looks the submodule up in `sys.modules` and
# returns the genuine module object regardless of what name the parent
# package binds. patch.object on that reference works on every supported
# Python version.
import importlib
discover_api_module = importlib.import_module("lightning_enable_mcp.tools.discover_api")
from decimal import Decimal

from lightning_enable_mcp.price_service import PriceService, PriceUnavailableError
from lightning_enable_mcp.tools.discover_api import (
    discover_api,
    _get_registry_base_url,
    _get_tried_urls,
    _extract_service_info,
    _extract_l402_info,
    _extract_endpoints,
    _parse_price_sats,
    _affordable_calls,
)


class TestDiscoverApiHelpers:
    """Tests for discover_api helper functions."""

    def test_get_registry_base_url_default(self):
        """Test default registry URL."""
        with patch.dict("os.environ", {}, clear=True):
            url = _get_registry_base_url()
            assert url == "https://api.lightningenable.com"

    def test_get_registry_base_url_from_env(self):
        """Test registry URL from L402_REGISTRY_URL env var."""
        with patch.dict("os.environ", {"L402_REGISTRY_URL": "https://custom.registry.com/"}):
            url = _get_registry_base_url()
            assert url == "https://custom.registry.com"

    def test_get_registry_base_url_fallback(self):
        """Test registry URL from LIGHTNING_ENABLE_API_URL fallback."""
        with patch.dict(
            "os.environ",
            {"LIGHTNING_ENABLE_API_URL": "https://api.custom.com"},
            clear=True,
        ):
            url = _get_registry_base_url()
            assert url == "https://api.custom.com"

    def test_get_tried_urls_json_extension(self):
        """Test URL list when URL ends with .json."""
        urls = _get_tried_urls("https://example.com/manifest.json")
        assert urls[0] == "https://example.com/manifest.json"
        assert any("well-known" in u for u in urls)

    def test_get_tried_urls_base_url(self):
        """Test URL list for a base URL."""
        urls = _get_tried_urls("https://api.example.com")
        assert any("/.well-known/l402-manifest.json" in u for u in urls)
        # Base URL should also be tried
        assert "https://api.example.com" in urls

    def test_extract_service_info(self):
        """Test extracting service info from manifest."""
        manifest = {
            "service": {
                "name": "Test API",
                "description": "A test API",
                "base_url": "https://api.test.com",
                "categories": ["ai", "data"],
            }
        }
        info = _extract_service_info(manifest)
        assert info["name"] == "Test API"
        assert info["categories"] == ["ai", "data"]

    def test_extract_l402_info(self):
        """Test extracting L402 info from manifest."""
        manifest = {
            "l402": {
                "default_price_sats": 100,
                "payment_flow": "402-challenge",
                "capabilities": {
                    "preimage_in_response": True,
                    "supported_currencies": ["BTC"],
                },
            }
        }
        info = _extract_l402_info(manifest)
        assert info["default_price_sats"] == 100
        assert info["capabilities"]["preimage_in_response"] is True

    def test_extract_endpoints(self):
        """Test extracting endpoints from manifest."""
        manifest = {
            "endpoints": [
                {
                    "id": "ep1",
                    "path": "/data",
                    "method": "GET",
                    "summary": "Get data",
                    "l402_enabled": True,
                    "pricing": {"model": "per-request", "base_price_sats": 50},
                    "tags": ["data"],
                }
            ]
        }
        eps = _extract_endpoints(manifest)
        assert len(eps) == 1
        assert eps[0]["path"] == "/data"
        assert eps[0]["pricing"]["base_price_sats"] == 50


class TestDiscoverApi:
    """Tests for discover_api tool."""

    @pytest.mark.asyncio
    async def test_no_params_returns_usage_error(self):
        """Test that calling with no params returns usage error."""
        result = await discover_api()
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Please provide" in parsed["error"]
        assert "examples" in parsed

    @pytest.mark.asyncio
    async def test_registry_search_success(self):
        """Test successful registry search."""
        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "items": [
                {
                    "name": "Weather API",
                    "description": "Get weather data",
                    "manifestUrl": "https://weather.api.com/l402.json",
                    "parsedCategories": ["weather"],
                    "endpointCount": 5,
                    "defaultPriceSats": 10,
                }
            ],
            "total": 1,
        }

        mock_client = AsyncMock()
        mock_client.get = AsyncMock(return_value=mock_response)
        mock_client.__aenter__ = AsyncMock(return_value=mock_client)
        mock_client.__aexit__ = AsyncMock(return_value=False)

        with patch.object(discover_api_module, "httpx") as mock_httpx:
            mock_httpx.AsyncClient.return_value = mock_client

            result = await discover_api(query="weather")
            parsed = json.loads(result)

            assert parsed["success"] is True
            assert parsed["source"] == "registry"
            assert len(parsed["results"]) == 1
            assert parsed["results"][0]["name"] == "Weather API"
            assert parsed["results"][0]["default_price_sats"] == 10

    @pytest.mark.asyncio
    async def test_registry_search_http_error(self):
        """Test registry search with HTTP error."""
        mock_response = MagicMock()
        mock_response.status_code = 500

        mock_client = AsyncMock()
        mock_client.get = AsyncMock(return_value=mock_response)
        mock_client.__aenter__ = AsyncMock(return_value=mock_client)
        mock_client.__aexit__ = AsyncMock(return_value=False)

        with patch.object(discover_api_module, "httpx") as mock_httpx:
            mock_httpx.AsyncClient.return_value = mock_client

            result = await discover_api(query="test")
            parsed = json.loads(result)

            assert parsed["success"] is False
            assert "500" in parsed["error"]

    @pytest.mark.asyncio
    async def test_manifest_fetch_success(self):
        """Test fetching a specific API manifest."""
        manifest = json.dumps({
            "service": {"name": "Test API", "description": "A test"},
            "l402": {"default_price_sats": 100},
            "endpoints": [
                {"path": "/data", "method": "GET", "l402_enabled": True}
            ],
        })

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.text = manifest

        mock_client = AsyncMock()
        mock_client.get = AsyncMock(return_value=mock_response)
        mock_client.__aenter__ = AsyncMock(return_value=mock_client)
        mock_client.__aexit__ = AsyncMock(return_value=False)

        with patch.object(discover_api_module, "httpx") as mock_httpx:
            mock_httpx.AsyncClient.return_value = mock_client

            result = await discover_api(url="https://api.example.com")
            parsed = json.loads(result)

            assert parsed["success"] is True
            assert parsed["source"] == "manifest"
            assert parsed["service"]["name"] == "Test API"
            assert len(parsed["endpoints"]) == 1

    @pytest.mark.asyncio
    async def test_manifest_not_found(self):
        """Test that missing manifest returns error."""
        mock_response = MagicMock()
        mock_response.status_code = 404

        mock_client = AsyncMock()
        mock_client.get = AsyncMock(return_value=mock_response)
        mock_client.__aenter__ = AsyncMock(return_value=mock_client)
        mock_client.__aexit__ = AsyncMock(return_value=False)

        with patch.object(discover_api_module, "httpx") as mock_httpx:
            mock_httpx.AsyncClient.return_value = mock_client

            result = await discover_api(url="https://no-manifest.example.com")
            parsed = json.loads(result)

            assert parsed["success"] is False
            assert "Could not find" in parsed["error"]
            assert "tried_urls" in parsed

    @pytest.mark.asyncio
    async def test_httpx_not_available(self):
        """Test error when httpx is not installed."""
        with patch.object(discover_api_module, "httpx", None):
            result = await discover_api(query="test")
            parsed = json.loads(result)
            assert parsed["success"] is False
            assert "httpx is required" in parsed["error"]


# =============================================================================
# Budget-annotation test scaffolding
#
# The manifest is a THIRD-PARTY, ATTACKER-AUTHORABLE document: it is fetched
# from a public registry or an arbitrary URL. Nothing in it may be trusted to
# be a number, let alone a sane one.
# =============================================================================


def _mock_client_returning(payload: str):
    """An httpx.AsyncClient mock whose every GET returns `payload`."""
    mock_response = MagicMock()
    mock_response.status_code = 200
    mock_response.text = payload

    mock_client = AsyncMock()
    mock_client.get = AsyncMock(return_value=mock_response)
    mock_client.__aenter__ = AsyncMock(return_value=mock_client)
    mock_client.__aexit__ = AsyncMock(return_value=False)
    return mock_client


def _fake_budget_service(remaining_sats=10_000, spent_sats=250):
    """A budget service stub. `remaining_sats=None` models 'cannot be determined'."""
    svc = MagicMock()
    svc.get_status = MagicMock(
        return_value={
            "session": {
                "spentSats": spent_sats,
                "spentUsd": 0.25,
                "remainingUsd": 9.75,
                "requestCount": 1,
                "sessionStarted": "2026-01-01T00:00:00+00:00",
                "isFirstPayment": False,
                "cooldownActive": False,
            }
        }
    )
    svc.get_remaining_session_sats = AsyncMock(return_value=remaining_sats)
    return svc


def _fake_price_service(btc_usd="100000"):
    """PriceService stub. spec= means a call to a NON-EXISTENT method raises."""
    price = MagicMock(spec=PriceService)
    price.get_btc_price = AsyncMock(return_value=Decimal(btc_usd))
    return price


async def _discover_manifest_with_price(manifest: dict, budget_service):
    """Run discover_api(url=...) against `manifest` with a live BTC price."""
    mock_client = _mock_client_returning(json.dumps(manifest))
    with patch.object(discover_api_module, "httpx") as mock_httpx:
        mock_httpx.AsyncClient.return_value = mock_client
        with patch(
            "lightning_enable_mcp.price_service.get_price_service",
            return_value=_fake_price_service(),
        ):
            return json.loads(await discover_api(url="https://api.example.com", budget_service=budget_service))


def _manifest_priced(base_price_sats, include_key: bool = True):
    """A one-endpoint manifest with the given (possibly hostile) price value."""
    pricing = {"model": "per-request"}
    if include_key:
        pricing["base_price_sats"] = base_price_sats
    return {
        "service": {"name": "Hostile API"},
        "endpoints": [{"path": "/data", "method": "GET", "l402_enabled": True, "pricing": pricing}],
    }


class TestParsePriceSats:
    """FINDING 3: strict parsing of an attacker-authorable price value."""

    def test_positive_int_is_a_price(self):
        assert _parse_price_sats(100) == 100

    def test_positive_float_is_a_price(self):
        assert _parse_price_sats(2.5) == 2.5

    def test_zero_is_not_a_price(self):
        assert _parse_price_sats(0) is None

    def test_negative_is_not_a_price(self):
        assert _parse_price_sats(-50) is None

    def test_none_is_not_a_price(self):
        assert _parse_price_sats(None) is None

    def test_string_is_not_a_price(self):
        """A JSON string is not a number, even when it looks like one."""
        assert _parse_price_sats("100") is None

    def test_bool_is_not_a_price(self):
        """bool is a subclass of int — True must NEVER read as a price of 1 sat."""
        assert _parse_price_sats(True) is None
        assert _parse_price_sats(False) is None

    def test_nested_object_is_not_a_price(self):
        assert _parse_price_sats({"amount": 100}) is None

    def test_list_is_not_a_price(self):
        assert _parse_price_sats([100]) is None

    def test_non_finite_floats_are_not_a_price(self):
        """inf/nan must not reach int() (OverflowError/ValueError) or a division."""
        assert _parse_price_sats(float("inf")) is None
        assert _parse_price_sats(float("-inf")) is None
        assert _parse_price_sats(float("nan")) is None

    def test_price_above_total_bitcoin_supply_is_not_a_price(self):
        """
        JSON ints are unbounded. A price exceeding the entire 21M BTC supply is
        unpayable by definition — and float() on a big enough int raises
        OverflowError, which an attacker could use to blow up the annotation.
        """
        assert _parse_price_sats(10**400) is None
        assert _parse_price_sats(2_100_000_000_000_000 + 1) is None
        # The supply itself is absurd but representable, so it still parses.
        assert _parse_price_sats(2_100_000_000_000_000) == 2_100_000_000_000_000


class TestAffordableCalls:
    """FINDING 3 + 6: affordability is 'unknown' unless BOTH inputs are known."""

    def test_known_price_and_budget(self):
        assert _affordable_calls(10_000, 100) == 100

    def test_unknown_price_is_unknown(self):
        assert _affordable_calls(10_000, None) == "unknown"

    def test_unknown_budget_is_unknown(self):
        """Budget undeterminable (e.g. no BTC price) -> unknown, NEVER 0, never unlimited."""
        assert _affordable_calls(None, 100) == "unknown"

    def test_both_unknown_is_unknown(self):
        assert _affordable_calls(None, None) == "unknown"

    def test_result_is_an_int_not_a_float(self):
        assert isinstance(_affordable_calls(10_000, 2.5), int)


class TestManifestPriceIsUntrusted:
    """
    FINDING 3: a zero/negative/malformed manifest price must read "unknown".

    Before the fix these all reported affordable_calls: "unlimited" — telling
    the agent it could make unbounded paid calls on an attacker's say-so.
    """

    @pytest.mark.asyncio
    @pytest.mark.parametrize(
        "hostile_price",
        [0, 0.0, -1, -50.5, "100", True, False, None, {"amount": 100}, [100], "free",
         10**400, float("inf"), float("nan")],
        ids=["zero", "zero_float", "negative", "negative_float", "string", "bool_true",
             "bool_false", "null", "nested_object", "list", "word_free",
             "huge_int", "infinity", "nan"],
    )
    async def test_unparseable_price_reads_unknown(self, hostile_price):
        parsed = await _discover_manifest_with_price(
            _manifest_priced(hostile_price), _fake_budget_service()
        )
        endpoint = parsed["endpoints"][0]
        assert endpoint["affordable_calls"] == "unknown", (
            f"price {hostile_price!r} produced {endpoint['affordable_calls']!r}"
        )
        # Never claim unlimited/free, and never price an unparseable value.
        assert endpoint["affordable_calls"] != "unlimited"
        assert "cost_usd" not in endpoint
        # A hostile price must not blow up (and thereby suppress) the whole
        # budget block via the outer catch-all.
        assert parsed["budget"]["remaining_sats"] == 10_000

    @pytest.mark.asyncio
    async def test_missing_price_key_reads_unknown(self):
        """A pricing block with no base_price_sats is an UNKNOWN price."""
        parsed = await _discover_manifest_with_price(
            _manifest_priced(None, include_key=False), _fake_budget_service()
        )
        assert parsed["endpoints"][0]["affordable_calls"] == "unknown"

    @pytest.mark.asyncio
    async def test_positive_int_price_is_priced_and_counted(self):
        parsed = await _discover_manifest_with_price(
            _manifest_priced(100), _fake_budget_service(remaining_sats=10_000)
        )
        endpoint = parsed["endpoints"][0]
        assert endpoint["affordable_calls"] == 100  # 10_000 // 100
        assert endpoint["cost_usd"] == 0.1  # 100 sats @ $100k/BTC

    @pytest.mark.asyncio
    async def test_positive_float_price_is_priced_and_counted(self):
        parsed = await _discover_manifest_with_price(
            _manifest_priced(2.5), _fake_budget_service(remaining_sats=10_000)
        )
        endpoint = parsed["endpoints"][0]
        assert endpoint["affordable_calls"] == 4000  # 10_000 // 2.5
        assert endpoint["cost_usd"] == 0.0025


class TestManifestBudgetAnnotations:
    """FINDING 6: the budget annotations must actually carry real numbers."""

    @pytest.mark.asyncio
    async def test_remaining_sats_is_reported(self):
        """Before the fix this read 0 forever (get_status has no 'remainingSats' key)."""
        parsed = await _discover_manifest_with_price(
            _manifest_priced(100), _fake_budget_service(remaining_sats=10_000, spent_sats=250)
        )
        budget = parsed["budget"]
        assert budget["remaining_sats"] == 10_000
        assert budget["session_spent_sats"] == 250
        assert budget["session_limit_sats"] == 10_250

    @pytest.mark.asyncio
    async def test_remaining_usd_is_reported(self):
        """Before the fix get_btc_price_usd() raised AttributeError into a bare except."""
        parsed = await _discover_manifest_with_price(
            _manifest_priced(100), _fake_budget_service(remaining_sats=10_000)
        )
        assert parsed["budget"]["remaining_usd"] == 10.0  # 10_000 sats @ $100k/BTC

    @pytest.mark.asyncio
    async def test_undeterminable_budget_is_none_not_zero(self):
        """
        CRITICAL: an unknown remaining budget is None/omitted — NEVER 0 and never
        a hardcoded rate. Endpoint affordability degrades to "unknown".
        """
        parsed = await _discover_manifest_with_price(
            _manifest_priced(100), _fake_budget_service(remaining_sats=None)
        )
        assert parsed["budget"]["remaining_sats"] is None
        assert parsed["budget"]["session_limit_sats"] is None
        assert "remaining_usd" not in parsed["budget"]
        assert parsed["endpoints"][0]["affordable_calls"] == "unknown"
        # The price is still known, so cost per call is still reported.
        assert parsed["endpoints"][0]["cost_usd"] == 0.1

    @pytest.mark.asyncio
    async def test_price_unavailable_still_discovers(self):
        """Fail-closed: no BTC price -> no USD annotation, but discovery still works."""
        mock_client = _mock_client_returning(json.dumps(_manifest_priced(100)))
        price = MagicMock(spec=PriceService)
        price.get_btc_price = AsyncMock(side_effect=PriceUnavailableError("all sources failed"))

        with patch.object(discover_api_module, "httpx") as mock_httpx:
            mock_httpx.AsyncClient.return_value = mock_client
            with patch("lightning_enable_mcp.price_service.get_price_service", return_value=price):
                parsed = json.loads(
                    await discover_api(
                        url="https://api.example.com", budget_service=_fake_budget_service()
                    )
                )

        assert parsed["success"] is True
        assert parsed["endpoints"][0]["affordable_calls"] == 100
        assert "cost_usd" not in parsed["endpoints"][0]
        assert "remaining_usd" not in parsed["budget"]
        assert parsed["budget"]["remaining_sats"] == 10_000

    @pytest.mark.asyncio
    async def test_budget_aware_false_skips_annotations(self):
        mock_client = _mock_client_returning(json.dumps(_manifest_priced(100)))
        with patch.object(discover_api_module, "httpx") as mock_httpx:
            mock_httpx.AsyncClient.return_value = mock_client
            parsed = json.loads(
                await discover_api(
                    url="https://api.example.com",
                    budget_aware=False,
                    budget_service=_fake_budget_service(),
                )
            )
        assert parsed["budget"] is None
        assert "affordable_calls" not in parsed["endpoints"][0]

    @pytest.mark.asyncio
    async def test_endpoint_without_pricing_block_is_not_annotated(self):
        """No pricing block -> claim nothing at all."""
        manifest = {
            "service": {"name": "Test API"},
            "endpoints": [{"path": "/free", "method": "GET"}],
        }
        parsed = await _discover_manifest_with_price(manifest, _fake_budget_service())
        assert "affordable_calls" not in parsed["endpoints"][0]


class TestRegistryPriceIsUntrusted:
    """FINDING 3 + 6 applied to the registry path — same untrusted third-party data."""

    async def _search(self, default_price_sats, budget_service):
        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "items": [{"name": "Weather API", "defaultPriceSats": default_price_sats}],
            "total": 1,
        }
        mock_client = AsyncMock()
        mock_client.get = AsyncMock(return_value=mock_response)
        mock_client.__aenter__ = AsyncMock(return_value=mock_client)
        mock_client.__aexit__ = AsyncMock(return_value=False)

        with patch.object(discover_api_module, "httpx") as mock_httpx:
            mock_httpx.AsyncClient.return_value = mock_client
            return json.loads(await discover_api(query="weather", budget_service=budget_service))

    @pytest.mark.asyncio
    @pytest.mark.parametrize("hostile_price", [0, -10, True, "100", None, {"a": 1}])
    async def test_unparseable_registry_price_reads_unknown(self, hostile_price):
        parsed = await self._search(hostile_price, _fake_budget_service())
        assert parsed["results"][0]["affordable_calls"] == "unknown"

    @pytest.mark.asyncio
    async def test_valid_registry_price_is_counted(self):
        parsed = await self._search(10, _fake_budget_service(remaining_sats=10_000))
        assert parsed["results"][0]["affordable_calls"] == 1000

    @pytest.mark.asyncio
    async def test_registry_budget_reports_real_remaining(self):
        """Before the fix: remaining_sats/session_limit_sats were hardwired to 0."""
        parsed = await self._search(10, _fake_budget_service(remaining_sats=10_000, spent_sats=250))
        assert parsed["budget"]["remaining_sats"] == 10_000
        assert parsed["budget"]["session_spent_sats"] == 250
        assert parsed["budget"]["session_limit_sats"] == 10_250

    @pytest.mark.asyncio
    async def test_registry_unknown_budget_is_none_not_zero(self):
        parsed = await self._search(10, _fake_budget_service(remaining_sats=None))
        assert parsed["budget"]["remaining_sats"] is None
        assert parsed["results"][0]["affordable_calls"] == "unknown"
