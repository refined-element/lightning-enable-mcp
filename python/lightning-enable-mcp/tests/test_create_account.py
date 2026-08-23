"""
Tests for create_lightning_enable_account tool (self-bootstrapping signup / MONEY PATH).

Covers email validation, missing-wallet, budget-deny, out-of-band confirmation
(code never leaks), the paid signup path (POSTs the email, records spend, parses
the returned apiKey), and the config-merge that unlocks the API-key-gated tools
without clobbering existing keys.
"""

import json
from decimal import Decimal

import pytest
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.config import ApprovalLevel, ApprovalCheckResult
from lightning_enable_mcp.tools.create_account import (
    create_lightning_enable_account,
    _merge_api_key_into_config,
)

TEST_EMAIL = "agent@example.com"

# What the /api/signup/l402 endpoint returns after the L402 payment settles.
_ACCOUNT_PAYLOAD = json.dumps({
    "status": "active",
    "merchantId": "merch_123",
    "apiKey": "le_live_abc123",
    "email": TEST_EMAIL,
    "planTier": "individual",
    "subscriptionStatus": "trialing",
    "trialEndsAt": "2026-08-05T00:00:00Z",
    "dashboardUrl": "https://api.lightningenable.com/dashboard",
})


def _approval(level, **kwargs):
    return ApprovalCheckResult(
        level=level,
        amount_sats=kwargs.get("amount_sats", 1000),
        amount_usd=kwargs.get("amount_usd", Decimal("0.05")),
        denial_reason=kwargs.get("denial_reason"),
        remaining_session_budget_usd=kwargs.get("remaining_session_budget_usd", Decimal("5.00")),
    )


def _budget_with(level, **kwargs):
    budget = MagicMock()
    budget.check_approval_level = AsyncMock(return_value=_approval(level, **kwargs))
    budget.record_spend = MagicMock()
    budget.record_payment_time = MagicMock()
    pending = MagicMock()
    pending.nonce = "ABC123"
    budget.create_pending_confirmation = MagicMock(return_value=pending)
    budget.validate_and_consume_confirmation = MagicMock(return_value=pending)
    return budget


def _paid_client(paid_sats: int = 100, body: str = _ACCOUNT_PAYLOAD):
    client = AsyncMock()
    client.fetch = AsyncMock(return_value=(body, paid_sats, None))
    return client


class TestCreateAccountValidation:
    @pytest.mark.asyncio
    async def test_missing_email_returns_error_and_does_not_fetch(self):
        client = _paid_client()
        result = json.loads(await create_lightning_enable_account(email="", l402_client=client))
        assert result["success"] is False
        assert "Email is required" in result["error"]
        client.fetch.assert_not_called()

    @pytest.mark.asyncio
    async def test_invalid_email_returns_error_and_does_not_fetch(self):
        client = _paid_client()
        result = json.loads(await create_lightning_enable_account(email="not-an-email", l402_client=client))
        assert result["success"] is False
        assert "not a valid email" in result["error"]
        client.fetch.assert_not_called()

    @pytest.mark.asyncio
    async def test_missing_wallet_returns_error(self):
        result = json.loads(await create_lightning_enable_account(email=TEST_EMAIL, l402_client=None))
        assert result["success"] is False
        assert "No wallet configured" in result["error"]


