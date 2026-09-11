"""
Tests for LND Wallet
"""

import base64
import httpx
import pytest
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.lnd_wallet import (
    MIN_FEE_LIMIT_SATS,
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
        wallet._client = _legacy_only_client(mock_response)

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
        wallet._client = _legacy_only_client(mock_response)

        with pytest.raises(LndPaymentError, match="insufficient balance"):
            await wallet.pay_invoice("lnbc100n1...")

    @pytest.mark.asyncio
    async def test_pay_invoice_no_preimage(self):
        """Missing preimage after a settled payment raises the non-retryable error.

        LND reported no payment_error, so the payment SETTLED — the funds are gone.
        This must raise the terminal PreimageUnavailableError (do NOT retry), NOT the
        generic LndPaymentError, which the caller would surface as a failure and an
        agent would retry, paying twice. (This test previously asserted LndPaymentError,
        which encoded the P0 double-pay bug.)
        """
        from lightning_enable_mcp.wallet_errors import PreimageUnavailableError

        wallet = self._make_wallet()

        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "payment_preimage": "",
            "payment_error": "",
        }

        wallet._connected = True
        wallet._client = _legacy_only_client(mock_response)

        with pytest.raises(PreimageUnavailableError) as excinfo:
            await wallet.pay_invoice("lnbc100n1...")
        assert not isinstance(excinfo.value, LndPaymentError)

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
        wallet._client = _legacy_only_client(mock_response)

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
        wallet._client = _legacy_only_client(mock_response)

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


