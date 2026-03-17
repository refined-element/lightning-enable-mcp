"""
LND Wallet Client

Implements Lightning wallet operations via LND's REST API.
Connects directly to user's own Lightning node - ALWAYS returns preimage.

This is the recommended wallet for L402 because:
1. User controls their own node
2. LND always returns preimage for payments
3. Direct node access = lowest latency

Configuration (environment variables or config file):
- LND_REST_HOST: LND REST API host (e.g., "localhost:8080" or "127.0.0.1:8080")
- LND_MACAROON_HEX: Admin macaroon in hex format (required for payments)
- LND_SKIP_TLS_VERIFY: Set to "true" to skip TLS verification (dev only)

To get your macaroon in hex format:
- Linux/Mac: xxd -ps -c 1000 ~/.lnd/data/chain/bitcoin/mainnet/admin.macaroon
- Windows PowerShell: [System.BitConverter]::ToString(
      [System.IO.File]::ReadAllBytes("$env:USERPROFILE\\AppData\\Local\\Lnd\\data\\chain\\bitcoin\\mainnet\\admin.macaroon")
  ) -replace '-',''
"""

import base64
import logging
from dataclasses import dataclass
from datetime import datetime, timezone
from decimal import Decimal
from typing import Any

try:
    import httpx
except ImportError:
    httpx = None  # type: ignore

logger = logging.getLogger("lightning-enable-mcp.lnd")


class LndError(Exception):
    """Exception for LND-related errors."""
    pass


class LndPaymentError(LndError):
    """Exception for payment failures."""
    pass


@dataclass
class LndConfig:
    """LND connection configuration."""
    rest_host: str
    macaroon_hex: str
    skip_tls_verify: bool = False


@dataclass
class LndOnChainResult:
    """On-chain payment result from LND."""
    success: bool
    payment_id: str | None = None
    txid: str | None = None
    state: str | None = None
    amount_sats: int | None = None
    fee_sats: int | None = None
    error_code: str | None = None
    error_message: str | None = None

    @classmethod
    def succeeded(
        cls,
        payment_id: str,
        state: str,
        amount_sats: int,
        fee_sats: int = 0,
        txid: str | None = None,
    ) -> "LndOnChainResult":
        return cls(
            success=True,
            payment_id=payment_id,
            txid=txid,
            state=state,
            amount_sats=amount_sats,
            fee_sats=fee_sats,
        )

    @classmethod
    def failed(cls, code: str, message: str) -> "LndOnChainResult":
        return cls(success=False, error_code=code, error_message=message)