class TestCreateAccountBudget:
    @pytest.mark.asyncio
    async def test_budget_deny_blocks_and_does_not_fetch(self, tmp_path):
        client = _paid_client()
        budget = _budget_with(ApprovalLevel.DENY, denial_reason="over session limit")
        result = json.loads(await create_lightning_enable_account(
            email=TEST_EMAIL, l402_client=client, budget_service=budget,
            config_path=str(tmp_path / "config.json"),
        ))
        assert result["success"] is False
        assert "denied by budget policy" in result["error"]
        client.fetch.assert_not_called()
        budget.record_spend.assert_not_called()

    @pytest.mark.asyncio
    async def test_confirmation_required_blocks_and_does_not_leak_code(self, tmp_path):
        client = _paid_client()
        budget = _budget_with(ApprovalLevel.FORM_CONFIRM)
        result_str = await create_lightning_enable_account(
            email=TEST_EMAIL, max_sats=500, l402_client=client, budget_service=budget,
            config_path=str(tmp_path / "config.json"),
        )
        result = json.loads(result_str)
        assert result["success"] is False
        assert result["requiresConfirmation"] is True
        assert "ABC123" not in result_str  # code never reaches the model
        assert "nonce" not in result
        client.fetch.assert_not_called()
        budget.record_spend.assert_not_called()

    @pytest.mark.asyncio
    async def test_human_relayed_nonce_proceeds_and_records_spend(self, tmp_path):
        client = _paid_client(paid_sats=100)
        budget = _budget_with(ApprovalLevel.FORM_CONFIRM)
        config_file = tmp_path / "config.json"
        result = json.loads(await create_lightning_enable_account(
            email=TEST_EMAIL, max_sats=500, confirmation_nonce="abc123",
            l402_client=client, budget_service=budget, config_path=str(config_file),
        ))
        # Bound to (max_sats, tool name, signup URL); tool upcases the code.
        args = budget.validate_and_consume_confirmation.call_args[0]
        assert args[0] == "ABC123"
        assert args[1] == 500
        assert args[2] == "create_lightning_enable_account"
        assert args[3].endswith("/api/signup/l402")
        assert result["success"] is True
        assert result["apiKey"] == "le_live_abc123"
        client.fetch.assert_awaited_once()
        # PASSIVE: the client (l402_client.fetch) records spend + cooldown exactly
        # once inside the payment leg; the tool must NOT record again (the old
        # tool-side record double-counted every activation).
        budget.record_spend.assert_not_called()
        budget.record_payment_time.assert_not_called()


class TestCreateAccountSignupFlow:
    @pytest.mark.asyncio
    async def test_posts_email_to_signup_endpoint(self, tmp_path):
        client = _paid_client()
        await create_lightning_enable_account(
            email=TEST_EMAIL, l402_client=client,
            config_path=str(tmp_path / "config.json"),
        )
        client.fetch.assert_awaited_once()
        kwargs = client.fetch.call_args.kwargs
        assert kwargs["url"].endswith("/api/signup/l402")
        assert kwargs["method"] == "POST"
        assert json.loads(kwargs["body"]) == {"email": TEST_EMAIL}

    @pytest.mark.asyncio
    async def test_success_returns_key_and_merchant_details(self, tmp_path):
        client = _paid_client()
        result = json.loads(await create_lightning_enable_account(
            email=TEST_EMAIL, l402_client=client,
            config_path=str(tmp_path / "config.json"),
        ))
        assert result["success"] is True
        assert result["apiKey"] == "le_live_abc123"
        assert result["merchantId"] == "merch_123"
        assert result["planTier"] == "individual"
        assert result["subscriptionStatus"] == "trialing"
        assert result["activation"]["paid"] is True
        assert result["activation"]["amountSats"] == 100

    @pytest.mark.asyncio
    async def test_success_merges_api_key_into_config(self, tmp_path):
        config_file = tmp_path / "config.json"
        client = _paid_client()
        result = json.loads(await create_lightning_enable_account(
            email=TEST_EMAIL, l402_client=client, config_path=str(config_file),
        ))
        assert result["config"]["written"] is True
        saved = json.loads(config_file.read_text(encoding="utf-8"))
        assert saved["lightningEnableApiKey"] == "le_live_abc123"

    @pytest.mark.asyncio
    async def test_config_merge_preserves_existing_keys(self, tmp_path):
        config_file = tmp_path / "config.json"
        config_file.write_text(json.dumps({
            "wallets": {"nwcConnectionString": "nostr+walletconnect://keep-me"},
            "tiers": {"autoApprove": 1.0},
        }), encoding="utf-8")

        client = _paid_client()
        await create_lightning_enable_account(
            email=TEST_EMAIL, l402_client=client, config_path=str(config_file),
        )
        saved = json.loads(config_file.read_text(encoding="utf-8"))
        # New key added, existing keys untouched (no clobber).
        assert saved["lightningEnableApiKey"] == "le_live_abc123"
        assert saved["wallets"]["nwcConnectionString"] == "nostr+walletconnect://keep-me"
        assert saved["tiers"]["autoApprove"] == 1.0

    @pytest.mark.asyncio
    async def test_server_returns_no_api_key_is_an_error(self, tmp_path):
        body = json.dumps({"status": "active", "merchantId": "m1"})  # no apiKey
        client = _paid_client(body=body)
        result = json.loads(await create_lightning_enable_account(
            email=TEST_EMAIL, l402_client=client,
            config_path=str(tmp_path / "config.json"),
        ))
        assert result["success"] is False
        assert "no apiKey" in result["error"]

    @pytest.mark.asyncio
    async def test_non_json_response_is_an_error(self, tmp_path):
        client = _paid_client(body="<html>gateway error</html>")
        result = json.loads(await create_lightning_enable_account(
            email=TEST_EMAIL, l402_client=client,
            config_path=str(tmp_path / "config.json"),
        ))
        assert result["success"] is False
        assert "not valid JSON" in result["error"]

    @pytest.mark.asyncio
    async def test_fetch_exception_is_handled(self, tmp_path):
        client = AsyncMock()
        client.fetch = AsyncMock(side_effect=Exception("wallet down"))
        result = json.loads(await create_lightning_enable_account(
            email=TEST_EMAIL, l402_client=client,
            config_path=str(tmp_path / "config.json"),
        ))
        assert result["success"] is False
        assert "wallet down" in result["error"]


