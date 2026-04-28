using System.Net;
using System.Text;
using LightningEnable.Mcp.Services;

namespace LightningEnable.Mcp.Tests.Services;

public class PriceServiceTests
{
    [Fact]
    public async Task GetBtcPriceAsync_ReturnsCoinGeckoPrice_WhenAllSourcesSucceed()
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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = request.RequestUri!.ToString();
            if (url.Contains("coingecko"))
            {
                CoinGeckoCallCount++;
                return Respond(_coingeckoBody, _coingeckoFail);
            }
            if (url.Contains("coinbase"))
            {
                CoinbaseCallCount++;
                return Respond(_coinbaseBody, _coinbaseFail);
            }
            if (url.Contains("kraken"))
            {
                KrakenCallCount++;
                return Respond(_krakenBody, _krakenFail);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
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
