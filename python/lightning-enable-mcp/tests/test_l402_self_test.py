"""
Tests for the test_l402_payment self-test tool.

The tool delegates to the proven access_l402_resource path against a hardcoded
1-sat endpoint, then interprets the result into a plain pass/fail verdict.
"""

import json
import pytest
from unittest.mock import AsyncMock

# Alias the tool on import: pytest would otherwise try to collect a callable named
# `test_l402_payment` as a test case.
from lightning_enable_mcp.tools.test_l402_payment import (
    test_l402_payment as run_self_test,
    interpret,
    _diagnose,
    _resolve_test_endpoint,
    MAX_TEST_SATS,
)


class TestL402SelfTest:
    @pytest.mark.asyncio
    async def test_passes_hits_endpoint_and_forwards_hard_cap(self):
        client = AsyncMock()
        client.fetch = AsyncMock(return_value=("pong", 1))  # (response_text, amount_paid)

        result = await run_self_test(l402_client=client)
        data = json.loads(result)

        assert data["test"] == "passed"
        assert data["walletWorking"] is True
        assert data["amountSats"] == 1

        # Must target the hardcoded 1-sat test endpoint, never a caller URL.
        assert client.fetch.call_args.kwargs["url"].endswith("/l402/test/ping")
        # ...and forward the hard cap — the tool's core funds-safety guarantee.
        assert client.fetch.call_args.kwargs["max_sats"] == MAX_TEST_SATS
        assert MAX_TEST_SATS <= 10

    @pytest.mark.asyncio
    async def test_no_client_reports_no_wallet(self):
        # access_l402_resource returns a BARE non-JSON string when no client is wired;
        # interpret must still degrade to the structured no_wallet verdict.
        result = await run_self_test(l402_client=None)
        data = json.loads(result)
        assert data["success"] is False
        assert data["test"] == "failed"
        assert data["reason"] == "no_wallet"

    def test_interpret_paid_is_passed(self):
        raw = json.dumps({"success": True, "paid_sats": 1})
        data = json.loads(interpret(raw, "endpoint"))
        assert data["test"] == "passed"
        assert data["walletWorking"] is True

    def test_interpret_success_without_payment_is_inconclusive_not_success(self):
        raw = json.dumps({"success": True, "paid_sats": None})
        data = json.loads(interpret(raw, "endpoint"))
        assert data["test"] == "inconclusive"
        # An unproven wallet must NOT read as a passing self-test.
        assert data["success"] is False

    def test_interpret_requires_confirmation_is_needs_confirmation(self):
        raw = json.dumps({
            "success": False,
            "requiresConfirmation": True,
            "error": "L402 payment requires human confirmation",
            "howToConfirm": "Ask the human for the code shown in the server console.",
        })
        data = json.loads(interpret(raw, "endpoint"))
        assert data["test"] == "needs_confirmation"
        assert "reason" not in data  # a healthy wallet awaiting a code isn't a failure
        assert "confirmation_nonce" in data["message"]

    def test_interpret_budget_deny_surfaces_specific_reason(self):
        raw = json.dumps({
            "success": False,
            "error": "Payment denied by budget policy",
            "denialReason": "Payment of $0.05 would exceed session limit of $5.00",
        })
        data = json.loads(interpret(raw, "endpoint"))
        assert data["test"] == "failed"
        assert data["reason"] == "budget_block"
        # The specific limit must be surfaced, not the generic "denied by budget policy".
        assert "session limit of $5.00" in data["message"]

    def test_interpret_non_json_no_wallet(self):
        raw = "Error: L402 client not initialized. Check NWC connection."
        data = json.loads(interpret(raw, "endpoint"))
        assert data["test"] == "failed"
        assert data["reason"] == "no_wallet"

    def test_interpret_failure_diagnoses(self):
        raw = json.dumps({"success": False, "error": "NWC wallet not configured. Set NWC_CONNECTION_STRING"})
        data = json.loads(interpret(raw, "endpoint"))
        assert data["test"] == "failed"
        assert data["reason"] == "no_wallet"
        assert "NWC_CONNECTION_STRING" in data["howToFix"]

    @pytest.mark.parametrize("error,reason", [
        ("Wallet not configured. Set STRIKE_API_KEY", "no_wallet"),
        ("Rate limit exceeded", "rate_limited"),                        # must NOT be budget_block
        ("timed out waiting for preimage from relay", "network"),       # network before preimage
        ("Payment succeeded but no preimage was returned", "no_preimage"),
        ("OpenNode does not return preimages", "no_preimage"),
        ("Insufficient balance in wallet", "insufficient_funds"),
        ("Unable to retrieve balance", "unknown"),                      # balance-FETCH is not insufficient_funds
        ("Payment exceeds per-session budget of $20", "budget_block"),
        ("Connection timed out reaching relay", "network"),
        ("Something totally unexpected happened", "unknown"),
    ])
    def test_diagnose_maps_reason_codes(self, error, reason):
        r, fix = _diagnose(error)
        assert r == reason
        assert fix

    def test_endpoint_targets_test_ping(self):
        assert _resolve_test_endpoint().endswith("/l402/test/ping")

    def test_endpoint_whitespace_env_falls_back_to_prod(self, monkeypatch):
        # A blank/whitespace override must fall back to prod (parity with .NET),
        # not produce a malformed "   /l402/test/ping".
        monkeypatch.setenv("LIGHTNING_ENABLE_API_URL", "   ")
        assert _resolve_test_endpoint() == "https://api.lightningenable.com/l402/test/ping"
