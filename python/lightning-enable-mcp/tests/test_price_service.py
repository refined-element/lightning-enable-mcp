"""
Tests for the rewritten PriceService.

Covers:
- Successful first-source path
- Fall-through when one source fails
- Fall-through to Kraken when both CoinGecko and Coinbase fail
- All-sources-fail raises PriceUnavailableError (no $100K fallback)
- Cache serves repeat calls within the 60s window
- Sats/USD conversion uses the fresh price
"""

from __future__ import annotations

from decimal import Decimal

import httpx
import pytest

from lightning_enable_mcp.price_service import (
    PriceService,
    PriceUnavailableError,
)


def _route(
    coingecko: tuple[int, dict] | None,
    coinbase: tuple[int, dict] | None,
    kraken: tuple[int, dict] | None,
):
    """Return an httpx.MockTransport handler that routes by hostname."""
    counts = {"coingecko": 0, "coinbase": 0, "kraken": 0}

    def handler(request: httpx.Request) -> httpx.Response:
        host = request.url.host
        if "coingecko" in host:
            counts["coingecko"] += 1
            if coingecko is None:
                return httpx.Response(503, json={"error": "unavailable"})
            return httpx.Response(coingecko[0], json=coingecko[1])
        if "coinbase" in host:
            counts["coinbase"] += 1
            if coinbase is None:
                return httpx.Response(503, json={"error": "unavailable"})
            return httpx.Response(coinbase[0], json=coinbase[1])
        if "kraken" in host:
            counts["kraken"] += 1
            if kraken is None:
                return httpx.Response(503, json={"error": "unavailable"})
            return httpx.Response(kraken[0], json=kraken[1])
        return httpx.Response(404, json={"error": "unrouted"})

    return handler, counts


def _build_service(
    *,
    coingecko: tuple[int, dict] | None = None,
    coinbase: tuple[int, dict] | None = None,
    kraken: tuple[int, dict] | None = None,
) -> tuple[PriceService, dict[str, int]]:
    """Build a PriceService backed by httpx.MockTransport."""
    handler, counts = _route(coingecko, coinbase, kraken)
    transport = httpx.MockTransport(handler)
    service = PriceService()
    # Inject a client that uses the mock transport.
    service._client = httpx.AsyncClient(transport=transport, timeout=5.0)
    return service, counts


def _coingecko_ok(price: Decimal) -> tuple[int, dict]:
    return 200, {"bitcoin": {"usd": float(price)}}


def _coinbase_ok(price: Decimal) -> tuple[int, dict]:
    return 200, {"data": {"amount": str(price), "base": "BTC", "currency": "USD"}}


def _kraken_ok(price: Decimal) -> tuple[int, dict]:
    return 200, {"error": [], "result": {"XXBTZUSD": {"c": [str(price), "0.001"]}}}


@pytest.mark.asyncio
async def test_returns_price_when_all_sources_succeed():
    service, _ = _build_service(
        coingecko=_coingecko_ok(Decimal("76800")),
        coinbase=_coinbase_ok(Decimal("76900")),
        kraken=_kraken_ok(Decimal("76700")),
    )

    price = await service.get_btc_price()

    # First source to complete wins. Assert in band.
    assert Decimal("76700") <= price <= Decimal("76900")
    snapshot = service.get_last_snapshot()
    assert snapshot is not None
    assert snapshot.source in {"CoinGecko", "Coinbase", "Kraken"}
    await service.close()


@pytest.mark.asyncio
async def test_falls_through_when_first_source_fails():
    service, _ = _build_service(
        coingecko=None,
        coinbase=_coinbase_ok(Decimal("76900")),
        kraken=None,
    )

    price = await service.get_btc_price()

    assert price == Decimal("76900")
    snapshot = service.get_last_snapshot()
    assert snapshot is not None and snapshot.source == "Coinbase"
    await service.close()


@pytest.mark.asyncio
async def test_uses_kraken_when_coingecko_and_coinbase_fail():
    service, _ = _build_service(
        coingecko=None,
        coinbase=None,
        kraken=_kraken_ok(Decimal("76700")),
    )

    price = await service.get_btc_price()

    assert price == Decimal("76700")
    snapshot = service.get_last_snapshot()
    assert snapshot is not None and snapshot.source == "Kraken"
    await service.close()


@pytest.mark.asyncio
async def test_raises_when_all_sources_fail():
    service, _ = _build_service()

    with pytest.raises(PriceUnavailableError, match="all failed"):
        await service.get_btc_price()
    await service.close()


@pytest.mark.asyncio
async def test_no_100k_fallback_when_all_sources_fail():
    """Regression test for the v1.12.3 bug: hardcoded $100,000 fallback."""
    service, _ = _build_service()

    with pytest.raises(PriceUnavailableError):
        await service.get_btc_price()

    # No snapshot should exist — no successful fetch happened.
    assert service.get_last_snapshot() is None
    # Sync accessor returns 0, not a fake price, so callers can detect it.
    assert service.get_cached_btc_price() == Decimal("0")
    await service.close()


@pytest.mark.asyncio
async def test_serves_from_cache_within_window():
    service, counts = _build_service(
        coingecko=_coingecko_ok(Decimal("76800")),
    )

    first = await service.get_btc_price()
    second = await service.get_btc_price()

    assert first == Decimal("76800")
    assert second == Decimal("76800")
    # Second call must hit the cache and not refetch any source.
    assert counts["coingecko"] == 1
    await service.close()


@pytest.mark.asyncio
async def test_sats_to_usd_uses_fresh_price():
    service, _ = _build_service(coingecko=_coingecko_ok(Decimal("76800")))

    usd = await service.sats_to_usd(100_000_000)  # 1 BTC

    assert usd == Decimal("76800.00")
    await service.close()


@pytest.mark.asyncio
async def test_usd_to_sats_uses_fresh_price():
    service, _ = _build_service(coingecko=_coingecko_ok(Decimal("80000")))

    sats = await service.usd_to_sats(Decimal("80"))  # $80 at $80k/BTC = 100k sats

    assert sats == 100_000
    await service.close()
