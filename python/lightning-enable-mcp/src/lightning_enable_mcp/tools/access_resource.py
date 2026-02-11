"""
Access L402 Resource Tool

Fetches URLs with automatic L402 payment handling.
Uses the new BudgetService with multi-tier approval logic.
"""

import json
import logging
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..budget import BudgetManager
    from ..budget_service import BudgetService
    from ..l402_client import L402Client

from ..config import ApprovalLevel

logger = logging.getLogger("lightning-enable-mcp.tools.access")


async def access_l402_resource(
    url: str,
    method: str = "GET",
    headers: dict[str, str] | None = None,
    body: str | None = None,
    max_sats: int = 1000,
    confirmed: bool = False,
    l402_client: "L402Client | None" = None,
    budget_manager: "BudgetManager | None" = None,
    budget_service: "BudgetService | None" = None,
) -> str:
    """
    Fetch a URL with automatic L402 payment handling.

    If the server returns a 402 Payment Required response with an L402 challenge,
    this function will automatically pay the invoice and retry the request.

    NOTE: Most MCP clients (including Claude Code) don't support elicitation yet.
    L402 payments above auto_approve threshold require explicit confirmation
    by calling this tool again with confirmed=True.

    Args:
        url: The URL to fetch
        method: HTTP method (GET, POST, PUT, DELETE)
        headers: Optional additional request headers
        body: Optional request body for POST/PUT requests
        max_sats: Maximum satoshis to pay for this request
        confirmed: Set to True to confirm a payment above the auto-approve threshold
        l402_client: L402 client instance
        budget_manager: Legacy budget manager (deprecated, use budget_service)
        budget_service: BudgetService for multi-tier approval logic

    Returns:
        Response body text or error message
    """
    if not l402_client:
        return "Error: L402 client not initialized. Check NWC connection."

    headers = headers or {}
    method = method.upper()

    # Validate method
    if method not in ("GET", "POST", "PUT", "DELETE"):
        return f"Error: Invalid HTTP method: {method}"

    try:
        # Use new BudgetService if available, otherwise fall back to legacy BudgetManager
        if budget_service:
            # Check approval level using new multi-tier system
            result = await budget_service.check_approval_level(max_sats)

            if result.level == ApprovalLevel.DENY:
                return json.dumps({
                    "success": False,
                    "error": "Payment denied by budget policy",
                    "denialReason": result.denial_reason,
                    "url": url,
                    "budget": {
                        "maxSats": max_sats,
                        "maxUsd": float(result.amount_usd),
                        "remainingSessionUsd": float(result.remaining_session_budget_usd),
                    },
                    "note": "Edit ~/.lightning-enable/config.json to change limits."
                })

            # Check if payment requires confirmation (FORM_CONFIRM or URL_CONFIRM)
            if result.requires_confirmation and not confirmed:
                url_display = url[:50] + "..." if len(url) > 50 else url
                return json.dumps({
                    "success": False,
                    "requiresConfirmation": True,
                    "approvalLevel": result.level.value,
                    "error": "L402 payment requires your confirmation",
                    "message": f"This L402 request to {url_display} may cost up to ${result.amount_usd:.2f} ({max_sats:,} sats). "
                              "To proceed, call access_l402_resource again with confirmed=True.",
                    "howToConfirm": 'Call: access_l402_resource(url="...", confirmed=True)',
                    "amount": {
                        "maxSats": max_sats,
                        "maxUsd": float(result.amount_usd)
                    },
                    "budget": {
                        "remainingSessionUsd": float(result.remaining_session_budget_usd),
                    }
                })

            # LOG_AND_APPROVE: Log for user awareness but proceed
            if result.level == ApprovalLevel.LOG_AND_APPROVE:
                logger.info(f"Log-and-approve L402 request: up to {max_sats} sats (${result.amount_usd:.2f}) for {url[:50]}...")

        elif budget_manager:
            # Legacy budget manager fallback
            status = budget_manager.get_status()
            if status["remaining"] <= 0:
                return json.dumps({
                    "success": False,
                    "error": "Session budget exhausted",
                    "message": f"Spent {status['spent']}/{status['limits']['per_session']} sats. "
                              "Use configure_budget to increase limit.",
                    "budget": status
                })

            # Check if payment requires confirmation (above auto_approve threshold)
            auto_approve_sats = getattr(budget_manager, 'auto_approve_sats', 1000)
            if max_sats > auto_approve_sats and not confirmed:
                # Estimate USD value (~$0.001 per sat at ~$100k/BTC)
                estimated_usd = max_sats * 0.001
                url_display = url[:50] + "..." if len(url) > 50 else url
                return json.dumps({
                    "success": False,
                    "requiresConfirmation": True,
                    "error": "L402 payment requires your confirmation",
                    "message": f"This L402 request to {url_display} may cost up to ~${estimated_usd:.2f} ({max_sats:,} sats), "
                              f"which exceeds the auto-approve threshold of {auto_approve_sats:,} sats. "
                              "To proceed, call access_l402_resource again with confirmed=True.",
                    "howToConfirm": 'Call: access_l402_resource(url="...", confirmed=True)',
                    "amount": {
                        "maxSats": max_sats,
                        "estimatedUsd": round(estimated_usd, 2)
                    },
                    "thresholds": {
                        "autoApprove": auto_approve_sats,
                        "note": "Payments above this require confirmation"
                    }
                })

        # Make request with L402 handling
        response_text, amount_paid = await l402_client.fetch(
            url=url,
            method=method,
            headers=headers,
            body=body,
            max_sats=max_sats,
        )

        # Record payment if one was made
        if amount_paid is not None:
            if budget_service:
                budget_service.record_spend(amount_paid)
                budget_service.record_payment_time()
                logger.info(f"Paid {amount_paid} sats for L402 access to {url}")

                # Get updated session info
                status = budget_service.get_status()
                session_info = {
                    "spentSats": status["session"]["spentSats"],
                    "spentUsd": status["session"]["spentUsd"],
                    "remainingUsd": status["session"]["remainingUsd"],
                    "requestCount": status["session"]["requestCount"],
                }
            elif budget_manager:
                budget_manager.record_payment(
                    url=url,
                    amount_sats=amount_paid,
                    invoice="(auto-paid)",
                    preimage="(auto-paid)",
                    status="success",
                )
                logger.info(f"Paid {amount_paid} sats for L402 access to {url}")
                session_info = {
                    "spentSats": budget_manager.session_spent,
                    "remainingSats": budget_manager.max_per_session - budget_manager.session_spent,
                }
            else:
                session_info = None
        else:
            session_info = None

        # Format response
        result = {
            "success": True,
            "url": url,
            "method": method,
            "paid_sats": amount_paid,
            "response": response_text[:5000] if len(response_text) > 5000 else response_text,
        }

        if amount_paid:
            result["message"] = f"Paid {amount_paid} sats for access"

        if session_info:
            result["session"] = session_info

        return json.dumps(result, indent=2)

    except Exception as e:
        logger.exception(f"Error accessing {url}")

        error_result = {
            "success": False,
            "url": url,
            "method": method,
            "error": str(e),
        }

        return json.dumps(error_result, indent=2)
