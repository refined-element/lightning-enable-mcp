"""
Tests for settle_agent_service tool (MONEY PATH)

Covers URL/method validation, budget-deny, confirmation-required, and the
spend-recording path (record_spend only AFTER the L402 fetch returns a paid
amount).
"""

import json
from decimal import Decimal
import pytest
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.config import ApprovalLevel, ApprovalCheckResult
from lightning_enable_mcp.tools.settle_agent_service import settle_agent_service


def _approval(level, **kwargs):
    return ApprovalCheckResult(
        level=level,
        amount_sats=kwargs.get("amount_sats", 500),
        amount_usd=kwargs.get("amount_usd", Decimal("0.50")),
        denial_reason=kwargs.get("denial_reason"),
        remaining_session_budget_usd=kwargs.get(
            "remaining_session_budget_usd", Decimal("5.00")
        ),
    )


def _budget_with(level, **kwargs):
    budget = MagicMock()
    budget.check_approval_level = AsyncMock(return_value=_approval(level, **kwargs))
    budget.get_status.return_value = {
        "session": {
            "spentSats": 500,
            "spentUsd": 0.5,
            "remainingUsd": 4.5,
            "requestCount": 1,
        }
    }
    # Out-of-band confirmation machinery (settle uses it like access_l402_resource).
    pending = MagicMock()
    pending.nonce = "ABC123"
    budget.create_pending_confirmation = MagicMock(return_value=pending)
    budget.validate_and_consume_confirmation = MagicMock(return_value=pending)
    return budget


