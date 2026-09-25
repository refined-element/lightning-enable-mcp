"""
Settle Agent Service Tool (MONEY PATH)

Settles an agent service agreement via L402 payment (CONSUMER/REQUESTER side).
Pays the L402 endpoint specified in the agreement, completing the service
transaction. Uses the SAME wallet + budget-gating flow as access_l402_resource:
the tool runs BudgetService.check_approval_level for its confirmation gate, then
delegates the payment to the L402 client, which atomically reserves against the
session cap and commits the reservation as spend (arming the cooldown) exactly
once. It does NOT use the Lightning Enable API key.

For the PROVIDER side (selling a service), use create_l402_challenge to generate
a Lightning invoice at the agreed price, then verify_l402_payment to confirm
payment before delivering the service.
"""

import json
import logging
from typing import TYPE_CHECKING, Optional
from urllib.parse import urlparse

from mcp.types import Tool

from .._url_redact import redact_url_for_display as _redact_url_for_display
from ..config import ApprovalLevel
from ..confirmation_channel import ConfirmationRequest
from ..l402_client import L402MethodNotAllowedError, L402RedirectError, enforce_paid_http_method
from ..receipt_seam import PaymentReceiptScope, policy_label
from . import sanitize_error
from ._ssrf_guard import SsrfError, validate_url_allowed

if TYPE_CHECKING:
    from ..budget_service import BudgetService
    from ..l402_client import L402Client

logger = logging.getLogger("lightning-enable-mcp.tools.settle_agent_service")

# HTTP method whitelist (mirrors .NET AgentSettleTool)
# Policy restriction to what the L402 settlement client (L402Client.fetch) supports.