class TestLndPreimageValidation:
    """
    The LND wallet boundary must validate preimages as 64-char hex.

    LND normally always returns a real preimage, but "normally" is not a
    guard. The release notes claim validation at EVERY wallet boundary, and
    this one base64-decoded whatever arrived and hex-encoded it, so a short or
    empty-ish value was published to the caller as proof of payment.
    """

    def _wallet_returning_preimage_b64(self, preimage_b64):
        wallet = LndWallet(
            rest_host="localhost:8080",
            macaroon_hex="abc123",
            skip_tls_verify=True,
        )
        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "payment_preimage": preimage_b64,
            "payment_error": "",
            "payment_hash": base64.b64encode(b"hash").decode(),
        }
        wallet._connected = True
        wallet._client = _legacy_only_client(mock_response)
        return wallet

    @pytest.mark.asyncio
    @pytest.mark.parametrize("raw_bytes", [
        b"\xde\xad\xbe\xef",   # 4 bytes -> "deadbeef", 8 hex chars, not a preimage
        b"\x01" * 31,          # one byte short
        b"\x01" * 33,          # one byte long
    ])
    async def test_rejects_wrong_length_preimage(self, raw_bytes):
        # PreimageUnavailableError, not LndPaymentError: LND reported no
        # payment_error, so the payment SETTLED. Reporting a failure would invite a
        # retry that pays twice (see the wallet_errors module contract).
        from lightning_enable_mcp.wallet_errors import PreimageUnavailableError

        wallet = self._wallet_returning_preimage_b64(base64.b64encode(raw_bytes).decode())
        with pytest.raises(PreimageUnavailableError):
            await wallet.pay_invoice("lnbc100n1...")

    @pytest.mark.asyncio
    async def test_settled_but_unprovable_is_not_reported_as_a_payment_failure(self):
        """A non-preimage means unprovable, NOT failed — a failure invites a double-pay."""
        from lightning_enable_mcp.wallet_errors import PreimageUnavailableError

        wallet = self._wallet_returning_preimage_b64(
            base64.b64encode(bytes.fromhex("deadbeef")).decode()
        )
        with pytest.raises(PreimageUnavailableError) as excinfo:
            await wallet.pay_invoice("lnbc100n1...")
        assert not isinstance(excinfo.value, LndPaymentError)
        assert excinfo.value.provider == "lnd"

    @pytest.mark.asyncio
    async def test_accepts_valid_32_byte_preimage(self):
        preimage_bytes = bytes.fromhex("deadbeef" * 8)
        wallet = self._wallet_returning_preimage_b64(
            base64.b64encode(preimage_bytes).decode()
        )
        assert await wallet.pay_invoice("lnbc100n1...") == "deadbeef" * 8

    @pytest.mark.asyncio
    async def test_error_never_echoes_the_bogus_value(self):
        """Engineering standard #5: never log/return preimage-position content."""
        from lightning_enable_mcp.wallet_errors import PreimageUnavailableError

        wallet = self._wallet_returning_preimage_b64(
            base64.b64encode(bytes.fromhex("deadbeef")).decode()
        )
        with pytest.raises(PreimageUnavailableError) as excinfo:
            await wallet.pay_invoice("lnbc100n1...")
        assert "deadbeef" not in str(excinfo.value)

    def _wallet_returning_raw(self, response):
        """Build a wallet whose next pay_invoice response is exactly ``response``."""
        wallet = LndWallet(
            rest_host="localhost:8080",
            macaroon_hex="abc123",
            skip_tls_verify=True,
        )
        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = response
        wallet._connected = True
        wallet._client = _legacy_only_client(mock_response)
        return wallet

    @pytest.mark.asyncio
    @pytest.mark.parametrize("response", [
        {"payment_error": "", "payment_hash": base64.b64encode(b"hash").decode()},  # key absent
        {"payment_preimage": None, "payment_error": ""},                            # explicit null
        {"payment_preimage": "", "payment_error": ""},                              # empty string
    ])
    async def test_settled_but_no_preimage_is_not_a_retryable_failure(self, response):
        # LND reported no payment_error, so the payment SETTLED — the funds are gone.
        # A missing/empty preimage means the payment is UNPROVABLE, not that it FAILED.
        # The old code raised the generic LndPaymentError("Payment succeeded but no
        # preimage returned") here, which the caller surfaces as a failure and an agent
        # retries — paying a second time. Per the wallet_errors contract this must raise
        # the terminal, non-retryable PreimageUnavailableError instead (mirrors the .NET
        # SucceededWithoutPreimage contract).
        from lightning_enable_mcp.wallet_errors import PreimageUnavailableError

        wallet = self._wallet_returning_raw(response)
        with pytest.raises(PreimageUnavailableError) as excinfo:
            await wallet.pay_invoice("lnbc100n1...")
        # Must be the terminal do-not-retry error, NOT the generic payment failure.
        assert not isinstance(excinfo.value, LndPaymentError)
        assert excinfo.value.provider == "lnd"

    @pytest.mark.asyncio
    async def test_genuine_failure_still_raises_the_retryable_payment_error(self):
        # No over-correction: when LND reports a payment_error the payment did NOT
        # settle, so this stays a normal (retryable) LndPaymentError and must NOT be
        # downgraded to the settled-but-unprovable PreimageUnavailableError.
        from lightning_enable_mcp.wallet_errors import PreimageUnavailableError

        wallet = self._wallet_returning_raw(
            {"payment_preimage": "", "payment_error": "insufficient_balance"}
        )
        with pytest.raises(LndPaymentError) as excinfo:
            await wallet.pay_invoice("lnbc100n1...")
        assert not isinstance(excinfo.value, PreimageUnavailableError)

    @pytest.mark.asyncio
    @pytest.mark.parametrize("bad_b64", [
        "not-valid-base64",  # non-alphabet chars -> Incorrect padding
        "abc",               # 3 valid chars -> Incorrect padding
    ])
    async def test_settled_but_malformed_base64_preimage_is_not_a_retryable_failure(
        self, bad_b64
    ):
        # LND reported no payment_error, so the payment SETTLED — the funds are gone.
        # A preimage that is not decodable base64 is settled-but-UNPROVABLE, not a
        # payment failure. The old code let base64.b64decode raise binascii.Error, which
        # fell through to the generic `except` and became a RETRYABLE LndPaymentError —
        # inviting a double-pay. It must raise the terminal, non-retryable
        # PreimageUnavailableError instead, matching the no-preimage / invalid-format
        # cases, and carry the payment_hash as the reconciliation tracking_id.
        from lightning_enable_mcp.wallet_errors import PreimageUnavailableError

        payment_hash_b64 = base64.b64encode(b"reconcile-me").decode()
        wallet = self._wallet_returning_raw(
            {"payment_preimage": bad_b64, "payment_error": "", "payment_hash": payment_hash_b64}
        )
        with pytest.raises(PreimageUnavailableError) as excinfo:
            await wallet.pay_invoice("lnbc100n1...")
        # Must be the terminal do-not-retry error, NOT the generic payment failure.
        assert not isinstance(excinfo.value, LndPaymentError)
        assert excinfo.value.provider == "lnd"
        assert excinfo.value.tracking_id == payment_hash_b64
        # Engineering standard #5: never echo the offending preimage-position value.
        assert bad_b64 not in str(excinfo.value)

    @pytest.mark.asyncio
    @pytest.mark.parametrize("bad_preimage", [
        123,            # JSON number -> base64.b64decode(123) raises TypeError
        123.45,         # JSON float -> TypeError
        ["deadbeef"],   # JSON array -> TypeError
        {"hex": "de"},  # JSON object -> TypeError
    ])
    async def test_settled_but_non_string_preimage_is_not_a_retryable_failure(
        self, bad_preimage
    ):
        # LND reported no payment_error, so the payment SETTLED — the funds are gone.
        # A wrong-type payment_preimage (e.g. a JSON number) makes base64.b64decode raise
        # TypeError, which the round-2 `except (binascii.Error, ValueError)` did NOT catch
        # — so it fell through to the generic `except` and became a RETRYABLE
        # LndPaymentError, inviting a double-pay. It must raise the terminal, non-retryable
        # PreimageUnavailableError instead, matching the no-preimage / malformed-base64
        # cases, and carry the payment_hash as the reconciliation tracking_id.
        from lightning_enable_mcp.wallet_errors import PreimageUnavailableError

        payment_hash_b64 = base64.b64encode(b"reconcile-me").decode()
        wallet = self._wallet_returning_raw(
            {
                "payment_preimage": bad_preimage,
                "payment_error": "",
                "payment_hash": payment_hash_b64,
            }
        )
        with pytest.raises(PreimageUnavailableError) as excinfo:
            await wallet.pay_invoice("lnbc100n1...")
        # Must be the terminal do-not-retry error, NOT the generic payment failure.
        assert not isinstance(excinfo.value, LndPaymentError)
        assert excinfo.value.provider == "lnd"
        assert excinfo.value.tracking_id == payment_hash_b64


