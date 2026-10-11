"""
Renew Lightning Enable Subscription Tool (pay for your own plan over L402)

Buys or extends one period of the Agentic Commerce plan for the account behind the
configured merchant API key. Needs the API key (it pays for an EXISTING account) AND
a preimage-capable wallet. Python port of the .NET RenewSubscriptionTool — the result
shapes and field names match.

Flow (reuses the existing L402 machinery):
  1. POST {} to /api/merchant/subscription/renew via L402Client.fetch, with X-Api-Key
  2. The server replies 402 with a fixed-sats L402 challenge
  3. L402Client pays it and retries the SAME POST (X-Api-Key merged into the retry)
     with `Authorization: L402 <macaroon>:<preimage>`
  4. The server returns { success, alreadyCredited, planTier, currentPeriodEnd,
     paidSats, paymentHash }

The price is set by the server; ``max_sats`` is only the ceiling this call may pay.

Funds-safety: gated through BudgetService exactly like create_lightning_enable_account
— above the auto-approve threshold the tool returns ``requiresConfirmation`` and the
code goes to the human on the configured approval channel, never into the result. The
confirmation is bound to the amount, this tool's name, and the renew URL.

The merchant API key must never appear in a result or a log line: every model-visible
string is redacted for the exact configured key as well as for credential-shaped
tokens, and the final result is checked once more before it is returned.

Every call starts a fresh challenge (L402Client keeps no token cache for this POST),
so a second call buys a second period rather than replaying the first credential.
"""

import hashlib
import json
import logging
import os
import re
from typing import TYPE_CHECKING, Any

from mcp.types import Tool

from ..confirmation_channel import ConfirmationRequest
from ..lightning_enable_api import PRODUCER_API_KEY_REQUIRED
from ..receipt_seam import PaymentReceiptScope, policy_label
from . import sanitize_error
from .consolidated import CONFIRMATION_NONCE_DESCRIPTION

if TYPE_CHECKING:
    from ..budget_service import BudgetService
    from ..l402_client import L402Client
    from ..lightning_enable_api import LightningEnableApiClient

logger = logging.getLogger("lightning-enable-mcp.tools.renew_subscription")

TOOL_NAME = "renew_lightning_enable_subscription"

_DEFAULT_BASE_URL = "https://api.lightningenable.com"
_RENEW_PATH = "/api/merchant/subscription/renew"
_REDACTED = "[REDACTED]"

# Server error codes are snake_case identifiers; anything else is not surfaced as a code.
_ERROR_CODE_SHAPE = re.compile(r"^[a-z0-9_]{1,64}$")
# Tolerant fallback: L402Client caps the echoed body at 200 chars, so the JSON can
# arrive truncated and unparseable while the leading "error" member is intact.
_ERROR_CODE_FIELD = re.compile(r'"error"\s*:\s*"([^"]{1,64})"')
# "Request failed: 409 {...}" / "Request failed after payment: 409 {...}"
_STATUS_CODE = re.compile(r"Request failed(?: after payment)?: (\d{3})\b")
_PREIMAGE_SHAPE = re.compile(r"^[0-9a-fA-F]{64}$")


def _renew_url() -> str:
    """Renew endpoint URL. Base is overridable via LIGHTNING_ENABLE_API_URL (same env
    var, same fallback as the signup tool and the producer API client)."""
    base = os.getenv("LIGHTNING_ENABLE_API_URL")
    if not base or base.startswith("${"):
        base = _DEFAULT_BASE_URL
    return f"{base.rstrip('/')}{_RENEW_PATH}"


def _redact_key(message: str, api_key: str | None) -> str:
    """Remove every occurrence of the configured API key from a model-visible string."""
    if not api_key or not message:
        return message
    return message.replace(api_key, _REDACTED)


def _safe(message: str, api_key: str | None) -> str:
    """Redact the configured key FIRST (before truncation can split it), then scrub
    credential-shaped tokens and cap the length."""
    return sanitize_error(_redact_key(message, api_key))


