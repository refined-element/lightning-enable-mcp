"""Seller-side setup for ``l402_producer``: API key in, monetized endpoint out.

``create`` and ``verify`` handle ONE challenge each. They are only worth calling once the
account behind them is set up, and until now that setup was raw REST an agent could not
reach: point payouts at a wallet the merchant owns (``configure_receive``), see where the
account stands (``status``), register an upstream API (``create_proxy``), price its routes
(``add_endpoint``), list it so other agents can find it (``publish``), and read back what
was minted (``list_challenges``).

Two rules run through all of it:

* **The NWC connection string is never returned.** It authorises live calls against the
  merchant's wallet, so every result says ``<set>`` — and the final JSON is scrubbed of the
  string before it leaves, in case an upstream error body quoted it.
* **Errors are surfaced whole.** The Lightning Enable API answers in RFC 9457
  ``application/problem+json`` with ``type``/``detail`` alongside the legacy
  ``error``/``message``. An agent gets the prose *and* the stable slug, never the API key.

Deliberately NOT re-exported from ``tools/__init__``: ``publish``, ``create_proxy`` and
``list_challenges`` are far too generic to sit in the package namespace beside
``publish_agent_capability``, and ``server.py`` imports this module directly under
disambiguating aliases. Import from ``lightning_enable_mcp.tools.producer_setup``.
"""

import json
import logging
from typing import TYPE_CHECKING, Any

from . import sanitize_error
from .setup_wallet import _env, describe_wallet_state, parse_nwc_connection_string

if TYPE_CHECKING:
    from ..config import ConfigurationService
    from ..lightning_enable_api import LightningEnableApiClient

logger = logging.getLogger("lightning-enable-mcp.tools.producer_setup")

#: Fixed marker for a stored wallet credential. Never a masked prefix of the real value:
#: the merchant already has the string in their wallet app, and no surface needs to echo it.
SET_MARKER = "<set>"
UNSET_MARKER = "<unset>"

#: Accepted ``status`` filters on ``GET /api/l402/challenges``.
CHALLENGE_STATUSES = ("paid", "unpaid", "expired")

#: Fields copied out of a challenge row. An allowlist, not a passthrough: a future API
#: field must be added here deliberately, so a credential-shaped one cannot ride along.
CHALLENGE_FIELDS = (
    "paymentHash",
    "resource",
    "amountSats",
    "status",
    "createdAt",
    "paidAt",
    "expiresAt",
    "idempotencyKey",
)

_NO_CLIENT = "Lightning Enable API service not available"


def _fail(error: str, **extra: Any) -> str:
    """A descriptive failure result. Never blank, never a bare status code."""
    return json.dumps({"success": False, "error": error, **extra}, indent=2)


def _from_api(result: dict[str, Any], **extra: Any) -> str:
    """Turn a failed ``_producer_request`` result into a tool result, keeping its members."""
    payload: dict[str, Any] = {"success": False}
    for key in ("error", "errorType", "errorCode", "httpStatus", "validationErrors"):
        if key in result:
            payload[key] = result[key]
    payload.setdefault("error", "The Lightning Enable API call failed.")
    payload.update(extra)
    return json.dumps(payload, indent=2)


def _client_error(api_client: "LightningEnableApiClient | None") -> str | None:
    """The gate every producer action shares: a client, holding a merchant API key."""
    if api_client is None:
        return _NO_CLIENT
    if not api_client.is_configured:
        from ..lightning_enable_api import PRODUCER_API_KEY_REQUIRED

        return PRODUCER_API_KEY_REQUIRED
    return None


def _public_url(api_client: "LightningEnableApiClient", path: str) -> str:
    """An absolute URL on the Lightning Enable API for a proxy-relative path."""
    return f"{api_client.base_url}{path}"


def resolve_mcp_nwc_connection(
    config_service: "ConfigurationService | None" = None,
) -> tuple[str | None, dict]:
    """The NWC connection string THIS server pays with, when its wallet is an NWC wallet.

    Returns ``(connection string or None, wallet state)``. The state carries the provider
    and where its credential came from — never the credential itself — so a refusal can
    name the wallet that is actually configured.

    Only an NWC wallet qualifies: LND, Strike and OpenNode credentials are not connection
    strings and cannot be handed to Lightning Enable as a receiving wallet. The provider is
    resolved by ``describe_wallet_state``, which mirrors the server's own priority order,
    so this agrees with the wallet the agent is actually spending from.
    """
    state = describe_wallet_state(config_service)
    if state.get("provider") != "NWC":
        return None, state

    if config_service is None:
        from ..config import get_config_service

        config_service = get_config_service()

    # Env beats config, exactly as the server's own wallet selection does.
    value = _env("NWC_CONNECTION_STRING") or config_service.configuration.wallets.nwc_connection_string
    return (value or None), state