class TestSettleAgentServiceValidation:
    """Input validation and security gating."""

    @pytest.mark.asyncio
    async def test_missing_endpoint_returns_error(self):
        result = await settle_agent_service(l402_endpoint="", l402_client=MagicMock())
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "endpoint URL is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_invalid_url_returns_error(self):
        result = await settle_agent_service(
            l402_endpoint="not-a-url", l402_client=MagicMock()
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Invalid L402 endpoint URL" in parsed["error"]

    @pytest.mark.asyncio
    async def test_plain_http_non_localhost_rejected(self):
        result = await settle_agent_service(
            l402_endpoint="http://example.com/l402", l402_client=MagicMock()
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "requires HTTPS" in parsed["error"]

    @pytest.mark.asyncio
    async def test_plain_http_localhost_allowed_through_validation(self):
        """localhost http passes URL validation (reaches the client check)."""
        result = await settle_agent_service(
            l402_endpoint="http://localhost:5096/l402", l402_client=None
        )
        parsed = json.loads(result)
        # Not rejected for HTTPS; rejected later for missing client.
        assert parsed["success"] is False
        assert "client not available" in parsed["error"]

    @pytest.mark.asyncio
    async def test_invalid_method_rejected(self):
        result = await settle_agent_service(
            l402_endpoint="https://example.com/l402", method="TRACE",
            l402_client=MagicMock(),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Invalid HTTP method" in parsed["error"]

    @pytest.mark.asyncio
    async def test_unsupported_method_rejected(self):
        """HEAD (and OPTIONS/PATCH) are not in the whitelist — the L402 client can't settle them."""
        result = await settle_agent_service(
            l402_endpoint="https://example.com/l402", method="HEAD",
            l402_client=MagicMock(),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Invalid HTTP method" in parsed["error"]
        assert "HEAD" in parsed["error"]

    @pytest.mark.asyncio
    async def test_no_client_returns_error(self):
        result = await settle_agent_service(
            l402_endpoint="https://example.com/l402", l402_client=None
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "client not available" in parsed["error"]


class TestSettleAgentServiceBudget:
    """Budget gating — the money-path guard."""

    @pytest.mark.asyncio
    async def test_budget_deny_blocks_and_does_not_fetch(self):
        client = AsyncMock()
        budget = _budget_with(ApprovalLevel.DENY, denial_reason="over session limit")

        result = await settle_agent_service(
            l402_endpoint="https://example.com/l402", max_sats=999999,
            l402_client=client, budget_service=budget,
        )
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert parsed["error"] == "Budget limit exceeded"
        assert parsed["denialReason"] == "over session limit"
        # MUST NOT pay when budget denies.
        client.fetch.assert_not_called()
        budget.record_spend.assert_not_called()

    @pytest.mark.asyncio
    async def test_confirmation_required_blocks_and_does_not_leak_code(self):
        """No nonce on the first call -> requiresConfirmation, no fetch, and the code
        (printed to stderr) must NOT appear in the model-visible result."""
        client = AsyncMock()
        budget = _budget_with(ApprovalLevel.FORM_CONFIRM)

        result = await settle_agent_service(
            l402_endpoint="https://example.com/l402", max_sats=500,
            l402_client=client, budget_service=budget,
        )
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert parsed["requiresConfirmation"] is True
        assert parsed["approvalLevel"] == "form_confirm"
        assert "ABC123" not in result  # the confirmation code is never returned to the model
        assert "nonce" not in parsed
        # MUST NOT pay until the human-relayed code is supplied.
        client.fetch.assert_not_called()
        budget.record_spend.assert_not_called()

    @pytest.mark.asyncio
    async def test_human_relayed_nonce_proceeds_and_records_spend(self):
        client = AsyncMock()
        client.fetch.return_value = ("service result body", 500)
        budget = _budget_with(ApprovalLevel.FORM_CONFIRM)

        result = await settle_agent_service(
            l402_endpoint="https://example.com/l402", max_sats=500,
            confirmation_nonce="abc123", l402_client=client, budget_service=budget,
        )
        parsed = json.loads(result)

        budget.validate_and_consume_confirmation.assert_called_once_with("ABC123", 500, "settle_agent_service", "https://example.com/l402")
        assert parsed["success"] is True
        assert parsed["settlement"]["paid"] is True
        assert parsed["settlement"]["amountSats"] == 500
        client.fetch.assert_called_once()
        # Spend recorded AFTER a successful paid fetch.
        budget.record_spend.assert_called_once_with(500)
        budget.record_payment_time.assert_called_once()


class TestSettleAgentServicePayment:
    """The actual settlement / spend-recording behavior."""

    @pytest.mark.asyncio
    async def test_auto_approve_paid_records_spend(self):
        client = AsyncMock()
        client.fetch.return_value = ("paid body", 200)
        budget = _budget_with(ApprovalLevel.AUTO_APPROVE)

        result = await settle_agent_service(
            l402_endpoint="https://example.com/l402", max_sats=300,
            agreement_id="agr-1", l402_client=client, budget_service=budget,
        )
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["settlement"]["paid"] is True
        assert parsed["settlement"]["amountSats"] == 200
        assert parsed["settlement"]["agreementId"] == "agr-1"
        budget.record_spend.assert_called_once_with(200)
        budget.record_payment_time.assert_called_once()

    @pytest.mark.asyncio
    async def test_no_payment_required_does_not_record_spend(self):
        client = AsyncMock()
        client.fetch.return_value = ("free body", None)
        budget = _budget_with(ApprovalLevel.AUTO_APPROVE)

        result = await settle_agent_service(
            l402_endpoint="https://example.com/l402", l402_client=client,
            budget_service=budget,
        )
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["settlement"]["paid"] is False
        assert "No payment was required" in parsed["message"]
        budget.record_spend.assert_not_called()

    @pytest.mark.asyncio
    async def test_fetch_exception_is_handled(self):
        client = AsyncMock()
        client.fetch.side_effect = Exception("wallet down")
        budget = _budget_with(ApprovalLevel.AUTO_APPROVE)

        result = await settle_agent_service(
            l402_endpoint="https://example.com/l402", l402_client=client,
            budget_service=budget,
        )
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "wallet down" in parsed["error"]
        budget.record_spend.assert_not_called()
