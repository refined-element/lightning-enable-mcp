"""
Strike Wallet Client

Implements Lightning wallet operations via Strike's REST API.
Allows users to pay Lightning invoices from their Strike balance.

Strike returns preimage via lightning.preImage on execute-payment-quote response.
This enables full L402 authentication support.
Source currency should be set to BTC to pay from BTC balance.

Configuration: Set STRIKE_API_KEY environment variable.
Get your API key from: https://dashboard.strike.me/
"""

import logging
import asyncio
from dataclasses import dataclass, field
from typing import Any
from decimal import Decimal

try:
    import httpx
except ImportError:
    httpx = None  # type: ignore

logger = logging.getLogger("lightning-enable-mcp.strike")

BASE_URL = "https://api.strike.me/v1"


class StrikeError(Exception):
    """Exception for Strike-related errors."""
    pass


class StrikePaymentError(StrikeError):
    """Exception for payment failures."""
    pass


@dataclass
class StrikeConfig:
    """Strike configuration."""
    api_key: str


@dataclass
class CurrencyBalance:
    """Balance for a single currency."""
    currency: str
    available: Decimal
    total: Decimal
    pending: Decimal = Decimal("0")


@dataclass
class MultiCurrencyBalance:
    """Multi-currency balance result."""
    success: bool
    balances: list[CurrencyBalance] = field(default_factory=list)
    error_code: str | None = None
    error_message: str | None = None

    @classmethod
    def succeeded(cls, balances: list[CurrencyBalance]) -> "MultiCurrencyBalance":
        return cls(success=True, balances=balances)

    @classmethod
    def failed(cls, code: str, message: str) -> "MultiCurrencyBalance":
        return cls(success=False, error_code=code, error_message=message)


@dataclass
class TickerResult:
    """BTC price ticker result."""
    success: bool
    btc_usd_price: Decimal | None = None
    error_code: str | None = None
    error_message: str | None = None

    @classmethod
    def succeeded(cls, price: Decimal) -> "TickerResult":
        return cls(success=True, btc_usd_price=price)

    @classmethod
    def failed(cls, code: str, message: str) -> "TickerResult":
        return cls(success=False, error_code=code, error_message=message)


@dataclass
class ExchangeResult:
    """Currency exchange result."""
    success: bool
    exchange_id: str | None = None
    source_currency: str | None = None
    target_currency: str | None = None
    source_amount: Decimal | None = None
    target_amount: Decimal | None = None
    rate: Decimal | None = None
    fee: Decimal | None = None
    state: str | None = None
    error_code: str | None = None
    error_message: str | None = None

    @classmethod
    def succeeded(
        cls,
        exchange_id: str,
        source_currency: str,
        target_currency: str,
        source_amount: Decimal,
        target_amount: Decimal,
        rate: Decimal | None = None,
        fee: Decimal | None = None,
        state: str = "COMPLETED",
    ) -> "ExchangeResult":
        return cls(
            success=True,
            exchange_id=exchange_id,
            source_currency=source_currency,
            target_currency=target_currency,
            source_amount=source_amount,
            target_amount=target_amount,
            rate=rate,
            fee=fee,
            state=state,
        )

    @classmethod
    def failed(cls, code: str, message: str) -> "ExchangeResult":
        return cls(success=False, error_code=code, error_message=message)


@dataclass
class OnChainResult:
    """On-chain payment result."""
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
    ) -> "OnChainResult":
        return cls(
            success=True,
            payment_id=payment_id,
            txid=txid,
            state=state,
            amount_sats=amount_sats,
            fee_sats=fee_sats,
        )

    @classmethod
    def failed(cls, code: str, message: str) -> "OnChainResult":
        return cls(success=False, error_code=code, error_message=message)