async def settle_agent_service(
    l402_endpoint: str,
    method: str = "GET",
    body: str | None = None,
    agreement_id: str | None = None,
    max_sats: int = 1000,
    confirmation_nonce: "Optional[str]" = None,
    l402_client: "L402Client | None" = None,
    budget_service: "BudgetService | None" = None,
) -> str:
    """
    Settle an agent service agreement via L402 payment (consumer/requester side).

    Args:
        l402_endpoint: L402 endpoint URL from the service agreement
        method: HTTP method, GET or HEAD only. Defaults to GET
        body: Optional request body (rarely needed; write methods are refused)
        agreement_id: Optional agreement event ID for tracking
        max_sats: Maximum satoshis to pay (default 1000)
        confirmation_nonce: The code the human read from the server console (for settlements
            above the auto-approve threshold). The code is NEVER in a tool result — ask the
            human for it. Omit on the first call to request one.
        l402_client: L402 client instance (wallet-backed)
        budget_service: BudgetService for multi-tier approval gating

    Returns:
        JSON with settlement result or an error message.
    """
    # Budget policy label for the durable receipt; set once the budget check runs.
    payment_policy = None
    # Declared outside the try so the except handler can report whether a durable
    # receipt landed when the client raises AFTER the invoice settled.
    receipt_scope = None
    try:
        # Input validation
        if not l402_endpoint or not l402_endpoint.strip():
            return json.dumps({
                "success": False,
                "error": (
                    "L402 endpoint URL is required. Get it from an agent_services "
                    "action=discover or action=request result."
                ),
            })

        parsed = urlparse(l402_endpoint)
        if parsed.scheme.lower() not in ("http", "https") or not parsed.netloc:
            return json.dumps({
                "success": False,
                "error": "Invalid L402 endpoint URL. Must be an HTTPS URL.",
            })

        # Security (A1): settlement is a money-moving request to an agent-supplied URL.
        # HTTPS is REQUIRED — there is no localhost / plain-HTTP carve-out. The former
        # "dev" exception was an SSRF foothold (loopback is exactly where internal
        # services live) and there is deliberately no allowlist or env-var escape hatch
        # the model could set; tests that need loopback use the injectable seams in
        # ssrf_transport.
        if parsed.scheme.lower() != "https":
            return json.dumps({
                "success": False,
                "error": "L402 settlement requires HTTPS. Plain HTTP endpoints are not accepted.",
            })

        # SSRF preflight (same guard as access_l402_resource / discover_api): refuse
        # targets that are, or resolve to, a private / loopback / link-local / metadata /
        # reserved address BEFORE any request, budget check, or wallet call. The message
        # is generic — it never echoes the resolved internal host or IP. The connect-time
        # pin in ssrf_transport (wired into L402Client) remains the authoritative gate and
        # closes the DNS-rebind window this resolve-then-validate check cannot.
        try:
            await validate_url_allowed(l402_endpoint)
        except SsrfError as ssrf_error:
            return json.dumps({
                "success": False,
                "error": str(ssrf_error),
                "agreementId": agreement_id,
            })

        # A3: the settlement endpoint is caller-chosen, so this is a generic paid fetch —
        # GET/HEAD only, refused before any budget or client call. Shares the gate with
        # L402Client.fetch so the two layers can never disagree.
        try:
            method = enforce_paid_http_method(method)
        except L402MethodNotAllowedError as exc:
            return json.dumps({
                "success": False,
                "error": f"Invalid HTTP method: {exc}",
                "allowedMethods": ["GET", "HEAD"],
            })

        if l402_client is None:
            return json.dumps({
                "success": False,
                "error": "L402 HTTP client not available. Ensure a wallet is configured.",
            })

        # Budget gating BEFORE payment (mirrors access_l402_resource)
        if budget_service is not None:
            result = await budget_service.check_approval_level(max_sats)
            payment_policy = policy_label(result.level)

            if result.level == ApprovalLevel.DENY:
                return json.dumps({
                    "success": False,
                    "error": "Budget limit exceeded",
                    "denialReason": result.denial_reason,
                    "l402Endpoint": l402_endpoint,
                    "agreementId": agreement_id,
                    "details": {
                        "requestedSats": max_sats,
                        "maxUsd": float(result.amount_usd),
                        "remainingSessionUsd": float(result.remaining_session_budget_usd),
                    },
                    "hint": "Denied by budget policy (session/per-payment/cooldown) — raising max_sats won't help. Lower the amount, wait out any cooldown, or raise your limits in ~/.lightning-enable/config.json.",
                })

            # OUT-OF-BAND confirmation for above-threshold settlements. The code is printed to
            # the server console (stderr) ONLY — never in this result — so the human operator
            # (not the model) must read it and relay it back. Closes the self-approval hole on
            # this fund-moving tool (mirrors access_l402_resource / pay_invoice).
            if result.requires_confirmation:
                endpoint_display = (
                    l402_endpoint[:50] + "..." if len(l402_endpoint) > 50 else l402_endpoint
                )
                if confirmation_nonce:
                    confirmation = budget_service.validate_and_consume_confirmation(
                        confirmation_nonce.strip().upper(), max_sats, "settle_agent_service", l402_endpoint
                    )
                    if confirmation is None:
                        return json.dumps({
                            "success": False,
                            "error": (
                                "Confirmation code is invalid, expired, already used, or does not match THIS "
                                "settlement's amount, tool, and endpoint. Codes are bound to the exact amount, tool, "
                                "and destination approved — a code cannot be redirected to a different endpoint."
                            ),
                            "message": (
                                "Ask the human operator for the code shown in the server console, then call "
                                "settle_agent_service again with confirmation_nonce set to it."
                            ),
                        })
                    # Human-relayed code validated (amount + tool + endpoint bound) — fall through and settle.
                else:
                    # OUT-OF-BAND CONFIRMATION: the code goes to the human on the CONFIGURED
                    # approval channel (stderr locally; webhook/file/refuse when hosted) and
                    # never into this result, so an injected agent can't self-approve.
                    dispatch = await budget_service.request_confirmation(
                        ConfirmationRequest(
                            amount_sats=max_sats,
                            amount_usd=result.amount_usd,
                            tool_name="settle_agent_service",
                            description=endpoint_display,
                            destination=l402_endpoint,
                            title="L402 SETTLEMENT CONFIRMATION REQUIRED",
                            summary=(
                                f"settle_agent_service — up to ${result.amount_usd:.2f} "
                                f"({max_sats:,} sats), {endpoint_display}"
                            ),
                        )
                    )
                    if not dispatch.delivered:
                        return json.dumps({
                            "success": False,
                            "requiresConfirmation": False,
                            "confirmationChannel": dispatch.channel_name,
                            "error": dispatch.refusal_reason,
                            "message": (
                                "The settlement was REFUSED, not queued for approval — no human can be asked "
                                "for a code on this server. Retrying will not help until the operator "
                                "changes the configuration."
                            ),
                            "amount": {"maxSats": max_sats, "maxUsd": float(result.amount_usd)},
                            "agreementId": agreement_id,
                        })
                    return json.dumps({
                        "success": False,
                        "requiresConfirmation": True,
                        "confirmationChannel": dispatch.channel_name,
                        "approvalLevel": result.level.value,
                        "error": "L402 settlement requires human confirmation",
                        "message": (
                            f"Settling this service via {endpoint_display} may cost up to ${result.amount_usd:.2f} "
                            f"({max_sats:,} sats), above the auto-approve threshold. A confirmation code was "
                            f"{dispatch.operator_hint} — visible to the human operator, NOT to you. Ask the human to "
                            "read that code and give it to you."
                        ),
                        "howToConfirm": (
                            "Ask the human operator for the confirmation code, then call "
                            'settle_agent_service(l402_endpoint="...", confirmation_nonce="<code-from-human>").'
                        ),
                        "amount": {"maxSats": max_sats, "maxUsd": float(result.amount_usd)},
                        "expiresInSeconds": 120,
                        "agreementId": agreement_id,
                    })

            if result.level == ApprovalLevel.LOG_AND_APPROVE:
                logger.info(
                    f"Log-and-approve ASA settlement: up to {max_sats} sats "
                    f"(${result.amount_usd:.2f}) for {l402_endpoint[:50]}..."
                )

        # Ambient payment intent: the durable receipt is written at the wallet seam
        # (ReceiptRecordingWallet) when the L402 invoice is paid; this scope enriches
        # it with the redacted settlement endpoint and returns the honest
        # receipt_written signal.
        receipt_scope = PaymentReceiptScope(
            "l402", context=_redact_url_for_display(l402_endpoint), policy=payment_policy
        )
        with receipt_scope:
            response_text, amount_paid, _payment_receipt = await l402_client.fetch(
                url=l402_endpoint,
                method=method,
                headers={},
                body=body,
                max_sats=max_sats,
            )

        # PASSIVE: the client (l402_client.fetch) already recorded the spend + payment
        # history + cooldown EXACTLY ONCE. The tool must NOT record any of that again — it
        # only formats the result and READS (never writes) the session totals for display.
        session_info = None
        if amount_paid is not None and amount_paid > 0:
            if budget_service is not None:
                logger.info(f"Settled {amount_paid} sats for ASA service at {l402_endpoint}")
                status = budget_service.get_status()
                session_info = {
                    "spentSats": status["session"]["spentSats"],
                    "spentUsd": status["session"]["spentUsd"],
                    "remainingUsd": status["session"]["remainingUsd"],
                    "requestCount": status["session"]["requestCount"],
                }

            response_body = (
                response_text[:5000] if len(response_text) > 5000 else response_text
            )
            payload = {
                "success": True,
                "receipt_written": receipt_scope.receipt_written or False,
                "settlement": {
                    "paid": True,
                    "amountSats": amount_paid,
                    "l402Endpoint": l402_endpoint,
                    "agreementId": agreement_id,
                },
                "response": {
                    "content": response_body,
                },
                "message": f"Service settled successfully. Paid {amount_paid} sats via L402.",
            }
            if session_info:
                payload["session"] = session_info
            return json.dumps(payload, indent=2)

        # No payment was required (free tier or already paid)
        response_body = (
            response_text[:5000] if len(response_text) > 5000 else response_text
        )
        return json.dumps({
            "success": True,
            "settlement": {
                "paid": False,
                "l402Endpoint": l402_endpoint,
                "agreementId": agreement_id,
            },
            "response": {
                "content": response_body,
            },
            "message": "Service accessed successfully. No payment was required.",
        }, indent=2)

    except Exception as e:
        logger.exception(f"Error settling service at {l402_endpoint}")

        # PASSIVE: the L402 invoice was paid inside l402_client.fetch, which ALREADY recorded
        # the spend + cooldown (once) before raising. The tool must NOT record anything here —
        # doing so would double-count. ``amount_paid`` is attached to the raised error purely
        # so the tool can SURFACE the settled amount + token; it is not a signal to record.
        amount_paid = getattr(e, "amount_paid", None)
        l402_token = getattr(e, "l402_token", None)

        # A paid-but-not-2xx retry (a 3xx redirect OR an error such as HTTP 500) is an honest
        # "already paid, then not delivered" outcome, NEVER a settlement failure. Surface the
        # paid amount + credential (+ redirect target if any) with an explicit "do NOT pay
        # again" message so the agent retries the target WITH the token instead of re-paying —
        # rather than falling through to the generic "Error settling service" that would tell
        # the agent nothing was paid and invite a second payment. (Parity with .NET.)
        if amount_paid:
            redirect_location = e.location if isinstance(e, L402RedirectError) else None
            payload = {
                "success": False,
                "alreadyPaid": True,
                "l402Endpoint": l402_endpoint,
                "agreementId": agreement_id,
                # Money moved — the seam wrote (or failed to write) the receipt
                # inside the client's payment leg; surface a definite boolean.
                "receipt_written": (
                    (receipt_scope.receipt_written if receipt_scope else None) or False
                ),
                "payment": {
                    "paid": True,
                    "amountSats": amount_paid,
                    "l402Token": l402_token,
                },
            }
            if redirect_location is not None:
                payload["redirect_location"] = redirect_location
                payload["message"] = (
                    f"Payment succeeded ({amount_paid} sats). The resource redirected to {redirect_location}. "
                    "You have ALREADY PAID — do NOT pay again. Retry the redirect target with the l402Token above."
                )
            else:
                payload["message"] = (
                    f"Payment succeeded ({amount_paid} sats), but the endpoint returned an error on the "
                    "authorized retry. You have ALREADY PAID — do NOT pay again. Retry the endpoint with the "
                    "l402Token above."
                )
            return json.dumps(payload, indent=2)

        return json.dumps({
            "success": False,
            "receipt_written": receipt_scope.receipt_written if receipt_scope else None,
            "error": f"Error settling service: {sanitize_error(str(e))}",
            "l402Endpoint": l402_endpoint,
            "agreementId": agreement_id,
        })