async def configure_receive(
    nwc_connection_string: str | None = None,
    api_client: "LightningEnableApiClient | None" = None,
    config_service: "ConfigurationService | None" = None,
) -> str:
    """Point the merchant's L402 payouts at a wallet they control.

    Stores the Nostr Wallet Connect string (``PUT /api/merchant/nwc-connection``) and then
    switches the account's payment lane to it (``PUT /api/merchant/payment-provider``).
    With no argument, reuses the wallet this MCP server itself pays with — but only when
    that wallet IS an NWC wallet; an LND / Strike / OpenNode server is refused with a
    message naming its wallet rather than sent something that cannot be a connection string.

    Returns:
        JSON reporting ``nwcConnectionString: "<set>"`` and the provider. The string itself
        never appears in the result, and the result is scrubbed of it before returning.
    """
    error = _client_error(api_client)
    if error:
        return _fail(error)
    assert api_client is not None  # noqa: S101 — narrowed by _client_error

    used_mcp_wallet = False
    source = "the nwc_connection_string argument"

    if not nwc_connection_string or not nwc_connection_string.strip():
        resolved, state = resolve_mcp_nwc_connection(config_service)
        if not resolved:
            if not state.get("configured"):
                return _fail(
                    "No NWC connection string was given, and no wallet is configured on this "
                    "MCP server to fall back to. Pass nwc_connection_string=... (copy it from "
                    "your wallet app — it starts with nostr+walletconnect://), or run "
                    "setup_wallet first."
                )
            return _fail(
                f"No NWC connection string was given, and this MCP server's wallet is "
                f"{state.get('provider')}, which cannot be handed to Lightning Enable as a "
                "receiving wallet — only a Nostr Wallet Connect string can. Pass "
                "nwc_connection_string=... from a wallet app that supports NWC "
                "(CoinOS, Alby Hub, CLINK)."
            )
        nwc_connection_string = resolved
        used_mcp_wallet = True
        source = f"this MCP server's own NWC wallet, from {state.get('source')}"

    # Validate locally so an obvious typo costs no round trip — and so a malformed
    # credential is never put on the wire. The parse error names the bad field and never
    # quotes the value.
    try:
        parse_nwc_connection_string(nwc_connection_string)
    except ValueError as e:
        return _fail(str(e))

    connection = nwc_connection_string.strip()

    def scrub(text: str) -> str:
        """Last line of defence: the credential never leaves, whoever put it in the text."""
        return text.replace(connection, SET_MARKER)

    try:
        saved = await api_client.save_nwc_connection(connection)
        if not saved.get("success"):
            return scrub(
                _from_api(
                    saved,
                    hint=(
                        "The connection string was NOT stored and the payment provider was "
                        "left unchanged."
                    ),
                )
            )

        switched = await api_client.set_payment_provider("nwc")
        if not switched.get("success"):
            return scrub(
                _from_api(
                    switched,
                    nwcConnectionString=SET_MARKER,
                    hint=(
                        "The connection string WAS stored, but the account's payment "
                        "provider was not switched to nwc. Retry "
                        "l402_producer action=configure_receive to finish."
                    ),
                )
            )

        return scrub(
            json.dumps(
                {
                    "success": True,
                    "provider": "nwc",
                    "nwcConnectionString": SET_MARKER,
                    "usedMcpWallet": used_mcp_wallet,
                    "source": source,
                    "message": (
                        "Lightning Enable will mint L402 invoices on your own wallet and poll "
                        "it for payment — no webhook needed. Lightning Enable does not hold "
                        "the funds. Next: l402_producer action=create_proxy to monetize an API, "
                        "or action=create to mint a one-off challenge."
                    ),
                },
                indent=2,
            )
        )
    except Exception as e:  # noqa: BLE001 — a tool must never raise at the agent
        logger.exception("Error configuring the receiving wallet")
        return _fail(scrub(sanitize_error(str(e))))