# ---------------------------------------------------------------------------
# routerrpc SendPaymentV2 - the supported payment route.
#
# LND REMOVED the legacy lnrpc.SendPaymentSync REST route
# (POST /v1/channels/transactions). A modern node answers it with
# 404 {"code":5,"message":"Not Found"} and never creates a payment, so EVERY
# LND payment failed with "LND API error (404)" and no attempt recorded on the
# node. Payments must go to POST /v2/router/send instead, falling back to the
# old route only when the node does not serve v2 (a 404 means nothing was
# submitted, so the fallback cannot double-pay).
# ---------------------------------------------------------------------------

ZERO_PREIMAGE = "0" * 64
REAL_PREIMAGE = "deadbeef" * 8


class _FakeStream:
    """Stands in for httpx's streaming response context manager."""

    def __init__(self, status_code=200, lines=None, text="", hang=False):
        self.status_code = status_code
        self._lines = list(lines or [])
        self.text = text
        self._hang = hang
        self.read = False

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc):
        return False

    async def aread(self):
        self.read = True
        return b""

    async def aiter_lines(self):
        if self._hang:
            import asyncio

            await asyncio.sleep(3600)  # never yields - models a stalled LND stream
        for line in self._lines:
            yield line


class _FakeLndClient:
    """Fake httpx.AsyncClient exposing just the two seams LndWallet uses."""

    def __init__(self, stream=None, request_response=None):
        self._stream = stream or _FakeStream(status_code=404)
        self._request_response = request_response
        self.stream_calls = []
        self.request_calls = []

    def stream(self, method, url, **kwargs):
        self.stream_calls.append({"method": method, "url": url, **kwargs})
        return self._stream

    async def request(self, method, url, json=None):
        self.request_calls.append({"method": method, "url": url, "json": json})
        if self._request_response is None:
            raise AssertionError("unexpected legacy request to " + str(url))
        return self._request_response


