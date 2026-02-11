"""
Price Service

Provides BTC/USD price fetching with caching and conversion utilities.
Fetches prices from multiple sources (CoinGecko, Coinbase, Strike) with
automatic fallback on API failures.
"""

import asyncio
import logging
import os
from dataclasses import dataclass
from datetime import datetime, timedelta
from decimal import Decimal
from typing import Any

try:
    import httpx
except ImportError:
    httpx = None  # type: ignore

logger = logging.getLogger("lightning-enable-mcp.price")

# Satoshis per Bitcoin
SATS_PER_BTC = Decimal("100000000")

# Default fallback price if all sources fail (conservative estimate)
DEFAULT_FALLBACK_PRICE = Decimal("100000")

# Default cache duration
DEFAULT_CACHE_DURATION_MINUTES = 15


class PriceServiceError(Exception):
    """Exception for price service errors."""

    pass


@dataclass
class PriceResult:
    """Result of a price fetch operation."""

    success: bool
    price: Decimal | None = None
    source: str | None = None
    error_message: str | None = None

    @classmethod
    def succeeded(cls, price: Decimal, source: str) -> "PriceResult":
        return cls(success=True, price=price, source=source)

    @classmethod
    def failed(cls, message: str) -> "PriceResult":
        return cls(success=False, error_message=message)


