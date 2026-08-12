"""
Test L402 Payment Tool

A one-command self-test for the Lightning wallet: pays the public 1-sat L402 test
endpoint end to end, proving the wallet is connected, returns a preimage, and can
complete an L402 payment.

It adds NO new payment logic — it delegates to the same proven
``access_l402_resource`` path against a HARDCODED endpoint (no user-supplied URL,
so no SSRF surface), then translates the raw result into a plain pass/fail verdict
with a fix hint.

Interpretation checks the STRUCTURED signals the delegated path emits (a completed
payment, a confirmation requirement, a budget denial, a non-JSON error) before
falling back to matching the error string, so a rate-limit is never mistaken for a
budget block and the no-wallet case still returns the structured verdict.
"""

import json
import os
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..budget_service import BudgetService
    from ..payment_history_service import PaymentHistoryService
    from ..l402_client import L402Client

from .access_resource import access_l402_resource

# The public 1-sat L402 test resource. Hardcoded on purpose so this tool can never
# be repurposed into an arbitrary-URL payer. Only the base host is overridable, via
# the same env var the rest of the MCP uses to point at a non-prod instance.
TEST_PATH = "/l402/test/ping"

# The endpoint charges 1 sat; small headroom, hard-capped so the self-test can never
# spend more than a rounding error regardless of what the endpoint returns.
MAX_TEST_SATS = 10


def _resolve_test_endpoint() -> str:
    """Resolve the hardcoded test path against the configured LE API base."""
    base = (os.environ.get("LIGHTNING_ENABLE_API_URL") or "").strip()
    if not base or base.startswith("${"):
        base = "https://api.lightningenable.com"
    return base.rstrip("/") + TEST_PATH


def _diagnose(error: str) -> tuple[str, str]:
    """Map a raw error string to a stable reason code + a plain-language fix.

    Order matters: the most specific / most-often-confused buckets are checked first
    (rate-limit before budget; network before preimage). Budget denials and
    confirmation requirements are handled structurally in ``interpret``, not here.
    """
    e = (error or "").lower()

    # A blank error usually means a client-side timeout / dropped connection (e.g. an
    # httpx.ReadTimeout stringifies to empty) — surface that, not "unknown".
    if not e.strip():
        return (
            "network",
            "The request failed without a specific error — usually a timeout or a dropped connection. "
            "Check your network / the NWC relay is reachable, then retry.",
        )

    if "not configured" in e or "no wallet" in e or "not initialized" in e:
        return (
            "no_wallet",
            "No Lightning wallet is configured. Set NWC_CONNECTION_STRING (CoinOS/Alby Hub/CLINK), "
            "STRIKE_API_KEY, or LND credentials — or add a wallet to ~/.lightning-enable/config.json. "
            "NWC and Strike both work for L402.",
        )
    # Before budget: "Rate limit exceeded" contains "limit"/"exceed" but is NOT a budget issue.
    if "rate limit" in e:
        return (
            "rate_limited",
            "You've hit the request rate limit (shared with access_l402_resource). Wait for the window "
            "to reset (about a minute) and retry — this is not a budget or wallet problem.",
        )
    # Before preimage: a network blip whose text mentions "preimage" is still a network issue.
    if (
        "timeout" in e or "timed out" in e or "unreachable" in e
        or "network" in e or "resolve" in e or "connect" in e
    ):
        return (
            "network",
            "Could not reach the wallet or the test endpoint. Check the wallet connection (is the NWC "
            "relay reachable?) and your network, then retry.",
        )
    if "opennode" in e or "preimage" in e:
        return (
            "no_preimage",
            "The wallet paid but did not return a preimage, which L402 requires. OpenNode cannot do "
            "L402 — switch to NWC (CoinOS, Alby Hub, CLINK), LND, or Strike.",
        )
    # Only "insufficient" — not bare "balance"/"funds", which also appear in balance-*fetch* failures.
    if "insufficient" in e:
        return (
            "insufficient_funds",
            "The wallet has too little balance to pay ~1 sat. Fund it with a small amount and retry.",
        )
    # Fallback for budget wording that reaches here (the structured path handles the normal shape).
    if "budget" in e:
        return (
            "budget_block",
            "The MCP budget blocked even this 1-sat payment. Loosen the auto-approve tier / per-payment "
            "limit in ~/.lightning-enable/config.json.",
        )
    return (
        "unknown",
        "Check the error message above. Confirm a preimage-returning wallet (NWC, LND, or Strike) is "
        "connected and funded, then retry.",
    )