def _legacy_only_client(request_response):
    """A node that does NOT serve routerrpc, so pay_invoice falls back to /v1.

    Used by the tests that assert the legacy route's base64 preimage semantics.
    """
    return _FakeLndClient(stream=_FakeStream(status_code=404), request_response=request_response)


def _frame(**payment):
    """One grpc-gateway server-streaming frame: {"result": <lnrpc.Payment>}."""
    import json as _json

    return _json.dumps({"result": payment})


def _router_wallet(stream=None, request_response=None):
    wallet = LndWallet(rest_host="localhost:8080", macaroon_hex="abc123", skip_tls_verify=True)
    wallet._connected = True
    wallet._client = _FakeLndClient(stream=stream, request_response=request_response)
    return wallet


class TestLndRouterSendPaymentV2:
    """The payment must use routerrpc SendPaymentV2, not the removed v1 route."""

    @pytest.mark.asyncio
    async def test_pay_invoice_posts_to_router_v2(self):
        stream = _FakeStream(lines=[_frame(status="SUCCEEDED", payment_preimage=REAL_PREIMAGE)])
        wallet = _router_wallet(stream=stream)

        assert await wallet.pay_invoice("lnbc30n1...") == REAL_PREIMAGE

        assert len(wallet._client.stream_calls) == 1
        call = wallet._client.stream_calls[0]
        assert call["method"] == "POST"
        assert call["url"].endswith("/v2/router/send")
        # The removed route must not be touched at all on a healthy node.
        assert wallet._client.request_calls == []

    @pytest.mark.asyncio
    async def test_pay_invoice_sends_a_nonzero_fee_limit(self):
        """SendPaymentV2 considers ONLY zero-fee routes when fee_limit_sat is 0."""
        stream = _FakeStream(lines=[_frame(status="SUCCEEDED", payment_preimage=REAL_PREIMAGE)])
        wallet = _router_wallet(stream=stream)
        await wallet.pay_invoice("lnbc30n1...")

        body = wallet._client.stream_calls[0]["json"]
        assert body["payment_request"] == "lnbc30n1..."
        assert int(body["fee_limit_sat"]) > 0
        # A node-side timeout keeps the call bounded even if the client never cancels.
        assert int(body["timeout_seconds"]) > 0

    @pytest.mark.asyncio
    async def test_failed_frame_is_a_retryable_payment_error(self):
        stream = _FakeStream(
            lines=[
                _frame(
                    status="FAILED",
                    failure_reason="FAILURE_REASON_NO_ROUTE",
                    payment_preimage=ZERO_PREIMAGE,
                )
            ]
        )
        wallet = _router_wallet(stream=stream)
        with pytest.raises(LndPaymentError, match="NO_ROUTE"):
            await wallet.pay_invoice("lnbc30n1...")

    @pytest.mark.asyncio
    async def test_all_zero_preimage_is_never_returned_as_proof(self):
        """LND fills payment_preimage with 32 zero bytes when there is no proof.

        It is 64 valid hex characters, so a length/hex check alone accepts it - and the
        agent would publish it as an L402 Authorization preimage for a payment it cannot
        prove.
        """
        from lightning_enable_mcp.wallet_errors import PreimageUnavailableError

        stream = _FakeStream(
            lines=[_frame(status="SUCCEEDED", payment_preimage=ZERO_PREIMAGE, payment_hash="ab" * 32)]
        )
        wallet = _router_wallet(stream=stream)
        with pytest.raises(PreimageUnavailableError) as excinfo:
            await wallet.pay_invoice("lnbc30n1...")
        assert not isinstance(excinfo.value, LndPaymentError)
        assert excinfo.value.tracking_id == "ab" * 32

    @pytest.mark.asyncio
    async def test_in_flight_without_a_terminal_frame_is_pending_not_failed(self):
        """Reporting an in-flight payment as failed invites a double-pay."""
        from lightning_enable_mcp.wallet_errors import PaymentPendingError

        stream = _FakeStream(lines=[_frame(status="IN_FLIGHT", payment_preimage=ZERO_PREIMAGE)])
        wallet = _router_wallet(stream=stream)
        with pytest.raises(PaymentPendingError):
            await wallet.pay_invoice("lnbc30n1...")

    @pytest.mark.asyncio
    async def test_no_frames_at_all_is_pending_not_failed(self):
        from lightning_enable_mcp.wallet_errors import PaymentPendingError

        wallet = _router_wallet(stream=_FakeStream(lines=[]))
        with pytest.raises(PaymentPendingError):
            await wallet.pay_invoice("lnbc30n1...")

    @pytest.mark.asyncio
    async def test_a_stalled_stream_cannot_hang_the_agent_forever(self):
        """The reported symptom: pay_invoice blocked for minutes with no result.

        A stalled read must surface as a BOUNDED, non-retryable "pending" result, never
        an unbounded await.
        """
        import asyncio

        from lightning_enable_mcp.wallet_errors import PaymentPendingError

        wallet = _router_wallet(stream=_FakeStream(hang=True))
        wallet._payment_timeout_seconds = 1  # keep the test fast; same code path

        with pytest.raises(PaymentPendingError):
            await asyncio.wait_for(wallet.pay_invoice("lnbc30n1..."), timeout=30)

    @pytest.mark.asyncio
    async def test_falls_back_to_the_legacy_route_when_v2_is_absent(self):
        """Old nodes without routerrpc: a 404 means nothing was submitted, so the
        fallback is double-pay-safe."""
        preimage_b64 = base64.b64encode(bytes.fromhex(REAL_PREIMAGE)).decode()
        legacy = MagicMock()
        legacy.status_code = 200
        legacy.json.return_value = {"payment_preimage": preimage_b64, "payment_error": ""}

        wallet = _router_wallet(stream=_FakeStream(status_code=404), request_response=legacy)
        assert await wallet.pay_invoice("lnbc30n1...") == REAL_PREIMAGE
        assert wallet._client.request_calls[0]["url"] == "channels/transactions"

    @pytest.mark.asyncio
    async def test_v2_http_error_does_not_fall_back(self):
        """A non-404 error came from a route that EXISTS - retrying it on the legacy
        route could submit the payment twice."""
        wallet = _router_wallet(stream=_FakeStream(status_code=500, text="boom"))
        with pytest.raises(LndError, match="500"):
            await wallet.pay_invoice("lnbc30n1...")
        assert wallet._client.request_calls == []