def _extract_error_code(message: str) -> str | None:
    """Pull the server's ``error`` code out of the client's failure text. Only a
    snake_case identifier is returned; anything else is None."""
    if not message:
        return None
    start = message.find("{")
    if start < 0:
        return None
    code: Any = None
    try:
        parsed = json.loads(message[start:])
        if isinstance(parsed, dict):
            code = parsed.get("error")
    except Exception:
        match = _ERROR_CODE_FIELD.search(message[start:])
        code = match.group(1) if match else None
    return code if isinstance(code, str) and _ERROR_CODE_SHAPE.match(code) else None


def _extract_status_code(message: str) -> int | None:
    match = _STATUS_CODE.search(message or "")
    return int(match.group(1)) if match else None


def _derive_payment_hash(token: str | None) -> str | None:
    """SHA256(preimage) — public, and what support needs to find the payment. Derived
    from the paid credential so the preimage itself never leaves this function."""
    if not token or not isinstance(token, str):
        return None
    preimage = token.rsplit(":", 1)[-1]
    if not _PREIMAGE_SHAPE.match(preimage):
        return None
    return hashlib.sha256(bytes.fromhex(preimage)).hexdigest()


def _paid_but_failed_warning(paid_sats: int, payment_hash: str | None) -> str:
    return (
        f"A payment of {paid_sats:,} sats was taken from your wallet but the renewal did not "
        "complete. Do NOT re-run this tool or you may pay again — the server credits each "
        "payment once, and this tool cannot resend the same credential. Contact "
        "support@lightningenable.com with the payment hash"
        + (f" ({payment_hash})." if payment_hash else " from your wallet's payment record.")
    )


def _int_or_none(value: Any) -> int | None:
    return value if isinstance(value, int) and not isinstance(value, bool) else None


def _str_or_none(value: Any) -> str | None:
    return value if isinstance(value, str) else None


async def _budget_gate(
    budget_service: "BudgetService",
    max_sats: int,
    confirmation_nonce: str | None,
    renew_url: str,
) -> tuple[str | None, str]:
    """Budget / out-of-band confirmation gate. Returns (blocked_result, policy_label);
    a non-None blocked result is returned as-is and nothing is paid."""
    from ..config import ApprovalLevel

    approval = await budget_service.check_approval_level(max_sats)
    policy = policy_label(approval.level)
    amount = {"maxSats": max_sats, "usd": round(float(approval.amount_usd), 2)}

    if approval.level == ApprovalLevel.DENY:
        return json.dumps({
            "success": False,
            "error": f"Subscription renewal denied by budget policy: {approval.denial_reason}",
            "budget": {
                "amountSats": max_sats,
                "amountUsd": float(approval.amount_usd),
                "remainingSessionUsd": float(approval.remaining_session_budget_usd),
            },
        }), policy

    if approval.requires_confirmation:
        if confirmation_nonce and confirmation_nonce.strip():
            confirmation = budget_service.validate_and_consume_confirmation(
                confirmation_nonce.strip().upper(), max_sats, TOOL_NAME, renew_url
            )
            if confirmation is None:
                return json.dumps({
                    "success": False,
                    "error": (
                        "Confirmation code is invalid, expired, already used, or does not match THIS "
                        "renewal's amount, tool, and destination. Codes are bound to the exact amount, "
                        "tool, and destination approved — a code cannot be redirected."
                    ),
                    "message": (
                        "The code may have expired (codes are short-lived), been used already, or been "
                        "issued for a different amount/tool. Request a new one by calling "
                        f"{TOOL_NAME} without a confirmation_nonce."
                    ),
                }), policy
            logger.info("Subscription renewal of $%.2f confirmed by the operator's code", float(approval.amount_usd))
        else:
            # OUT-OF-BAND CONFIRMATION: the code goes to the human on the CONFIGURED
            # approval channel and never into this result, so an injected agent can't
            # approve itself.
            dispatch = await budget_service.request_confirmation(
                ConfirmationRequest(
                    amount_sats=max_sats,
                    amount_usd=approval.amount_usd,
                    tool_name=TOOL_NAME,
                    description="Lightning Enable subscription renewal",
                    destination=renew_url,
                    title="SUBSCRIPTION RENEWAL CONFIRMATION REQUIRED",
                    summary=f"{TOOL_NAME} — up to ${approval.amount_usd:.2f} ({max_sats:,} sats)",
                )
            )
            if not dispatch.delivered:
                return json.dumps({
                    "success": False,
                    "requiresConfirmation": False,
                    "confirmationChannel": dispatch.channel_name,
                    "error": dispatch.refusal_reason,
                    "message": (
                        "The renewal was REFUSED, not queued for approval — no human can be asked for "
                        "a code on this server. Retrying will not help until the operator changes the "
                        "configuration."
                    ),
                    "amount": amount,
                }), policy
            return json.dumps({
                "success": False,
                "requiresConfirmation": True,
                "confirmationChannel": dispatch.channel_name,
                "error": "Subscription renewal requires human confirmation",
                "message": (
                    f"This renewal may cost up to ${approval.amount_usd:.2f} ({max_sats:,} sats), above "
                    f"the auto-approve threshold. A confirmation code was {dispatch.operator_hint} — "
                    "visible to the human operator, NOT to you. Ask the human to read that code and "
                    "give it to you."
                ),
                "howToConfirm": (
                    "Ask the human operator for the confirmation code, then call "
                    f'{TOOL_NAME}(confirmation_nonce="<code-from-human>") with the same max_sats.'
                ),
                "expiresInSeconds": dispatch.expires_in_seconds,
                "amount": amount,
            }), policy

    if approval.level == ApprovalLevel.LOG_AND_APPROVE:
        logger.info(
            "Auto-approved subscription renewal: $%.2f (%s sats)", float(approval.amount_usd), max_sats
        )

    return None, policy


