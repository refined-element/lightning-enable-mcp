using System.Net;
using System.Reflection;
using System.Text;
using LightningEnable.Mcp.Services;

namespace LightningEnable.Mcp.Tests.Services;

public class PriceServiceTests
{
    [Fact]
    public async Task GetBtcPriceAsync_ReturnsValidPrice_WhenAllSourcesSucceed()
    {
        var handler = new FakeHttpHandler();
        handler.SetCoinGeckoResponse(76800m);
        handler.SetCoinbaseResponse(76900m);
        handler.SetKrakenResponse(76700m);

        using var http = new HttpClient(handler);
        var service = new PriceService(http);

        var price = await service.GetBtcPriceAsync();

        // Any of the three may complete first under parallel fetch — assert
        // it landed within the price band each fake returned.
        price.Should().BeInRange(76700m, 76900m);

        var snapshot = service.GetLastSnapshot();
        snapshot.Should().NotBeNull();
        snapshot!.Source.Should().BeOneOf("CoinGecko", "Coinbase", "Kraken");
    }

    [Fact]
    public async Task GetBtcPriceAsync_FallsThrough_WhenFirstSourceFails()
    {
        var handler = new FakeHttpHandler();
        handler.SetCoinGeckoFailure();
        handler.SetCoinbaseResponse(76900m);
        handler.SetKrakenFailure();

        using var http = new HttpClient(handler);
        var service = new PriceService(http);

        var price = await service.GetBtcPriceAsync();

        price.Should().Be(76900m);
        service.GetLastSnapshot()!.Source.Should().Be("Coinbase");
    }

    [Fact]
    public async Task GetBtcPriceAsync_UsesKraken_WhenCoinGeckoAndCoinbaseFail()
    {
        var handler = new FakeHttpHandler();
        handler.SetCoinGeckoFailure();
        handler.SetCoinbaseFailure();
        handler.SetKrakenResponse(76700m);

        using var http = new HttpClient(handler);
        var service = new PriceService(http);

        var price = await service.GetBtcPriceAsync();

        price.Should().Be(76700m);
        service.GetLastSnapshot()!.Source.Should().Be("Kraken");
    }

    [Fact]
    public async Task GetBtcPriceAsync_Throws_WhenAllSourcesFail()
    {
        var handler = new FakeHttpHandler();
        handler.SetCoinGeckoFailure();
        handler.SetCoinbaseFailure();
        handler.SetKrakenFailure();

        using var http = new HttpClient(handler);
        var service = new PriceService(http);

        var act = async () => await service.GetBtcPriceAsync();

        await act.Should().ThrowAsync<PriceUnavailableException>()
            .WithMessage("*all failed*");
    }

    [Fact]
    public async Task GetBtcPriceAsync_DoesNotFallBackTo100K_WhenAllSourcesFail()
    {
        // Regression test for the v1.12.3 bug: a hardcoded $100,000 fallback
        // silently inflated USD conversions when sources failed.
        var handler = new FakeHttpHandler();
        handler.SetCoinGeckoFailure();
        handler.SetCoinbaseFailure();
        handler.SetKrakenFailure();

        using var http = new HttpClient(handler);
        var service = new PriceService(http);

        var act = async () => await service.GetBtcPriceAsync();

        await act.Should().ThrowAsync<PriceUnavailableException>();
        service.GetLastSnapshot().Should().BeNull(
            because: "no successful fetch happened — there must be no snapshot to surface");
    }

    [Fact]
    public async Task GetBtcPriceAsync_ServesFromCache_WithinWindow()
    {
        var handler = new FakeHttpHandler();
        handler.SetCoinGeckoResponse(76800m);
        handler.SetCoinbaseFailure();
        handler.SetKrakenFailure();

        using var http = new HttpClient(handler);
        var service = new PriceService(http);

        var first = await service.GetBtcPriceAsync();
        var second = await service.GetBtcPriceAsync();

        first.Should().Be(76800m);
        second.Should().Be(76800m);
        handler.CoinGeckoCallCount.Should().Be(1, because: "the second call must hit the cache");
    }

    [Fact]
    public async Task SatsToUsdAsync_UsesFreshPrice()
    {
        var handler = new FakeHttpHandler();
        handler.SetCoinGeckoResponse(76800m);
        handler.SetCoinbaseFailure();
        handler.SetKrakenFailure();

        using var http = new HttpClient(handler);
        var service = new PriceService(http);

        var usd = await service.SatsToUsdAsync(100_000_000); // 1 BTC

        usd.Should().Be(76800m);
    }

    [Fact]
    public async Task UsdToSatsAsync_UsesFreshPrice()
    {
        var handler = new FakeHttpHandler();
        handler.SetCoinGeckoResponse(80000m);
        handler.SetCoinbaseFailure();
        handler.SetKrakenFailure();

        using var http = new HttpClient(handler);
        var service = new PriceService(http);

        var sats = await service.UsdToSatsAsync(80m); // $80 at $80k/BTC = 100k sats

        sats.Should().Be(100_000);
    }