# ---------------------------------------------------------------------------
# Transport failures AFTER the 2xx headers arrived.
#
# Once LND has answered 2xx on /v2/router/send the node may already have accepted
# the payment. A read error from that point on (connection dropped mid-stream, a
# torn chunk, a read timeout) proves nothing about the payment's outcome, so it
# must surface as PENDING with the invoice payment hash for reconciliation -
# never as the retryable LndPaymentError, which invites a double-pay.
# ---------------------------------------------------------------------------


def _signed_invoice(amount_sats: int | None, payment_hash_hex: str = "ab" * 32) -> str:
    """A real, signed mainnet BOLT11 so the wallet's bolt11 decoding runs for real."""
    from bolt11 import Bolt11, MilliSatoshi, Tag, TagChar, Tags, encode

    tags = Tags(
        [
            Tag(TagChar.payment_hash, payment_hash_hex),
            Tag(TagChar.description, "fee-limit fixture"),
            Tag(TagChar.payment_secret, "cd" * 32),
        ]
    )
    invoice = Bolt11(
        currency="bc",
        date=1700000000,
        tags=tags,
        amount_msat=MilliSatoshi(amount_sats * 1000) if amount_sats else None,
    )
    return encode(invoice, "11" * 32)


class _BrokenAfterHeadersStream(_FakeStream):
    """200 + headers arrive, then the body read raises a transport error."""

    def __init__(self, error: Exception, lines=None):
        super().__init__(status_code=200, lines=lines)
        self._error = error

    async def aiter_lines(self):
        for line in self._lines:
            yield line
        raise self._error