def _derive_provider(account: dict[str, Any]) -> str | None:
    """Which lane the account receives on.

    ``GET /api/merchant/me`` reports credential PRESENCE, not the provider column, so this
    derives it from the onboarding flags in the same priority the API's own factory uses.
    An explicit ``paymentProvider`` member wins if the API ever starts returning one.
    """
    explicit = account.get("paymentProvider")
    if isinstance(explicit, str) and explicit.strip():
        return explicit.strip().lower()

    onboarding = account.get("onboarding") or {}
    if onboarding.get("hasNwcConnection"):
        return "nwc"
    if onboarding.get("hasStrikeKey"):
        return "strike"
    if onboarding.get("hasOpenNodeKey"):
        return "opennode"
    return None


def _challenge_summary(row: Any) -> dict[str, Any]:
    """One challenge, allowlisted. Never a macaroon, never a preimage."""
    if not isinstance(row, dict):
        return {}
    return {field: row.get(field) for field in CHALLENGE_FIELDS if field in row}


async def producer_status(
    limit: int = 5,
    api_client: "LightningEnableApiClient | None" = None,
) -> str:
    """Where the seller account stands: plan, receiving wallet, checklist, recent mints.

    Args:
        limit: How many recent challenges to include (1-50).
        api_client: Lightning Enable API client instance.

    Returns:
        JSON summary. The checklist is best-effort — an account whose quickstart is
        unavailable still gets the rest, with the reason in ``checklistError``.
    """
    error = _client_error(api_client)
    if error:
        return _fail(error)
    assert api_client is not None  # noqa: S101 — narrowed by _client_error

    take = max(1, min(int(limit or 5), 50))

    try:
        account_result = await api_client.get_merchant_account()
        if not account_result.get("success"):
            return _from_api(
                account_result,
                hint="Could not read the account. Check LIGHTNING_ENABLE_API_KEY.",
            )
        account = account_result.get("data") or {}
        onboarding = account.get("onboarding") or {}
        provider = _derive_provider(account)

        quickstart_result = await api_client.get_quickstart()
        checklist = None
        checklist_error = None
        if quickstart_result.get("success"):
            guide = quickstart_result.get("data") or {}
            checklist = {
                "completedSteps": guide.get("completedSteps"),
                "totalSteps": guide.get("totalSteps"),
                "requiredStepsCompleted": guide.get("requiredStepsCompleted"),
                "requiredStepsTotal": guide.get("requiredStepsTotal"),
                "isReadyForProduction": guide.get("isReadyForProduction"),
                "steps": [
                    {
                        "stepNumber": step.get("stepNumber"),
                        "title": step.get("title"),
                        "isCompleted": step.get("isCompleted"),
                        "isRequired": step.get("isRequired"),
                    }
                    for step in (guide.get("steps") or [])
                    if isinstance(step, dict)
                ],
            }
        else:
            checklist_error = quickstart_result.get("error")

        challenges_result = await api_client.list_challenges(limit=take)
        recent: list[dict[str, Any]] = []
        challenge_total = None
        challenges_error = None
        if challenges_result.get("success"):
            data = challenges_result.get("data") or {}
            recent = [_challenge_summary(r) for r in (data.get("challenges") or [])]
            challenge_total = data.get("total")
        else:
            challenges_error = challenges_result.get("error")

        return json.dumps(
            {
                "success": True,
                "merchant": {
                    "merchantId": account.get("merchantId"),
                    "name": account.get("name"),
                    "planTier": account.get("planTier"),
                    "subscriptionStatus": account.get("subscriptionStatus"),
                    "isActive": account.get("isActive"),
                    "l402Enabled": (account.get("features") or {}).get("l402Enabled"),
                },
                "receive": {
                    "provider": provider,
                    "configured": bool(onboarding.get("isFullyConfigured")),
                    "nwcConnectionString": (
                        SET_MARKER if onboarding.get("hasNwcConnection") else UNSET_MARKER
                    ),
                    "hasStrikeKey": bool(onboarding.get("hasStrikeKey")),
                    "hasOpenNodeKey": bool(onboarding.get("hasOpenNodeKey")),
                },
                "proxies": {
                    "count": onboarding.get("proxyCount"),
                    "hasActive": bool(onboarding.get("hasActiveProxy")),
                },
                "checklist": checklist,
                "checklistError": checklist_error,
                "recentChallenges": recent,
                "challengeTotal": challenge_total,
                "challengesError": challenges_error,
            },
            indent=2,
        )
    except Exception as e:  # noqa: BLE001 — a tool must never raise at the agent
        logger.exception("Error reading producer status")
        return _fail(sanitize_error(str(e)))


