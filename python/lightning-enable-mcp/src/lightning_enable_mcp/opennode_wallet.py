"""
OpenNode Wallet Client

Implements Lightning wallet operations via OpenNode API for paying invoices.
This is an alternative to NWC for environments where OpenNode is the primary
payment infrastructure.
"""

import logging
from dataclasses import dataclass
from typing import Any

try:
    import httpx
except ImportError:
    httpx = None  # type: ignore

logger = logging.getLogger("lightning-enable-mcp.opennode")


class OpenNodeError(Exception):
    """Exception for OpenNode-related errors."""
    pass


class OpenNodePaymentError(OpenNodeError):
    """Exception for payment failures."""
    pass


@dataclass
class OpenNodeConfig:
    """OpenNode configuration."""

    api_key: str
    environment: str = "production"  # "production" or "dev"

    @property
    def base_url(self) -> str:
        """Get the API base URL based on environment."""
        if self.environment in ("dev", "development", "testnet"):
            return "https://dev-api.opennode.com/v1"
        return "https://api.opennode.com/v1"


class OpenNodeWallet:
    """
    OpenNode wallet client for Lightning payments.

    This provides the same interface as NWCWallet but uses OpenNode's
    withdrawal API to pay invoices.
    """

    def __init__(
        self,
        api_key: str,
        environment: str = "production",
    ) -> None:
        """
        Initialize OpenNode wallet.

        Args:
            api_key: OpenNode API key with withdrawal permissions
            environment: "production" for mainnet, "dev" for testnet
        """
        if httpx is None:
            raise ImportError("httpx is required for OpenNodeWallet. Install with: pip install httpx")

        self.config = OpenNodeConfig(api_key=api_key, environment=environment)
        self._client: httpx.AsyncClient | None = None
        self._connected = False

    async def connect(self) -> None:
        """Initialize the HTTP client."""
        if self._connected:
            return

        self._client = httpx.AsyncClient(
            base_url=self.config.base_url,
            headers={
                "Authorization": self.config.api_key,
                "Content-Type": "application/json",
            },
            timeout=30.0,
        )
        self._connected = True
        logger.info(f"OpenNode wallet connected to {self.config.environment} environment")

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
        Make an API request to OpenNode.

        Args:
            method: HTTP method (GET, POST, etc.)
            path: API path (without base URL)
            json_data: Request body data

        Returns:
            Response data dict
        """
        if not self._connected or not self._client:
            await self.connect()

        try:
            response = await self._client.request(
                method=method,
                url=path,
                json=json_data,
            )

            data = response.json()

            if response.status_code >= 400:
                error_msg = data.get("message", str(response.status_code))
                raise OpenNodeError(f"API error: {error_msg}")

            return data.get("data", data)

        except httpx.RequestError as e:
            raise OpenNodeError(f"Request failed: {e!s}") from e

    async def pay_invoice(self, bolt11: str, amount_sats: int | None = None) -> str:
        """
        Pay a Lightning invoice via OpenNode withdrawal.

        Args:
            bolt11: BOLT11 invoice string
            amount_sats: Amount in satoshis (optional, uses invoice amount if not specified)

        Returns:
            Payment preimage as hex string (or withdrawal ID if preimage not available)

        Raises:
            OpenNodePaymentError: If payment fails
        """
        logger.info(f"Paying invoice via OpenNode: {bolt11[:30]}...")

        # Build withdrawal request
        payload: dict[str, Any] = {
            "type": "ln",  # Lightning Network
            "address": bolt11,
        }

        # Only include amount if specified (OpenNode will use invoice amount otherwise)
        if amount_sats and amount_sats > 0:
            payload["amount"] = amount_sats

        try:
            result = await self._request("POST", "/withdrawals", payload)

            status = result.get("status", "").lower()
            withdrawal_id = result.get("id", "unknown")

            logger.info(f"Withdrawal created: {withdrawal_id}, status: {status}")

            if status in ("paid", "confirmed", "completed"):
                # Payment successful
                # OpenNode may not return preimage, use withdrawal ID as fallback
                preimage = result.get("preimage") or result.get("reference") or withdrawal_id

                # Validate preimage - shouldn't be the invoice
                if isinstance(preimage, str) and preimage.startswith(("lnbc", "lntb")):
                    logger.warning("OpenNode returned invoice instead of preimage, using withdrawal ID")
                    preimage = withdrawal_id

                logger.info(f"Payment successful: {preimage}")
                return preimage

            elif status in ("pending", "processing"):
                # Payment in progress - this is normal for Lightning
                # Return the withdrawal ID for tracking
                logger.info(f"Payment processing: {withdrawal_id}")
                return withdrawal_id

            else:
                raise OpenNodePaymentError(
                    f"Payment failed with status: {status}. "
                    f"Details: {result}"
                )

        except OpenNodeError:
            raise
        except Exception as e:
            raise OpenNodePaymentError(f"Payment failed: {e!s}") from e

    async def get_balance(self) -> int:
        """
        Get wallet balance.

        Note: OpenNode may not provide real-time balance via API.
        This attempts to get account info or returns -1 if unavailable.

        Returns:
            Balance in satoshis, or -1 if unavailable
        """
        try:
            # OpenNode doesn't have a direct balance endpoint for merchant accounts
            # This would need to be checked via the dashboard or account API
            result = await self._request("GET", "/account/balance")
            balance_sats = result.get("balance", {}).get("BTC", 0)
            # Convert from BTC to sats if needed
            if isinstance(balance_sats, float) and balance_sats < 1:
                balance_sats = int(balance_sats * 100_000_000)
            return int(balance_sats)
        except OpenNodeError as e:
            logger.warning(f"Could not get balance: {e}")
            return -1

    async def get_info(self) -> dict[str, Any]:
        """
        Get wallet/account info.

        Returns:
            Account info dict
        """
        try:
            return await self._request("GET", "/account")
        except OpenNodeError:
            return {
                "type": "opennode",
                "environment": self.config.environment,
                "status": "connected",
            }

    async def get_withdrawal_status(self, withdrawal_id: str) -> dict[str, Any]:
        """
        Get the status of a withdrawal.

        Args:
            withdrawal_id: OpenNode withdrawal ID

        Returns:
            Withdrawal status dict
        """
        return await self._request("GET", f"/withdrawal/{withdrawal_id}")


# Factory function to create wallet from environment
def create_wallet_from_env() -> OpenNodeWallet | None:
    """
    Create an OpenNode wallet from environment variables.

    Environment variables:
        OPENNODE_API_KEY: Required - OpenNode API key
        OPENNODE_ENVIRONMENT: Optional - "production" or "dev" (default: production)

    Returns:
        OpenNodeWallet instance or None if not configured
    """
    import os

    api_key = os.getenv("OPENNODE_API_KEY")
    if not api_key:
        return None

    environment = os.getenv("OPENNODE_ENVIRONMENT", "production")

    return OpenNodeWallet(api_key=api_key, environment=environment)
