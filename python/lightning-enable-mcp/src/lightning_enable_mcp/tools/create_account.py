"""
Create Lightning Enable Account Tool (self-bootstrapping signup)

Activates a Lightning Enable account via the L402 "Fast Lane" and returns the
merchant API key. This is an OUT-OF-THE-BOX tool: it requires NO Lightning Enable
API key (it CREATES one) — only a connected wallet that can pay a tiny activation
fee (~100 sats).

Flow (reuses the existing L402 machinery — no L402/NWC re-implementation):
  1. POST {email} to https://api.lightningenable.com/api/signup/l402 via L402Client.fetch
  2. The server replies 402 with an L402 challenge (macaroon + invoice)
  3. L402Client pays the invoice with the wallet and retries the SAME POST
     (body preserved) with `Authorization: L402 <macaroon>:<preimage>`
  4. The server returns the new merchant's { apiKey, merchantId, planTier, ... }

Funds-safety: the payment is gated through BudgetService exactly like
pay_l402_challenge / settle_agent_service — above the auto-approve threshold the
tool returns `requiresConfirmation` and prints an out-of-band code to the server
console (stderr) that the human (not the model) must relay back. The confirmation
is bound to the amount, this tool's name, and the signup URL.

On success the returned apiKey is ALSO merged into ~/.lightning-enable/config.json
(without clobbering other keys) so the API-key-gated producer/ASA tools unlock.
"""

import json
import logging
import os
import re
import sys
from pathlib import Path
from typing import TYPE_CHECKING, Optional

from . import sanitize_error

if TYPE_CHECKING:
    from ..budget_service import BudgetService
    from ..payment_history_service import PaymentHistoryService
    from ..l402_client import L402Client
    from ..receipt_service import ReceiptService

logger = logging.getLogger("lightning-enable-mcp.tools.create_account")

# Reasonable email shape check (not RFC-perfect, just enough to fail fast before
# minting a Lightning invoice for an obviously bad address).
_EMAIL_RE = re.compile(r"^[^@\s]+@[^@\s]+\.[^@\s]+$")

_DEFAULT_BASE_URL = "https://api.lightningenable.com"
_SIGNUP_PATH = "/api/signup/l402"

_CONFIG_KEY = "lightningEnableApiKey"


def _signup_url() -> str:
    """Signup endpoint URL. Base is overridable via LIGHTNING_ENABLE_API_URL
    (same env var the L402 producer API client honors) for dev/sandbox."""
    base = os.getenv("LIGHTNING_ENABLE_API_URL")
    if not base or base.startswith("${"):
        base = _DEFAULT_BASE_URL
    return f"{base.rstrip('/')}{_SIGNUP_PATH}"


def _merge_api_key_into_config(api_key: str, config_path: "Optional[str]" = None) -> tuple[bool, str, "Optional[str]"]:
    """Merge the API key into ~/.lightning-enable/config.json without clobbering
    other keys (wallets, tiers, limits, ...).

    Returns (success, path, error). Best-effort: a write failure must NOT fail the
    signup — the apiKey is still returned in the tool result for the caller to use.
    """
    path = Path(config_path) if config_path else Path.home() / ".lightning-enable" / "config.json"
    try:
        path.parent.mkdir(parents=True, exist_ok=True)

        data: dict = {}
        if path.exists():
            raw = path.read_text(encoding="utf-8")
            if raw.strip():
                # Non-empty existing file: only merge if it parses to a JSON object.
                # If it's malformed or not an object, DO NOT overwrite it — that would
                # destroy the user's other secrets (wallet creds, budget limits). Return
                # the key in the tool result instead so it can be saved by hand.
                try:
                    loaded = json.loads(raw)
                except Exception:
                    return False, str(path), (
                        "existing config is unparseable; not overwriting it — save the API key "
                        f"manually as '{_CONFIG_KEY}' in {path}."
                    )
                if not isinstance(loaded, dict):
                    return False, str(path), (
                        "existing config is not a JSON object; not overwriting it — save the API key "
                        f"manually as '{_CONFIG_KEY}' in {path}."
                    )
                data = loaded
            # else: genuinely empty/whitespace file → safe to write fresh (data stays {}).

        data[_CONFIG_KEY] = api_key

        with open(path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)

        # Best-effort permission tightening (config can hold wallet creds). Reuse
        # the shared helper; failures are non-fatal (it logs its own warning).
        try:
            from ..config import _restrict_file_permissions
            _restrict_file_permissions(path)
        except Exception:
            pass

        return True, str(path), None
    except Exception as e:  # noqa: BLE001 — best-effort, never fail signup on this
        logger.warning("Could not merge API key into config at %s: %s", path, e)
        return False, str(path), sanitize_error(str(e))


