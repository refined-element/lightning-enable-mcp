"""
Discover API Tool

Discover L402-enabled API endpoints by searching the registry
or fetching a manifest from a specific URL.

Two modes:
1. Registry search: query the L402 API registry by keyword/category
2. Manifest fetch: fetch a specific API's manifest from well-known locations
"""

import json
import logging
import math
import os
from . import sanitize_error
from typing import TYPE_CHECKING, Any
from urllib.parse import quote as url_quote

if TYPE_CHECKING:
    from ..budget_service import BudgetService

try:
    import httpx
except ImportError:
    httpx = None  # type: ignore

logger = logging.getLogger("lightning-enable-mcp.tools.discover_api")

WELL_KNOWN_PATHS = [
    "/.well-known/l402-manifest.json",
    "/l402-manifest.json",
    "/l402.json",
]


# The entire Bitcoin supply, in sats. A single call cannot cost more than every
# bitcoin that will ever exist, so anything above this is malformed rather than
# expensive. Bounding here also keeps an unbounded JSON int away from float(),
# which raises OverflowError above ~1.8e308.
MAX_PRICE_SATS = 21_000_000 * 100_000_000


def _parse_price_sats(value: Any) -> int | float | None:
    """
    Strictly parse a price (in sats) out of an UNTRUSTED manifest / registry entry.

    Manifests are third-party documents fetched from a public registry or an
    arbitrary URL — an attacker controls every byte. Anything that is not a
    positive, finite, plausible number is rejected as UNKNOWN rather than coerced
    into a price or waved through as free.

    Returns None when the value cannot be trusted as a price. Python type traps
    handled explicitly:
      - bool is a subclass of int, so `True` would otherwise read as 1 sat.
      - a JSON string ("100") is not a number, however numeric it looks.
      - inf/nan are floats but would blow up int() or the affordability division.
      - 0 / negative prices are not "free" — they are malformed.
      - JSON ints are unbounded; a huge one would raise OverflowError inside
        float(), which the caller's catch-all would silently turn into a
        missing budget block.
    """
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    if isinstance(value, float) and not math.isfinite(value):
        return None
    if value <= 0 or value > MAX_PRICE_SATS:
        return None
    return value