_MID_STREAM_ERRORS = [
    pytest.param(httpx.ReadError("connection reset"), id="ReadError"),
    pytest.param(httpx.RemoteProtocolError("peer closed connection"), id="RemoteProtocolError"),
    pytest.param(httpx.ReadTimeout("read timed out"), id="ReadTimeout"),
]


class TestLndTransportErrorAfterHeaders:
    @pytest.mark.asyncio
    @pytest.mark.parametrize("error", _MID_STREAM_ERRORS)
    async def test_read_error_with_no_frames_is_pending_with_the_invoice_hash(self, error):
        from lightning_enable_mcp.wallet_errors import PaymentPendingError

        bolt11 = _signed_invoice(30, payment_hash_hex="ab" * 32)
        wallet = _router_wallet(stream=_BrokenAfterHeadersStream(error))

        with pytest.raises(PaymentPendingError) as excinfo:
            await wallet.pay_invoice(bolt11)

        assert not isinstance(excinfo.value, LndPaymentError), "must not be the retryable failure"
        assert excinfo.value.provider == "lnd"
        assert excinfo.value.tracking_id == "ab" * 32
        assert wallet._client.request_calls == [], "no legacy-route retry after a 2xx"

    @pytest.mark.asyncio
    @pytest.mark.parametrize("error", _MID_STREAM_ERRORS)
    async def test_read_error_after_an_in_flight_frame_is_pending_not_failed(self, error):
        from lightning_enable_mcp.wallet_errors import PaymentPendingError

        bolt11 = _signed_invoice(30, payment_hash_hex="ab" * 32)
        stream = _BrokenAfterHeadersStream(
            error, lines=[_frame(status="IN_FLIGHT", payment_preimage=ZERO_PREIMAGE)]
        )
        wallet = _router_wallet(stream=stream)

        with pytest.raises(PaymentPendingError) as excinfo:
            await wallet.pay_invoice(bolt11)

        assert not isinstance(excinfo.value, LndPaymentError)
        assert excinfo.value.tracking_id == "ab" * 32
        assert wallet._client.request_calls == []

    @pytest.mark.asyncio
    async def test_connect_error_before_any_response_is_still_a_connection_failure(self):
        """No 2xx was ever observed, so nothing was submitted: the plain error stands."""

        class _NeverConnects(_FakeStream):
            async def __aenter__(self):
                raise httpx.ConnectError("refused")

        wallet = _router_wallet(stream=_NeverConnects())
        with pytest.raises(LndError, match="Failed to connect"):
            await wallet.pay_invoice(_signed_invoice(30))