class TestCreateAccountPaidButRetryFailed:
    """Funds-safety: L402Client.fetch settled the invoice but the authorized retry
    failed (raises L402Error with .amount_paid). The client already recorded the
    settled spend once before raising; the tool must surface it — never re-record."""

    @pytest.mark.asyncio
    async def test_surfaces_settled_amount_without_double_recording(self, tmp_path):
        from lightning_enable_mcp.l402_client import L402Error
        err = L402Error("Request failed after payment: 500 Internal Server Error")
        err.amount_paid = 100  # invoice SETTLED even though the retry failed

        client = AsyncMock()
        client.fetch = AsyncMock(side_effect=err)
        budget = _budget_with(ApprovalLevel.AUTO_APPROVE)
        history = MagicMock()

        result = json.loads(await create_lightning_enable_account(
            email=TEST_EMAIL, l402_client=client, budget_service=budget,
            payment_history_service=history, config_path=str(tmp_path / "config.json"),
        ))

        assert result["success"] is False
        # The settled payment is surfaced so the human knows money left the wallet.
        assert result["activation"]["paid"] is True
        assert result["activation"]["amountSats"] == 100
        assert "warning" in result
        # PASSIVE: the client recorded the spend once before raising — the tool
        # must NOT record again (the old tool-side record double-counted).
        budget.record_spend.assert_not_called()
        budget.record_payment_time.assert_not_called()
        history.record_payment.assert_not_called()

    @pytest.mark.asyncio
    async def test_plain_error_without_amount_paid_does_not_record_spend(self, tmp_path):
        from lightning_enable_mcp.l402_client import L402Error
        client = AsyncMock()
        client.fetch = AsyncMock(side_effect=L402Error("connection refused"))  # no amount_paid
        budget = _budget_with(ApprovalLevel.AUTO_APPROVE)

        result = json.loads(await create_lightning_enable_account(
            email=TEST_EMAIL, l402_client=client, budget_service=budget,
            config_path=str(tmp_path / "config.json"),
        ))

        assert result["success"] is False
        assert "activation" not in result  # nothing settled
        budget.record_spend.assert_not_called()