    [Fact]
    public async Task GetBtcPriceAsync_PropagatesCallerCancellation_AsOperationCanceledException()
    {
        // Caller cancellation must surface as OperationCanceledException, not
        // be silently turned into a phantom PriceUnavailableException.
        var handler = new FakeHttpHandler();
        handler.SetSlowResponseForAllSources(TimeSpan.FromSeconds(10));

        using var http = new HttpClient(handler);
        var service = new PriceService(http);

        using var cts = new CancellationTokenSource();
        var task = service.GetBtcPriceAsync(cts.Token);
        cts.Cancel();

        var act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetBtcPriceAsync_AllFailMessage_IncludesPerSourceReasons()
    {
        // The PriceUnavailableException must enumerate why each source failed,
        // so operators can debug without a separate log dive.
        var handler = new FakeHttpHandler();
        handler.SetCoinGeckoFailure();
        handler.SetCoinbaseFailure();
        handler.SetKrakenFailure();

        using var http = new HttpClient(handler);
        var service = new PriceService(http);

        var act = async () => await service.GetBtcPriceAsync();

        var ex = await act.Should().ThrowAsync<PriceUnavailableException>();
        ex.Which.Message.Should().Contain("CoinGecko");
        ex.Which.Message.Should().Contain("Coinbase");
        ex.Which.Message.Should().Contain("Kraken");
    }

    [Fact]
    public async Task GetBtcPriceAsync_StaleCache_IsNeverServed_ThrowsInstead()
    {
        // Pins the fail-closed contract the docs now describe. The docs previously said the
        // service throws only "if all sources fail AND no recent cached value is available",
        // implying an expired cache entry could still rescue an all-sources-down fetch. It
        // cannot, and must not: budget limits are USD-denominated, so a stale price would
        // silently mis-evaluate them. Past the 60s window the cache is dead to us.
        var handler = new FakeHttpHandler();
        handler.SetCoinGeckoFailure();
        handler.SetCoinbaseFailure();
        handler.SetKrakenFailure();

        using var http = new HttpClient(handler);
        var service = new PriceService(http);
        PlantCachedSnapshot(service, new PriceSnapshot(50_000m, "CoinGecko", DateTime.UtcNow.AddMinutes(-5)));

        var act = async () => await service.GetBtcPriceAsync();

        await act.Should().ThrowAsync<PriceUnavailableException>(
            because: "an expired cache entry is not a fallback — a stale price must never be served");
    }

    [Fact]
    public async Task GetBtcPriceAsync_FreshCache_IsServedWithoutHittingAnySource()
    {
        // The other half of the documented contract: within the 60s window the cached value
        // is served and no source is contacted.
        var handler = new FakeHttpHandler();
        handler.SetCoinGeckoResponse(76_800m);
        handler.SetCoinbaseResponse(76_800m);
        handler.SetKrakenResponse(76_800m);

        using var http = new HttpClient(handler);
        var service = new PriceService(http);
        PlantCachedSnapshot(service, new PriceSnapshot(50_000m, "CoinGecko", DateTime.UtcNow.AddSeconds(-5)));

        var price = await service.GetBtcPriceAsync();

        price.Should().Be(50_000m, "a cache entry younger than 60s is served as-is");
        handler.CoinGeckoCallCount.Should().Be(0);
        handler.CoinbaseCallCount.Should().Be(0);
        handler.KrakenCallCount.Should().Be(0);
    }

    /// <summary>Plants a cache entry with a chosen age, so cache-window behaviour is testable without waiting 60s.</summary>
    private static void PlantCachedSnapshot(PriceService service, PriceSnapshot snapshot)
    {
        var field = typeof(PriceService).GetField("_cached", BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull("this test pins the behaviour of PriceService._cached");
        field!.SetValue(service, snapshot);
    }

    // ── Test infrastructure ─────────────────────────────────────────────────

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private string? _coingeckoBody;
        private bool _coingeckoFail;
        private string? _coinbaseBody;
        private bool _coinbaseFail;
        private string? _krakenBody;
        private bool _krakenFail;

        public int CoinGeckoCallCount { get; private set; }
        public int CoinbaseCallCount { get; private set; }
        public int KrakenCallCount { get; private set; }

        public void SetCoinGeckoResponse(decimal usd)
        {
            _coingeckoBody = $"{{\"bitcoin\":{{\"usd\":{usd}}}}}";
            _coingeckoFail = false;
        }

        public void SetCoinGeckoFailure() => _coingeckoFail = true;

        public void SetCoinbaseResponse(decimal usd)
        {
            _coinbaseBody = $"{{\"data\":{{\"amount\":\"{usd}\",\"base\":\"BTC\",\"currency\":\"USD\"}}}}";
            _coinbaseFail = false;
        }

        public void SetCoinbaseFailure() => _coinbaseFail = true;

        public void SetKrakenResponse(decimal usd)
        {
            _krakenBody =
                $"{{\"error\":[],\"result\":{{\"XXBTZUSD\":{{\"c\":[\"{usd}\",\"0.001\"]}}}}}}";
            _krakenFail = false;
        }

        public void SetKrakenFailure() => _krakenFail = true;

        public void SetSlowResponseForAllSources(TimeSpan delay)
        {
            _slowDelay = delay;
        }

        private TimeSpan? _slowDelay;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_slowDelay.HasValue)
            {
                await Task.Delay(_slowDelay.Value, cancellationToken);
            }

            var url = request.RequestUri!.ToString();
            if (url.Contains("coingecko"))
            {
                CoinGeckoCallCount++;
                return await Respond(_coingeckoBody, _coingeckoFail);
            }
            if (url.Contains("coinbase"))
            {
                CoinbaseCallCount++;
                return await Respond(_coinbaseBody, _coinbaseFail);
            }
            if (url.Contains("kraken"))
            {
                KrakenCallCount++;
                return await Respond(_krakenBody, _krakenFail);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static Task<HttpResponseMessage> Respond(string? body, bool fail)
        {
            if (fail || body == null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