async def renew_lightning_enable_subscription(
    max_sats: int = 50000,
    confirmation_nonce: str | None = None,
    l402_client: "L402Client | None" = None,
    budget_service: "BudgetService | None" = None,
    api_client: "LightningEnableApiClient | None" = None,
) -> str:
    """
    Pay the L402 invoice that buys or extends one period of Lightning Enable Agentic
    Commerce for the account behind the configured API key.

    Args:
        max_sats: Maximum satoshis this call may pay (the server sets the price).
        confirmation_nonce: The code the human read off the approval channel (for an
            above-threshold renewal). Omit on the first call to request one.
        l402_client: L402 client instance (wallet-backed) — pays the renewal invoice.
        budget_service: BudgetService for multi-tier approval + out-of-band confirmation.
        api_client: LightningEnableApiClient — supplies the merchant API key.

    Returns:
        JSON string with the new period end and payment details, or an error.
    """
    # Captured once, before anything can fail, so every path below can redact it.
    # Never logged, never serialized.
    api_key = getattr(api_client, "api_key", None) if api_client is not None else None
    result = await _renew(max_sats, confirmation_nonce, l402_client, budget_service, api_client, api_key)
    # Belt and braces: whatever path produced the result, the key does not leave.
    return _redact_key(result, api_key)


