"""
Tests for discover_api tool
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock, patch

from lightning_enable_mcp.tools.discover_api import (
    discover_api,
    _get_registry_base_url,
    _get_tried_urls,
    _extract_service_info,
    _extract_l402_info,
    _extract_endpoints,
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

        with patch("lightning_enable_mcp.tools.discover_api.httpx") as mock_httpx:
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

        with patch("lightning_enable_mcp.tools.discover_api.httpx") as mock_httpx:
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

        with patch("lightning_enable_mcp.tools.discover_api.httpx") as mock_httpx:
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

        with patch("lightning_enable_mcp.tools.discover_api.httpx") as mock_httpx:
            mock_httpx.AsyncClient.return_value = mock_client

            result = await discover_api(url="https://no-manifest.example.com")
            parsed = json.loads(result)

            assert parsed["success"] is False
            assert "Could not find" in parsed["error"]
            assert "tried_urls" in parsed

    @pytest.mark.asyncio
    async def test_httpx_not_available(self):
        """Test error when httpx is not installed."""
        with patch("lightning_enable_mcp.tools.discover_api.httpx", None):
            result = await discover_api(query="test")
            parsed = json.loads(result)
            assert parsed["success"] is False
            assert "httpx is required" in parsed["error"]