async def create_lightning_enable_account(
    email: str,
    max_sats: int = 1000,
    confirmation_nonce: "Optional[str]" = None,
    l402_client: "L402Client | None" = None,
    budget_service: "BudgetService | None" = None,
    payment_history_service: "PaymentHistoryService | None" = None,
    receipt_service: "ReceiptService | None" = None,
    config_path: "Optional[str]" = None,
) -> str:
    """
    Create (activate) a Lightning Enable account with a Lightning micropayment and
    return the merchant API key.

    Args:
        email: Email address to register the account under.
        max_sats: Maximum satoshis to pay for activation (fee is ~100 sats).
        confirmation_nonce: The code the human read from the server console (for an
            above-threshold activation fee). Omit on the first call to request one.
        l402_client: L402 client instance (wallet-backed) — pays the activation invoice.
        budget_service: BudgetService for multi-tier approval + out-of-band confirmation.
        payment_history_service: PaymentHistoryService for the session audit trail.
        receipt_service: ReceiptService for the durable, off-context-path spend receipt.
        config_path: Override for the config file path (testing). Defaults to
            ~/.lightning-enable/config.json.

    Returns:
        JSON string with the new account's apiKey + merchant details, or an error.
    """
    # Captured for the durable receipt; overwritten with the real approval tier
    # once the budget check runs (parity with access_l402_resource).
    payment_policy = "auto (no budget check)"
    try:
        # Validate email BEFORE minting any invoice.
        if not email or not email.strip():
            return json.dumps({
                "success": False,
                "error": "Email is required to create a Lightning Enable account.",
            })
        email = email.strip()
        if not _EMAIL_RE.match(email):
            return json.dumps({
                "success": False,
                "error": f"'{email}' is not a valid email address. Provide a real email — the account and API key are tied to it.",
            })

        if l402_client is None:
            return json.dumps({
                "success": False,
                "error": "No wallet configured. Account activation pays a tiny Lightning fee (~100 sats), "
                         "so a preimage-capable wallet (LND, NWC, or Strike) is required. Set "
                         "LND_REST_HOST+LND_MACAROON_HEX, NWC_CONNECTION_STRING, or STRIKE_API_KEY.",
            })

        signup_url = _signup_url()

        # Budget gating BEFORE payment (mirrors settle_agent_service / pay_l402_challenge).
        # We gate on max_sats (the ceiling) because the exact fee isn't known until the
        # 402 challenge is minted inside fetch — the confirmation is bound to max_sats,
        # this tool, and the signup URL, so a code can't be redirected or reused.
        # NOTE: the destination bound to the confirmation is the (constant) signup URL,
        # NOT the email — every activation POSTs to the same endpoint.
        if budget_service is not None:
            from ..config import ApprovalLevel
            approval = await budget_service.check_approval_level(max_sats)
            payment_policy = getattr(approval.level, "value", str(approval.level))

            if approval.level == ApprovalLevel.DENY:
                return json.dumps({
                    "success": False,
                    "error": f"Account activation denied by budget policy: {approval.denial_reason}",
                    "details": {
                        "requestedSats": max_sats,
                        "maxUsd": float(approval.amount_usd),
                        "remainingSessionUsd": float(approval.remaining_session_budget_usd),
                    },
                    "hint": "The activation fee ceiling exceeds your budget policy. Lower max_sats, wait out any "
                            "cooldown, or raise your limits in ~/.lightning-enable/config.json.",
                })

            if approval.requires_confirmation:
                if confirmation_nonce:
                    confirmation = budget_service.validate_and_consume_confirmation(
                        confirmation_nonce.strip().upper(), max_sats, "create_lightning_enable_account", signup_url
                    )
                    if confirmation is None:
                        return json.dumps({
                            "success": False,
                            "error": (
                                "Confirmation code is invalid, expired, already used, or does not match THIS "
                                "activation's amount, tool, and destination. Codes are bound to the exact amount, "
                                "tool, and destination approved — a code cannot be redirected."
                            ),
                            "message": (
                                "Ask the human operator for the code shown in the server console, then call "
                                "create_lightning_enable_account again with confirmation_nonce set to it."
                            ),
                        })
                    # Human-relayed code validated — fall through and activate.
                else:
                    pending = budget_service.create_pending_confirmation(
                        max_sats, approval.amount_usd, "create_lightning_enable_account", signup_url,
                        destination=signup_url,
                    )
                    print(
                        "[Lightning Enable] *** ACCOUNT ACTIVATION CONFIRMATION REQUIRED ***\n"
                        f"  create_lightning_enable_account — up to ${approval.amount_usd:.2f} ({max_sats:,} sats)\n"
                        f"  email {email}\n"
                        f"  Confirmation code: {pending.nonce}\n"
                        "  To approve, give this code to the agent. Expires in 120s.",
                        file=sys.stderr,
                        flush=True,
                    )
                    return json.dumps({
                        "success": False,
                        "requiresConfirmation": True,
                        "approvalLevel": approval.level.value,
                        "error": "Account activation requires human confirmation",
                        "message": (
                            f"Activating this account may cost up to ${approval.amount_usd:.2f} ({max_sats:,} sats), "
                            "above the auto-approve threshold. A confirmation code was printed to the server "
                            "console/logs — visible to the human operator, NOT to you. Ask the human to read that "
                            "code and give it to you."
                        ),
                        "howToConfirm": (
                            "Ask the human operator for the confirmation code shown in the server console, then call "
                            'create_lightning_enable_account(email="...", confirmation_nonce="<code-from-human>").'
                        ),
                        "amount": {"maxSats": max_sats, "maxUsd": float(approval.amount_usd)},
                        "expiresInSeconds": 120,
                    })

            if approval.level == ApprovalLevel.LOG_AND_APPROVE:
                logger.info(
                    "Log-and-approve account activation: up to %s sats ($%.2f) for %s",
                    max_sats, float(approval.amount_usd), email,
                )

        # Execute the L402 signup flow: POST {email} -> 402 -> pay -> retry POST.
        body = json.dumps({"email": email})
        response_text, amount_paid = await l402_client.fetch(
            url=signup_url,
            method="POST",
            headers={"Content-Type": "application/json"},
            body=body,
            max_sats=max_sats,
        )

        # Record spend ONLY after a payment actually settled.
        if amount_paid is not None and amount_paid > 0:
            if budget_service is not None:
                budget_service.record_spend(amount_paid)
                budget_service.record_payment_time()
            if payment_history_service is not None:
                payment_history_service.record_payment(
                    url="account_activation",
                    amount_sats=amount_paid,
                    status="success",
                )
            # Durable, off-context-path spend receipt (best-effort — a receipt failure
            # must NEVER turn a settled payment into an error the caller might retry).
            if receipt_service is not None:
                try:
                    spent = None
                    if budget_service is not None:
                        try:
                            spent = budget_service.get_status()["session"]["spentSats"]
                        except Exception:
                            spent = None
                    receipt_service.log_payment(
                        endpoint="account_activation",
                        amount_sats=amount_paid,
                        policy=payment_policy,
                        session_spent_sats=spent,
                    )
                except Exception:
                    logger.warning("Receipt logging failed (activation payment already settled)")

        # Parse the account payload the server returned after payment.
        try:
            account = json.loads(response_text)
        except Exception:
            return json.dumps({
                "success": False,
                "error": "Account activation paid but the server response was not valid JSON.",
                "amountSats": amount_paid,
            })

        api_key = account.get("apiKey")
        if not api_key:
            return json.dumps({
                "success": False,
                "error": "Account activation completed but no apiKey was returned by the server.",
                "server": account,
                "amountSats": amount_paid,
            })

        # Self-bootstrapping payoff: persist the key so the API-key-gated
        # producer/ASA tools pick it up (merge, don't clobber).
        config_ok, config_file, config_err = _merge_api_key_into_config(api_key, config_path)

        result = {
            "success": True,
            "apiKey": api_key,
            "merchantId": account.get("merchantId"),
            "email": account.get("email", email),
            "planTier": account.get("planTier"),
            "subscriptionStatus": account.get("subscriptionStatus"),
            "trialEndsAt": account.get("trialEndsAt"),
            "dashboardUrl": account.get("dashboardUrl"),
            "activation": {
                "paid": amount_paid is not None and amount_paid > 0,
                "amountSats": amount_paid,
            },
            "config": {
                "written": config_ok,
                "path": config_file,
                "key": _CONFIG_KEY,
            },
            "message": (
                "Lightning Enable account activated. Your API key has been "
                + ("saved to " + config_file + " — " if config_ok else "returned above (save it: config write failed — ")
                + "restart the MCP server to unlock the producer/ASA tools (create_l402_challenge, "
                + "verify_l402_payment, and the agent-to-agent commerce tools)."
            ),
        }
        if not config_ok and config_err:
            result["config"]["error"] = config_err

        return json.dumps(result, indent=2)

    except Exception as e:
        logger.exception("Error creating Lightning Enable account")

        # Paid-but-retry-failed: L402Client.fetch raises an L402Error carrying
        # `.amount_paid` when the activation invoice SETTLED (money left the wallet)
        # but the authorized retry returned >=400. Record that real spend so the
        # budget/history/receipt don't silently omit it, and surface the settled sats
        # so the human knows a payment went through (and won't re-run and double-pay).
        # Best-effort; never mask the original error. Mirrors access_l402_resource.
        amount_paid = getattr(e, "amount_paid", None)
        if amount_paid:
            try:
                if budget_service is not None:
                    budget_service.record_spend(amount_paid)
                    budget_service.record_payment_time()
                if payment_history_service is not None:
                    payment_history_service.record_payment(
                        url="account_activation",
                        amount_sats=amount_paid,
                        status="paid_signup_retry_failed",
                    )
                if receipt_service is not None:
                    spent = None
                    if budget_service is not None:
                        try:
                            spent = budget_service.get_status()["session"]["spentSats"]
                        except Exception:
                            spent = None
                    receipt_service.log_payment(
                        endpoint="account_activation",
                        amount_sats=amount_paid,
                        policy=payment_policy,
                        session_spent_sats=spent,
                    )
            except Exception:
                logger.warning("Failed to record paid-but-retry-failed activation spend")

            return json.dumps({
                "success": False,
                "error": sanitize_error(str(e)),
                "activation": {"paid": True, "amountSats": amount_paid},
                "warning": (
                    f"A payment of {amount_paid} sats SETTLED but account activation did not complete. "
                    "Do NOT re-run this tool or you may pay again — contact support@lightningenable.com "
                    "with this email to recover the account."
                ),
            }, indent=2)

        return json.dumps({"success": False, "error": sanitize_error(str(e))})
