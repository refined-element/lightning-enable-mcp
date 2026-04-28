"""
Price Service

Fetches BTC/USD price from three independent public sources (CoinGecko,
Coinbase, Kraken) in parallel and returns the first successful response.

Design:
- 60-second cache: keeps price close to spot while absorbing burst traffic
  and avoiding rate-limit pressure on any single source.
- Parallel fetch on cache miss: a slow or failing source cannot hold up the
  others — the first success wins.
- No hardcoded fallback price. If every source fails, the service RAISES
  PriceUnavailableError. A wrong fake price would silently mis-evaluate
  budgets. Better to fail loud.
- Every fetch attempt is logged (source, latency, success/failure).
"""

from __future__ import annotations

import asyncio
import logging
import time
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from decimal import Decimal
from typing import Awaitable, Callable

try:
    import httpx
except ImportError:
    httpx = None  # type: ignore

logger = logging.getLogger("lightning-enable-mcp.price")

# Satoshis per Bitcoin
SATS_PER_BTC = Decimal("100000000")

# Cache duration — short, so price stays close to spot.
CACHE_DURATION = timedelta(seconds=60)

# Per-source timeout. A slow source must not block the others.
PER_SOURCE_TIMEOUT_SECONDS = 5.0


class PriceUnavailableError(Exception):
    """
    Raised when CoinGecko, Coinbase, and Kraken all fail and no recent
    cached value is available. The MCP refuses to fall back to a fake price
    because that would mis-evaluate budgets.
    """


@dataclass(frozen=True)
class PriceSnapshot:
    """A point-in-time price with provenance."""

    btc_usd: Decimal
    source: str
    fetched_at: datetime


