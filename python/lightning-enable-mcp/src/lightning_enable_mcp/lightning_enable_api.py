"""
Lightning Enable API Client

Client for calling the Lightning Enable API to create L402 challenges and verify payments.
Used by merchants/producers who want AI agents to charge other agents for access.
"""

import logging
import os
import time
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

    # =========================================================================
    # Agent Service Agreement (ASA) operations
    #
    # Mirrors the .NET AgentService.cs. v1 uses the Lightning Enable REST API
    # for discovery, publishing, requests, and attestations. Discovery works
    # without an API key (public registry); publishing/requests/attestations
    # require LIGHTNING_ENABLE_API_KEY.
    # =========================================================================

    async def discover_capabilities(
        self,
        category: str | None,
        hashtags: list[str] | None,
        query: str | None,
        limit: int,
    ) -> dict[str, Any]:
        """
        Query the agent capability registry for services matching filters.

        Falls back to the manifest registry if the dedicated capabilities
        endpoint is unavailable (matching .NET FallbackRegistryDiscoveryAsync).

        Returns:
            Dict with success, capabilities (list), total, or error.
        """
        params: dict[str, str] = {"limit": str(min(limit, 100))}
        if category and category.strip():
            params["category"] = category
        if query and query.strip():
            params["q"] = query
        if hashtags:
            params["hashtags"] = ",".join(hashtags)

        try:
            response = await self._client.get(
                f"{self._base_url}/api/agents/capabilities",
                params=params,
            )

            if response.status_code >= 400:
                # Fall back to the manifest registry for discovery
                return await self._fallback_registry_discovery(category, query, limit)

            data = response.json()
            root = data if isinstance(data, dict) else {}

            items = root.get("items")
            if items is None:
                items = root.get("capabilities")
            if items is None and isinstance(data, list):
                items = data

            capabilities = []
            if isinstance(items, list):
                for item in items:
                    capabilities.append(self._parse_capability(item))

            total = root.get("total", len(capabilities))

            return {
                "success": True,
                "capabilities": capabilities,
                "total": total,
            }

        except httpx.TimeoutException:
            return {"success": False, "error": "Discovery failed: Request timed out"}
        except Exception as e:
            return {"success": False, "error": f"Discovery failed: {e}"}

    async def _fallback_registry_discovery(
        self,
        category: str | None,
        query: str | None,
        limit: int,
    ) -> dict[str, Any]:
        """
        Falls back to the manifest registry if the dedicated capabilities
        endpoint is not available. Allows discovery to work even before
        /api/agents/capabilities is deployed.
        """
        params: dict[str, str] = {"pageSize": str(min(limit, 100))}
        if query and query.strip():
            params["q"] = query
        if category and category.strip():
            params["category"] = category

        try:
            response = await self._client.get(
                f"{self._base_url}/api/manifests/registry",
                params=params,
            )

            if response.status_code >= 400:
                return {
                    "success": False,
                    "error": (
                        f"Registry search failed with status {response.status_code}. "
                        "The agent capability registry may be temporarily unavailable."
                    ),
                }

            data = response.json()
            root = data if isinstance(data, dict) else {}

            capabilities = []
            items = root.get("items")
            if isinstance(items, list):
                for item in items:
                    cats = item.get("parsedCategories")
                    categories = (
                        [c for c in cats if c]
                        if isinstance(cats, list)
                        else []
                    )
                    capabilities.append({
                        "eventId": None,
                        "serviceId": item.get("name"),
                        "pubkey": None,
                        "content": item.get("description"),
                        "categories": categories,
                        "hashtags": [],
                        "priceSats": item.get("defaultPriceSats", 0) or 0,
                        "l402Endpoint": item.get("proxyBaseUrl"),
                        "createdAt": None,
                    })

            total = root.get("total", len(capabilities))

            return {
                "success": True,
                "capabilities": capabilities,
                "total": total,
            }

        except httpx.TimeoutException:
            return {
                "success": False,
                "error": "Fallback registry discovery failed: Request timed out",
            }
        except Exception as e:
            return {
                "success": False,
                "error": f"Fallback registry discovery failed: {e}",
            }

    @staticmethod
    def _parse_capability(item: dict[str, Any]) -> dict[str, Any]:
        """Parse a single capability item from a discovery response."""
        if not isinstance(item, dict):
            return {
                "eventId": None,
                "serviceId": None,
                "pubkey": None,
                "content": None,
                "categories": [],
                "hashtags": [],
                "priceSats": 0,
                "l402Endpoint": None,
                "createdAt": None,
            }

        cats = item.get("categories")
        categories = [c for c in cats if c] if isinstance(cats, list) else []
        tags = item.get("hashtags")
        hashtags = [t for t in tags if t] if isinstance(tags, list) else []

        return {
            "eventId": item.get("eventId") or item.get("id"),
            "serviceId": item.get("serviceId") or item.get("dTag"),
            "pubkey": item.get("pubkey"),
            "content": item.get("content"),
            "categories": categories,
            "hashtags": hashtags,
            "priceSats": item.get("priceSats", 0) or 0,
            "l402Endpoint": item.get("l402Endpoint"),
            "createdAt": item.get("createdAt"),
        }

    async def publish_capability(
        self,
        service_id: str,
        categories: list[str],
        content: str,
        price_sats: int,
        l402_endpoint: str | None,
        target_url: str | None,
        hashtags: list[str] | None,
    ) -> dict[str, Any]:
        """
        Publish an agent capability advertisement (kind 38400 event).
        Requires an API key for authentication.

        Returns:
            Dict with success, eventId, l402Endpoint, or error.
        """
        if not self.is_configured:
            return {
                "success": False,
                "error": (
                    "Lightning Enable API key not configured. "
                    "Set LIGHTNING_ENABLE_API_KEY environment variable or add "
                    "'lightningEnableApiKey' to ~/.lightning-enable/config.json."
                ),
            }

        request_body: dict[str, Any] = {
            "serviceId": service_id,
            "categories": categories,
            "content": content,
            "priceSats": price_sats,
            "l402Endpoint": l402_endpoint,
            "targetUrl": target_url,
            "hashtags": hashtags or [],
        }

        try:
            response = await self._client.post(
                f"{self._base_url}/api/agents/capabilities",
                json=request_body,
            )
            data = self._safe_json(response)

            if response.status_code >= 400:
                return {"success": False, "error": self._error_message(response, data)}

            return {
                "success": True,
                "eventId": data.get("eventId") if isinstance(data, dict) else None,
                "l402Endpoint": (
                    data.get("l402Endpoint") if isinstance(data, dict) else None
                ) or l402_endpoint,
            }

        except httpx.TimeoutException:
            return {"success": False, "error": "Request timed out"}
        except httpx.HTTPError as e:
            return {"success": False, "error": f"HTTP error: {e}"}

    async def unpublish_capability(
        self,
        pubkey: str,
        service_id: str,
        mode: str,
        reason: str | None,
    ) -> dict[str, Any]:
        """
        Take a published capability down (NIP-A5 listing lifecycle).

        Calls the backend unpublish endpoint, which soft-retires the L402 proxy
        and emits the on-Nostr removal (NIP-09 kind 5 + status=removed 38400).
        Requires an API key.

        Returns:
            Dict with success, serviceId, proxyId, mode, retired, or error.
        """
        if not self.is_configured:
            return {
                "success": False,
                "error": (
                    "Lightning Enable API key not configured. "
                    "Set LIGHTNING_ENABLE_API_KEY environment variable or add "
                    "'lightningEnableApiKey' to ~/.lightning-enable/config.json."
                ),
            }

        from urllib.parse import quote

        path_pubkey = quote(pubkey, safe="")
        path_service = quote(service_id, safe="")
        request_body: dict[str, Any] = {"mode": mode}
        if reason:
            request_body["reason"] = reason

        try:
            response = await self._client.post(
                f"{self._base_url}/api/agents/{path_pubkey}/capabilities/{path_service}/unpublish",
                json=request_body,
            )
            data = self._safe_json(response)

            if response.status_code >= 400:
                return {"success": False, "error": self._error_message(response, data)}

            return {
                "success": True,
                "serviceId": (
                    data.get("serviceId") if isinstance(data, dict) else None
                )
                or service_id,
                "proxyId": data.get("proxyId") if isinstance(data, dict) else None,
                "mode": (data.get("mode") if isinstance(data, dict) else None) or mode,
                "retired": data.get("retired") if isinstance(data, dict) else None,
            }

        except httpx.TimeoutException:
            return {"success": False, "error": "Request timed out"}
        except httpx.HTTPError as e:
            return {"success": False, "error": f"HTTP error: {e}"}

    async def request_service(
        self,
        capability_event_id: str,
        budget_sats: int,
        parameters: str | None,
    ) -> dict[str, Any]:
        """
        Send a service request referencing a provider's capability (kind 38401).
        Requires an API key.

        Returns:
            Dict with success, requestEventId, l402Endpoint, or error.
        """
        if not self.is_configured:
            return {
                "success": False,
                "error": (
                    "Lightning Enable API key not configured. "
                    "Set LIGHTNING_ENABLE_API_KEY environment variable or add "
                    "'lightningEnableApiKey' to ~/.lightning-enable/config.json."
                ),
            }

        request_body: dict[str, Any] = {
            "capabilityEventId": capability_event_id,
            "budgetSats": budget_sats,
            "parameters": parameters,
        }

        try:
            response = await self._client.post(
                f"{self._base_url}/api/agents/requests",
                json=request_body,
            )
            data = self._safe_json(response)

            if response.status_code >= 400:
                return {"success": False, "error": self._error_message(response, data)}

            return {
                "success": True,
                "requestEventId": (
                    data.get("requestEventId") if isinstance(data, dict) else None
                ),
                "l402Endpoint": (
                    data.get("l402Endpoint") if isinstance(data, dict) else None
                ),
            }

        except httpx.TimeoutException:
            return {"success": False, "error": "Request timed out"}
        except httpx.HTTPError as e:
            return {"success": False, "error": f"HTTP error: {e}"}

    async def publish_attestation(
        self,
        subject_pubkey: str,
        agreement_id: str,
        rating: int,
        content: str,
        proof: str | None,
    ) -> dict[str, Any]:
        """
        Publish an attestation/review for an agent (kind 38403 event).
        Requires an API key.

        Returns:
            Dict with success, eventId, attestationId, or error.
        """
        if not self.is_configured:
            return {
                "success": False,
                "error": (
                    "Lightning Enable API key not configured. "
                    "Set LIGHTNING_ENABLE_API_KEY environment variable or add "
                    "'lightningEnableApiKey' to ~/.lightning-enable/config.json."
                ),
            }

        # Mirror .NET: att-{agreementId[:16]}-{unix_seconds}
        agreement_prefix = agreement_id[: min(16, len(agreement_id))]
        attestation_id = f"att-{agreement_prefix}-{int(time.time())}"

        request_body: dict[str, Any] = {
            "subjectPubkey": subject_pubkey,
            "agreementId": agreement_id,
            "rating": rating,
            "content": content,
            "proof": proof,
            "attestationId": attestation_id,
        }

        try:
            response = await self._client.post(
                f"{self._base_url}/api/agents/attestations",
                json=request_body,
            )
            data = self._safe_json(response)

            if response.status_code >= 400:
                return {"success": False, "error": self._error_message(response, data)}

            return {
                "success": True,
                "eventId": data.get("eventId") if isinstance(data, dict) else None,
                "attestationId": attestation_id,
            }

        except httpx.TimeoutException:
            return {"success": False, "error": "Request timed out"}
        except httpx.HTTPError as e:
            return {"success": False, "error": f"HTTP error: {e}"}

    async def get_attestations(
        self,
        pubkey: str,
        limit: int,
    ) -> dict[str, Any]:
        """
        Query attestations for an agent's reputation (kind 38403 events).
        Works without an API key.

        Returns:
            Dict with success, attestations (list), or error.
        """
        params = {
            "pubkey": pubkey,
            "limit": str(min(limit, 100)),
        }

        try:
            response = await self._client.get(
                f"{self._base_url}/api/agents/attestations",
                params=params,
            )

            if response.status_code >= 400:
                return {
                    "success": False,
                    "error": f"API returned {response.status_code}",
                }

            data = response.json()
            root = data if isinstance(data, dict) else {}

            items = root.get("items")
            if items is None:
                items = root.get("attestations")
            if items is None and isinstance(data, list):
                items = data

            attestations = []
            if isinstance(items, list):
                for item in items:
                    if not isinstance(item, dict):
                        continue
                    attestations.append({
                        "eventId": item.get("eventId"),
                        "reviewerPubkey": item.get("pubkey")
                        or item.get("reviewerPubkey"),
                        "rating": item.get("rating", 0) or 0,
                        "content": item.get("content"),
                        "agreementId": item.get("agreementId"),
                        "proof": item.get("proof"),
                        "createdAt": item.get("createdAt"),
                    })

            return {"success": True, "attestations": attestations}

        except httpx.TimeoutException:
            return {"success": False, "error": "Query failed: Request timed out"}
        except Exception as e:
            return {"success": False, "error": f"Query failed: {e}"}

    @staticmethod
    def _safe_json(response: httpx.Response) -> Any:
        """Parse a JSON response body, returning None on parse failure."""
        try:
            return response.json()
        except Exception:
            return None

    @staticmethod
    def _error_message(response: httpx.Response, data: Any) -> str:
        """Extract a human-readable error from an error response."""
        error_message = f"API returned {response.status_code}"
        if isinstance(data, dict):
            error_message = data.get("message") or data.get("error") or error_message
        return error_message

    async def close(self) -> None:
        """Close the HTTP client."""
        await self._client.aclose()
