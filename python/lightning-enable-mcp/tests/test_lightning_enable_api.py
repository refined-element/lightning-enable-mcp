"""
Tests for LightningEnableApiClient
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock, patch

import httpx

from lightning_enable_mcp.lightning_enable_api import LightningEnableApiClient


class TestLightningEnableApiClient:
    """Tests for LightningEnableApiClient."""

    def test_not_configured_without_api_key(self):
        """Test client is not configured when no API key is set."""
        with patch.dict("os.environ", {}, clear=True):
            with patch("lightning_enable_mcp.lightning_enable_api.get_config_service") as mock_config:
                mock_config.return_value.configuration = MagicMock(lightning_enable_api_key=None)
                client = LightningEnableApiClient()
                assert client.is_configured is False

    def test_configured_with_env_var(self):
        """Test client is configured when LIGHTNING_ENABLE_API_KEY env var is set."""
        with patch.dict("os.environ", {"LIGHTNING_ENABLE_API_KEY": "test-key"}, clear=False):
            with patch("lightning_enable_mcp.lightning_enable_api.get_config_service") as mock_config:
                mock_config.return_value.configuration = MagicMock(lightning_enable_api_key=None)
                client = LightningEnableApiClient()
                assert client.is_configured is True

    def test_configured_with_config_file(self):
        """Test client is configured when lightningEnableApiKey is in config."""
        with patch.dict("os.environ", {}, clear=True):
            with patch("lightning_enable_mcp.lightning_enable_api.get_config_service") as mock_config:
                mock_config.return_value.configuration = MagicMock(
                    lightning_enable_api_key="config-key"
                )
                client = LightningEnableApiClient()
                assert client.is_configured is True

    def test_env_var_placeholder_falls_back_to_config(self):
        """Test that ${PLACEHOLDER} env var values fall back to config."""
        with patch.dict(
            "os.environ", {"LIGHTNING_ENABLE_API_KEY": "${LIGHTNING_ENABLE_API_KEY}"}, clear=False
        ):
            with patch("lightning_enable_mcp.lightning_enable_api.get_config_service") as mock_config:
                mock_config.return_value.configuration = MagicMock(
                    lightning_enable_api_key="config-key"
                )
                client = LightningEnableApiClient()
                assert client.is_configured is True

    def test_custom_base_url_from_env(self):
        """Test that LIGHTNING_ENABLE_API_URL env var is used."""
        with patch.dict(
            "os.environ",
            {"LIGHTNING_ENABLE_API_URL": "https://custom.api.com/", "LIGHTNING_ENABLE_API_KEY": "k"},
            clear=False,
        ):
            with patch("lightning_enable_mcp.lightning_enable_api.get_config_service") as mock_config:
                mock_config.return_value.configuration = MagicMock(lightning_enable_api_key=None)
                client = LightningEnableApiClient()
                assert client._base_url == "https://custom.api.com"  # trailing slash stripped

    @pytest.mark.asyncio
    async def test_create_challenge_success(self):
        """Test successful challenge creation API call."""
        with patch.dict("os.environ", {"LIGHTNING_ENABLE_API_KEY": "test-key"}, clear=False):
            with patch("lightning_enable_mcp.lightning_enable_api.get_config_service") as mock_config:
                mock_config.return_value.configuration = MagicMock(lightning_enable_api_key=None)
                client = LightningEnableApiClient()

                mock_response = MagicMock()
                mock_response.status_code = 200
                mock_response.json.return_value = {
                    "invoice": "lnbc100n1...",
                    "macaroon": "base64mac==",
                    "paymentHash": "hash123",
                    "expiresAt": "2026-03-13T12:00:00Z",
                }

                with patch.object(client._client, "post", new_callable=AsyncMock) as mock_post:
                    mock_post.return_value = mock_response
                    result = await client.create_challenge("my-resource", 100, "test description")

                    assert result["success"] is True
                    assert result["challenge"]["invoice"] == "lnbc100n1..."
                    assert result["challenge"]["macaroon"] == "base64mac=="

                    mock_post.assert_called_once()
                    call_kwargs = mock_post.call_args
                    assert "/api/l402/challenges" in call_kwargs[0][0]

    @pytest.mark.asyncio
    async def test_create_challenge_api_error(self):
        """Test challenge creation with API error."""
        with patch.dict("os.environ", {"LIGHTNING_ENABLE_API_KEY": "test-key"}, clear=False):
            with patch("lightning_enable_mcp.lightning_enable_api.get_config_service") as mock_config:
                mock_config.return_value.configuration = MagicMock(lightning_enable_api_key=None)
                client = LightningEnableApiClient()

                mock_response = MagicMock()
                mock_response.status_code = 403
                mock_response.json.return_value = {"error": "Subscription required"}

                with patch.object(client._client, "post", new_callable=AsyncMock) as mock_post:
                    mock_post.return_value = mock_response
                    result = await client.create_challenge("my-resource", 100)

                    assert result["success"] is False
                    assert "Subscription required" in result["error"]

    @pytest.mark.asyncio
    async def test_verify_token_valid(self):
        """Test successful token verification."""
        with patch.dict("os.environ", {"LIGHTNING_ENABLE_API_KEY": "test-key"}, clear=False):
            with patch("lightning_enable_mcp.lightning_enable_api.get_config_service") as mock_config:
                mock_config.return_value.configuration = MagicMock(lightning_enable_api_key=None)
                client = LightningEnableApiClient()

                mock_response = MagicMock()
                mock_response.status_code = 200
                mock_response.json.return_value = {
                    "valid": True,
                    "resource": "my-resource",
                }

                with patch.object(client._client, "post", new_callable=AsyncMock) as mock_post:
                    mock_post.return_value = mock_response
                    result = await client.verify_token("macaroon==", "preimage123")

                    assert result["success"] is True
                    assert result["valid"] is True
                    assert result["resource"] == "my-resource"

    @pytest.mark.asyncio
    async def test_verify_token_invalid(self):
        """Test token verification with invalid token."""
        with patch.dict("os.environ", {"LIGHTNING_ENABLE_API_KEY": "test-key"}, clear=False):
            with patch("lightning_enable_mcp.lightning_enable_api.get_config_service") as mock_config:
                mock_config.return_value.configuration = MagicMock(lightning_enable_api_key=None)
                client = LightningEnableApiClient()

                mock_response = MagicMock()
                mock_response.status_code = 200
                mock_response.json.return_value = {
                    "valid": False,
                }

                with patch.object(client._client, "post", new_callable=AsyncMock) as mock_post:
                    mock_post.return_value = mock_response
                    result = await client.verify_token("macaroon==", "wrong")

                    assert result["success"] is True
                    assert result["valid"] is False

    @pytest.mark.asyncio
    async def test_timeout_handling(self):
        """Test timeout error handling."""
        with patch.dict("os.environ", {"LIGHTNING_ENABLE_API_KEY": "test-key"}, clear=False):
            with patch("lightning_enable_mcp.lightning_enable_api.get_config_service") as mock_config:
                mock_config.return_value.configuration = MagicMock(lightning_enable_api_key=None)
                client = LightningEnableApiClient()

                with patch.object(client._client, "post", new_callable=AsyncMock) as mock_post:
                    mock_post.side_effect = httpx.TimeoutException("Request timed out")
                    result = await client.create_challenge("my-resource", 100)

                    assert result["success"] is False
                    assert "timed out" in result["error"].lower()