class StrikeWallet:
    """
    Strike wallet client for Lightning payments.

    Provides the same core interface as NWCWallet and OpenNodeWallet but uses
    Strike's API. Also supports Strike-specific features like multi-currency
    balances, exchange, and on-chain payments.

    Strike returns preimage via lightning.preImage on execute-payment-quote.
    L402 authentication is fully supported with Strike.
    Source currency should be BTC to pay from BTC balance.
    """

    def __init__(self, api_key: str) -> None:
        """
        Initialize Strike wallet.

        Args:
            api_key: Strike API key from dashboard.strike.me
        """
        if httpx is None:
            raise ImportError(
                "httpx is required for StrikeWallet. Install with: pip install httpx"
            )

        self.config = StrikeConfig(api_key=api_key)
        self._client: httpx.AsyncClient | None = None
        self._connected = False

    async def connect(self) -> None:
        """Initialize the HTTP client."""
        if self._connected:
            return

        self._client = httpx.AsyncClient(
            base_url=BASE_URL,
            headers={
                "Authorization": f"Bearer {self.config.api_key}",
                "Content-Type": "application/json",
                "Accept": "application/json",
            },
            timeout=30.0,
        )
        self._connected = True
        logger.info("Strike wallet connected")

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
        Make an API request to Strike.

        Args:
            method: HTTP method (GET, POST, PATCH, etc.)
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

            if response.status_code >= 400:
                error_text = response.text
                raise StrikeError(f"API error ({response.status_code}): {error_text}")

            if response.status_code == 204:  # No content
                return {}

            return response.json()

        except httpx.RequestError as e:
            raise StrikeError(f"Request failed: {e!s}") from e

    async def pay_invoice(self, bolt11: str, amount_sats: int | None = None) -> str:
        """
        Pay a Lightning invoice via Strike.

        Strike returns preimage via lightning.preImage on execute-payment-quote.
        Source currency is BTC to pay from BTC balance.

        Args:
            bolt11: BOLT11 invoice string
            amount_sats: Unused - Strike uses the invoice amount

        Returns:
            Payment preimage (hex) for L402 verification

        Raises:
            StrikePaymentError: If payment fails
        """
        logger.info(f"Paying invoice via Strike: {bolt11[:30]}...")

        try:
            # Step 1: Create payment quote
            quote_request = {
                "lnInvoice": bolt11,
                "sourceCurrency": "USD",
            }

            quote = await self._request("POST", "/payment-quotes/lightning", quote_request)
            quote_id = quote.get("paymentQuoteId")

            if not quote_id:
                raise StrikePaymentError("No payment quote ID returned")

            logger.info(f"Quote created: {quote_id}")

            # Step 2: Execute the payment
            payment = await self._request("PATCH", f"/payment-quotes/{quote_id}/execute")
            payment_id = payment.get("paymentId", quote_id)
            state = payment.get("state", "UNKNOWN")

            logger.info(f"Payment executed: {payment_id}, state: {state}")

            # Step 3: Poll for completion if pending
            if state == "PENDING":
                payment = await self._wait_for_payment(payment_id, timeout_secs=60)
                state = payment.get("state", "UNKNOWN")

            if state == "COMPLETED":
                logger.info(f"Payment completed: {payment_id}")
                return payment_id

            raise StrikePaymentError(f"Payment failed with state: {state}")

        except StrikeError:
            raise
        except Exception as e:
            raise StrikePaymentError(f"Payment failed: {e!s}") from e

    async def _wait_for_payment(
        self, payment_id: str, timeout_secs: int = 60
    ) -> dict[str, Any]:
        """Poll for payment completion."""
        end_time = asyncio.get_event_loop().time() + timeout_secs
        poll_interval = 2.0

        while asyncio.get_event_loop().time() < end_time:
            try:
                payment = await self._request("GET", f"/payments/{payment_id}")
                if payment.get("state") != "PENDING":
                    return payment
            except StrikeError:
                pass

            await asyncio.sleep(poll_interval)

        return {"paymentId": payment_id, "state": "TIMEOUT"}

    async def get_balance(self) -> int:
        """
        Get wallet BTC balance in satoshis.

        Returns:
            Balance in satoshis
        """
        try:
            balances = await self._request("GET", "/balances")

            for balance in balances:
                if balance.get("currency", "").upper() == "BTC":
                    current = balance.get("current") or balance.get("available")
                    if current:
                        btc = Decimal(str(current))
                        sats = int(btc * 100_000_000)
                        logger.info(f"Strike balance: {sats} sats")
                        return sats

            return 0

        except StrikeError as e:
            logger.warning(f"Could not get balance: {e}")
            return -1

    async def get_info(self) -> dict[str, Any]:
        """
        Get wallet info.

        Returns:
            Wallet info dict
        """
        return {
            "type": "strike",
            "status": "connected" if self._connected else "disconnected",
            "preimage_support": True,
            "l402_compatible": True,
            "note": "Strike returns preimage via lightning.preImage. L402 supported.",
        }

    # ===== Strike-Specific Features =====

    async def get_all_balances(self) -> MultiCurrencyBalance:
        """
        Get all currency balances (USD, BTC, etc.).

        Returns:
            MultiCurrencyBalance with all currency balances
        """
        try:
            balances_data = await self._request("GET", "/balances")

            balances = []
            for b in balances_data:
                currency = b.get("currency", "").upper()
                if not currency:
                    continue

                available = Decimal(str(b.get("available") or b.get("current") or "0"))
                total = Decimal(str(b.get("total") or available))
                pending = Decimal(str(b.get("pending") or "0"))

                balances.append(
                    CurrencyBalance(
                        currency=currency,
                        available=available,
                        total=total,
                        pending=pending,
                    )
                )

            logger.info(f"Retrieved {len(balances)} currency balances")
            return MultiCurrencyBalance.succeeded(balances)

        except StrikeError as e:
            return MultiCurrencyBalance.failed("API_ERROR", str(e))

    async def get_btc_price(self) -> TickerResult:
        """
        Get current BTC/USD price from Strike.

        Returns:
            TickerResult with BTC/USD price
        """
        try:
            tickers = await self._request("GET", "/rates/ticker")

            for ticker in tickers:
                source = ticker.get("sourceCurrency", "").upper()
                target = ticker.get("targetCurrency", "").upper()
                if source == "BTC" and target == "USD":
                    amount = ticker.get("amount")
                    if amount:
                        price = Decimal(str(amount))
                        logger.info(f"BTC/USD: ${price:,.2f}")
                        return TickerResult.succeeded(price)

            return TickerResult.failed("NO_RATE", "BTC/USD rate not found")

        except StrikeError as e:
            return TickerResult.failed("API_ERROR", str(e))

    async def exchange_currency(
        self,
        source_currency: str,
        target_currency: str,
        amount: Decimal,
    ) -> ExchangeResult:
        """
        Exchange currency (USD to BTC or BTC to USD).

        Args:
            source_currency: Currency to sell (USD or BTC)
            target_currency: Currency to buy (BTC or USD)
            amount: Amount in source currency

        Returns:
            ExchangeResult with exchange details
        """
        source_currency = source_currency.upper()
        target_currency = target_currency.upper()

        if source_currency not in ("USD", "BTC") or target_currency not in ("USD", "BTC"):
            return ExchangeResult.failed(
                "INVALID_CURRENCY", "Strike only supports USD and BTC exchange"
            )

        if source_currency == target_currency:
            return ExchangeResult.failed(
                "SAME_CURRENCY", "Source and target currency must be different"
            )

        if amount <= 0:
            return ExchangeResult.failed("INVALID_AMOUNT", "Amount must be positive")

        try:
            # Create exchange quote
            quote_request = {
                "sell": source_currency,
                "buy": target_currency,
                "amount": {"currency": source_currency, "amount": str(amount)},
            }

            logger.info(f"Creating exchange: {amount} {source_currency} -> {target_currency}")

            quote = await self._request("POST", "/currency-exchange-quotes", quote_request)
            quote_id = quote.get("id")

            if not quote_id:
                return ExchangeResult.failed("INVALID_QUOTE", "No quote ID returned")

            # Execute exchange
            result = await self._request(
                "PATCH", f"/currency-exchange-quotes/{quote_id}/execute"
            )

            source_amt = Decimal("0")
            target_amt = Decimal("0")
            fee = None

            if result.get("sourceAmount", {}).get("amount"):
                source_amt = Decimal(str(result["sourceAmount"]["amount"]))
            if result.get("targetAmount", {}).get("amount"):
                target_amt = Decimal(str(result["targetAmount"]["amount"]))
            if result.get("fee", {}).get("amount"):
                fee = Decimal(str(result["fee"]["amount"]))

            rate = target_amt / source_amt if source_amt > 0 else None

            logger.info(f"Exchange completed: {source_amt} {source_currency} -> {target_amt} {target_currency}")

            return ExchangeResult.succeeded(
                exchange_id=result.get("id", quote_id),
                source_currency=source_currency,
                target_currency=target_currency,
                source_amount=source_amt,
                target_amount=target_amt,
                rate=rate,
                fee=fee,
                state=result.get("state", "COMPLETED"),
            )

        except StrikeError as e:
            return ExchangeResult.failed("API_ERROR", str(e))

    async def send_onchain(self, address: str, amount_sats: int) -> OnChainResult:
        """
        Send an on-chain Bitcoin payment.

        Args:
            address: Bitcoin address (e.g., bc1q...)
            amount_sats: Amount in satoshis

        Returns:
            OnChainResult with payment details
        """
        if not address:
            return OnChainResult.failed("INVALID_ADDRESS", "Bitcoin address is required")

        if amount_sats <= 0:
            return OnChainResult.failed("INVALID_AMOUNT", "Amount must be positive")

        try:
            # Convert sats to BTC
            amount_btc = Decimal(amount_sats) / Decimal("100000000")

            quote_request = {
                "btcAddress": address,
                "sourceCurrency": "USD",
                "sourceAmount": {"currency": "BTC", "amount": str(amount_btc)},
            }

            logger.info(f"Creating on-chain quote: {amount_sats} sats to {address}")

            quote = await self._request("POST", "/payment-quotes/onchain", quote_request)
            quote_id = quote.get("paymentQuoteId")

            if not quote_id:
                return OnChainResult.failed("INVALID_QUOTE", "No quote ID returned")

            # Execute payment
            payment = await self._request("PATCH", f"/payment-quotes/{quote_id}/execute")
            payment_id = payment.get("paymentId", quote_id)
            state = payment.get("state", "UNKNOWN")

            # Poll for completion if pending
            if state == "PENDING":
                payment = await self._wait_for_payment(payment_id, timeout_secs=120)
                state = payment.get("state", "UNKNOWN")

            # Get fee from quote
            fee_sats = 0
            if quote.get("onchainFee", {}).get("amount"):
                fee_btc = Decimal(str(quote["onchainFee"]["amount"]))
                fee_currency = quote.get("onchainFee", {}).get("currency", "BTC")
                if fee_currency.upper() == "BTC":
                    fee_sats = int(fee_btc * 100_000_000)

            logger.info(f"On-chain payment: {payment_id}, state: {state}")

            return OnChainResult.succeeded(
                payment_id=payment_id,
                state=state,
                amount_sats=amount_sats,
                fee_sats=fee_sats,
            )

        except StrikeError as e:
            return OnChainResult.failed("API_ERROR", str(e))


def create_wallet_from_env() -> StrikeWallet | None:
    """
    Create a Strike wallet from environment variables.

    Environment variables:
        STRIKE_API_KEY: Required - Strike API key from dashboard.strike.me

    Returns:
        StrikeWallet instance or None if not configured
    """
    import os

    api_key = os.getenv("STRIKE_API_KEY")
    if not api_key:
        return None

    return StrikeWallet(api_key=api_key)