class TestCreateAccountReceipts:
    @pytest.mark.asyncio
    async def test_success_writes_durable_receipt_via_wallet_seam(self, tmp_path):
        # The durable receipt is written at the wallet seam (ReceiptRecordingWallet)
        # inside the client's payment leg, not by the tool — and the tool surfaces
        # the honest receipt_written signal at the top level.
        from types import SimpleNamespace
        from lightning_enable_mcp.receipt_seam import ReceiptRecordingWallet
        from lightning_enable_mcp.receipt_service import ReceiptService

        receipts = ReceiptService(wallet_label="unknown", receipts_path=tmp_path / "receipts.jsonl")

        class NWCWallet:  # noqa: N801 - class name drives the wallet label
            async def pay_invoice(self, bolt11, *a, **k):
                return "5f78ca4b8e2c11d3a9b0f6e1d2c3b4a5968778695a4b3c2d1e0f9a8b7c6d5e4f"

        seam = ReceiptRecordingWallet(NWCWallet(), receipts, None)

        async def fetch(url, method, headers, body, max_sats):
            # Simulate the client's payment leg: pays via the seam-wrapped wallet.
            await seam.pay_invoice("lnbc-placeholder")
            return (_ACCOUNT_PAYLOAD, 100, None)

        client = SimpleNamespace(fetch=fetch)
        budget = _budget_with(ApprovalLevel.AUTO_APPROVE)

        result = json.loads(await create_lightning_enable_account(
            email=TEST_EMAIL, l402_client=client, budget_service=budget,
            config_path=str(tmp_path / "config.json"),
        ))

        assert result["success"] is True
        assert result["receipt_written"] is True
        recs = receipts.read_recent(10)
        assert len(recs) == 1
        assert recs[0]["kind"] == "l402"
        assert recs[0]["context"] == "l402_fastlane_signup"
        assert recs[0]["wallet"] == "NWC"


class TestConfigClobberProtection:
    """A non-empty but malformed/non-object config must NOT be overwritten — doing so
    would destroy the user's other secrets (wallet creds, budget limits)."""

    def test_malformed_nonempty_file_is_not_clobbered(self, tmp_path):
        path = tmp_path / "config.json"
        original = '{"wallets": {"nwcConnectionString": "keep-me"} THIS IS BROKEN'
        path.write_text(original, encoding="utf-8")

        ok, out_path, err = _merge_api_key_into_config("new_key", str(path))
        assert ok is False
        assert "unparseable" in err
        # File left EXACTLY as it was — no clobber.
        assert path.read_text(encoding="utf-8") == original

    def test_nondict_json_is_not_clobbered(self, tmp_path):
        path = tmp_path / "config.json"
        path.write_text("[1, 2, 3]", encoding="utf-8")
        ok, out_path, err = _merge_api_key_into_config("new_key", str(path))
        assert ok is False
        assert "not a JSON object" in err
        assert path.read_text(encoding="utf-8") == "[1, 2, 3]"

    def test_whitespace_only_file_writes_fresh(self, tmp_path):
        path = tmp_path / "config.json"
        path.write_text("   \n\t ", encoding="utf-8")  # genuinely empty
        ok, out_path, err = _merge_api_key_into_config("k", str(path))
        assert ok is True
        assert json.loads(path.read_text(encoding="utf-8"))["lightningEnableApiKey"] == "k"

    @pytest.mark.asyncio
    async def test_signup_returns_key_without_clobbering_malformed_config(self, tmp_path):
        path = tmp_path / "config.json"
        original = "{ this is not json"
        path.write_text(original, encoding="utf-8")

        client = _paid_client()
        result = json.loads(await create_lightning_enable_account(
            email=TEST_EMAIL, l402_client=client, config_path=str(path),
        ))
        # Signup still succeeds and the key is still returned to the caller...
        assert result["success"] is True
        assert result["apiKey"] == "le_live_abc123"
        # ...but the malformed file was NOT overwritten.
        assert result["config"]["written"] is False
        assert "error" in result["config"]
        assert path.read_text(encoding="utf-8") == original


class TestMergeApiKeyHelper:
    def test_creates_file_when_missing(self, tmp_path):
        path = tmp_path / "nested" / "config.json"
        ok, out_path, err = _merge_api_key_into_config("le_key", str(path))
        assert ok is True
        assert err is None
        assert json.loads(path.read_text(encoding="utf-8"))["lightningEnableApiKey"] == "le_key"

    def test_overwrites_only_the_api_key(self, tmp_path):
        path = tmp_path / "config.json"
        path.write_text(json.dumps({"lightningEnableApiKey": "old", "currency": "USD"}), encoding="utf-8")
        _merge_api_key_into_config("new_key", str(path))
        saved = json.loads(path.read_text(encoding="utf-8"))
        assert saved["lightningEnableApiKey"] == "new_key"
        assert saved["currency"] == "USD"