class TestLndFeeLimit:
    """Routing-fee ceiling: 5% of the invoice, ceil'd, floored at 2 sats, env-overridable."""

    def _wallet(self, monkeypatch, override: str | None = None):
        if override is None:
            monkeypatch.delenv("LND_FEE_LIMIT_SATS", raising=False)
        else:
            monkeypatch.setenv("LND_FEE_LIMIT_SATS", override)
        return LndWallet(rest_host="localhost:8080", macaroon_hex="abc123", skip_tls_verify=True)

    @pytest.mark.parametrize(
        ("amount_sats", "expected"),
        [
            (1000, 50),  # 5% exactly
            (1001, 51),  # 5% = 50.05 -> ceil
            (30, 2),  # 5% = 1.5 -> ceil 2 == floor
            (10, 2),  # 5% = 0.5 -> ceil 1 -> floor 2
            (1, 2),  # floor
        ],
    )
    def test_five_percent_ceil_with_a_two_sat_floor(self, monkeypatch, amount_sats, expected):
        wallet = self._wallet(monkeypatch)
        assert wallet._fee_limit_sats(_signed_invoice(amount_sats)) == expected

    def test_amountless_invoice_gets_the_floor(self, monkeypatch):
        wallet = self._wallet(monkeypatch)
        assert wallet._fee_limit_sats(_signed_invoice(None)) == MIN_FEE_LIMIT_SATS

    def test_undecodable_invoice_gets_the_floor(self, monkeypatch):
        wallet = self._wallet(monkeypatch)
        assert wallet._fee_limit_sats("lnbc30n1...") == MIN_FEE_LIMIT_SATS

    def test_env_override_wins_over_the_percentage(self, monkeypatch):
        wallet = self._wallet(monkeypatch, override="7")
        assert wallet._fee_limit_sats(_signed_invoice(1000)) == 7

    @pytest.mark.parametrize("bad", ["0", "-3", "abc", "  "])
    def test_invalid_override_is_ignored(self, monkeypatch, bad):
        """0 means 'zero-fee routes only' to SendPaymentV2, so it must never pass through."""
        wallet = self._wallet(monkeypatch, override=bad)
        assert wallet._fee_limit_sats(_signed_invoice(1000)) == 50


# ---------------------------------------------------------------------------
# TLS policy: LND_TLS_CERT_PATH pins the node's self-signed tls.cert; LND_SKIP_TLS_VERIFY
# turns verification off (dev only). Mirrors the .NET BuildServerCertificateValidator.
# Repro: with LND_TLS_CERT_PATH set to LND's own tls.cert, the Python server failed with
# "[SSL: CERTIFICATE_VERIFY_FAILED] self-signed certificate" because the path was ignored.
# ---------------------------------------------------------------------------

import ssl as _ssl

from lightning_enable_mcp.lnd_wallet import build_tls_verify


def _self_signed_cert_pem(tmp_path, name="tls.cert"):
    """Write a throwaway self-signed cert (like LND's tls.cert) and return its path."""
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.x509.oid import NameOID
    from datetime import datetime, timedelta, timezone

    key = ec.generate_private_key(ec.SECP256R1())
    subject = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "lnd autogenerated cert")])
    now = datetime.now(timezone.utc)
    cert = (
        x509.CertificateBuilder()
        .subject_name(subject)
        .issuer_name(subject)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - timedelta(minutes=1))
        .not_valid_after(now + timedelta(days=1))
        .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
        .sign(key, hashes.SHA256())
    )
    path = tmp_path / name
    path.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    return path, cert