async def create_proxy(
    name: str = "",
    target_base_url: str = "",
    description: str | None = None,
    default_price_sats: int = 10,
    api_client: "LightningEnableApiClient | None" = None,
) -> str:
    """Register an upstream API so Lightning Enable can charge for it.

    Returns:
        JSON with the generated ``proxyId`` and the public L402 base URL agents will call.
    """
    error = _client_error(api_client)
    if error:
        return _fail(error)
    assert api_client is not None  # noqa: S101 — narrowed by _client_error

    if not name or not name.strip():
        return _fail("A name is required — it becomes the service name agents see.")
    if not target_base_url or not target_base_url.strip():
        return _fail(
            "target_base_url is required: the https:// base URL of the API you are "
            "monetizing. Lightning Enable forwards paid requests to it."
        )
    if default_price_sats <= 0:
        return _fail("default_price_sats must be greater than 0.")

    try:
        result = await api_client.create_proxy(
            name.strip(), target_base_url.strip(), description, int(default_price_sats)
        )
        if not result.get("success"):
            return _from_api(result)

        proxy = result.get("data") or {}
        proxy_id = proxy.get("proxyId")
        proxy_url = proxy.get("proxyUrl") or (f"/l402/proxy/{proxy_id}" if proxy_id else None)

        return json.dumps(
            {
                "success": True,
                "proxyId": proxy_id,
                "name": proxy.get("name"),
                "description": proxy.get("description"),
                "targetBaseUrl": proxy.get("targetBaseUrl"),
                "defaultPriceSats": proxy.get("defaultPriceSats"),
                "proxyUrl": proxy_url,
                "publicBaseUrl": _public_url(api_client, proxy_url) if proxy_url else None,
                "message": (
                    "Proxy registered. Every request under the public base URL now answers "
                    "402 until it is paid."
                ),
                "nextStep": (
                    f"l402_producer action=add_endpoint proxy_id={proxy_id} to price a route, "
                    "then action=publish to list it."
                ),
            },
            indent=2,
        )
    except Exception as e:  # noqa: BLE001 — a tool must never raise at the agent
        logger.exception("Error creating proxy")
        return _fail(sanitize_error(str(e)))


async def add_endpoint(
    proxy_id: str = "",
    endpoint_id: str = "",
    path: str = "",
    http_method: str = "GET",
    summary: str | None = None,
    price_sats: int = 0,
    api_client: "LightningEnableApiClient | None" = None,
) -> str:
    """Price one route on a proxy and put it in the published manifest.

    Returns:
        JSON with the endpoint and the absolute URL a paying agent calls.
    """
    error = _client_error(api_client)
    if error:
        return _fail(error)
    assert api_client is not None  # noqa: S101 — narrowed by _client_error

    if not proxy_id or not proxy_id.strip():
        return _fail(
            "proxy_id is required. Create one with l402_producer action=create_proxy, or "
            "read your existing ones with action=status."
        )
    if not endpoint_id or not endpoint_id.strip():
        return _fail(
            "endpoint_id is required: a short stable id for this route, unique on the proxy."
        )
    if not path or not path.strip():
        return _fail("path is required, like /forecast — the route on the upstream API.")
    if price_sats < 0:
        return _fail("price_sats cannot be negative.")

    method = (http_method or "GET").strip().upper()
    route = path.strip()

    try:
        result = await api_client.create_manifest_endpoint(
            proxy_id.strip(), endpoint_id.strip(), route, method, summary, int(price_sats)
        )
        if not result.get("success"):
            return _from_api(result)

        endpoint = result.get("data") or {}
        public = _public_url(
            api_client, f"/l402/proxy/{proxy_id.strip()}{endpoint.get('path') or route}"
        )
        return json.dumps(
            {
                "success": True,
                "proxyId": proxy_id.strip(),
                "endpointId": endpoint.get("endpointId"),
                "path": endpoint.get("path"),
                "httpMethod": endpoint.get("httpMethod"),
                "summary": endpoint.get("summary"),
                "basePriceSats": endpoint.get("basePriceSats"),
                "url": public,
                "message": (
                    f"{endpoint.get('httpMethod')} {public} now answers 402 until the payer "
                    "presents a valid L402 token."
                ),
                "nextStep": (
                    f"l402_producer action=publish proxy_id={proxy_id.strip()} to list the "
                    "service so other agents can discover it."
                ),
            },
            indent=2,
        )
    except Exception as e:  # noqa: BLE001 — a tool must never raise at the agent
        logger.exception("Error adding manifest endpoint")
        return _fail(sanitize_error(str(e)))


