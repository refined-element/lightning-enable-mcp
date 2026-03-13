"""
Lightning Enable API Client

Client for calling the Lightning Enable API to create L402 challenges and verify payments.
Used by merchants/producers who want AI agents to charge other agents for access.
"""

import logging
import os
from typing import Any

import httpx

from .config import get_config_service

logger = logging.getLogger("lightning-enable-mcp.api")

DEFAULT_BASE_URL = "https://api.lightningenable.com"
REQUEST_TIMEOUT = 30.0


class LightningEnableApiClient:
    """
    HTTP client for the Lightning Enable API.

    Reads API key from:
    1. LIGHTNING_ENABLE_API_KEY environment variable
    2. lightningEnableApiKey in ~/.lightning-enable/config.json

    Reads API URL from:
    1. LIGHTNING_ENABLE_API_URL environment variable
    2. Default: https://api.lightningenable.com
    """

    def __init__(self) -> None:
        """Initialize the API client."""
        # Read API key: env var -> config file
        self._api_key = os.getenv("LIGHTNING_ENABLE_API_KEY")
        if not self._api_key or self._api_key.startswith("${"):
            config_service = get_config_service()
            config = config_service.configuration
            self._api_key = getattr(config, "lightning_enable_api_key", None)

        # Read API URL: env var -> default
        base_url = os.getenv("LIGHTNING_ENABLE_API_URL")
        self._base_url = base_url.rstrip("/") if base_url else DEFAULT_BASE_URL

        # Build default headers
        headers: dict[str, str] = {
            "Accept": "application/json",
            "Content-Type": "application/json",
            "User-Agent": "LightningEnable-MCP-Python/1.0",
        }
        if self._api_key:
            headers["X-Api-Key"] = self._api_key

        self._client = httpx.AsyncClient(
            timeout=REQUEST_TIMEOUT,
            headers=headers,
        )

        if self._api_key:
            logger.info("Lightning Enable API client configured with API key")
        else:
            logger.info("Lightning Enable API client initialized without API key (producer tools unavailable)")

    @property
    def is_configured(self) -> bool:
        """Whether the client has an API key configured."""
        return bool(self._api_key)

    async def create_challenge(
        self,
        resource: str,
        price_sats: int,
        description: str | None = None,
    ) -> dict[str, Any]:
        """
        Create an L402 challenge (invoice + macaroon) for a resource.

        Args:
            resource: Resource identifier
            price_sats: Price in satoshis
            description: Optional invoice description

        Returns:
            Dict with success, challenge details, or error
        """
        request_body: dict[str, Any] = {
            "resource": resource,
            "priceSats": price_sats,
        }
        if description:
            request_body["description"] = description

        try:
            response = await self._client.post(
                f"{self._base_url}/api/l402/challenges",
                json=request_body,
            )

            response_data = response.json()

            if response.status_code >= 400:
                error_message = f"API returned {response.status_code}"
                if isinstance(response_data, dict):
                    error_message = response_data.get("message") or response_data.get("error") or error_message
                return {"success": False, "error": error_message}

            return {
                "success": True,
                "challenge": {
                    "invoice": response_data.get("invoice"),
                    "macaroon": response_data.get("macaroon"),
                    "paymentHash": response_data.get("paymentHash"),
                    "expiresAt": response_data.get("expiresAt"),
                },
            }

        except httpx.TimeoutException:
            return {"success": False, "error": "Request timed out"}
        except httpx.HTTPError as e:
            return {"success": False, "error": f"HTTP error: {e}"}
        except Exception as e:
            return {"success": False, "error": str(e)}

    async def verify_token(
        self,
        macaroon: str,
        preimage: str,
    ) -> dict[str, Any]:
        """
        Verify an L402 token (macaroon + preimage) to confirm payment was made.

        Args:
            macaroon: Base64-encoded macaroon
            preimage: Hex-encoded preimage

        Returns:
            Dict with success, valid flag, resource, or error
        """
        request_body = {
            "macaroon": macaroon,
            "preimage": preimage,
        }

        try:
            response = await self._client.post(
                f"{self._base_url}/api/l402/challenges/verify",
                json=request_body,
            )

            response_data = response.json()

            if response.status_code >= 400:
                error_message = f"API returned {response.status_code}"
                if isinstance(response_data, dict):
                    error_message = response_data.get("message") or response_data.get("error") or error_message
                return {"success": False, "error": error_message}

            return {
                "success": True,
                "valid": response_data.get("valid", False),
                "resource": response_data.get("resource"),
            }

        except httpx.TimeoutException:
            return {"success": False, "error": "Request timed out"}
        except httpx.HTTPError as e:
            return {"success": False, "error": f"HTTP error: {e}"}
        except Exception as e:
            return {"success": False, "error": str(e)}

    async def close(self) -> None:
        """Close the HTTP client."""
        await self._client.aclose()