def interpret(raw: str, endpoint: str) -> str:
    """Turn the raw access_l402_resource result into a plain self-test verdict.

    Kept pure so the diagnostics are unit-testable without a wallet.
    """
    try:
        data = json.loads(raw)
    except Exception:
        # access_l402_resource returns a BARE non-JSON string on some early errors
        # (e.g. "Error: L402 client not initialized. Check NWC connection."). Map the
        # known ones to a structured verdict instead of "could not be interpreted".
        low = (raw or "").lower()
        if "not initialized" in low or "not configured" in low or "no wallet" in low:
            reason, fix = _diagnose(raw)
            return json.dumps({
                "success": False,
                "test": "failed",
                "reason": reason,
                "message": f"❌ L402 self-test failed: {raw.strip()}",
                "howToFix": fix,
                "endpoint": endpoint,
                "walletWorking": False,
            }, indent=2)
        return json.dumps({
            "success": False,
            "test": "failed",
            "reason": "unexpected",
            "message": f"❌ L402 self-test result could not be interpreted: {(raw or '')[:200]}",
            "endpoint": endpoint,
        }, indent=2)

    if data.get("success"):
        paid = data.get("paid_sats")
        if paid:
            return json.dumps({
                "success": True,
                "test": "passed",
                "message": (
                    f"✅ L402 works end to end. Paid {paid} sat(s), preimage verified. "
                    "Your wallet is ready to pay L402 resources anywhere."
                ),
                "endpoint": endpoint,
                "amountSats": paid,
                # A self-test moves real sats — pass through the underlying
                # access_l402_resource's durable-receipt signal.
                "receipt_written": data.get("receipt_written"),
                "walletWorking": True,
            }, indent=2)
        # success without a payment: the test endpoint should always issue a 402, so
        # the L402 flow was not exercised — the wallet is unproven, NOT a pass.
        return json.dumps({
            "success": False,
            "test": "inconclusive",
            "message": (
                "Endpoint returned success without requiring payment, so the L402 flow was not "
                "exercised — the wallet is unproven. Retry, or verify the test endpoint."
            ),
            "endpoint": endpoint,
            "walletWorking": None,
        }, indent=2)

    # ---- Failure branch: read STRUCTURED signals before the error string. ----

    # (1) Needs human confirmation — a healthy wallet, not a failure.
    if data.get("requiresConfirmation"):
        return json.dumps({
            "success": False,
            "test": "needs_confirmation",
            "message": (
                "Your budget config requires human confirmation for this payment. A confirmation code "
                "was printed to the server console/logs (visible to the operator, not to the agent). "
                "Re-run test_l402_payment with confirmation_nonce set to that code to finish the test."
            ),
            "howToConfirm": data.get("howToConfirm"),
            "endpoint": endpoint,
            "walletWorking": None,
        }, indent=2)

    # (2) Budget denial has a dedicated shape (a specific denialReason). Detect it
    # structurally so the specific reason is surfaced verbatim, not the generic error.
    deny_reason = data.get("denialReason")
    if deny_reason or data.get("error") == "Payment denied by budget policy":
        detail = deny_reason or "budget policy"
        return json.dumps({
            "success": False,
            "test": "failed",
            "reason": "budget_block",
            "message": f"❌ L402 self-test blocked by budget: {detail}",
            "howToFix": (
                "The MCP budget denied even this ~1-sat payment. Raise the relevant cap in "
                "~/.lightning-enable/config.json (the message above names which limit was hit)."
            ),
            "endpoint": endpoint,
            "walletWorking": False,
        }, indent=2)

    # (2.5) Idempotency: the same 1-sat invoice was already paid moments ago (L402
    # reuses it for ~60s to prevent double-charges). NOT a wallet failure — it's
    # evidence a prior payment succeeded; the wallet is fine.
    err_peek = (data.get("error") or "").lower()
    if "already" in err_peek and "paid" in err_peek:
        return json.dumps({
            "success": False,
            "test": "inconclusive",
            "message": (
                "The 1-sat test invoice was already paid moments ago — L402 reuses the same invoice "
                "for ~60 seconds to prevent double-charges. Your wallet is fine; wait a minute and run "
                "the test again for a fresh payment."
            ),
            "endpoint": endpoint,
            "walletWorking": None,
        }, indent=2)

    # (3) Otherwise diagnose from the error string.
    error = data.get("error", "")
    reason, fix = _diagnose(error)
    return json.dumps({
        "success": False,
        "test": "failed",
        "reason": reason,
        "message": f"❌ L402 self-test failed: {error}",
        "howToFix": fix,
        "endpoint": endpoint,
        "walletWorking": False,
    }, indent=2)


async def test_l402_payment(
    confirmation_nonce: "str | None" = None,
    l402_client: "L402Client | None" = None,
    budget_service: "BudgetService | None" = None,
    payment_history_service: "PaymentHistoryService | None" = None,
) -> str:
    """Self-test the wallet by paying the public 1-sat L402 endpoint end to end.

    If the budget config requires confirmation for this amount, the first call
    returns test="needs_confirmation" and the server prints a code to its console;
    re-call with confirmation_nonce set to that code to finish.
    """
    endpoint = _resolve_test_endpoint()

    # Delegate to the existing, proven L402 payment path. No new payment code,
    # no bypass of budget/confirmation. max_sats hard-caps the spend.
    raw = await access_l402_resource(
        url=endpoint,
        method="GET",
        max_sats=MAX_TEST_SATS,
        confirmation_nonce=confirmation_nonce,
        l402_client=l402_client,
        budget_service=budget_service,
        payment_history_service=payment_history_service,
    )

    return interpret(raw, endpoint)
