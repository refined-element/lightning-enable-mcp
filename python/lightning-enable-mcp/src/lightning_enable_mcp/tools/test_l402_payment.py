"""
Test L402 Payment Tool

A one-command self-test for the Lightning wallet: pays the public 1-sat L402 test
endpoint end to end, proving the wallet is connected, returns a preimage, and can
complete an L402 payment.

It adds NO new payment logic — it delegates to the same proven
``access_l402_resource`` path against a HARDCODED endpoint (no user-supplied URL,
so no SSRF surface), then translates the raw result into a plain pass/fail verdict
with a fix hint. That makes it a better onboarding primitive than telling a
beginner to curl a magic URL.
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
    base = os.environ.get("LIGHTNING_ENABLE_API_URL")
    if not base or base.startswith("${"):
        base = "https://api.lightningenable.com"
    return base.rstrip("/") + TEST_PATH


def _diagnose(error: str) -> tuple[str, str]:
    """Map a raw error string to a stable reason code + a plain-language fix."""
    e = (error or "").lower()
    if "not configured" in e or "no wallet" in e:
        return (
            "no_wallet",
            "No Lightning wallet is configured. Set NWC_CONNECTION_STRING (CoinOS/Alby Hub/CLINK), "
            "STRIKE_API_KEY, or LND credentials — or add a wallet to ~/.lightning-enable/config.json. "
            "NWC and Strike both work for L402.",
        )
    if "preimage" in e or "opennode" in e:
        return (
            "no_preimage",
            "The wallet paid but did not return a preimage, which L402 requires. OpenNode cannot do "
            "L402 — switch to NWC (CoinOS, Alby Hub, CLINK), LND, or Strike.",
        )
    if "insufficient" in e or "balance" in e or "funds" in e:
        return (
            "insufficient_funds",
            "The wallet has too little balance to pay ~1 sat. Fund it with a small amount and retry.",
        )
    if "budget" in e or "limit" in e or "exceed" in e:
        return (
            "budget_block",
            "The MCP budget blocked even this 1-sat payment. Loosen the auto-approve tier / per-payment "
            "limit in ~/.lightning-enable/config.json.",
        )
    if "timeout" in e or "timed out" in e or "connection" in e or "resolve" in e:
        return (
            "network",
            "Could not reach the wallet or the test endpoint. Check the wallet connection (is the NWC "
            "relay reachable?) and your network, then retry.",
        )
    return (
        "unknown",
        "Check the error message above. Confirm a preimage-returning wallet (NWC, LND, or Strike) is "
        "connected and funded, then retry.",
    )


def interpret(raw_json: str, endpoint: str) -> str:
    """Turn the raw access_l402_resource result into a plain self-test verdict.

    Kept pure so the diagnostics are unit-testable without a wallet.
    """
    try:
        data = json.loads(raw_json)
    except Exception as e:  # pragma: no cover - defensive
        return json.dumps({
            "success": False,
            "test": "failed",
            "reason": "unexpected",
            "message": f"❌ L402 self-test result could not be interpreted: {e}",
            "endpoint": endpoint,
        })

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
                "walletWorking": True,
            }, indent=2)
        # success without a payment: the test endpoint should always issue a 402, so
        # the L402 flow was not exercised. Inconclusive.
        return json.dumps({
            "success": True,
            "test": "inconclusive",
            "message": (
                "Endpoint returned success without requiring payment, so the L402 flow was not "
                "exercised. Retry, or verify the test endpoint."
            ),
            "endpoint": endpoint,
        }, indent=2)

    error = data.get("error", "") or data.get("denialReason", "")
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
    l402_client: "L402Client | None" = None,
    budget_service: "BudgetService | None" = None,
    payment_history_service: "PaymentHistoryService | None" = None,
) -> str:
    """Self-test the wallet by paying the public 1-sat L402 endpoint end to end."""
    endpoint = _resolve_test_endpoint()

    # Delegate to the existing, proven L402 payment path. No new payment code,
    # no bypass of budget/confirmation. max_sats hard-caps the spend.
    raw = await access_l402_resource(
        url=endpoint,
        method="GET",
        max_sats=MAX_TEST_SATS,
        l402_client=l402_client,
        budget_service=budget_service,
        payment_history_service=payment_history_service,
    )

    return interpret(raw, endpoint)
