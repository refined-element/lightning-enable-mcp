"""
Tests for LND Wallet
"""

import base64
import pytest
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.lnd_wallet import (
    LndConfig,
    LndError,
    LndOnChainResult,
    LndPaymentError,
    LndWallet,
)


class TestLndConfig:
    """Tests for LND configuration."""

    def test_create_config(self):
        """Test creating a config with required fields."""
        config = LndConfig(
            rest_host="localhost:8080",
            macaroon_hex="0201036c6e6402",
        )
        assert config.rest_host == "localhost:8080"
        assert config.macaroon_hex == "0201036c6e6402"
        assert config.skip_tls_verify is False

    def test_skip_tls_verify(self):
        """Test skip_tls_verify flag."""
        config = LndConfig(
            rest_host="localhost:8080",
            macaroon_hex="abc123",
            skip_tls_verify=True,
        )
        assert config.skip_tls_verify is True


class TestLndOnChainResult:
    """Tests for LndOnChainResult."""

    def test_succeeded(self):
        """Test creating a successful result."""
        result = LndOnChainResult.succeeded(
            payment_id="txid123",
            txid="txid123",
            state="PENDING",
            amount_sats=50000,
            fee_sats=150,
        )
        assert result.success is True
        assert result.payment_id == "txid123"
        assert result.txid == "txid123"
        assert result.state == "PENDING"
        assert result.amount_sats == 50000
        assert result.fee_sats == 150

    def test_failed(self):
        """Test creating a failed result."""
        result = LndOnChainResult.failed("NOT_CONFIGURED", "LND not configured")
        assert result.success is False
        assert result.error_code == "NOT_CONFIGURED"
        assert result.error_message == "LND not configured"


