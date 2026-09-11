"""Schemas for the consolidated ``action``-style tools.

Each of these replaces a family of single-purpose tools whose schemas were all
pushed into the agent's context every session. The handlers are unchanged — the
server dispatches on ``action`` (or ``source``) straight into the same per-tool
function the old name called, so behaviour, budget checks, confirmation gating and
SSRF guards are identical. Only the advertised surface changed.

Descriptions are deliberately terse: every byte here is re-sent to the model on
every session. The one thing kept verbatim everywhere is how out-of-band
confirmation works, because an agent that misunderstands that will either stall or
try to approve its own spending.

Every old name still works: see ``server.DEPRECATED_ALIASES``.
"""

from mcp.types import Tool

# Repeated verbatim across the paying tools. Short on purpose, but it must keep the
# fact that makes out-of-band confirmation work: the code lives on the server
# console, where the human can see it and the agent cannot.
CONFIRMATION_NONCE_DESCRIPTION = (
    "Code the human reads off the server console (never returned to you). "
    "Omit to request one."
)


BUDGET_TOOL = Tool(
    name="budget",
    description=(
        "View or tighten this session's spending limits. Tightening is one-way - "
        "an agent can lower its caps but never raise the operator's config limits."
    ),
    inputSchema={
        "type": "object",
        "properties": {
            "action": {
                "type": "string",
                "enum": ["status", "tighten"],
                "description": (
                    "status: read limits, tiers and session spend. "
                    "tighten: lower the runtime sats caps."
                ),
            },
            "per_request": {
                "type": "integer",
                "description": "tighten: max sats per request",
                "default": 1000,
            },
            "per_session": {
                "type": "integer",
                "description": "tighten: max sats per session",
                "default": 10000,
            },
        },
        "required": ["action"],
    },
)


RECEIPTS_TOOL = Tool(
    name="receipts",
    description=(
        "List payments this wallet made. The durable log persists across restarts "
        "and says how to revoke the wallet; the session list is in-memory only."
    ),
    inputSchema={
        "type": "object",
        "properties": {
            "source": {
                "type": "string",
                "enum": ["durable", "session"],
                "description": (
                    "durable: the append-only log at ~/.lightning-enable/"
                    "receipts.jsonl. session: payments since startup."
                ),
            },
            "limit": {
                "type": "integer",
                "description": "Max entries (durable clamps to 1-200)",
            },
            "since": {
                "type": "string",
                "description": "session: ISO timestamp to filter from",
            },
        },
        "required": ["source"],
    },
)


WALLET_OPS_TOOL = Tool(
    name="wallet_ops",
    description=(
        "Wallet operations beyond invoices: BTC price, currency exchange, on-chain "
        "sends. Strike only (send_onchain also works on LND)."
    ),
    inputSchema={
        "type": "object",
        "properties": {
            "action": {
                "type": "string",
                "enum": ["price", "exchange", "send_onchain"],
                "description": (
                    "price: BTC/USD. exchange: convert currency in your wallet. "
                    "send_onchain: irreversible - ALWAYS needs confirmation_nonce, "
                    "so the first call only prints a code to the server console."
                ),
            },
            "source_currency": {
                "type": "string",
                "description": "exchange: from, USD or BTC",
            },
            "target_currency": {
                "type": "string",
                "description": "exchange: to, BTC or USD",
            },
            "amount": {
                "type": "number",
                "description": "exchange: amount in source_currency",
            },
            "address": {
                "type": "string",
                "description": "send_onchain: destination address",
            },
            "amount_sats": {
                "type": "integer",
                "description": "send_onchain: satoshis to send",
            },
            "confirmation_nonce": {
                "type": "string",
                "description": CONFIRMATION_NONCE_DESCRIPTION,
            },
        },
        "required": ["action"],
    },
)


