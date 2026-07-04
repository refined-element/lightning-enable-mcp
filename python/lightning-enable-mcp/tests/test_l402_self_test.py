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
)


class TestL402SelfTest:
    @pytest.mark.asyncio
    async def test_passes_and_hits_hardcoded_endpoint(self):
        client = AsyncMock()
        client.fetch = AsyncMock(return_value=("pong", 1))  # (response_text, amount_paid)

        result = await run_self_test(l402_client=client)
        data = json.loads(result)

        assert data["test"] == "passed"
        assert data["walletWorking"] is True
        assert data["amountSats"] == 1

        # Must target the hardcoded 1-sat test endpoint, never a caller URL.
        called_url = client.fetch.call_args.kwargs["url"]
        assert called_url.endswith("/l402/test/ping")

    @pytest.mark.asyncio
    async def test_no_client_reports_failure(self):
        # access_l402_resource returns an error when no client is wired.
        result = await run_self_test(l402_client=None)
        data = json.loads(result)
        assert data["success"] is False
        assert data["test"] == "failed"

    def test_interpret_paid_is_passed(self):
        raw = json.dumps({"success": True, "paid_sats": 1})
        data = json.loads(interpret(raw, "endpoint"))
        assert data["test"] == "passed"
        assert data["walletWorking"] is True

    def test_interpret_success_without_payment_is_inconclusive(self):
        raw = json.dumps({"success": True, "paid_sats": None})
        data = json.loads(interpret(raw, "endpoint"))
        assert data["test"] == "inconclusive"

    def test_interpret_failure_diagnoses(self):
        raw = json.dumps({"success": False, "error": "NWC wallet not configured. Set NWC_CONNECTION_STRING"})
        data = json.loads(interpret(raw, "endpoint"))
        assert data["test"] == "failed"
        assert data["reason"] == "no_wallet"
        assert "NWC_CONNECTION_STRING" in data["howToFix"]

    @pytest.mark.parametrize("error,reason", [
        ("Wallet not configured. Set STRIKE_API_KEY", "no_wallet"),
        ("Payment succeeded but no preimage was returned", "no_preimage"),
        ("OpenNode does not return preimages", "no_preimage"),
        ("Insufficient balance in wallet", "insufficient_funds"),
        ("Payment exceeds per-session budget limit", "budget_block"),
        ("Connection timed out reaching relay", "network"),
        ("Something totally unexpected happened", "unknown"),
    ])
    def test_diagnose_maps_reason_codes(self, error, reason):
        r, fix = _diagnose(error)
        assert r == reason
        assert fix

    def test_endpoint_targets_test_ping(self):
        assert _resolve_test_endpoint().endswith("/l402/test/ping")