async def publish(
    proxy_id: str = "",
    service_name: str | None = None,
    service_description: str | None = None,
    categories: list[str] | None = None,
    api_client: "LightningEnableApiClient | None" = None,
) -> str:
    """Turn the manifest on and list the service publicly.

    ``service_name`` renames the proxy first: the manifest's ``service.name`` is the
    proxy's own name, not a manifest-settings field.

    Returns:
        JSON with the OpenAPI document URL and the JSON manifest URL.
    """
    error = _client_error(api_client)
    if error:
        return _fail(error)
    assert api_client is not None  # noqa: S101 — narrowed by _client_error

    if not proxy_id or not proxy_id.strip():
        return _fail(
            "proxy_id is required. Create one with l402_producer action=create_proxy."
        )
    slug = proxy_id.strip()

    try:
        if service_name and service_name.strip():
            renamed = await api_client.rename_proxy(slug, service_name.strip())
            if not renamed.get("success"):
                return _from_api(
                    renamed,
                    hint=(
                        "The service was NOT published — the rename failed first, so nothing "
                        "was changed."
                    ),
                )

        result = await api_client.update_manifest_settings(
            slug, service_description, categories
        )
        if not result.get("success"):
            return _from_api(result)

        settings = result.get("data") or {}
        manifest_path = settings.get("manifestUrl") or (
            f"/l402/proxy/{slug}/.well-known/l402-manifest.json"
        )
        return json.dumps(
            {
                "success": True,
                "proxyId": slug,
                "manifestEnabled": settings.get("manifestEnabled"),
                "publiclyListed": settings.get("manifestPubliclyListed"),
                "serviceDescription": settings.get("serviceDescription"),
                "categories": settings.get("categories"),
                "openapiUrl": _public_url(api_client, f"/l402/proxy/{slug}/openapi.json"),
                "manifestUrl": _public_url(api_client, manifest_path),
                "message": (
                    "Listed. Agents can now find this service through discover_api and read "
                    "its pricing from the manifest."
                ),
            },
            indent=2,
        )
    except Exception as e:  # noqa: BLE001 — a tool must never raise at the agent
        logger.exception("Error publishing manifest")
        return _fail(sanitize_error(str(e)))


async def list_challenges(
    status: str | None = None,
    limit: int = 20,
    offset: int = 0,
    api_client: "LightningEnableApiClient | None" = None,
) -> str:
    """Read back the challenges this merchant minted.

    Args:
        status: ``paid``, ``unpaid`` or ``expired``. Omit for every challenge.
        limit: Page size (1-200).
        offset: Rows to skip.

    Returns:
        JSON page of challenge summaries. Never a macaroon, never a preimage — the payment
        hash is the correlation handle.
    """
    error = _client_error(api_client)
    if error:
        return _fail(error)
    assert api_client is not None  # noqa: S101 — narrowed by _client_error

    normalized = (status or "").strip().lower() or None
    if normalized and normalized not in CHALLENGE_STATUSES:
        return _fail(
            f"status must be one of: {', '.join(CHALLENGE_STATUSES)}. Omit it to list every "
            "challenge."
        )

    take = max(1, min(int(limit or 20), 200))
    skip = max(0, int(offset or 0))

    try:
        result = await api_client.list_challenges(normalized, take, skip)
        if not result.get("success"):
            return _from_api(result)

        data = result.get("data") or {}
        return json.dumps(
            {
                "success": True,
                "challenges": [
                    _challenge_summary(row) for row in (data.get("challenges") or [])
                ],
                "total": data.get("total"),
                "limit": data.get("limit", take),
                "offset": data.get("offset", skip),
                "status": data.get("status", normalized),
            },
            indent=2,
        )
    except Exception as e:  # noqa: BLE001 — a tool must never raise at the agent
        logger.exception("Error listing L402 challenges")
        return _fail(sanitize_error(str(e)))