class PriceService:
    """
    Price service that fetches BTC/USD from multiple sources with caching.

    Provides methods for:
    - Getting current BTC/USD price
    - Converting satoshis to USD
    - Converting USD to satoshis

    Sources are tried in order:
    1. Strike (if API key is configured)
    2. CoinGecko (free, no API key required)
    3. Coinbase (free, no API key required)

    Prices are cached for 15 minutes by default to avoid rate limits.
    """

    def __init__(
        self,
        cache_duration_minutes: int = DEFAULT_CACHE_DURATION_MINUTES,
        fallback_price: Decimal | None = None,
    ) -> None:
        """
        Initialize the price service.

        Args:
            cache_duration_minutes: How long to cache prices (default 15 minutes)
            fallback_price: Price to use if all sources fail (default $100,000)
        """
        if httpx is None:
            raise ImportError(
                "httpx is required for PriceService. Install with: pip install httpx"
            )

        self._cache_duration = timedelta(minutes=cache_duration_minutes)
        self._fallback_price = fallback_price or DEFAULT_FALLBACK_PRICE
        self._cached_price: Decimal = self._fallback_price
        self._cache_expiry: datetime = datetime.min
        self._cache_source: str | None = None
        self._lock = asyncio.Lock()
        self._client: httpx.AsyncClient | None = None

    async def _get_client(self) -> httpx.AsyncClient:
        """Get or create the HTTP client."""
        if self._client is None or self._client.is_closed:
            self._client = httpx.AsyncClient(
                timeout=10.0,
                headers={
                    "Accept": "application/json",
                    "User-Agent": "lightning-enable-mcp/1.0",
                },
            )
        return self._client

    async def close(self) -> None:
        """Close the HTTP client."""
        if self._client and not self._client.is_closed:
            await self._client.aclose()
            self._client = None

    async def get_btc_price(self) -> Decimal:
        """
        Get the current BTC/USD price.

        Returns cached price if available and not expired.
        Tries multiple sources if cache is expired.
        Returns fallback price if all sources fail.

        Returns:
            BTC/USD price as Decimal
        """
        # Check cache first (without lock for performance)
        now = datetime.utcnow()
        if now < self._cache_expiry:
            return self._cached_price

        # Acquire lock and double-check cache
        async with self._lock:
            now = datetime.utcnow()
            if now < self._cache_expiry:
                return self._cached_price

            # Try to fetch new price
            result = await self._try_get_price()

            if result.success and result.price is not None and result.price > 0:
                self._cached_price = result.price
                self._cache_expiry = now + self._cache_duration
                self._cache_source = result.source
                logger.info(f"Updated BTC price to ${result.price:,.2f} from {result.source}")
                return self._cached_price

            # Return cached price even if expired, or fallback
            if self._cached_price > 0:
                logger.warning(
                    f"Price fetch failed, using cached price: ${self._cached_price:,.2f}"
                )
                return self._cached_price

            logger.warning(
                f"Price fetch failed, using fallback price: ${self._fallback_price:,.2f}"
            )
            return self._fallback_price

    async def sats_to_usd(self, sats: int) -> Decimal:
        """
        Convert satoshis to USD using current BTC price.

        Args:
            sats: Amount in satoshis

        Returns:
            USD value rounded to 2 decimal places
        """
        btc_price = await self.get_btc_price()
        btc = Decimal(sats) / SATS_PER_BTC
        usd = btc * btc_price
        return round(usd, 2)

    async def usd_to_sats(self, usd: Decimal | float | int) -> int:
        """
        Convert USD to satoshis using current BTC price.

        Args:
            usd: Amount in USD

        Returns:
            Equivalent amount in satoshis (rounded up)
        """
        btc_price = await self.get_btc_price()
        usd_decimal = Decimal(str(usd))
        btc = usd_decimal / btc_price
        sats = btc * SATS_PER_BTC
        # Round up to ensure we always have enough sats
        import math

        return math.ceil(sats)

    def get_cached_btc_price(self) -> Decimal:
        """
        Get the cached BTC price without fetching.

        Returns the last successfully fetched price, or fallback if none available.
        Useful for synchronous contexts where async is not possible.

        Returns:
            Cached BTC/USD price
        """
        return self._cached_price if self._cached_price > 0 else self._fallback_price

    def get_cache_source(self) -> str | None:
        """Get the source of the cached price."""
        return self._cache_source

    def is_cache_valid(self) -> bool:
        """Check if the cache is still valid."""
        return datetime.utcnow() < self._cache_expiry

    async def _try_get_price(self) -> PriceResult:
        """Try to get price from various sources in order."""
        # Try Strike first if API key is configured
        strike_api_key = os.getenv("STRIKE_API_KEY")
        if strike_api_key:
            result = await self._try_strike_price(strike_api_key)
            if result.success:
                return result

        # Try CoinGecko (free, no API key)
        result = await self._try_coingecko_price()
        if result.success:
            return result

        # Try Coinbase as fallback
        result = await self._try_coinbase_price()
        if result.success:
            return result

        return PriceResult.failed("All price sources failed")

    async def _try_strike_price(self, api_key: str) -> PriceResult:
        """
        Fetch BTC/USD price from Strike.

        Args:
            api_key: Strike API key

        Returns:
            PriceResult with price or error
        """
        try:
            client = await self._get_client()
            response = await client.get(
                "https://api.strike.me/v1/rates/ticker",
                headers={"Authorization": f"Bearer {api_key}"},
            )

            if response.status_code != 200:
                return PriceResult.failed(f"Strike API error: {response.status_code}")

            data = response.json()

            # Strike returns an array of rate objects
            for rate in data:
                source = rate.get("sourceCurrency", "").upper()
                target = rate.get("targetCurrency", "").upper()

                if source == "BTC" and target == "USD":
                    amount = rate.get("amount")
                    if amount:
                        price = Decimal(str(amount))
                        return PriceResult.succeeded(price, "strike")

            return PriceResult.failed("BTC/USD rate not found in Strike response")

        except httpx.RequestError as e:
            logger.debug(f"Strike price fetch failed: {e}")
            return PriceResult.failed(f"Strike request error: {e!s}")
        except Exception as e:
            logger.debug(f"Strike price parse failed: {e}")
            return PriceResult.failed(f"Strike parse error: {e!s}")

    async def _try_coingecko_price(self) -> PriceResult:
        """
        Fetch BTC/USD price from CoinGecko.

        Returns:
            PriceResult with price or error
        """
        try:
            client = await self._get_client()
            response = await client.get(
                "https://api.coingecko.com/api/v3/simple/price",
                params={"ids": "bitcoin", "vs_currencies": "usd"},
            )

            if response.status_code != 200:
                return PriceResult.failed(f"CoinGecko API error: {response.status_code}")

            data = response.json()

            if "bitcoin" in data and "usd" in data["bitcoin"]:
                price = Decimal(str(data["bitcoin"]["usd"]))
                return PriceResult.succeeded(price, "coingecko")

            return PriceResult.failed("BTC/USD not found in CoinGecko response")

        except httpx.RequestError as e:
            logger.debug(f"CoinGecko price fetch failed: {e}")
            return PriceResult.failed(f"CoinGecko request error: {e!s}")
        except Exception as e:
            logger.debug(f"CoinGecko price parse failed: {e}")
            return PriceResult.failed(f"CoinGecko parse error: {e!s}")

    async def _try_coinbase_price(self) -> PriceResult:
        """
        Fetch BTC/USD price from Coinbase.

        Returns:
            PriceResult with price or error
        """
        try:
            client = await self._get_client()
            response = await client.get(
                "https://api.coinbase.com/v2/prices/BTC-USD/spot"
            )

            if response.status_code != 200:
                return PriceResult.failed(f"Coinbase API error: {response.status_code}")

            data = response.json()

            if "data" in data and "amount" in data["data"]:
                amount = data["data"]["amount"]
                price = Decimal(str(amount))
                return PriceResult.succeeded(price, "coinbase")

            return PriceResult.failed("BTC/USD not found in Coinbase response")

        except httpx.RequestError as e:
            logger.debug(f"Coinbase price fetch failed: {e}")
            return PriceResult.failed(f"Coinbase request error: {e!s}")
        except Exception as e:
            logger.debug(f"Coinbase price parse failed: {e}")
            return PriceResult.failed(f"Coinbase parse error: {e!s}")


# Module-level singleton for convenience
_default_service: PriceService | None = None


def get_price_service() -> PriceService:
    """
    Get the default price service singleton.

    Returns:
        PriceService instance
    """
    global _default_service
    if _default_service is None:
        _default_service = PriceService()
    return _default_service


async def get_btc_price() -> Decimal:
    """
    Get current BTC/USD price using the default service.

    Returns:
        BTC/USD price
    """
    service = get_price_service()
    return await service.get_btc_price()


async def sats_to_usd(sats: int) -> Decimal:
    """
    Convert satoshis to USD using the default service.

    Args:
        sats: Amount in satoshis

    Returns:
        USD value rounded to 2 decimal places
    """
    service = get_price_service()
    return await service.sats_to_usd(sats)


async def usd_to_sats(usd: Decimal | float | int) -> int:
    """
    Convert USD to satoshis using the default service.

    Args:
        usd: Amount in USD

    Returns:
        Equivalent amount in satoshis
    """
    service = get_price_service()
    return await service.usd_to_sats(usd)
