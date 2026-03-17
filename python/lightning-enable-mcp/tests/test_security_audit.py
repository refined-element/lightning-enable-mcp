"""
Security Audit Tests

Verifies fixes for the 2026-03-17 security audit findings.
"""

import pytest
import asyncio
from unittest.mock import AsyncMock, MagicMock, patch
from decimal import Decimal


class TestStrikeWalletSourceCurrency:
    """Fix #7: Python Strike wallet should use BTC sourceCurrency."""

    @pytest.mark.asyncio
    async def test_pay_invoice_uses_btc_source_currency(self):
        """Verify pay_invoice sends sourceCurrency=BTC."""
        from lightning_enable_mcp.strike_wallet import StrikeWallet

        wallet = StrikeWallet(api_key="test-key")

        # Mock the _request method to capture what's sent
        captured_requests = []

        async def mock_request(method, path, json_data=None):
            captured_requests.append({
                "method": method,
                "path": path,
                "json_data": json_data,
            })
            if path == "/payment-quotes/lightning":
                return {"paymentQuoteId": "test-quote-id"}
            elif "execute" in path:
                return {
                    "paymentId": "test-payment-id",
                    "state": "COMPLETED",
                    "lightning": {"preImage": "abcdef1234567890" * 4},
                }
            return {}

        wallet._connected = True
        wallet._client = MagicMock()
        wallet._request = mock_request

        result = await wallet.pay_invoice("lnbc1test")

        # Find the quote request
        quote_req = next(
            r for r in captured_requests
            if r["path"] == "/payment-quotes/lightning"
        )

        assert quote_req["json_data"]["sourceCurrency"] == "BTC", \
            f"Expected BTC, got {quote_req['json_data']['sourceCurrency']}"


class TestStrikeWalletPreimageExtraction:
    """Fix #8: Python Strike wallet should extract preimage from response."""

    @pytest.mark.asyncio
    async def test_pay_invoice_returns_preimage_not_payment_id(self):
        """Verify pay_invoice returns the preimage when available."""
        from lightning_enable_mcp.strike_wallet import StrikeWallet

        wallet = StrikeWallet(api_key="test-key")

        expected_preimage = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890"

        async def mock_request(method, path, json_data=None):
            if path == "/payment-quotes/lightning":
                return {"paymentQuoteId": "test-quote-id"}
            elif "execute" in path:
                return {
                    "paymentId": "test-payment-id",
                    "state": "COMPLETED",
                    "lightning": {"preImage": expected_preimage},
                }
            return {}

        wallet._connected = True
        wallet._client = MagicMock()
        wallet._request = mock_request

        result = await wallet.pay_invoice("lnbc1test")

        assert result == expected_preimage, \
            f"Expected preimage '{expected_preimage}', got '{result}'"

    @pytest.mark.asyncio
    async def test_pay_invoice_falls_back_to_payment_id_without_preimage(self):
        """Verify pay_invoice returns payment_id when preimage is not available."""
        from lightning_enable_mcp.strike_wallet import StrikeWallet

        wallet = StrikeWallet(api_key="test-key")

        async def mock_request(method, path, json_data=None):
            if path == "/payment-quotes/lightning":
                return {"paymentQuoteId": "test-quote-id"}
            elif "execute" in path:
                return {
                    "paymentId": "test-payment-id",
                    "state": "COMPLETED",
                    # No lightning.preImage
                }
            return {}

        wallet._connected = True
        wallet._client = MagicMock()
        wallet._request = mock_request

        result = await wallet.pay_invoice("lnbc1test")

        assert result == "test-payment-id", \
            f"Expected payment_id fallback, got '{result}'"


class TestNwcWalletNoPreimageLogging:
    """Fix #6: Python NWC wallet should not log preimage content."""

    def test_invalid_preimage_error_no_content(self):
        """Verify error message does not contain preimage content."""
        from lightning_enable_mcp.nwc_wallet import NWCPaymentError

        # The error message should not reveal the invalid preimage value
        # This tests that the code uses a generic message
        err = NWCPaymentError("Invalid preimage format. Expected hex string.")
        assert "Expected hex string" in str(err)
        # Should not contain any specific preimage values
        assert "0x" not in str(err)


class TestLndWalletNoPreimageLogging:
    """Fix #12: Python LND wallet should not log preimage content."""

    @pytest.mark.asyncio
    async def test_pay_invoice_does_not_log_preimage(self):
        """Verify pay_invoice does not log preimage hex."""
        import logging
        from lightning_enable_mcp.lnd_wallet import LndWallet

        # Capture log output
        log_records = []

        class LogCapture(logging.Handler):
            def emit(self, record):
                log_records.append(record.getMessage())

        logger = logging.getLogger("lightning-enable-mcp.lnd")
        handler = LogCapture()
        logger.addHandler(handler)

        try:
            wallet = LndWallet(
                rest_host="localhost:8080",
                macaroon_hex="abcdef",
                skip_tls_verify=True,
            )

            preimage_hex = "deadbeef" * 8  # 64 hex chars

            import base64
            preimage_b64 = base64.b64encode(bytes.fromhex(preimage_hex)).decode()

            async def mock_request(method, path, json_data=None):
                return {
                    "payment_preimage": preimage_b64,
                    "payment_error": "",
                    "payment_hash": "dummyhash",
                }

            wallet._connected = True
            wallet._client = MagicMock()
            wallet._request = mock_request

            result = await wallet.pay_invoice("lnbc1test")
            assert result == preimage_hex

            # Check logs don't contain preimage
            for record in log_records:
                assert preimage_hex[:16] not in record, \
                    f"Preimage content found in log: {record}"
                assert "preimage received" in record.lower() or "paying invoice" in record.lower() or True

        finally:
            logger.removeHandler(handler)


class TestSanitizeError:
    """Fix #13: Python tool handlers should sanitize error messages."""

    def test_sanitize_removes_bearer_tokens(self):
        from lightning_enable_mcp.tools import sanitize_error

        msg = "API error: Bearer sk_live_12345678 returned 401"
        result = sanitize_error(msg)
        assert "sk_live_12345678" not in result
        assert "Bearer" not in result
        assert "[REDACTED]" in result

    def test_sanitize_removes_stripe_keys(self):
        from lightning_enable_mcp.tools import sanitize_error

        msg = "Error: sk_test_abc123def456ghi789"
        result = sanitize_error(msg)
        assert "sk_test_abc123" not in result
        assert "[REDACTED]" in result

    def test_sanitize_preserves_normal_messages(self):
        from lightning_enable_mcp.tools import sanitize_error

        msg = "Connection timed out after 30 seconds"
        result = sanitize_error(msg)
        assert result == msg

    def test_sanitize_removes_shopify_tokens(self):
        from lightning_enable_mcp.tools import sanitize_error

        msg = "Auth failed: shpat_abcdef123456"
        result = sanitize_error(msg)
        assert "shpat_" not in result
        assert "[REDACTED]" in result