async def _renew(
    max_sats: int,
    confirmation_nonce: str | None,
    l402_client: "L402Client | None",
    budget_service: "BudgetService | None",
    api_client: "LightningEnableApiClient | None",
    api_key: str | None,
) -> str:
    # Declared outside the try so the except handler can report whether a payment was
    # observed when the client raises AFTER the renewal settled.
    receipt_scope: PaymentReceiptScope | None = None
    try:
        # The client would happily send a blank X-Api-Key, so the gate lives here,
        # before any invoice is minted.
        if api_client is None or not api_client.is_configured or not (api_key or "").strip():
            return json.dumps({"success": False, "error": PRODUCER_API_KEY_REQUIRED})

        if l402_client is None:
            return json.dumps({
                "success": False,
                "error": "No wallet configured. Renewing pays a Lightning invoice, so a "
                         "preimage-capable wallet (LND, NWC, or Strike) is required. Set "
                         "LND_REST_HOST+LND_MACAROON_HEX, NWC_CONNECTION_STRING, or STRIKE_API_KEY.",
            })

        renew_url = _renew_url()

        # Budget gating BEFORE payment, on max_sats (the ceiling): the exact price isn't
        # known until the 402 is minted inside fetch.
        payment_policy = "auto (no budget check)"
        if budget_service is not None:
            blocked, payment_policy = await _budget_gate(
                budget_service, max_sats, confirmation_nonce, renew_url
            )
            if blocked is not None:
                return blocked

        # Ambient payment intent: the durable receipt is written at the wallet seam
        # (ReceiptRecordingWallet) when the renewal invoice is paid inside fetch.
        receipt_scope = PaymentReceiptScope(
            "l402", context="l402_subscription_renewal", policy=payment_policy
        )
        with receipt_scope:
            # POST {} -> 402 -> pay -> retry POST with the credential; the client merges
            # these headers (X-Api-Key included) into the paid retry.
            response_text, amount_paid, _payment_receipt = await l402_client.fetch(
                url=renew_url,
                method="POST",
                headers={"Content-Type": "application/json", "X-Api-Key": api_key},
                body="{}",
                max_sats=max_sats,
                # First-party write to the fixed, operator-configured origin — never a
                # model-chosen URL — so it uses the internal keyword-only allowance.
                first_party_write=True,
            )

        # PASSIVE: the client already recorded spend + history + cooldown exactly once
        # inside the payment leg. Recording again here would double-count.
        paid_sats = amount_paid if amount_paid is not None and amount_paid > 0 else 0
        paid = paid_sats > 0
        receipt_written = (receipt_scope.receipt_written or False) if paid else None

        try:
            renewal = json.loads(response_text)
        except Exception:
            return json.dumps({
                "success": False,
                "error": "Subscription renewal paid but the server response was not valid JSON.",
                "receipt_written": receipt_written,
                "paymentHash": None,
                "activation": {"paid": paid, "amountSats": paid_sats},
                "warning": _paid_but_failed_warning(paid_sats, None) if paid else None,
            })

        if not isinstance(renewal, dict):
            renewal = {}

        payment_hash = _str_or_none(renewal.get("paymentHash"))
        current_period_end = _str_or_none(renewal.get("currentPeriodEnd"))
        if not current_period_end:
            return json.dumps({
                "success": False,
                "error": "Subscription renewal completed but the server returned no currentPeriodEnd.",
                "receipt_written": receipt_written,
                "paymentHash": payment_hash,
                "activation": {"paid": paid, "amountSats": paid_sats},
                "warning": _paid_but_failed_warning(paid_sats, payment_hash) if paid else None,
            })

        already_credited = renewal.get("alreadyCredited") is True
        server_paid_sats = _int_or_none(renewal.get("paidSats"))
        return json.dumps({
            "success": True,
            "receipt_written": receipt_written,
            "currentPeriodEnd": current_period_end,
            "paidSats": server_paid_sats if server_paid_sats is not None else paid_sats,
            "paymentHash": payment_hash,
            "alreadyCredited": already_credited,
            "planTier": _str_or_none(renewal.get("planTier")),
            "activation": {"paid": paid, "amountSats": paid_sats},
            "message": (
                f"This payment was already credited; the Lightning Enable subscription is paid through {current_period_end}."
                if already_credited
                else f"Lightning Enable subscription renewed. It is now paid through {current_period_end}."
            ),
        }, indent=2)

    except Exception as e:  # noqa: BLE001 — every failure becomes a structured result
        raw = str(e)
        safe_error = _safe(raw, api_key)
        # Log the redacted text only, without a traceback: the raw message can carry a
        # server body that echoes the API key.
        logger.warning("Subscription renewal failed (%s): %s", type(e).__name__, safe_error)

        error_code = _extract_error_code(_redact_key(raw, api_key))
        status_code = _extract_status_code(raw)

        # PAID BUT RENEWAL FAILED — the client raises with `.amount_paid` when the invoice
        # settled (or is in flight) yet the authorized retry did not return a renewal. The
        # spend is ALREADY recorded inside the client; the job here is the agent-facing
        # signal, or the agent reads a retryable error and pays again.
        amount_paid = getattr(e, "amount_paid", None)
        if amount_paid:
            from ..l402_client import L402RedirectError

            payment_hash = _derive_payment_hash(getattr(e, "l402_token", None))
            # A paid redirect: the client's text says "call this tool again with that URL",
            # and its message carries no "HTTP nnn" — so it would otherwise read as an
            # in-flight payment. This tool never returns the token, so replace both.
            paid_redirect = isinstance(e, L402RedirectError)
            if paid_redirect:
                status_code = e.status_code
            return json.dumps({
                "success": False,
                "error": (
                    "The subscription renewal was paid, but the server answered with a redirect instead "
                    "of confirming it. Do NOT pay again; contact support@lightningenable.com with the "
                    "payment hash."
                    if paid_redirect
                    else safe_error
                ),
                "errorCode": error_code,
                "statusCode": status_code,
                "receipt_written": (receipt_scope.receipt_written if receipt_scope else None) or False,
                "paymentHash": payment_hash,
                "activation": {"paid": True, "amountSats": amount_paid},
                "warning": _paid_but_failed_warning(amount_paid, payment_hash),
                "hint": (
                    "The renewal was paid but the server redirected instead of confirming it. Do not "
                    "re-pay; contact support with the payment hash."
                    if paid_redirect
                    # Do NOT say "check wallet balance" — the wallet paid; that contradicts
                    # the client's own "do not retry it" guidance.
                    else "The renewal invoice was paid but the server did not accept it yet — the payment "
                    "may still be in flight, or the wallet settled it without returning a preimage "
                    "(L402 needs one to prove payment). Check the payment status with your wallet "
                    "provider; do not re-pay."
                    if status_code in (None, 402)
                    else f"The server answered HTTP {status_code} after the payment (see errorCode and "
                         "error). Retrying would pay a new invoice; it would not resubmit this one."
                ),
            }, indent=2)

        # The wallet seam observed a payment under this call but the exception carries no
        # amount: still flag it, so the agent does not read a bare error as retryable.
        receipt_written = receipt_scope.receipt_written if receipt_scope else None
        if receipt_written is not None:
            return json.dumps({
                "success": False,
                "receipt_written": receipt_written,
                "error": f"Error renewing Lightning Enable subscription: {safe_error}",
                "activation": {"paid": True},
                "warning": (
                    "A payment may have been taken from your wallet before this error. Do NOT re-run "
                    "this tool or you may pay again — contact support@lightningenable.com with the "
                    "time of this call."
                ),
            }, indent=2)

        from ..l402_client import L402Error

        if isinstance(e, L402Error):
            # Genuinely unpaid — the server refused before any challenge was paid (Stripe
            # subscriber, Business plan, prepaid cap, disabled account, bad key) or the
            # client bailed before spending (ceiling too low, inconsistent challenge).
            if status_code == 401:
                hint = (
                    "The Lightning Enable API key was rejected and no payment was made. Check "
                    "LIGHTNING_ENABLE_API_KEY (or lightningEnableApiKey in ~/.lightning-enable/config.json)."
                )
            elif status_code in (None, 402):
                hint = (
                    "The renewal challenge could not be completed and no payment was made. Check wallet "
                    "balance and configuration, and that max_sats covers the quoted price."
                )
            else:
                hint = (
                    "The renewal endpoint refused the request before any payment was made (see errorCode "
                    "and error). Nothing was charged; retrying will get the same answer until that changes."
                )
            return json.dumps({
                "success": False,
                "error": safe_error,
                "errorCode": error_code,
                "statusCode": status_code,
                "activation": {"paid": False, "amountSats": 0},
                "hint": hint,
            })

        # null = nothing was paid.
        return json.dumps({
            "success": False,
            "receipt_written": None,
            "error": f"Error renewing Lightning Enable subscription: {safe_error}",
        })


# MCP tool schema (lives beside its handler; registered in tools/registry.py).
RENEW_LIGHTNING_ENABLE_SUBSCRIPTION_TOOL = Tool(
    name=TOOL_NAME,
    description=(
        "Pay the L402 invoice that buys or extends 30 days of Lightning Enable Agentic "
        "Commerce for the configured API key. Needs a preimage-capable wallet."
    ),
    inputSchema={
        "type": "object",
        "properties": {
            "max_sats": {
                "type": "integer",
                "description": "Max sats to pay",
                "default": 50000,
            },
            "confirmation_nonce": {
                "type": "string",
                "description": CONFIRMATION_NONCE_DESCRIPTION,
            },
        },
    },
)
