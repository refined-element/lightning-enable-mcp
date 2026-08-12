"""
Access L402 Resource Tool

Fetches URLs with automatic L402 payment handling.
Uses the new BudgetService with multi-tier approval logic.
"""

import json
import logging
import sys
from typing import TYPE_CHECKING, Optional

if TYPE_CHECKING:
    from ..budget_service import BudgetService
    from ..payment_history_service import PaymentHistoryService
    from ..l402_client import L402Client

from ..config import ApprovalLevel
from ..l402_client import L402RedirectError
from ..receipt_seam import PaymentReceiptScope, policy_label
from .._url_redact import redact_url_for_display as _redact_url_for_display
from . import sanitize_error
from ._ssrf_guard import SsrfError, validate_url_allowed

logger = logging.getLogger("lightning-enable-mcp.tools.access")


async def access_l402_resource(
    url: str,
    method: str = "GET",
    headers: dict[str, str] | None = None,
    body: str | None = None,
    max_sats: int = 1000,
    confirmation_nonce: "Optional[str]" = None,
    l402_client: "L402Client | None" = None,
    budget_service: "BudgetService | None" = None,
    payment_history_service: "PaymentHistoryService | None" = None,
) -> str:
    """
    Fetch a URL with automatic L402 payment handling.

    If the server returns a 402 Payment Required response with an L402 challenge,
    this function will automatically pay the invoice and retry the request.

    L402 payments above the auto-approve threshold require OUT-OF-BAND confirmation: the
    server prints a code to its console/stderr (the human operator sees it; the model does
    not), and you must ask the human for that code and pass it as confirmation_nonce. The
    code is never in a tool result, so a prompt-injected agent cannot self-approve.

    Args:
        url: The URL to fetch
        method: HTTP method (GET, POST, PUT, DELETE)
        headers: Optional additional request headers
        body: Optional request body for POST/PUT requests
        max_sats: Maximum satoshis to pay for this request
        confirmation_nonce: The code the human read from the server console (for payments
            above the auto-approve threshold). Omit on the first call to request one.
        l402_client: L402 client instance
        budget_service: BudgetService for multi-tier approval logic
        payment_history_service: PaymentHistoryService for the session audit trail

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

    # SSRF guard (F-10e): refuse targets that resolve to a private/internal/reserved
    # address (loopback, RFC1918, link-local incl. 169.254.169.254 cloud metadata,
    # etc.) BEFORE any request is made. The message is generic — it never echoes the
    # resolved internal host/IP. See _ssrf_guard for the DNS-rebind residual window.
    try:
        await validate_url_allowed(url)
    except SsrfError as e:
        return json.dumps({
            "success": False,
            "url": url,
            "method": method,
            "error": str(e),
        }, indent=2)

    # Captured for the durable receipt (success path). Overwritten with the real
    # approval tier once the budget check runs.
    payment_policy = "auto (no budget check)"

    # Declared outside the try so the except handler can report whether a durable
    # receipt landed when the client raises AFTER the invoice settled.
    receipt_scope = None

    try:
        # BudgetService is the single source of truth for spending limits + the
        # out-of-band confirmation flow.
        if budget_service:
            # Check approval level using new multi-tier system
            result = await budget_service.check_approval_level(max_sats)
            payment_policy = policy_label(result.level)

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

            # OUT-OF-BAND confirmation for above-threshold L402 payments. The code is
            # printed to the server console (stderr) only — never in this result — so the
            # human operator (not the model) must read it and relay it back.
            if result.requires_confirmation:
                url_display = _redact_url_for_display(url)
                if confirmation_nonce:
                    confirmation = budget_service.validate_and_consume_confirmation(
                        confirmation_nonce.strip().upper(), max_sats, "access_l402_resource", url
                    )
                    if confirmation is None:
                        return json.dumps({
                            "success": False,
                            "error": (
                                "Confirmation code is invalid, expired, already used, or does not match THIS "
                                "request's amount, tool, and URL. Codes are bound to the exact amount, tool, and "
                                "destination approved — a code cannot be redirected to a different URL."
                            ),
                            "message": (
                                "Ask the human operator for the code shown in the server console, then call "
                                "access_l402_resource again with confirmation_nonce set to it."
                            ),
                        })
                    # Human-relayed code validated (amount + tool + URL bound) — fall through.
                else:
                    pending = budget_service.create_pending_confirmation(
                        max_sats, result.amount_usd, "access_l402_resource", url_display,
                        destination=url,
                    )
                    print(
                        "[Lightning Enable] *** L402 PAYMENT CONFIRMATION REQUIRED ***\n"
                        f"  access_l402_resource — up to ${result.amount_usd:.2f} ({max_sats:,} sats), {url_display}\n"
                        f"  Confirmation code: {pending.nonce}\n"
                        "  To approve, give this code to the agent. Expires in 120s.",
                        file=sys.stderr,
                        flush=True,
                    )
                    return json.dumps({
                        "success": False,
                        "requiresConfirmation": True,
                        "approvalLevel": result.level.value,
                        "error": "L402 payment requires human confirmation",
                        "message": (
                            f"This L402 request to {url_display} may cost up to ${result.amount_usd:.2f} "
                            f"({max_sats:,} sats), above the auto-approve threshold. A confirmation code was printed "
                            "to the server console/logs — visible to the human operator, NOT to you. Ask the human to "
                            "read that code and give it to you."
                        ),
                        "howToConfirm": (
                            "Ask the human operator for the confirmation code shown in the server console, then call "
                            'access_l402_resource(url="...", confirmation_nonce="<code-from-human>").'
                        ),
                        "amount": {"maxSats": max_sats, "maxUsd": float(result.amount_usd)},
                        "expiresInSeconds": 120,
                        "budget": {"remainingSessionUsd": float(result.remaining_session_budget_usd)},
                    })

            # LOG_AND_APPROVE: Log for user awareness but proceed
            if result.level == ApprovalLevel.LOG_AND_APPROVE:
                logger.info(f"Log-and-approve L402 request: up to {max_sats} sats (${result.amount_usd:.2f}) for {_redact_url_for_display(url)}")

        # Ambient payment intent: the durable receipt is written by the wallet-seam
        # decorator (ReceiptRecordingWallet) the moment the invoice is paid inside
        # l402_client.fetch — this scope only enriches it (redacted endpoint +
        # policy) and carries back the honest receipt_written signal. The tool
        # itself writes nothing, so an L402 payment produces exactly ONE receipt.
        receipt_scope = PaymentReceiptScope(
            "l402", context=_redact_url_for_display(url), policy=payment_policy
        )
        with receipt_scope:
            response_text, amount_paid = await l402_client.fetch(
                url=url,
                method=method,
                headers=headers,
                body=body,
                max_sats=max_sats,
            )

        # PASSIVE: the client (l402_client.fetch) already recorded the spend + payment
        # history + cooldown EXACTLY ONCE. The tool must NOT record any of that again — it
        # only READS (never writes) the session totals for display.
        session_info = None
        if amount_paid is not None:
            if budget_service:
                logger.info(f"Paid {amount_paid} sats for L402 access to {_redact_url_for_display(url)}")

                # Read (do not write) the updated session info for display.
                status = budget_service.get_status()
                session_info = {
                    "spentSats": status["session"]["spentSats"],
                    "spentUsd": status["session"]["spentUsd"],
                    "remainingUsd": status["session"]["remainingUsd"],
                    "requestCount": status["session"]["requestCount"],
                }

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
            result["receipt_written"] = receipt_scope.receipt_written or False

        if session_info:
            result["session"] = session_info

        return json.dumps(result, indent=2)

    except Exception as e:
        logger.exception(f"Error accessing {_redact_url_for_display(url)}")

        # Paid-but-retry-failed (a 3xx redirect OR an error such as HTTP 500): the invoice
        # settled inside l402_client.fetch, which ALREADY recorded the spend + cooldown once
        # AND wrote the durable receipt at the wallet seam before raising. The tool is
        # PASSIVE — it records nothing; it only surfaces the settled amount + token and the
        # honest receipt signal: None = nothing was paid, True/False = money moved and the
        # receipt did/didn't land.
        amount_paid = getattr(e, "amount_paid", None)
        l402_token = getattr(e, "l402_token", None)

        error_result = {
            "success": False,
            "url": url,
            "method": method,
            "error": sanitize_error(str(e)),
            "receipt_written": receipt_scope.receipt_written if receipt_scope else None,
        }

        # Unfollowed 3xx redirect (follow_redirects=False): surface the target as an
        # actionable next URL so the agent can re-call, instead of a cryptic HTTP error.
        # We never follow it (header-leak / L402 host-change reasons). Parity with .NET's
        # redirect_location field.
        if isinstance(e, L402RedirectError):
            error_result["redirect_location"] = e.location

        # Paid-but-not-2xx (redirect OR error): surface the paid amount + credential + an
        # explicit "already paid, do NOT re-pay" message so the agent retries the target WITH
        # the token instead of paying a second time (parity with .NET). This covers BOTH a
        # paid-retry redirect and a paid-retry error (e.g. HTTP 500) — never a "verify the
        # URL" / not-paid result for a settlement whose invoice was paid.
        if amount_paid:
            error_result["alreadyPaid"] = True
            # Money moved — the signal must be a definite boolean, never null.
            error_result["receipt_written"] = (
                (receipt_scope.receipt_written if receipt_scope else None) or False
            )
            error_result["payment"] = {
                "paid": True,
                "amountSats": amount_paid,
                "l402Token": l402_token,
            }
            if isinstance(e, L402RedirectError):
                error_result["message"] = (
                    f"Payment succeeded ({amount_paid} sats). The resource redirected to {e.location}. "
                    "You have ALREADY PAID — do NOT pay again. Retry the redirect target with the l402Token above."
                )
            else:
                error_result["message"] = (
                    f"Payment succeeded ({amount_paid} sats), but the endpoint returned an error on the "
                    "authorized retry. You have ALREADY PAID — do NOT pay again. Retry the endpoint with the "
                    "l402Token above."
                )

        return json.dumps(error_result, indent=2)