L402_PRODUCER_TOOL = Tool(
    name="l402_producer",
    description=(
        "Sell access with L402: set up the seller account, monetize an API, mint "
        "challenges and verify payer tokens. Requires LIGHTNING_ENABLE_API_KEY."
    ),
    inputSchema={
        "type": "object",
        "properties": {
            "action": {
                "type": "string",
                "enum": [
                    "create",
                    "verify",
                    "configure_receive",
                    "status",
                    "create_proxy",
                    "add_endpoint",
                    "publish",
                    "list_challenges",
                ],
                "description": (
                    "create: mint an invoice + macaroon challenge. verify: check a "
                    "payer's token before granting access. configure_receive: point "
                    "payouts at your own NWC wallet. status: plan, wallet, checklist "
                    "and recent mints (read-only). create_proxy: put an API behind "
                    "L402. add_endpoint: price one route. publish: list the service "
                    "publicly. list_challenges: read what you minted (read-only). "
                    "Pass only the arguments tagged with your action."
                ),
            },
            "resource": {
                "type": "string",
                "description": "create: URL or name you are charging for",
            },
            "price_sats": {
                "type": "integer",
                "description": "create/add_endpoint: price in sats",
            },
            "description": {
                "type": "string",
                "description": "create: invoice text; create_proxy: what the API does",
            },
            "macaroon": {"type": "string", "description": "verify: base64 macaroon"},
            "preimage": {"type": "string", "description": "verify: hex preimage"},
            "nwc_connection_string": {
                "type": "string",
                "description": (
                    "configure_receive: nostr+walletconnect:// string; omit to reuse "
                    "this server's own NWC wallet"
                ),
            },
            "name": {"type": "string", "description": "create_proxy: service name"},
            "target_base_url": {
                "type": "string",
                "description": "create_proxy: https:// base URL to monetize",
            },
            "default_price_sats": {
                "type": "integer",
                "description": "create_proxy: default price per request",
            },
            "proxy_id": {
                "type": "string",
                "description": "add_endpoint/publish: id from create_proxy",
            },
            "endpoint_id": {
                "type": "string",
                "description": "add_endpoint: stable id for the route",
            },
            "path": {
                "type": "string",
                "description": "add_endpoint: route path like /forecast",
            },
            "http_method": {
                "type": "string",
                "description": "add_endpoint: GET, POST, ... (default GET)",
            },
            "summary": {
                "type": "string",
                "description": "add_endpoint: one-line summary",
            },
            "service_name": {
                "type": "string",
                "description": "publish: rename the listed service",
            },
            "service_description": {
                "type": "string",
                "description": "publish: registry description",
            },
            "categories": {
                "type": "array",
                "items": {"type": "string"},
                "description": "publish: registry categories",
            },
            "challenge_status": {
                "type": "string",
                "enum": ["paid", "unpaid", "expired"],
                "description": "list_challenges: filter; omit for all",
            },
            "limit": {
                "type": "integer",
                "description": "status/list_challenges: max rows",
            },
            "offset": {
                "type": "integer",
                "description": "list_challenges: rows to skip",
            },
        },
        "required": ["action"],
    },
)


AGENT_SERVICES_TOOL = Tool(
    name="agent_services",
    description=(
        "Agent Service Agreements over Nostr: discover, request, settle, publish "
        "and review agent services. Requires LIGHTNING_ENABLE_API_KEY."
    ),
    inputSchema={
        "type": "object",
        "properties": {
            "action": {
                "type": "string",
                "enum": [
                    "discover",
                    "request",
                    "settle",
                    "publish",
                    "unpublish",
                    "attest",
                    "reputation",
                ],
                "description": (
                    "discover: search listings. request: ask for a service. "
                    "settle: PAY an agreement's L402 endpoint. publish: list your "
                    "service. unpublish: remove it. attest: review a finished "
                    "agreement. reputation: read reviews. Pass only the arguments "
                    "tagged with your action."
                ),
            },
            "category": {"type": "string", "description": "discover: category"},
            "query": {"type": "string", "description": "discover: keyword"},
            "hashtags": {
                "type": "array",
                "items": {"type": "string"},
                "description": "discover/publish: hashtags",
            },
            "limit": {
                "type": "integer",
                "description": "discover/reputation: max results",
            },
            "capability_event_id": {
                "type": "string",
                "description": "request: capability event id",
            },
            "budget_sats": {"type": "integer", "description": "request: max sats"},
            "parameters": {
                "type": "string",
                "description": "request: extra params, JSON string",
            },
            "l402_endpoint": {
                "type": "string",
                "description": "settle/publish: L402 endpoint",
            },
            "method": {"type": "string", "description": "settle: HTTP method"},
            "body": {"type": "string", "description": "settle: request body"},
            "agreement_id": {
                "type": "string",
                "description": "settle/attest: agreement id",
            },
            "max_sats": {"type": "integer", "description": "settle: max sats"},
            "confirmation_nonce": {
                "type": "string",
                "description": CONFIRMATION_NONCE_DESCRIPTION,
            },
            "service_id": {
                "type": "string",
                "description": "publish/unpublish: listing d-tag",
            },
            "categories": {
                "type": "array",
                "items": {"type": "string"},
                "description": "publish: categories",
            },
            "content": {
                "type": "string",
                "description": "publish: service text; attest: review text",
            },
            "price_sats": {
                "type": "integer",
                "description": "publish: price per request",
            },
            "target_url": {
                "type": "string",
                "description": "publish: API URL to wrap in a new L402 proxy",
            },
            "reason": {"type": "string", "description": "unpublish: reason"},
            "subject_pubkey": {
                "type": "string",
                "description": "attest: agent being reviewed",
            },
            "rating": {"type": "integer", "description": "attest: 1-5"},
            "proof": {"type": "string", "description": "attest: preimage hash"},
            "pubkey": {"type": "string", "description": "reputation: agent pubkey"},
        },
        "required": ["action"],
    },
)


#: Every action-style tool, keyed by name, with its discriminator property and the
#: actions it accepts. Used by the server to reject an unknown/missing action with a
#: descriptive error instead of silently doing nothing.
ACTION_TOOLS: dict[str, tuple[str, tuple[str, ...]]] = {
    "budget": ("action", ("status", "tighten")),
    "receipts": ("source", ("durable", "session")),
    "wallet_ops": ("action", ("price", "exchange", "send_onchain")),
    "l402_producer": (
        "action",
        (
            "create",
            "verify",
            "configure_receive",
            "status",
            "create_proxy",
            "add_endpoint",
            "publish",
            "list_challenges",
        ),
    ),
    "agent_services": (
        "action",
        (
            "discover",
            "request",
            "settle",
            "publish",
            "unpublish",
            "attest",
            "reputation",
        ),
    ),
}
