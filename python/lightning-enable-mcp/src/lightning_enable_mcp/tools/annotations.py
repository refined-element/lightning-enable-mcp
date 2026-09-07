"""MCP tool annotations for every advertised tool.

One table, so the read-only / money-moving classification is reviewable in a single
place instead of scattered across 20 modules. ``registry.py`` stamps these onto the
tools it advertises.

Conventions (per the MCP spec):

* ``title`` — short human-readable label shown in tool pickers. Always set.
* ``readOnlyHint`` — always set explicitly, never left to the default, so a client
  can tell "read" from "write" without guessing.
* ``destructiveHint`` / ``idempotentHint`` — only meaningful when
  ``readOnlyHint`` is false, so they are omitted on read-only tools.
* ``openWorldHint`` — defaults to true, so it is set only where the tool touches
  nothing but local state (budget, receipts, confirmation codes).

Annotations are hints, not a security boundary: the real spend controls are the
budget service, the out-of-band confirmation codes, and the SSRF guards.

**Consolidated tools are annotated for their widest action.** ``budget`` is not
read-only even though ``action="status"`` is, and ``wallet_ops`` / ``agent_services``
are destructive even though ``price`` / ``discover`` / ``reputation`` are not —
annotations are per tool, so the conservative classification wins.
"""

from mcp.types import ToolAnnotations

#: Tool name → annotations. Covers the consolidated surface and every legacy name
#: the ``full`` profile re-advertises.
TOOL_ANNOTATIONS: dict[str, ToolAnnotations] = {
    # ── Consolidated surface ────────────────────────────────────────────────
    "access_l402_resource": ToolAnnotations(
        title="Fetch paid resource", readOnlyHint=False, destructiveHint=True
    ),
    "pay_invoice": ToolAnnotations(
        title="Pay Lightning invoice", readOnlyHint=False, destructiveHint=True
    ),
    "pay_l402_challenge": ToolAnnotations(
        title="Pay L402 challenge", readOnlyHint=False, destructiveHint=True
    ),
    "test_l402_payment": ToolAnnotations(
        title="Wallet self-test", readOnlyHint=False, destructiveHint=True
    ),
    "get_balance": ToolAnnotations(title="Wallet balance", readOnlyHint=True),
    "budget": ToolAnnotations(
        title="Budget",
        readOnlyHint=False,
        destructiveHint=False,
        idempotentHint=True,
        openWorldHint=False,
    ),
    "receipts": ToolAnnotations(
        title="Payment receipts", readOnlyHint=True, openWorldHint=False
    ),
    "create_invoice": ToolAnnotations(
        title="Create invoice", readOnlyHint=False, destructiveHint=False
    ),
    "check_invoice_status": ToolAnnotations(
        title="Invoice status", readOnlyHint=True
    ),
    "verify_confirmation_code": ToolAnnotations(
        title="Verify confirmation code", readOnlyHint=True, openWorldHint=False
    ),
    "discover_api": ToolAnnotations(title="Discover L402 APIs", readOnlyHint=True),
    "create_lightning_enable_account": ToolAnnotations(
        title="Create Lightning Enable account",
        readOnlyHint=False,
        destructiveHint=True,
    ),
    "wallet_ops": ToolAnnotations(
        title="Wallet operations", readOnlyHint=False, destructiveHint=True
    ),
    "l402_producer": ToolAnnotations(
        title="L402 producer", readOnlyHint=False, destructiveHint=False
    ),
    "agent_services": ToolAnnotations(
        title="Agent services", readOnlyHint=False, destructiveHint=True
    ),
    # ── Legacy names (advertised only under the `full` profile) ─────────────
    "get_budget_status": ToolAnnotations(
        title="Budget status (deprecated)", readOnlyHint=True, openWorldHint=False
    ),
    "configure_budget": ToolAnnotations(
        title="Tighten budget (deprecated)",
        readOnlyHint=False,
        destructiveHint=False,
        idempotentHint=True,
        openWorldHint=False,
    ),
    "get_receipts": ToolAnnotations(
        title="Durable receipts (deprecated)", readOnlyHint=True, openWorldHint=False
    ),
    "get_payment_history": ToolAnnotations(
        title="Session payments (deprecated)", readOnlyHint=True, openWorldHint=False
    ),
    "get_btc_price": ToolAnnotations(title="BTC price (deprecated)", readOnlyHint=True),
    "exchange_currency": ToolAnnotations(
        title="Exchange currency (deprecated)", readOnlyHint=False, destructiveHint=True
    ),
    "send_onchain": ToolAnnotations(
        title="Send on-chain (deprecated)", readOnlyHint=False, destructiveHint=True
    ),
    "create_l402_challenge": ToolAnnotations(
        title="Create L402 challenge (deprecated)",
        readOnlyHint=False,
        destructiveHint=False,
    ),
    "verify_l402_payment": ToolAnnotations(
        title="Verify L402 payment (deprecated)", readOnlyHint=True
    ),
    "discover_agent_services": ToolAnnotations(
        title="Discover agent services (deprecated)", readOnlyHint=True
    ),
    "publish_agent_capability": ToolAnnotations(
        title="Publish capability (deprecated)",
        readOnlyHint=False,
        destructiveHint=False,
    ),
    "unpublish_agent_capability": ToolAnnotations(
        title="Unpublish capability (deprecated)",
        readOnlyHint=False,
        destructiveHint=True,
    ),
    "request_agent_service": ToolAnnotations(
        title="Request agent service (deprecated)",
        readOnlyHint=False,
        destructiveHint=False,
    ),
    "publish_agent_attestation": ToolAnnotations(
        title="Publish attestation (deprecated)",
        readOnlyHint=False,
        destructiveHint=False,
    ),
    "get_agent_reputation": ToolAnnotations(
        title="Agent reputation (deprecated)", readOnlyHint=True
    ),
    "settle_agent_service": ToolAnnotations(
        title="Settle agent service (deprecated)",
        readOnlyHint=False,
        destructiveHint=True,
    ),
}