class PriceService:
    """
    BTC/USD price service. Fetches from CoinGecko, Coinbase, Kraken in
    parallel; first success wins.

    Sources are intentionally limited to public, unauthenticated APIs that
    are widely trusted. Strike is NOT used here even when configured — it's
    surfaced separately via the explicit get_btc_price tool for Strike-only
    users.
    """

    def __init__(self) -> None:
        if httpx is None:
            raise ImportError(
                "httpx is required for PriceService. Install with: pip install httpx"
            )

        self._lock = asyncio.Lock()
        self._client: httpx.AsyncClient | None = None
        self._snapshot: PriceSnapshot | None = None

    async def _get_client(self) -> httpx.AsyncClient:
        """Get or create the shared HTTP client."""
        if self._client is None or self._client.is_closed:
            self._client = httpx.AsyncClient(
                timeout=PER_SOURCE_TIMEOUT_SECONDS,
                headers={
                    "Accept": "application/json",
                    "User-Agent": "lightning-enable-mcp/price",
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

        Returns the cached price if it's less than CACHE_DURATION old,
        otherwise fires CoinGecko/Coinbase/Kraken in parallel and returns
        the first successful response. Raises PriceUnavailableError if all
        sources fail.
        """
        snapshot = self._snapshot
        if snapshot is not None and datetime.now(timezone.utc) - snapshot.fetched_at < CACHE_DURATION:
            return snapshot.btc_usd

        async with self._lock:
            # Re-check after acquiring lock — another caller may have refreshed.
            snapshot = self._snapshot
            if snapshot is not None and datetime.now(timezone.utc) - snapshot.fetched_at < CACHE_DURATION:
                return snapshot.btc_usd

            fresh = await self._fetch_first_successful()
            self._snapshot = fresh
            return fresh.btc_usd

    async def sats_to_usd(self, sats: int) -> Decimal:
        """
        Convert satoshis to USD using the current BTC price.
        Raises PriceUnavailableError if the price cannot be fetched.
        """
        btc_price = await self.get_btc_price()
        usd = Decimal(sats) / SATS_PER_BTC * btc_price
        return round(usd, 2)

    async def usd_to_sats(self, usd: Decimal | float | int) -> int:
        """
        Convert USD to satoshis using the current BTC price (rounded up).
        Raises PriceUnavailableError if the price cannot be fetched.
        """
        import math

        btc_price = await self.get_btc_price()
        usd_decimal = Decimal(str(usd))
        sats = usd_decimal / btc_price * SATS_PER_BTC
        return math.ceil(sats)

    def get_last_snapshot(self) -> PriceSnapshot | None:
        """
        Returns the most recent successfully fetched snapshot (price + source +
        timestamp) without triggering a fresh fetch. None if no fetch has
        succeeded yet.
        """
        return self._snapshot

    def get_cached_btc_price(self) -> Decimal:
        """
        Returns the last successfully fetched price for synchronous callers.
        Returns Decimal("0") if no fetch has succeeded yet — callers must
        handle this case rather than relying on a hardcoded fake price.
        """
        if self._snapshot is None:
            return Decimal("0")
        return self._snapshot.btc_usd

    def get_cache_source(self) -> str | None:
        """The source of the cached price, or None if no fetch has succeeded."""
        return self._snapshot.source if self._snapshot else None

    def is_cache_valid(self) -> bool:
        """Whether the cached value is within CACHE_DURATION."""
        if self._snapshot is None:
            return False
        return datetime.now(timezone.utc) - self._snapshot.fetched_at < CACHE_DURATION

    # ── internals ────────────────────────────────────────────────────────────

    async def _fetch_first_successful(self) -> PriceSnapshot:
        """
        Fire CoinGecko, Coinbase, and Kraken in parallel; return the first
        successful PriceSnapshot. Raise PriceUnavailableError if all fail.
        """
        attempts: list[tuple[str, Callable[[], Awaitable[Decimal]]]] = [
            ("CoinGecko", self._fetch_coingecko),
            ("Coinbase", self._fetch_coinbase),
            ("Kraken", self._fetch_kraken),
        ]
        tasks = {asyncio.create_task(self._try(source, fetch)): source for source, fetch in attempts}

        winner: PriceSnapshot | None = None
        try:
            for completed in asyncio.as_completed(tasks):
                result = await completed
                if result is not None:
                    winner = result
                    break
        finally:
            for task in tasks:
                if not task.done():
                    task.cancel()
            # Drain cancelled tasks so they don't surface as warnings.
            for task in tasks:
                try:
                    await task
                except (asyncio.CancelledError, Exception):
                    pass

        if winner is not None:
            logger.info(
                "BTC price fetched: $%s from %s",
                f"{winner.btc_usd:,.2f}",
                winner.source,
            )
            return winner

        message = (
            "BTC price unavailable: CoinGecko, Coinbase, and Kraken all failed. "
            "Cannot evaluate budget safely."
        )
        logger.error(message)
        raise PriceUnavailableError(message)

    async def _try(
        self,
        source: str,
        fetch: Callable[[], Awaitable[Decimal]],
    ) -> PriceSnapshot | None:
        """Run a single source fetch with timing and error logging."""
        started = time.monotonic()
        try:
            price = await fetch()
            elapsed_ms = (time.monotonic() - started) * 1000
            if price <= 0:
                logger.warning(
                    "BTC price fetch from %s returned non-positive value %s after %.1fms",
                    source, price, elapsed_ms,
                )
                return None
            logger.debug("BTC price fetched from %s: $%s in %.1fms", source, price, elapsed_ms)
            return PriceSnapshot(
                btc_usd=price,
                source=source,
                fetched_at=datetime.now(timezone.utc),
            )
        except asyncio.CancelledError:
            # Sibling source already won — not a failure.
            raise
        except Exception as ex:
            elapsed_ms = (time.monotonic() - started) * 1000
            logger.warning(
                "BTC price fetch from %s failed after %.1fms: %s",
                source, elapsed_ms, ex,
            )
            return None

    async def _fetch_coingecko(self) -> Decimal:
        client = await self._get_client()
        response = await client.get(
            "https://api.coingecko.com/api/v3/simple/price",
            params={"ids": "bitcoin", "vs_currencies": "usd"},
        )
        response.raise_for_status()
        data = response.json()
        if "bitcoin" in data and "usd" in data["bitcoin"]:
            return Decimal(str(data["bitcoin"]["usd"]))
        raise ValueError("CoinGecko response missing bitcoin.usd")

    async def _fetch_coinbase(self) -> Decimal:
        client = await self._get_client()
        response = await client.get("https://api.coinbase.com/v2/prices/BTC-USD/spot")
        response.raise_for_status()
        data = response.json()
        if "data" in data and "amount" in data["data"]:
            return Decimal(str(data["data"]["amount"]))
        raise ValueError("Coinbase response missing data.amount")

    async def _fetch_kraken(self) -> Decimal:
        client = await self._get_client()
        response = await client.get(
            "https://api.kraken.com/0/public/Ticker",
            params={"pair": "XBTUSD"},
        )
        response.raise_for_status()
        data = response.json()

        errors = data.get("error") or []
        if errors:
            raise ValueError(f"Kraken error: {errors[0]}")

        result = data.get("result") or {}
        # Pair key is typically "XXBTZUSD" but may vary.
        for pair_data in result.values():
            close = pair_data.get("c")
            if isinstance(close, list) and close:
                return Decimal(str(close[0]))

        raise ValueError("Kraken response missing close price")


# Module-level singleton for convenience.
_default_service: PriceService | None = None


def get_price_service() -> PriceService:
    """Get the default price service singleton."""
    global _default_service
    if _default_service is None:
        _default_service = PriceService()
    return _default_service


async def get_btc_price() -> Decimal:
    """Get current BTC/USD price using the default service."""
    return await get_price_service().get_btc_price()


async def sats_to_usd(sats: int) -> Decimal:
    """Convert satoshis to USD using the default service."""
    return await get_price_service().sats_to_usd(sats)


async def usd_to_sats(usd: Decimal | float | int) -> int:
    """Convert USD to satoshis using the default service."""
    return await get_price_service().usd_to_sats(usd)
