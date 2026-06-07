"""
Settle Agent Service Tool (MONEY PATH)

Settles an agent service agreement via L402 payment (CONSUMER/REQUESTER side).
Pays the L402 endpoint specified in the agreement, completing the service
transaction. Uses the SAME wallet + budget-gating flow as access_l402_resource
(BudgetService.check_approval_level / record_spend / record_payment_time) — it
does NOT use the Lightning Enable API key.

For the PROVIDER side (selling a service), use create_l402_challenge to generate
a Lightning invoice at the negotiated price, then verify_l402_payment to confirm
payment before delivering the service.
"""

import json
import logging
from urllib.parse import urlparse
from typing import TYPE_CHECKING

from ..config import ApprovalLevel
from . import sanitize_error

if TYPE_CHECKING:
    from ..budget_service import BudgetService
    from ..l402_client import L402Client

logger = logging.getLogger("lightning-enable-mcp.tools.settle_agent_service")

# HTTP method whitelist (mirrors .NET AgentSettleTool)
_ALLOWED_METHODS = {"GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"}
_LOCALHOST_HOSTS = {"localhost", "127.0.0.1", "::1"}


async def settle_agent_service(
    l402_endpoint: str,
    method: str = "GET",
    body: str | None = None,
    agreement_id: str | None = None,
    max_sats: int = 1000,
    confirmed: bool = False,
    l402_client: "L402Client | None" = None,
    budget_service: "BudgetService | None" = None,
) -> str:
    """
    Settle an agent service agreement via L402 payment (consumer/requester side).

    Args:
        l402_endpoint: L402 endpoint URL from the service agreement
        method: HTTP method (GET, POST, ...). Defaults to GET
        body: Optional request body for POST requests (e.g., service params as JSON)
        agreement_id: Optional agreement event ID for tracking
        max_sats: Maximum satoshis to pay (default 1000)
        confirmed: Set True to confirm a payment above the auto-approve threshold
        l402_client: L402 client instance (wallet-backed)
        budget_service: BudgetService for multi-tier approval gating

    Returns:
        JSON with settlement result or an error message.
    """
    try:
        # Input validation
        if not l402_endpoint or not l402_endpoint.strip():
            return json.dumps({
                "success": False,
                "error": "L402 endpoint URL is required. Get it from discover_agent_services or request_agent_service results.",
            })

        parsed = urlparse(l402_endpoint)
        if parsed.scheme not in ("http", "https") or not parsed.netloc:
            return json.dumps({
                "success": False,
                "error": "Invalid L402 endpoint URL. Must be an HTTP or HTTPS URL.",
            })

        # Security: reject plain HTTP except for localhost (dev use)
        if parsed.scheme == "http" and parsed.hostname not in _LOCALHOST_HOSTS:
            return json.dumps({
                "success": False,
                "error": "L402 settlement requires HTTPS. Plain HTTP is only allowed for localhost during development.",
            })

        method = method.upper()
        if method not in _ALLOWED_METHODS:
            return json.dumps({
                "success": False,
                "error": f"Invalid HTTP method '{method}'. Allowed methods: {', '.join(sorted(_ALLOWED_METHODS))}.",
            })

        if l402_client is None:
            return json.dumps({
                "success": False,
                "error": "L402 HTTP client not available. Ensure a wallet is configured.",
            })

        # The underlying L402Client.fetch only handles GET/POST/PUT/DELETE.
        # HEAD/OPTIONS/PATCH pass the whitelist above but cannot be settled.
        if method not in ("GET", "POST", "PUT", "DELETE"):
            return json.dumps({
                "success": False,
                "error": f"HTTP method '{method}' is allowed but not supported by the L402 settlement client. Use GET, POST, PUT, or DELETE.",
            })

        # Budget gating BEFORE payment (mirrors access_l402_resource)
        if budget_service is not None:
            result = await budget_service.check_approval_level(max_sats)

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
                    "hint": "Increase max_sats, or edit ~/.lightning-enable/config.json to change limits.",
                })

            if result.requires_confirmation and not confirmed:
                endpoint_display = (
                    l402_endpoint[:50] + "..." if len(l402_endpoint) > 50 else l402_endpoint
                )
                return json.dumps({
                    "success": False,
                    "requiresConfirmation": True,
                    "approvalLevel": result.level.value,
                    "error": "L402 settlement requires your confirmation",
                    "message": (
                        f"Settling this service via {endpoint_display} may cost up to "
                        f"${result.amount_usd:.2f} ({max_sats:,} sats). "
                        "To proceed, call settle_agent_service again with confirmed=True."
                    ),
                    "howToConfirm": 'Call: settle_agent_service(l402_endpoint="...", confirmed=True)',
                    "amount": {
                        "maxSats": max_sats,
                        "maxUsd": float(result.amount_usd),
                    },
                    "budget": {
                        "remainingSessionUsd": float(result.remaining_session_budget_usd),
                    },
                    "agreementId": agreement_id,
                })

            if result.level == ApprovalLevel.LOG_AND_APPROVE:
                logger.info(
                    f"Log-and-approve ASA settlement: up to {max_sats} sats "
                    f"(${result.amount_usd:.2f}) for {l402_endpoint[:50]}..."
                )

        # Execute the L402 payment flow
        response_text, amount_paid = await l402_client.fetch(
            url=l402_endpoint,
            method=method,
            headers={},
            body=body,
            max_sats=max_sats,
        )

        # Record spend ONLY after a payment actually happened
        session_info = None
        if amount_paid is not None and amount_paid > 0:
            if budget_service is not None:
                budget_service.record_spend(amount_paid)
                budget_service.record_payment_time()
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
        return json.dumps({
            "success": False,
            "error": f"Error settling service: {sanitize_error(str(e))}",
            "l402Endpoint": l402_endpoint,
            "agreementId": agreement_id,
        })