class LndWallet:
    """
    LND wallet client for Lightning payments via LND REST API.

    Provides the same core interface as NWCWallet, StrikeWallet, and OpenNodeWallet.
    LND always returns the preimage for outgoing payments, making it ideal for L402.

    LND REST API notes:
    - Numbers are returned as strings in JSON (e.g., "1000" not 1000)
    - Preimage and r_hash are returned as base64, must convert to hex
    - Auth via Grpc-Metadata-macaroon header with hex-encoded macaroon
    """

    def __init__(
        self,
        rest_host: str,
        macaroon_hex: str,
        skip_tls_verify: bool = False,
    ) -> None:
        """
        Initialize LND wallet.

        Args:
            rest_host: LND REST API host (e.g., "localhost:8080")
            macaroon_hex: Admin macaroon in hex format
            skip_tls_verify: Skip TLS certificate verification (dev only)
        """
        if httpx is None:
            raise ImportError(
                "httpx is required for LndWallet. Install with: pip install httpx"
            )

        self.config = LndConfig(
            rest_host=rest_host,
            macaroon_hex=macaroon_hex,
            skip_tls_verify=skip_tls_verify,
        )
        self._client: httpx.AsyncClient | None = None
        self._connected = False

    @property
    def is_configured(self) -> bool:
        """Whether the wallet has valid configuration."""
        return bool(self.config.rest_host) and bool(self.config.macaroon_hex)

    @property
    def provider_name(self) -> str:
        """Return the provider name."""
        return "LND"

    async def connect(self) -> None:
        """Initialize the HTTP client and verify connection."""
        if self._connected:
            return

        # Determine base URL - add scheme if not present
        host = self.config.rest_host
        if not host.startswith("https://") and not host.startswith("http://"):
            host = f"https://{host}"

        base_url = f"{host}/v1/"

        self._client = httpx.AsyncClient(
            base_url=base_url,
            headers={
                "Grpc-Metadata-macaroon": self.config.macaroon_hex,
                "Content-Type": "application/json",
            },
            timeout=60.0,
            verify=not self.config.skip_tls_verify,
        )

        self._connected = True
        logger.info(f"LND wallet connected to {self.config.rest_host}")

    async def disconnect(self) -> None:
        """Close the HTTP client."""
        if self._client:
            await self._client.aclose()
        self._connected = False

    async def _request(
        self,
        method: str,
        path: str,
        json_data: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        """
        Make an API request to LND REST API.

        Args:
            method: HTTP method (GET, POST)
            path: API path (relative to /v1/)
            json_data: Request body data

        Returns:
            Response data dict

        Raises:
            LndError: If the request fails
        """
        if not self._connected or not self._client:
            await self.connect()

        try:
            response = await self._client.request(
                method=method,
                url=path,
                json=json_data,
            )

            if response.status_code >= 400:
                error_text = response.text
                raise LndError(f"LND API error ({response.status_code}): {error_text}")

            if response.status_code == 204:
                return {}

            return response.json()

        except httpx.RequestError as e:
            raise LndError(f"Failed to connect to LND: {e!s}") from e

    async def pay_invoice(self, bolt11: str, amount_sats: int | None = None) -> str:
        """
        Pay a Lightning invoice via LND.

        LND ALWAYS returns the preimage - this is why it's ideal for L402.

        POST /v1/channels/transactions
        Request: {"payment_request": bolt11}
        Response: {"payment_preimage": "<base64>", "payment_error": "", "payment_hash": "<base64>"}

        Args:
            bolt11: BOLT11 invoice string
            amount_sats: Unused - LND uses the invoice amount

        Returns:
            Payment preimage as hex string

        Raises:
            LndPaymentError: If payment fails
        """
        if not self.is_configured:
            raise LndPaymentError(
                "LND not configured. Set LND_REST_HOST and LND_MACAROON_HEX environment variables."
            )

        logger.info(f"Paying invoice via LND: {bolt11[:30]}...")

        try:
            request_body = {"payment_request": bolt11}
            result = await self._request("POST", "channels/transactions", request_body)

            # Check for payment error
            payment_error = result.get("payment_error")
            if payment_error:
                logger.error(f"LND payment error: {payment_error}")
                raise LndPaymentError(f"Payment failed: {payment_error}")

            # LND returns preimage as base64 - convert to hex
            payment_preimage_b64 = result.get("payment_preimage")
            if payment_preimage_b64:
                preimage_bytes = base64.b64decode(payment_preimage_b64)
                preimage_hex = preimage_bytes.hex()
                logger.info("LND payment succeeded, preimage received")
                return preimage_hex

            raise LndPaymentError("Payment succeeded but no preimage returned")

        except LndError:
            raise
        except Exception as e:
            raise LndPaymentError(f"Payment failed: {e!s}") from e

    async def get_balance(self) -> int:
        """
        Get wallet Lightning channel balance in satoshis.

        GET /v1/balance/channels
        Response: {"local_balance": {"sat": "12345", "msat": "12345000"}, ...}

        Note: LND returns numbers as strings.

        Returns:
            Balance in satoshis
        """
        if not self.is_configured:
            raise LndError("LND not configured")

        try:
            result = await self._request("GET", "balance/channels")

            # local_balance.sat is spendable Lightning balance
            # LND returns numbers as strings
            local_balance = result.get("local_balance", {})
            balance_str = local_balance.get("sat", "0")
            balance_sats = int(balance_str)

            logger.info(f"LND balance: {balance_sats} sats")
            return balance_sats

        except LndError:
            raise
        except Exception as e:
            raise LndError(f"Failed to get LND balance: {e!s}") from e

    async def create_invoice(
        self,
        amount_sats: int,
        memo: str | None = None,
        expiry_secs: int = 3600,
    ) -> dict[str, Any]:
        """
        Create a Lightning invoice via LND.

        POST /v1/invoices
        Request: {"value": sats, "memo": "...", "expiry": secs}
        Response: {"r_hash": "<base64>", "payment_request": "lnbc..."}

        Args:
            amount_sats: Amount in satoshis
            memo: Optional invoice memo/description
            expiry_secs: Invoice expiry in seconds (default 3600)

        Returns:
            Dict with invoice_id (r_hash hex), bolt11 (payment_request), amount_sats, expires_at
        """
        if not self.is_configured:
            raise LndError(
                "LND not configured. Set LND_REST_HOST and LND_MACAROON_HEX environment variables."
            )

        logger.info(f"Creating LND invoice for {amount_sats} sats...")

        request_body: dict[str, Any] = {
            "value": str(amount_sats),
            "memo": memo or "Lightning payment",
            "expiry": str(expiry_secs),
        }

        result = await self._request("POST", "invoices", request_body)

        payment_request = result.get("payment_request")
        if not payment_request:
            raise LndError("No invoice returned from LND")

        # Convert r_hash from base64 to hex for invoice ID
        r_hash_b64 = result.get("r_hash", "")
        invoice_id = ""
        if r_hash_b64:
            r_hash_bytes = base64.b64decode(r_hash_b64)
            invoice_id = r_hash_bytes.hex()

        logger.info(f"LND invoice created: {invoice_id[:16]}...")

        return {
            "invoice_id": invoice_id,
            "bolt11": payment_request,
            "amount_sats": amount_sats,
            "expires_at": datetime.now(timezone.utc).timestamp() + expiry_secs,
        }

    async def get_invoice_status(self, invoice_id: str) -> dict[str, Any]:
        """
        Check the status of an invoice.

        GET /v1/invoice/{r_hash_hex}
        Response: {"state": "OPEN|SETTLED|CANCELED", "value": "1000", "settle_date": "1234567890"}

        Args:
            invoice_id: Invoice ID (r_hash in hex)

        Returns:
            Dict with id, state (PENDING/PAID/CANCELLED), amount_sats, settled_at
        """
        if not self.is_configured:
            raise LndError("LND not configured")

        result = await self._request("GET", f"invoice/{invoice_id}")

        # Map LND states to standard states
        lnd_state = (result.get("state") or "UNKNOWN").upper()
        state_map = {
            "OPEN": "PENDING",
            "SETTLED": "PAID",
            "CANCELED": "CANCELLED",
            "ACCEPTED": "PENDING",
        }
        state = state_map.get(lnd_state, lnd_state)

        # Parse amount (LND returns as string)
        value_str = result.get("value", "0")
        amount_sats = int(value_str) if value_str else 0

        # Parse settle date (LND returns as string unix timestamp)
        settled_at = None
        settle_date_str = result.get("settle_date")
        if settle_date_str:
            settle_ts = int(settle_date_str)
            if settle_ts > 0:
                settled_at = datetime.fromtimestamp(settle_ts, tz=timezone.utc).isoformat()

        return {
            "id": invoice_id,
            "state": state,
            "is_paid": state == "PAID",
            "is_pending": state == "PENDING",
            "amount_sats": amount_sats,
            "settled_at": settled_at,
        }

    async def send_onchain(self, address: str, amount_sats: int) -> LndOnChainResult:
        """
        Send an on-chain Bitcoin payment via LND.

        POST /v1/transactions
        Request: {"addr": address, "amount": sats, "target_conf": 6}
        Response: {"txid": "..."}

        Args:
            address: Bitcoin address (e.g., bc1q...)
            amount_sats: Amount in satoshis

        Returns:
            LndOnChainResult with payment details
        """
        if not self.is_configured:
            return LndOnChainResult.failed("NOT_CONFIGURED", "LND not configured")

        if not address:
            return LndOnChainResult.failed("INVALID_ADDRESS", "Bitcoin address is required")

        if amount_sats <= 0:
            return LndOnChainResult.failed("INVALID_AMOUNT", "Amount must be positive")

        try:
            logger.info(f"LND sending {amount_sats} sats on-chain to {address}...")

            request_body = {
                "addr": address,
                "amount": str(amount_sats),
                "target_conf": 6,  # Target 6 confirmations (~1 hour)
            }

            result = await self._request("POST", "transactions", request_body)
            txid = result.get("txid", "")

            logger.info(f"LND on-chain tx sent: {txid}")

            return LndOnChainResult.succeeded(
                payment_id=txid or "",
                txid=txid,
                state="PENDING",
                amount_sats=amount_sats,
                fee_sats=0,  # Fee will be in the tx details
            )

        except LndError as e:
            return LndOnChainResult.failed("API_ERROR", str(e))
        except Exception as e:
            return LndOnChainResult.failed("EXCEPTION", str(e))

    async def get_all_balances(self) -> dict[str, Any]:
        """
        Get all balances - LND is BTC-only (Lightning channels).

        Returns a dict with success, balances list, etc. matching the
        pattern used by StrikeWallet.get_all_balances().
        """
        try:
            balance_sats = await self.get_balance()
            balance_btc = Decimal(balance_sats) / Decimal("100000000")

            return {
                "success": True,
                "balances": [
                    {
                        "currency": "BTC",
                        "available": float(balance_btc),
                        "total": float(balance_btc),
                        "pending": 0,
                        "formatted": f"{balance_btc:.8f} BTC ({balance_sats:,} sats)",
                    }
                ],
                "provider": "LND",
                "message": f"Retrieved BTC balance from LND node ({balance_sats:,} sats)",
            }

        except Exception as e:
            return {
                "success": False,
                "error_code": "ERROR",
                "error_message": str(e),
            }

    async def get_info(self) -> dict[str, Any]:
        """
        Get wallet/node info.

        Returns:
            Dict with node info
        """
        try:
            result = await self._request("GET", "getinfo")
            return {
                "type": "lnd",
                "alias": result.get("alias"),
                "identity_pubkey": result.get("identity_pubkey"),
                "num_active_channels": result.get("num_active_channels"),
                "num_peers": result.get("num_peers"),
                "block_height": result.get("block_height"),
                "synced_to_chain": result.get("synced_to_chain"),
                "version": result.get("version"),
                "status": "connected",
                "preimage_support": True,
                "l402_compatible": True,
            }
        except Exception:
            return {
                "type": "lnd",
                "status": "connected" if self._connected else "disconnected",
                "preimage_support": True,
                "l402_compatible": True,
                "note": "LND always returns preimage. L402 fully supported.",
            }
