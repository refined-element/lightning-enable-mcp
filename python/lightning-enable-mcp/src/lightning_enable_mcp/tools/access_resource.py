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
    from ..receipt_service import ReceiptService

from ..config import ApprovalLevel
from ..l402_client import L402RedirectError
from . import sanitize_error
from ._ssrf_guard import SsrfError, validate_url_allowed

logger = logging.getLogger("lightning-enable-mcp.tools.access")


def _redact_url_for_display(url: str, limit: int = 50) -> str:
    """Return a display-safe URL with credentials stripped.

    The query string, fragment, and userinfo can carry secrets (e.g. ``?token=...``).
    This is printed to stderr / logged, so keep only scheme://host[:port]/path and mark
    when anything was dropped — never leak the sensitive parts (engineering standard #5).
    """
    try:
        from urllib.parse import urlsplit, urlunsplit

        parts = urlsplit(url)
        host = parts.hostname or ""
        if ":" in host:  # IPv6 literal — urlsplit unbrackets it; re-bracket so host:port is unambiguous
            host = f"[{host}]"
        netloc = f"{host}:{parts.port}" if parts.port else host
        dropped = bool(parts.query or parts.fragment or parts.username or parts.password)
        safe = urlunsplit((parts.scheme, netloc, parts.path, "", ""))
        if dropped:
            safe = f"{safe} (redacted)"
    except Exception:
        safe = url.split("?", 1)[0].split("#", 1)[0]
        if "//" in safe:
            scheme_sep, rest = safe.split("//", 1)
            if "@" in rest:
                rest = rest.split("@", 1)[1]
            safe = scheme_sep + "//" + rest
    return safe[:limit] + "..." if len(safe) > limit else safe


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
    receipt_service: "ReceiptService | None" = None,
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

    try:
        # BudgetService is the single source of truth for spending limits + the
        # out-of-band confirmation flow.
        if budget_service:
            # Check approval level using new multi-tier system
            result = await budget_service.check_approval_level(max_sats)
            payment_policy = getattr(result.level, "value", str(result.level))

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

        # Make request with L402 handling
        response_text, amount_paid = await l402_client.fetch(
            url=url,
            method=method,
            headers=headers,
            body=body,
            max_sats=max_sats,
        )

        # Record payment if one was made
        session_info = None
        if amount_paid is not None:
            if budget_service:
                budget_service.record_spend(amount_paid)
                budget_service.record_payment_time()
                logger.info(f"Paid {amount_paid} sats for L402 access to {_redact_url_for_display(url)}")

                # Get updated session info
                status = budget_service.get_status()
                session_info = {
                    "spentSats": status["session"]["spentSats"],
                    "spentUsd": status["session"]["spentUsd"],
                    "remainingUsd": status["session"]["remainingUsd"],
                    "requestCount": status["session"]["requestCount"],
                }

            # Audit trail (separate from limits). Preimage is NEVER stored.
            if payment_history_service:
                payment_history_service.record_payment(
                    url=_redact_url_for_display(url),
                    amount_sats=amount_paid,
                    status="success",
                )

            # Durable, off-context-path spend receipt (redacted endpoint, no secrets).
            # Best-effort — a receipt failure must NEVER turn a settled payment into
            # an error result (which a caller might retry, double-paying).
            if receipt_service is not None:
                try:
                    receipt_service.log_payment(
                        endpoint=_redact_url_for_display(url),
                        amount_sats=amount_paid,
                        policy=payment_policy,
                        session_spent_sats=(session_info or {}).get("spentSats"),
                    )
                except Exception:
                    logger.warning("Receipt logging failed (payment already settled)")

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
        logger.exception(f"Error accessing {_redact_url_for_display(url)}")

        # Paid-but-retry-failed (store split-flow): the invoice settled — money left the
        # wallet — even though the resource retry failed. Record the real spend so the
        # budget/history/receipt don't silently omit it (parity with the .NET runtime).
        # Best-effort; never mask the original error.
        amount_paid = getattr(e, "amount_paid", None)
        if amount_paid:
            try:
                if budget_service:
                    budget_service.record_spend(amount_paid)
                    budget_service.record_payment_time()
                if payment_history_service:
                    payment_history_service.record_payment(
                        url=_redact_url_for_display(url),
                        amount_sats=amount_paid,
                        status="paid_retry_failed",
                    )
                if receipt_service is not None:
                    spent = None
                    if budget_service:
                        try:
                            spent = budget_service.get_status()["session"]["spentSats"]
                        except Exception:
                            spent = None
                    receipt_service.log_payment(
                        endpoint=_redact_url_for_display(url),
                        amount_sats=amount_paid,
                        policy=payment_policy,
                        session_spent_sats=spent,
                    )
            except Exception:
                logger.warning("Failed to record paid-but-retry-failed spend")

        error_result = {
            "success": False,
            "url": url,
            "method": method,
            "error": sanitize_error(str(e)),
        }

        # Unfollowed 3xx redirect (follow_redirects=False): surface the target as an
        # actionable next URL so the agent can re-call, instead of a cryptic HTTP error.
        # We never follow it (header-leak / L402 host-change reasons). Parity with .NET's
        # redirect_location field. amount_paid, if present, was already recorded above.
        if isinstance(e, L402RedirectError):
            error_result["redirect_location"] = e.location
            if amount_paid:
                # Paid-retry redirect: surface the paid amount + credential + an explicit
                # "already paid, do NOT re-pay" message so the agent retries the redirect
                # target WITH the token instead of paying a second time (parity with .NET).
                error_result["alreadyPaid"] = True
                error_result["payment"] = {
                    "paid": True,
                    "amountSats": amount_paid,
                    "l402Token": getattr(e, "l402_token", None),
                }
                error_result["message"] = (
                    f"Payment succeeded ({amount_paid} sats). The resource redirected to {e.location}. "
                    "You have ALREADY PAID — do NOT pay again. Retry the redirect target with the l402Token above."
                )

        return json.dumps(error_result, indent=2)