def _affordable_calls(
    remaining_sats: int | None, price_sats: int | float | None
) -> int | str:
    """
    How many calls the remaining budget covers at `price_sats` each.

    Returns the string "unknown" unless BOTH inputs are known. It must NEVER
    return "unlimited" or "free": an unknown price and an undeterminable budget
    are exactly the cases where an agent must not be told it can keep spending.
    `remaining_sats=None` means the budget could not be determined (e.g. the BTC
    price is unavailable) — that is unknown, not zero.
    """
    if remaining_sats is None or price_sats is None:
        return "unknown"
    return int(remaining_sats // price_sats)


def _get_registry_base_url() -> str:
    """Get the L402 registry base URL from environment variables."""
    url = os.getenv("L402_REGISTRY_URL")
    if url:
        return url.rstrip("/")

    url = os.getenv("LIGHTNING_ENABLE_API_URL")
    if url:
        return url.rstrip("/")

    return "https://api.lightningenable.com"


def _get_tried_urls(url: str) -> list[str]:
    """Get the list of URLs that would be tried for manifest discovery."""
    base_url = url.rstrip("/")
    urls = []

    if base_url.lower().endswith(".json"):
        urls.append(base_url)

    for path in WELL_KNOWN_PATHS:
        urls.append(base_url + path)

    if not base_url.lower().endswith(".json"):
        urls.append(base_url)

    return urls


async def _try_fetch(client: "httpx.AsyncClient", url: str) -> str | None:
    """
    Try to fetch a manifest from a URL.

    Returns the JSON content if it looks like a valid L402 manifest, else None.
    """
    try:
        response = await client.get(url)
        if response.status_code >= 400:
            return None

        content = response.text
        doc = json.loads(content)

        # Quick validation: must have expected structure
        if any(key in doc for key in ("endpoints", "l402", "service")):
            return content

        return None
    except Exception:
        return None


async def _fetch_manifest(client: "httpx.AsyncClient", url: str) -> tuple[str | None, str | None]:
    """
    Fetch manifest from well-known locations.

    Returns (json_content, manifest_url) or (None, None).
    """
    base_url = url.rstrip("/")

    # If URL ends in .json, try it directly first
    if base_url.lower().endswith(".json"):
        content = await _try_fetch(client, base_url)
        if content:
            return content, base_url

    # Try well-known paths
    for path in WELL_KNOWN_PATHS:
        full_url = base_url + path
        content = await _try_fetch(client, full_url)
        if content:
            return content, full_url

    # Try the URL directly if not already tried
    if not base_url.lower().endswith(".json"):
        content = await _try_fetch(client, base_url)
        if content:
            return content, base_url

    return None, None


def _extract_service_info(root: dict[str, Any]) -> dict[str, Any]:
    """Extract service info from manifest."""
    info: dict[str, Any] = {}
    service = root.get("service", {})
    if not isinstance(service, dict):
        return info

    for key in ("name", "description", "base_url", "documentation_url"):
        if key in service:
            info[key] = service[key]

    cats = service.get("categories")
    if isinstance(cats, list):
        info["categories"] = cats

    return info


def _extract_l402_info(root: dict[str, Any]) -> dict[str, Any]:
    """Extract L402 info from manifest."""
    info: dict[str, Any] = {}
    l402 = root.get("l402", {})
    if not isinstance(l402, dict):
        return info

    if "default_price_sats" in l402:
        info["default_price_sats"] = l402["default_price_sats"]
    if "payment_flow" in l402:
        info["payment_flow"] = l402["payment_flow"]

    caps = l402.get("capabilities")
    if isinstance(caps, dict):
        caps_dict: dict[str, Any] = {}
        if "preimage_in_response" in caps:
            caps_dict["preimage_in_response"] = caps["preimage_in_response"]
        if "supported_currencies" in caps and isinstance(caps["supported_currencies"], list):
            caps_dict["supported_currencies"] = caps["supported_currencies"]
        info["capabilities"] = caps_dict

    return info


def _extract_endpoints(root: dict[str, Any]) -> list[dict[str, Any]]:
    """Extract endpoints from manifest."""
    endpoints_list = []
    endpoints_array = root.get("endpoints", [])
    if not isinstance(endpoints_array, list):
        return endpoints_list

    for ep in endpoints_array:
        if not isinstance(ep, dict):
            continue

        endpoint: dict[str, Any] = {}
        for key in ("id", "path", "method", "summary", "description"):
            if key in ep:
                endpoint[key] = ep[key]

        if "l402_enabled" in ep:
            endpoint["l402_enabled"] = ep["l402_enabled"]

        pricing = ep.get("pricing")
        if isinstance(pricing, dict):
            pricing_dict: dict[str, Any] = {}
            if "model" in pricing:
                pricing_dict["model"] = pricing["model"]
            if "base_price_sats" in pricing:
                pricing_dict["base_price_sats"] = pricing["base_price_sats"]
            endpoint["pricing"] = pricing_dict

        tags = ep.get("tags")
        if isinstance(tags, list):
            endpoint["tags"] = tags

        if ep.get("deprecated"):
            endpoint["deprecated"] = True

        endpoints_list.append(endpoint)

    return endpoints_list


async def discover_api(
    url: str | None = None,
    query: str | None = None,
    category: str | None = None,
    budget_aware: bool = True,
    budget_service: "BudgetService | None" = None,
) -> str:
    """
    Discover L402-enabled APIs.

    Use 'query' to search the registry for available APIs by keyword,
    or use 'url' to fetch a specific API's manifest with full endpoint
    details and pricing. Use 'category' to browse by category.
    With budget_aware=True, shows how many calls you can afford.

    Args:
        url: Base URL of the L402-enabled API, or direct URL to the manifest JSON file
        query: Search the L402 API registry by keyword (e.g., 'weather', 'ai', 'geocoding')
        category: Filter registry results by category (e.g., 'ai', 'data', 'finance')
        budget_aware: If True, annotate endpoints with affordable call counts. Default: True
        budget_service: BudgetService for budget annotations

    Returns:
        JSON with discovered APIs or manifest details
    """
    if httpx is None:
        return json.dumps({
            "success": False,
            "error": "httpx is required for discover_api. Install with: pip install httpx"
        })

    try:
        # Route: URL provided -> fetch manifest
        if url and url.strip():
            return await _fetch_and_format_manifest(
                url.strip(), budget_aware, budget_service
            )

        # Route: query/category provided -> search registry
        if (query and query.strip()) or (category and category.strip()):
            return await _search_registry(
                query, category, budget_aware, budget_service
            )

        # No params -> usage error
        return json.dumps({
            "success": False,
            "error": "Please provide either a 'url' to fetch an API manifest, or a 'query'/'category' to search the registry.",
            "examples": [
                {"description": "Search for weather APIs", "call": 'discover_api(query="weather")'},
                {"description": "Browse AI category", "call": 'discover_api(category="ai")'},
                {"description": "Get full details for a specific API", "call": 'discover_api(url="https://api.example.com")'},
            ]
        }, indent=2)

    except Exception as e:
        return json.dumps({
            "success": False,
            "error": f"Error discovering API: {sanitize_error(str(e))}"
        })


async def _search_registry(
    query: str | None,
    category: str | None,
    budget_aware: bool,
    budget_service: "BudgetService | None",
) -> str:
    """Search the L402 API registry for available APIs."""
    registry_url = _get_registry_base_url()
    query_params = ["pageSize=20"]

    if query and query.strip():
        query_params.append(f"q={url_quote(query.strip())}")
    if category and category.strip():
        query_params.append(f"category={url_quote(category.strip())}")

    request_url = f"{registry_url}/api/manifests/registry?{'&'.join(query_params)}"

    async with httpx.AsyncClient(timeout=15.0, headers={
        "Accept": "application/json",
        "User-Agent": "LightningEnable-MCP/1.0",
    }) as client:
        response = await client.get(request_url)

        if response.status_code >= 400:
            return json.dumps({
                "success": False,
                "error": f"Registry search failed with status {response.status_code}.",
                "registry_url": request_url,
                "hint": "The L402 API registry may be temporarily unavailable. Try again later or use discover_api(url=...) to fetch a specific manifest directly."
            })

        data = response.json()

    # Derive the remaining budget ONCE for all results. None means it could not
    # be determined, which renders as "unknown" — never 0, never "unlimited".
    budget_status: dict[str, Any] | None = None
    remaining_sats: int | None = None
    if budget_aware and budget_service:
        try:
            budget_status = budget_service.get_status()
            remaining_sats = await budget_service.get_remaining_session_sats()
        except Exception:
            pass

    items = []
    raw_items = data.get("items", [])
    if isinstance(raw_items, list):
        for item in raw_items:
            if not isinstance(item, dict):
                continue

            entry: dict[str, Any] = {}
            for key in ("name", "description", "manifestUrl", "proxyBaseUrl", "documentationUrl"):
                if key in item:
                    # Convert camelCase to snake_case for consistency
                    snake_key = key
                    if key == "manifestUrl":
                        snake_key = "manifest_url"
                    elif key == "proxyBaseUrl":
                        snake_key = "proxy_base_url"
                    elif key == "documentationUrl":
                        snake_key = "documentation_url"
                    entry[snake_key] = item[key]

            if "parsedCategories" in item and isinstance(item["parsedCategories"], list):
                entry["categories"] = item["parsedCategories"]
            if "endpointCount" in item:
                entry["endpoint_count"] = item["endpointCount"]
            if "defaultPriceSats" in item:
                entry["default_price_sats"] = item["defaultPriceSats"]

            # Budget annotation per result. Registry entries are third-party data
            # too, so the advertised price gets the same strict parsing as a
            # manifest's — an unusable price reads "unknown", not "unlimited".
            if budget_aware and budget_service and "default_price_sats" in entry:
                entry["affordable_calls"] = _affordable_calls(
                    remaining_sats, _parse_price_sats(entry["default_price_sats"])
                )

            items.append(entry)

    total = data.get("total", len(items))

    budget_info = None
    if budget_status is not None:
        spent_sats = budget_status.get("session", {}).get("spentSats", 0)
        budget_info = {
            "remaining_sats": remaining_sats,
            "session_limit_sats": (
                remaining_sats + spent_sats if remaining_sats is not None else None
            ),
            "session_spent_sats": spent_sats,
        }

    hint = (
        'Call discover_api(url="<manifest_url>") for full endpoint details and pricing of a specific API.'
        if items
        else "No APIs found. Try different keywords or browse categories."
    )

    return json.dumps({
        "success": True,
        "source": "registry",
        "query": query,
        "category": category,
        "results": items,
        "total": total,
        "budget": budget_info,
        "hint": hint,
    }, indent=2)


async def _fetch_and_format_manifest(
    url: str,
    budget_aware: bool,
    budget_service: "BudgetService | None",
) -> str:
    """Fetch and format a manifest from a specific URL."""
    async with httpx.AsyncClient(timeout=15.0, headers={
        "Accept": "application/json",
        "User-Agent": "LightningEnable-MCP/1.0",
    }) as client:
        manifest_json, manifest_url = await _fetch_manifest(client, url)

    if manifest_json is None:
        return json.dumps({
            "success": False,
            "error": "Could not find an L402 manifest at the given URL or any well-known locations.",
            "tried_urls": _get_tried_urls(url),
            "hint": "The API may not have an L402 manifest enabled. Try the URL with /.well-known/l402-manifest.json appended."
        })

    try:
        root = json.loads(manifest_json)
    except json.JSONDecodeError as e:
        return json.dumps({
            "success": False,
            "error": f"Failed to parse manifest JSON: {sanitize_error(str(e))}"
        })

    service_info = _extract_service_info(root)
    l402_info = _extract_l402_info(root)
    endpoints = _extract_endpoints(root)

    # Budget annotations
    budget_info = None
    if budget_aware and budget_service:
        try:
            session = budget_service.get_status().get("session", {})
            spent_sats = session.get("spentSats", 0)

            # Remaining sats is DERIVED (the Python budget is USD-native). None
            # means "cannot be determined" — it is reported as such, never as 0.
            remaining_sats = await budget_service.get_remaining_session_sats()

            # BTC price for the USD annotations. Unavailability is NOT fatal:
            # discovery still works, we just omit the USD figures rather than
            # invent them. (Bare except is load-bearing — do not widen.)
            btc_price = None
            try:
                from ..price_service import get_price_service
                price_svc = get_price_service()
                btc_price = await price_svc.get_btc_price()
            except Exception:
                pass

            # Annotate endpoints with affordability. The manifest is untrusted:
            # only a positive, finite, numeric price yields a real count or cost.
            for endpoint in endpoints:
                pricing = endpoint.get("pricing")
                if not isinstance(pricing, dict):
                    # No pricing block at all — claim nothing.
                    continue
                price_sats = _parse_price_sats(pricing.get("base_price_sats"))
                endpoint["affordable_calls"] = _affordable_calls(remaining_sats, price_sats)
                if price_sats is not None and btc_price and btc_price > 0:
                    cost_usd = float(price_sats) / 100_000_000 * float(btc_price)
                    endpoint["cost_usd"] = round(cost_usd, 6)

            budget_info = {
                "remaining_sats": remaining_sats,
                # Derived from the same figures: what is left plus what is gone.
                "session_limit_sats": (
                    remaining_sats + spent_sats if remaining_sats is not None else None
                ),
                "session_spent_sats": spent_sats,
            }
            if remaining_sats is not None and btc_price and btc_price > 0:
                budget_info["remaining_usd"] = round(
                    float(remaining_sats) / 100_000_000 * float(btc_price), 4
                )

        except Exception:
            pass

    return json.dumps({
        "success": True,
        "source": "manifest",
        "manifest_url": manifest_url,
        "service": service_info,
        "l402": l402_info,
        "endpoints": endpoints,
        "budget": budget_info,
        "endpoint_count": len(endpoints),
    }, indent=2)
