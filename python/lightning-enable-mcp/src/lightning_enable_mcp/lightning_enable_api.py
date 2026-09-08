"""
Lightning Enable API Client

Client for calling the Lightning Enable API to create L402 challenges and verify payments.
Used by merchants/producers who want AI agents to charge other agents for access.
"""

import logging
import os
import time
from typing import Any
from urllib.parse import quote

import httpx

from .config import get_config_service

logger = logging.getLogger("lightning-enable-mcp.api")

DEFAULT_BASE_URL = "https://api.lightningenable.com"
REQUEST_TIMEOUT = 30.0

#: Shown when a producer-setup call is made without a merchant API key. Names both places
#: the key can live and both ways to get one, because an agent that hits this has no other
#: route forward.
PRODUCER_API_KEY_REQUIRED = (
    "Lightning Enable API key not configured. "
    "Set LIGHTNING_ENABLE_API_KEY environment variable or add 'lightningEnableApiKey' to "
    "~/.lightning-enable/config.json. "
    "Requires an Agentic Commerce subscription at https://lightningenable.com. "
    "Get an API key: 30-day free trial at "
    "https://api.lightningenable.com/Checkout?plan=individual&utm_source=mcp&utm_medium=tool-hint&utm_campaign=gtm-aug-2026 "
    "— or call the `create_lightning_enable_account` tool to sign up right here."
)


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
        proxy_id: str,
        reason: str | None,
    ) -> dict[str, Any]:
        """
        Take a published listing down via the L402 proxy management API.

        Targets the *ungated* proxy pipeline that the live marketplace listings
        use (POST /api/proxy/{proxyId}/unpublish): soft-retires the proxy (stops
        the L402 endpoint serving and republishing) and emits the on-Nostr removal
        (NIP-09 kind 5 + status=removed 38400). Requires an API key.

        Returns:
            Dict with success, proxyId, retired, alreadyRetired, or error.
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

        path_proxy = quote(proxy_id, safe="")
        request_body: dict[str, Any] = {}
        if reason:
            request_body["reason"] = reason

        try:
            response = await self._client.post(
                f"{self._base_url}/api/proxy/{path_proxy}/unpublish",
                json=request_body,
            )
            data = self._safe_json(response)

            if response.status_code >= 400:
                return {"success": False, "error": self._error_message(response, data)}

            return {
                "success": True,
                "proxyId": (data.get("proxyId") if isinstance(data, dict) else None)
                or proxy_id,
                "retired": data.get("retired") if isinstance(data, dict) else None,
                "alreadyRetired": (
                    data.get("alreadyRetired") if isinstance(data, dict) else None
                ),
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

    # =========================================================================
    # Producer setup operations (the seller side of the account)
    #
    # Everything a merchant has to do BEFORE minting a challenge is worth
    # anything: point payouts at their own wallet, register an API, price its
    # endpoints, list it, and read back what was minted. Each maps to one route
    # on the Lightning Enable API and is called by the `l402_producer` tool.
    #
    # These go through `_producer_request`, which is RFC 9457-aware: the API
    # answers errors as application/problem+json carrying `type`/`title`/`detail`
    # ALONGSIDE the legacy `error`/`message` members, and an agent needs the
    # prose AND the stable slug. The two original methods above keep their own
    # narrower error handling so their published result shape cannot shift.
    # =========================================================================

    @property
    def base_url(self) -> str:
        """The Lightning Enable API this client talks to (no trailing slash)."""
        return self._base_url

    async def _producer_request(
        self,
        method: str,
        path: str,
        *,
        json_body: dict[str, Any] | None = None,
        params: dict[str, str] | None = None,
    ) -> dict[str, Any]:
        """One producer-setup API call.

        Returns ``{"success": True, "data": {...}, "httpStatus": n}`` or a failure dict
        carrying the API's own error members. The API key rides in a header set once in
        ``__init__`` and is never part of a result.
        """
        if not self.is_configured:
            return {"success": False, "error": PRODUCER_API_KEY_REQUIRED}

        try:
            response = await self._client.request(
                method,
                f"{self._base_url}{path}",
                json=json_body,
                params=params,
            )
        except httpx.TimeoutException:
            return {
                "success": False,
                "error": f"Request timed out calling {method} {path}",
            }
        except httpx.HTTPError as e:
            return {"success": False, "error": f"HTTP error calling {method} {path}: {e}"}

        data = self._safe_json(response)
        if response.status_code >= 400:
            return {"success": False, **self._problem_error(response, data)}

        return {
            "success": True,
            "data": data if isinstance(data, dict) else {},
            "httpStatus": response.status_code,
        }

    async def save_nwc_connection(self, nwc_connection_string: str) -> dict[str, Any]:
        """``PUT /api/merchant/nwc-connection`` — store the merchant's receiving wallet.

        The argument is a live wallet credential. It is sent, never returned, never
        logged, and never placed in an error message.
        """
        return await self._producer_request(
            "PUT",
            "/api/merchant/nwc-connection",
            json_body={"nwcConnectionString": nwc_connection_string},
        )

    async def set_payment_provider(self, provider: str) -> dict[str, Any]:
        """``PUT /api/merchant/payment-provider`` — pick the lane invoices are minted on."""
        return await self._producer_request(
            "PUT",
            "/api/merchant/payment-provider",
            json_body={"provider": provider},
        )

    async def get_merchant_account(self) -> dict[str, Any]:
        """``GET /api/merchant/me`` — plan, entitlements and onboarding flags."""
        return await self._producer_request("GET", "/api/merchant/me")

    async def get_quickstart(self) -> dict[str, Any]:
        """``GET /api/merchant/quickstart`` — the onboarding checklist, when present."""
        return await self._producer_request("GET", "/api/merchant/quickstart")

    async def list_challenges(
        self,
        status: str | None = None,
        limit: int = 20,
        offset: int = 0,
    ) -> dict[str, Any]:
        """``GET /api/l402/challenges`` — this merchant's minted challenges."""
        params: dict[str, str] = {"limit": str(limit), "offset": str(offset)}
        if status:
            params["status"] = status
        return await self._producer_request(
            "GET", "/api/l402/challenges", params=params
        )

    async def create_proxy(
        self,
        name: str,
        target_base_url: str,
        description: str | None = None,
        default_price_sats: int = 10,
    ) -> dict[str, Any]:
        """``POST /api/proxy`` — register an upstream API for L402 monetization."""
        body: dict[str, Any] = {
            "name": name,
            "targetBaseUrl": target_base_url,
            "defaultPriceSats": default_price_sats,
        }
        if description:
            body["description"] = description
        return await self._producer_request("POST", "/api/proxy", json_body=body)

    async def rename_proxy(self, proxy_id: str, name: str) -> dict[str, Any]:
        """``PUT /api/proxy/{proxyId}`` — set the proxy name.

        The manifest's ``service.name`` is the proxy's own ``Name``, so publishing under a
        different service name is a proxy update, not a manifest-settings field.
        """
        return await self._producer_request(
            "PUT", f"/api/proxy/{quote(proxy_id, safe='')}", json_body={"name": name}
        )

    async def create_manifest_endpoint(
        self,
        proxy_id: str,
        endpoint_id: str,
        path: str,
        http_method: str,
        summary: str | None,
        base_price_sats: int,
    ) -> dict[str, Any]:
        """``POST /api/proxy/{proxyId}/manifest/endpoints`` — price one route."""
        body: dict[str, Any] = {
            "endpointId": endpoint_id,
            "path": path,
            "httpMethod": http_method,
            "basePriceSats": base_price_sats,
        }
        if summary:
            body["summary"] = summary
        return await self._producer_request(
            "POST",
            f"/api/proxy/{quote(proxy_id, safe='')}/manifest/endpoints",
            json_body=body,
        )

    async def update_manifest_settings(
        self,
        proxy_id: str,
        service_description: str | None = None,
        categories: list[str] | None = None,
    ) -> dict[str, Any]:
        """``PUT /api/proxy/{proxyId}/manifest/settings`` — publish and list the manifest."""
        body: dict[str, Any] = {
            "manifestEnabled": True,
            "manifestPubliclyListed": True,
        }
        if service_description:
            body["serviceDescription"] = service_description
        if categories:
            body["categories"] = categories
        return await self._producer_request(
            "PUT",
            f"/api/proxy/{quote(proxy_id, safe='')}/manifest/settings",
            json_body=body,
        )

    @staticmethod
    def _problem_error(response: httpx.Response, data: Any) -> dict[str, Any]:
        """The API's own error members, as an agent-readable dict.

        Prefers RFC 9457 ``detail`` (the prose about THIS occurrence), then the legacy
        ``message``/``error``, then ``title``. ``type`` and the ``error`` slug are carried
        separately so a caller can branch on a stable identifier instead of prose. Keys
        that are not present are dropped rather than reported as null.
        """
        result: dict[str, Any] = {
            "error": f"API returned {response.status_code}",
            "httpStatus": response.status_code,
        }
        if not isinstance(data, dict):
            return result

        for key in ("detail", "message", "error", "title"):
            value = data.get(key)
            if isinstance(value, str) and value.strip():
                result["error"] = value
                break

        problem_type = data.get("type")
        if isinstance(problem_type, str) and problem_type:
            result["errorType"] = problem_type

        code = data.get("error")
        if isinstance(code, str) and code:
            result["errorCode"] = code

        # ASP.NET model-validation bodies put the per-field failures here and carry no
        # detail/message at all, so without this the agent only sees "One or more
        # validation errors occurred" and cannot tell which field it got wrong.
        validation = data.get("errors")
        if isinstance(validation, dict) and validation:
            result["validationErrors"] = validation

        return result

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