class TestLndTlsPolicy:
    def test_cert_path_pins_only_that_cert(self, tmp_path):
        path, cert = _self_signed_cert_pem(tmp_path)
        verify = build_tls_verify(skip_tls_verify=False, tls_cert_path=str(path))
        assert isinstance(verify, _ssl.SSLContext)
        assert verify.verify_mode == _ssl.CERT_REQUIRED
        # Exactly the pinned cert is trusted, nothing from the system store.
        certs = verify.get_ca_certs(binary_form=True)
        from cryptography.hazmat.primitives import serialization
        assert certs == [cert.public_bytes(serialization.Encoding.DER)]

    def test_cert_path_accepts_der(self, tmp_path):
        from cryptography.hazmat.primitives import serialization
        _, cert = _self_signed_cert_pem(tmp_path)
        der = tmp_path / "tls.der"
        der.write_bytes(cert.public_bytes(serialization.Encoding.DER))
        verify = build_tls_verify(skip_tls_verify=False, tls_cert_path=str(der))
        assert isinstance(verify, _ssl.SSLContext)
        assert len(verify.get_ca_certs(binary_form=True)) == 1

    def test_skip_flag_disables_verification_with_warning(self, caplog):
        with caplog.at_level("WARNING", logger="lightning_enable_mcp.lnd_wallet"):
            verify = build_tls_verify(skip_tls_verify=True, tls_cert_path=None)
        assert verify is False
        assert any("LND_SKIP_TLS_VERIFY" in r.getMessage() for r in caplog.records)

    def test_cert_path_wins_over_skip_flag(self, tmp_path):
        path, _ = _self_signed_cert_pem(tmp_path)
        verify = build_tls_verify(skip_tls_verify=True, tls_cert_path=str(path))
        assert isinstance(verify, _ssl.SSLContext)

    def test_neither_set_uses_default_verification(self):
        assert build_tls_verify(skip_tls_verify=False, tls_cert_path=None) is True
        assert build_tls_verify(skip_tls_verify=False, tls_cert_path="   ") is True
        # An unexpanded ${VAR} placeholder from a config file is treated as unset.
        assert build_tls_verify(skip_tls_verify=False, tls_cert_path="${LND_TLS_CERT_PATH}") is True

    def test_missing_cert_path_is_a_clear_error(self, tmp_path):
        missing = tmp_path / "nope.cert"
        with pytest.raises(LndError) as exc:
            build_tls_verify(skip_tls_verify=False, tls_cert_path=str(missing))
        msg = str(exc.value)
        assert "LND_TLS_CERT_PATH" in msg
        assert str(missing) in msg
        assert "could not be read" in msg

    def test_garbage_cert_file_is_a_clear_error(self, tmp_path):
        bad = tmp_path / "tls.cert"
        bad.write_text("this is not a certificate")
        with pytest.raises(LndError) as exc:
            build_tls_verify(skip_tls_verify=False, tls_cert_path=str(bad))
        assert "LND_TLS_CERT_PATH" in str(exc.value)

    @pytest.mark.asyncio
    async def test_connect_passes_pinned_context_to_httpx(self, tmp_path, monkeypatch):
        path, _ = _self_signed_cert_pem(tmp_path)
        captured = {}

        class _Client:
            def __init__(self, **kwargs):
                captured.update(kwargs)

            async def aclose(self):
                pass

        monkeypatch.setattr("lightning_enable_mcp.lnd_wallet.httpx.AsyncClient", _Client)
        wallet = LndWallet(rest_host="localhost:8080", macaroon_hex="abc123", tls_cert_path=str(path))
        await wallet.connect()
        assert isinstance(captured["verify"], _ssl.SSLContext)

    @pytest.mark.asyncio
    async def test_connect_passes_verify_false_when_skipping(self, monkeypatch):
        captured = {}

        class _Client:
            def __init__(self, **kwargs):
                captured.update(kwargs)

            async def aclose(self):
                pass

        monkeypatch.setattr("lightning_enable_mcp.lnd_wallet.httpx.AsyncClient", _Client)
        wallet = LndWallet(rest_host="localhost:8080", macaroon_hex="abc123", skip_tls_verify=True)
        await wallet.connect()
        assert captured["verify"] is False

    @pytest.mark.asyncio
    async def test_connect_with_bad_cert_path_raises_before_any_request(self, tmp_path):
        wallet = LndWallet(
            rest_host="localhost:8080",
            macaroon_hex="abc123",
            tls_cert_path=str(tmp_path / "missing.cert"),
        )
        with pytest.raises(LndError, match="LND_TLS_CERT_PATH"):
            await wallet.connect()
        assert wallet._connected is False