class TestLndWallet:
    """Tests for LndWallet."""

    def _make_wallet(self, rest_host="localhost:8080", macaroon_hex="abc123"):
        """Create a wallet for testing."""
        return LndWallet(
            rest_host=rest_host,
            macaroon_hex=macaroon_hex,
            skip_tls_verify=True,
        )

    def test_init(self):
        """Test wallet initialization."""
        wallet = self._make_wallet()
        assert wallet.config.rest_host == "localhost:8080"
        assert wallet.config.macaroon_hex == "abc123"
        assert wallet.config.skip_tls_verify is True
        assert wallet.is_configured is True
        assert wallet.provider_name == "LND"

    def test_not_configured_empty_host(self):
        """Test wallet reports not configured with empty host."""
        wallet = self._make_wallet(rest_host="")
        assert wallet.is_configured is False

    def test_not_configured_empty_macaroon(self):
        """Test wallet reports not configured with empty macaroon."""
        wallet = self._make_wallet(macaroon_hex="")
        assert wallet.is_configured is False

    @pytest.mark.asyncio
    async def test_connect_adds_https_scheme(self):
        """Test connect adds https:// when no scheme present."""
        wallet = self._make_wallet(rest_host="localhost:8080")
        await wallet.connect()

        assert wallet._client is not None
        assert str(wallet._client.base_url).startswith("https://localhost:8080/v1/")
        assert wallet._connected is True
        await wallet.disconnect()

    @pytest.mark.asyncio
    async def test_connect_preserves_scheme(self):
        """Test connect preserves existing http:// scheme."""
        wallet = self._make_wallet(rest_host="http://localhost:8080")
        await wallet.connect()

        assert str(wallet._client.base_url).startswith("http://localhost:8080/v1/")
        await wallet.disconnect()

    @pytest.mark.asyncio
    async def test_connect_idempotent(self):
        """Test calling connect twice doesn't create two clients."""
        wallet = self._make_wallet()
        await wallet.connect()
        client1 = wallet._client
        await wallet.connect()
        client2 = wallet._client
        assert client1 is client2
        await wallet.disconnect()

    @pytest.mark.asyncio
    async def test_disconnect(self):
        """Test disconnect closes client."""
        wallet = self._make_wallet()
        await wallet.connect()
        assert wallet._connected is True
        await wallet.disconnect()
        assert wallet._connected is False

    @pytest.mark.asyncio
    async def test_pay_invoice_success(self):
        """Test successful invoice payment returns hex preimage."""
        wallet = self._make_wallet()

        # Create a known preimage and its base64 encoding
        preimage_bytes = bytes.fromhex("deadbeef" * 8)
        preimage_b64 = base64.b64encode(preimage_bytes).decode()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "payment_preimage": preimage_b64,
            "payment_error": "",
            "payment_hash": base64.b64encode(b"hash").decode(),
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        result = await wallet.pay_invoice("lnbc100n1...")
        assert result == "deadbeef" * 8

    @pytest.mark.asyncio
    async def test_pay_invoice_payment_error(self):
        """Test payment error raises LndPaymentError."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "payment_preimage": "",
            "payment_error": "insufficient balance",
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        with pytest.raises(LndPaymentError, match="insufficient balance"):
            await wallet.pay_invoice("lnbc100n1...")

    @pytest.mark.asyncio
    async def test_pay_invoice_no_preimage(self):
        """Test missing preimage raises LndPaymentError."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "payment_preimage": "",
            "payment_error": "",
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        with pytest.raises(LndPaymentError, match="no preimage"):
            await wallet.pay_invoice("lnbc100n1...")

    @pytest.mark.asyncio
    async def test_pay_invoice_not_configured(self):
        """Test pay_invoice raises when not configured."""
        wallet = self._make_wallet(rest_host="")

        with pytest.raises(LndPaymentError, match="not configured"):
            await wallet.pay_invoice("lnbc100n1...")

    @pytest.mark.asyncio
    async def test_pay_invoice_http_error(self):
        """Test pay_invoice handles HTTP errors."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 500
        mock_response.text = "Internal Server Error"

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        with pytest.raises((LndError, LndPaymentError)):
            await wallet.pay_invoice("lnbc100n1...")

    @pytest.mark.asyncio
    async def test_get_balance_success(self):
        """Test successful balance retrieval with string numbers."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "local_balance": {"sat": "317142", "msat": "317142000"},
            "remote_balance": {"sat": "100000", "msat": "100000000"},
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        balance = await wallet.get_balance()
        assert balance == 317142

    @pytest.mark.asyncio
    async def test_get_balance_zero(self):
        """Test balance when no channels exist."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "local_balance": {"sat": "0", "msat": "0"},
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        balance = await wallet.get_balance()
        assert balance == 0

    @pytest.mark.asyncio
    async def test_get_balance_missing_local_balance(self):
        """Test balance when local_balance is missing."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {}

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        balance = await wallet.get_balance()
        assert balance == 0

    @pytest.mark.asyncio
    async def test_get_balance_not_configured(self):
        """Test get_balance raises when not configured."""
        wallet = self._make_wallet(rest_host="")

        with pytest.raises(LndError, match="not configured"):
            await wallet.get_balance()

    @pytest.mark.asyncio
    async def test_create_invoice_success(self):
        """Test successful invoice creation."""
        wallet = self._make_wallet()

        r_hash_bytes = bytes.fromhex("abcdef1234567890" * 2)
        r_hash_b64 = base64.b64encode(r_hash_bytes).decode()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "r_hash": r_hash_b64,
            "payment_request": "lnbc100n1pj9npjpp5...",
            "add_index": "42",
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        result = await wallet.create_invoice(100, memo="Test invoice")

        assert result["invoice_id"] == "abcdef1234567890" * 2
        assert result["bolt11"] == "lnbc100n1pj9npjpp5..."
        assert result["amount_sats"] == 100
        assert "expires_at" in result

    @pytest.mark.asyncio
    async def test_create_invoice_no_payment_request(self):
        """Test create_invoice raises when no payment_request returned."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "r_hash": base64.b64encode(b"hash").decode(),
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        with pytest.raises(LndError, match="No invoice returned"):
            await wallet.create_invoice(100)

    @pytest.mark.asyncio
    async def test_create_invoice_not_configured(self):
        """Test create_invoice raises when not configured."""
        wallet = self._make_wallet(macaroon_hex="")

        with pytest.raises(LndError, match="not configured"):
            await wallet.create_invoice(100)

    @pytest.mark.asyncio
    async def test_create_invoice_default_memo(self):
        """Test create_invoice uses default memo when none provided."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "r_hash": base64.b64encode(b"hash").decode(),
            "payment_request": "lnbc100n1...",
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        await wallet.create_invoice(100)

        # Verify the request body included default memo
        call_args = wallet._client.request.call_args
        assert call_args.kwargs["json"]["memo"] == "Lightning payment"

    @pytest.mark.asyncio
    async def test_get_invoice_status_settled(self):
        """Test invoice status for settled invoice."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "state": "SETTLED",
            "value": "100",
            "settled": True,
            "settle_date": "1710460800",
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        result = await wallet.get_invoice_status("abc123")
        assert result["id"] == "abc123"
        assert result["state"] == "PAID"
        assert result["is_paid"] is True
        assert result["is_pending"] is False
        assert result["amount_sats"] == 100
        assert result["settled_at"] is not None

    @pytest.mark.asyncio
    async def test_get_invoice_status_open(self):
        """Test invoice status for pending invoice."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "state": "OPEN",
            "value": "500",
            "settled": False,
            "settle_date": "0",
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        result = await wallet.get_invoice_status("def456")
        assert result["state"] == "PENDING"
        assert result["is_paid"] is False
        assert result["is_pending"] is True
        assert result["amount_sats"] == 500
        assert result["settled_at"] is None

    @pytest.mark.asyncio
    async def test_get_invoice_status_canceled(self):
        """Test invoice status for canceled invoice."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "state": "CANCELED",
            "value": "1000",
            "settled": False,
            "settle_date": "0",
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        result = await wallet.get_invoice_status("ghi789")
        assert result["state"] == "CANCELLED"
        assert result["is_paid"] is False

    @pytest.mark.asyncio
    async def test_get_invoice_status_accepted(self):
        """Test invoice status for accepted (in-flight) invoice maps to PENDING."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "state": "ACCEPTED",
            "value": "200",
            "settled": False,
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        result = await wallet.get_invoice_status("jkl012")
        assert result["state"] == "PENDING"
        assert result["is_pending"] is True

    @pytest.mark.asyncio
    async def test_get_invoice_status_not_configured(self):
        """Test get_invoice_status raises when not configured."""
        wallet = self._make_wallet(rest_host="")

        with pytest.raises(LndError, match="not configured"):
            await wallet.get_invoice_status("abc123")

    @pytest.mark.asyncio
    async def test_send_onchain_success(self):
        """Test successful on-chain payment."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "txid": "abc123txid456",
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        result = await wallet.send_onchain("bc1qexample...", 50000)
        assert result.success is True
        assert result.txid == "abc123txid456"
        assert result.state == "PENDING"
        assert result.amount_sats == 50000

    @pytest.mark.asyncio
    async def test_send_onchain_not_configured(self):
        """Test on-chain payment when not configured."""
        wallet = self._make_wallet(rest_host="")
        result = await wallet.send_onchain("bc1q...", 50000)
        assert result.success is False
        assert result.error_code == "NOT_CONFIGURED"

    @pytest.mark.asyncio
    async def test_send_onchain_empty_address(self):
        """Test on-chain payment with empty address."""
        wallet = self._make_wallet()
        result = await wallet.send_onchain("", 50000)
        assert result.success is False
        assert result.error_code == "INVALID_ADDRESS"

    @pytest.mark.asyncio
    async def test_send_onchain_zero_amount(self):
        """Test on-chain payment with zero amount."""
        wallet = self._make_wallet()
        result = await wallet.send_onchain("bc1q...", 0)
        assert result.success is False
        assert result.error_code == "INVALID_AMOUNT"

    @pytest.mark.asyncio
    async def test_send_onchain_negative_amount(self):
        """Test on-chain payment with negative amount."""
        wallet = self._make_wallet()
        result = await wallet.send_onchain("bc1q...", -100)
        assert result.success is False
        assert result.error_code == "INVALID_AMOUNT"

    @pytest.mark.asyncio
    async def test_send_onchain_api_error(self):
        """Test on-chain payment handles API errors."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 500
        mock_response.text = "Internal error"

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        result = await wallet.send_onchain("bc1q...", 50000)
        assert result.success is False
        assert result.error_code == "API_ERROR"

    @pytest.mark.asyncio
    async def test_get_all_balances_success(self):
        """Test get_all_balances returns BTC balance."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "local_balance": {"sat": "100000", "msat": "100000000"},
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        result = await wallet.get_all_balances()
        assert result["success"] is True
        assert len(result["balances"]) == 1
        assert result["balances"][0]["currency"] == "BTC"
        assert result["balances"][0]["available"] == 0.001
        assert result["provider"] == "LND"

    @pytest.mark.asyncio
    async def test_get_all_balances_error(self):
        """Test get_all_balances handles errors gracefully."""
        wallet = self._make_wallet()

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(
            side_effect=Exception("Connection refused")
        )

        result = await wallet.get_all_balances()
        assert result["success"] is False
        assert "error_code" in result

    @pytest.mark.asyncio
    async def test_get_info_success(self):
        """Test get_info returns node info."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "alias": "my-node",
            "identity_pubkey": "02abc123...",
            "num_active_channels": 5,
            "num_peers": 10,
            "block_height": 830000,
            "synced_to_chain": True,
            "version": "0.18.0-beta",
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        result = await wallet.get_info()
        assert result["type"] == "lnd"
        assert result["alias"] == "my-node"
        assert result["preimage_support"] is True
        assert result["l402_compatible"] is True
        assert result["status"] == "connected"

    @pytest.mark.asyncio
    async def test_get_info_handles_error(self):
        """Test get_info returns basic info on error."""
        wallet = self._make_wallet()

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(
            side_effect=Exception("Connection refused")
        )

        result = await wallet.get_info()
        assert result["type"] == "lnd"
        assert result["preimage_support"] is True
        assert result["l402_compatible"] is True

    @pytest.mark.asyncio
    async def test_request_auto_connects(self):
        """Test _request auto-connects if not connected."""
        wallet = self._make_wallet()
        assert wallet._connected is False

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {"local_balance": {"sat": "0"}}

        # Patch connect to set up a mock client
        original_connect = wallet.connect

        async def mock_connect():
            await original_connect()
            wallet._client = AsyncMock()
            wallet._client.request = AsyncMock(return_value=mock_response)

        wallet.connect = mock_connect

        balance = await wallet.get_balance()
        assert balance == 0

    @pytest.mark.asyncio
    async def test_request_http_error_raises(self):
        """Test _request raises LndError on HTTP errors."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 401
        mock_response.text = "Unauthorized"

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        with pytest.raises(LndError, match="401"):
            await wallet._request("GET", "balance/channels")

    @pytest.mark.asyncio
    async def test_preimage_base64_to_hex_conversion(self):
        """Test that base64 preimage from LND is correctly converted to hex."""
        wallet = self._make_wallet()

        # Known test vector: 32 bytes of 0x01
        preimage_bytes = b"\x01" * 32
        preimage_b64 = base64.b64encode(preimage_bytes).decode()
        expected_hex = "01" * 32

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "payment_preimage": preimage_b64,
            "payment_error": "",
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        result = await wallet.pay_invoice("lnbc100n1...")
        assert result == expected_hex

    @pytest.mark.asyncio
    async def test_r_hash_base64_to_hex_conversion(self):
        """Test that base64 r_hash from LND is correctly converted to hex."""
        wallet = self._make_wallet()

        # Known test vector
        r_hash_bytes = bytes.fromhex("aabbccdd" * 4)
        r_hash_b64 = base64.b64encode(r_hash_bytes).decode()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "r_hash": r_hash_b64,
            "payment_request": "lnbc100n1...",
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        result = await wallet.create_invoice(100)
        assert result["invoice_id"] == "aabbccdd" * 4

    @pytest.mark.asyncio
    async def test_numbers_as_strings_handled(self):
        """Test that LND's string-encoded numbers are parsed correctly."""
        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "local_balance": {"sat": "999999", "msat": "999999000"},
            "remote_balance": {"sat": "500000", "msat": "500000000"},
        }

        wallet._connected = True
        wallet._client = AsyncMock()
        wallet._client.request = AsyncMock(return_value=mock_response)

        balance = await wallet.get_balance()
        assert balance == 999999
        assert isinstance(balance, int)