# MCP tool schema (lives beside its handler; registered in tools/registry.py).
SETTLE_AGENT_SERVICE_TOOL = Tool(
    name="settle_agent_service",
    description=(
        "Settle an agent service agreement via L402 payment (CONSUMER/REQUESTER side). "
        "Pays the L402 endpoint specified in the agreement, completing the service transaction. "
        "Uses the same L402 auto-pay flow as access_l402_resource. "
        "The L402 endpoint URL comes from an agent_services action=discover or "
        "action=request result. "
        "NOTE: If you are the PROVIDER (selling a service), use l402_producer action=create to "
        "generate a Lightning invoice at the agreed price, then l402_producer action=verify "
        "to confirm payment "
        "before delivering the service."
    ),
    inputSchema={
        "type": "object",
        "properties": {
            "l402_endpoint": {
                "type": "string",
                "description": "L402 endpoint URL from the service agreement",
            },
            "method": {
                "type": "string",
                "description": "HTTP method, GET or HEAD only (paid HTTP is read-only). Defaults to GET", "enum": ["GET", "HEAD"],
                "default": "GET",
            },
            "body": {
                "type": "string",
                "description": "Optional request body (rarely needed; write methods are refused)",
            },
            "agreement_id": {
                "type": "string",
                "description": "Agreement event ID for tracking",
            },
            "max_sats": {
                "type": "integer",
                "description": "Maximum satoshis to pay",
                "default": 1000,
            },
            "confirmation_nonce": {
                "type": "string",
                "description": "Confirmation code the human operator read from the server console, for settlements above the auto-approve threshold. The code is NEVER in a tool result — ask the human for it. Omit on the first call to request one.",
            },
        },
        "required": ["l402_endpoint"],
    },
)
