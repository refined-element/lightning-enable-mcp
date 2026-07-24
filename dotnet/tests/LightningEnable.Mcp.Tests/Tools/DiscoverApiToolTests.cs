using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for the discover_api MCP tool.
/// </summary>
public class DiscoverApiToolTests
{
    private readonly Mock<IBudgetService> _budgetServiceMock;
    private readonly Mock<IPriceService> _priceServiceMock;

    public DiscoverApiToolTests()
    {
        _budgetServiceMock = new Mock<IBudgetService>();
        _priceServiceMock = new Mock<IPriceService>();

        // Default budget config
        _budgetServiceMock.Setup(b => b.GetConfig()).Returns(new BudgetConfig
        {
            MaxSatsPerSession = 10000,
            SessionSpent = 2000
        });

        _priceServiceMock.Setup(p => p.GetBtcPriceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100000m); // $100k/BTC for easy math
    }

    [Fact]
    public async Task DiscoverApi_NoParams_ReturnsUsageError()
    {
        var result = await DiscoverApiTool.DiscoverApi(
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("url");
        json.RootElement.TryGetProperty("examples", out var examples).Should().BeTrue();
        examples.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DiscoverApi_WithUrl_FetchesManifest()
    {
        // URL provided → should attempt manifest fetch (existing behavior)
        var result = await DiscoverApiTool.DiscoverApi(
            url: "https://this-domain-does-not-exist-12345.example.com",
            budgetAware: false,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        // Should have tried_urls (manifest fetch path), not registry path
        json.RootElement.TryGetProperty("tried_urls", out var triedUrls).Should().BeTrue();
        triedUrls.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DiscoverApi_WithQuery_AttemptsRegistrySearch()
    {
        // Query provided → should attempt registry search
        var result = await DiscoverApiTool.DiscoverApi(
            query: "weather",
            budgetAware: false,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        // May fail (no registry running in tests) but should attempt the registry path
        if (json.RootElement.GetProperty("success").GetBoolean())
        {
            json.RootElement.GetProperty("source").GetString().Should().Be("registry");
        }
        else
        {
            // Registry unavailable in unit tests — that's expected
            json.RootElement.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task DiscoverApi_WithCategory_AttemptsRegistrySearch()
    {
        var result = await DiscoverApiTool.DiscoverApi(
            category: "ai",
            budgetAware: false,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        // Registry may not be running, but should not throw
        json.RootElement.TryGetProperty("success", out _).Should().BeTrue();
    }

    [Fact]
    public async Task DiscoverApi_UrlTakesPrecedenceOverQuery()
    {
        // When both url and query are provided, url should win (manifest fetch path)
        var result = await DiscoverApiTool.DiscoverApi(
            url: "https://this-domain-does-not-exist-12345.example.com",
            query: "weather",
            budgetAware: false,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        // Should have tried_urls → manifest fetch path, not registry
        json.RootElement.TryGetProperty("tried_urls", out _).Should().BeTrue();
    }

    [Fact]
    public async Task DiscoverApi_InvalidUrl_ReturnsError()
    {
        var result = await DiscoverApiTool.DiscoverApi(
            url: "not-a-url", budgetAware: false, cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DiscoverApi_UnreachableUrl_ReturnsErrorWithTriedUrls()
    {
        // Use a URL that won't resolve
        var result = await DiscoverApiTool.DiscoverApi(
            url: "https://this-domain-does-not-exist-12345.example.com",
            budgetAware: false, cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.TryGetProperty("tried_urls", out var triedUrls).Should().BeTrue();
        triedUrls.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DiscoverApi_NoBudgetService_SkipsBudgetAnnotations()
    {
        // Without budget service, result should not have budget field
        var result = await DiscoverApiTool.DiscoverApi(
            url: "https://this-domain-does-not-exist-12345.example.com",
            budgetAware: true, cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        // Should fail (unreachable), but budget should not appear in error
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task DiscoverApi_BudgetAwareFalse_SkipsBudgetEvenWithService()
    {
        // Even with budget service, budgetAware=false should skip annotations
        var result = await DiscoverApiTool.DiscoverApi(
            url: "https://this-domain-does-not-exist-12345.example.com",
            budgetAware: false, budgetService: _budgetServiceMock.Object,
            priceService: _priceServiceMock.Object,
            cancellationToken: CancellationToken.None);

        // Should fail (unreachable), confirm budget service wasn't called
        _budgetServiceMock.Verify(b => b.GetConfig(), Times.Never);
    }

    #region Finding 3 — a hostile manifest must never claim "unlimited" affordability, nor throw

    // An L402 manifest is a THIRD-PARTY, ATTACKER-AUTHORABLE document: it is fetched from a
    // public registry or an arbitrary caller-supplied URL. Nothing about its shape is trusted.
    // The agent reads `affordable_calls` to decide how freely it may spend, so a malformed or
    // hostile price must degrade to an explicit "unknown" — never "unlimited", never "free",
    // and never an exception that takes down the whole discovery response.

    /// <summary>Builds a well-formed manifest whose only variable is the endpoint's raw price JSON.</summary>
    private static string ManifestWithPrice(string basePriceSatsJson) => $$"""
        {
          "service": { "name": "Hostile API", "base_url": "https://hostile.example.com" },
          "l402": { "default_price_sats": 100, "payment_flow": "l402" },
          "endpoints": [
            {
              "id": "ep1",
              "path": "/v1/data",
              "method": "GET",
              "summary": "Get data",
              "l402_enabled": true,
              "pricing": { "model": "flat", "base_price_sats": {{basePriceSatsJson}} }
            }
          ]
        }
        """;

    /// <summary>Runs the real extract → annotate pipeline and returns the single endpoint.</summary>
    private static Dictionary<string, object?> AnnotateFirstEndpoint(string manifestJson, long remainingSats = 8000)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        var endpoints = DiscoverApiTool.ExtractEndpoints(doc.RootElement);
        DiscoverApiTool.AnnotateEndpointsWithAffordability(endpoints, remainingSats, btcPrice: 100_000m);
        return endpoints[0];
    }

    /// <summary>Every price shape a hostile manifest can legally put in the JSON.</summary>
    public static TheoryData<string, string> HostilePriceShapes => new()
    {
        { "0",                        "a zero price" },
        { "-5",                       "a negative price" },
        { "null",                     "a null price" },
        { "\"abc\"",                  "a non-numeric string price" },
        { "\"\"",                     "an empty string price" },
        { "{\"amount\":100}",         "an object price" },
        { "[100]",                    "an array price" },
        { "true",                     "a boolean price" },
        { "1.5",                      "a fractional price" },
        { "99999999999999999999999",  "a price that overflows Int64" },
    };

    [Theory]
    [MemberData(nameof(HostilePriceShapes))]
    public void Annotate_HostilePrice_ReportsUnknown_NeverUnlimited(string priceJson, string why)
    {
        var endpoint = AnnotateFirstEndpoint(ManifestWithPrice(priceJson));

        endpoint["affordable_calls"].Should().Be("unknown",
            because: $"the manifest is attacker-authorable and {why} tells us nothing — " +
                     "the agent must be told the price is unknown, not that it can spend freely");
        endpoint.Should().NotContainKey("cost_usd",
            because: "a USD cost derived from an unusable price would be fabricated");
    }

    [Theory]
    [MemberData(nameof(HostilePriceShapes))]
    public void ExtractEndpoints_HostilePrice_DoesNotThrow(string priceJson, string why)
    {
        using var doc = JsonDocument.Parse(ManifestWithPrice(priceJson));
        var root = doc.RootElement;

        var act = () => DiscoverApiTool.ExtractEndpoints(root);

        act.Should().NotThrow(
            because: $"{why} must not kill the entire discovery response for every other endpoint");
    }

    [Theory]
    [MemberData(nameof(HostilePriceShapes))]
    public void Annotate_HostilePrice_NeverClaimsUnlimitedOrFree(string priceJson, string why)
    {
        var endpoint = AnnotateFirstEndpoint(ManifestWithPrice(priceJson));

        endpoint["affordable_calls"].Should().NotBe("unlimited",
            because: $"{why} must never be read as unlimited affordability");
        endpoint["affordable_calls"].Should().NotBe("free",
            because: $"{why} must never be read as a free endpoint");
    }

    [Fact]
    public void Annotate_PositivePrice_ReportsRealAffordableCallCount()
    {
        // Only a positive, numeric price produces a real count: 8000 sats / 100 sats = 80 calls,
        // and 100 sats at $100k/BTC = $0.10.
        var endpoint = AnnotateFirstEndpoint(ManifestWithPrice("100"), remainingSats: 8000);

        endpoint["affordable_calls"].Should().Be(80L);
        endpoint["cost_usd"].Should().Be(0.1m);
    }

    [Fact]
    public void Annotate_QuotedNumericPrice_IsParsedNotRejected()
    {
        // A quoted integer is unambiguous and cannot over-claim, so it is accepted.
        var endpoint = AnnotateFirstEndpoint(ManifestWithPrice("\"100\""), remainingSats: 8000);

        endpoint["affordable_calls"].Should().Be(80L);
    }

    [Fact]
    public void Annotate_PriceExceedingBudget_ReportsZeroAffordableCalls_NotUnknown()
    {
        // A real but unaffordable price is known — it affords zero calls.
        var endpoint = AnnotateFirstEndpoint(ManifestWithPrice("9000"), remainingSats: 8000);

        endpoint["affordable_calls"].Should().Be(0L);
    }

    [Fact]
    public void Annotate_MissingPriceField_ReportsUnknown()
    {
        var manifest = """
            {
              "endpoints": [
                { "id": "ep1", "path": "/v1/x", "method": "GET", "pricing": { "model": "flat" } }
              ]
            }
            """;

        AnnotateFirstEndpoint(manifest)["affordable_calls"].Should().Be("unknown",
            because: "a pricing block with no base_price_sats states no price at all");
    }

    [Fact]
    public void Annotate_NoPricingBlockAtAll_ReportsUnknown()
    {
        var manifest = """
            {
              "endpoints": [ { "id": "ep1", "path": "/v1/x", "method": "GET" } ]
            }
            """;

        AnnotateFirstEndpoint(manifest)["affordable_calls"].Should().Be("unknown",
            because: "an endpoint with no pricing at all must not be silently left unannotated, " +
                     "which an agent could misread as free");
    }

    [Fact]
    public void ExtractL402Info_HostileDefaultPrice_DoesNotThrow()
    {
        // The manifest-level l402.default_price_sats is read on the same untrusted document.
        var manifest = """
            { "l402": { "default_price_sats": "not-a-number", "payment_flow": "l402" }, "endpoints": [] }
            """;
        using var doc = JsonDocument.Parse(manifest);
        var root = doc.RootElement;

        var act = () => DiscoverApiTool.ExtractL402Info(root);

        act.Should().NotThrow(because: "no field of an untrusted manifest may crash discovery");
    }

    #endregion

    #region Finding 3 — registry search path uses the same safe price parsing

    private static string RegistryJson(string defaultPriceSatsJson) => $$"""
        {
          "items": [
            {
              "name": "Some API",
              "description": "An API",
              "endpointCount": 3,
              "defaultPriceSats": {{defaultPriceSatsJson}},
              "manifestUrl": "https://x.example.com/.well-known/l402-manifest.json"
            }
          ],
          "total": 1
        }
        """;

    [Theory]
    [MemberData(nameof(HostilePriceShapes))]
    public void BuildRegistryEntries_HostilePrice_DoesNotThrow_AndReportsUnknown(string priceJson, string why)
    {
        using var doc = JsonDocument.Parse(RegistryJson(priceJson));
        var root = doc.RootElement;
        List<Dictionary<string, object?>> entries = null!;

        var act = () => entries = DiscoverApiTool.BuildRegistryEntries(
            root, budgetAware: true, _budgetServiceMock.Object);

        act.Should().NotThrow(because: $"the registry response is untrusted input and {why} must not throw");
        entries[0]["affordable_calls"].Should().Be("unknown",
            because: $"{why} affords an unknown number of calls");
    }

    [Fact]
    public void BuildRegistryEntries_PositivePrice_ReportsAffordableCalls()
    {
        // Remaining session budget = 10000 - 2000 = 8000 sats → 8000 / 100 = 80 calls.
        using var doc = JsonDocument.Parse(RegistryJson("100"));

        var entries = DiscoverApiTool.BuildRegistryEntries(
            doc.RootElement, budgetAware: true, _budgetServiceMock.Object);

        entries[0]["affordable_calls"].Should().Be(80L);
    }

    [Fact]
    public void BuildRegistryEntries_BudgetAwareFalse_AddsNoAffordabilityClaim()
    {
        using var doc = JsonDocument.Parse(RegistryJson("100"));

        var entries = DiscoverApiTool.BuildRegistryEntries(
            doc.RootElement, budgetAware: false, _budgetServiceMock.Object);

        entries[0].Should().NotContainKey("affordable_calls");
        _budgetServiceMock.Verify(b => b.GetConfig(), Times.Never);
    }

    #endregion

    #region SSRF guard (F-10e — discover_api manifest fetch is now guarded on .NET)

    // Before this fix the .NET discover_api tool fetched agent-supplied URLs through an
    // UNGUARDED static HttpClient, leaving SSRF fully open (an agent could pass
    // url=http://169.254.169.254/... or a private range). The manifest fetch now runs a
    // cheap synchronous pre-check BEFORE any request and (in Program.cs) routes through a
    // client carrying the connect-time SsrfConnectValidator. These prove the pre-check
    // refuses private/metadata targets with a generic, non-echoing error and never reaches
    // the fetch path (no tried_urls is emitted — that only appears once a fetch is attempted).

    [Theory]
    [InlineData("http://169.254.169.254/")]              // cloud metadata (link-local literal)
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1/l402.json")]           // loopback literal
    [InlineData("http://[::1]/l402.json")]               // IPv6 loopback literal
    [InlineData("http://10.0.0.5/")]                      // RFC1918 literal
    [InlineData("http://192.168.1.1/l402.json")]         // RFC1918 literal
    [InlineData("http://100.64.0.1/")]                    // RFC6598 CGNAT literal
    [InlineData("http://localhost/l402.json")]           // localhost hostname
    [InlineData("http://metadata.google.internal/")]      // metadata hostname
    [InlineData("http://foo.internal/l402.json")]        // .internal suffix
    public async Task DiscoverApi_PrivateOrMetadataUrl_RefusedBeforeAnyFetch(string url)
    {
        var result = await DiscoverApiTool.DiscoverApi(
            url: url, budgetAware: false, cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("not allowed");

        // Refused by the pre-check BEFORE the manifest-fetch path ran → no tried_urls.
        json.RootElement.TryGetProperty("tried_urls", out _).Should()
            .BeFalse("a private/metadata target must be refused before any fetch is attempted");

        // Generic message — the internal host/IP is never echoed back to the caller.
        var error = json.RootElement.GetProperty("error").GetString()!;
        error.Should().NotContain("169.254.169.254");
        error.Should().NotContain("10.0.0.5");
        error.Should().NotContain("192.168.1.1");
    }

    [Theory]
    [InlineData("ftp://example.com/manifest.json")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com/")]
    public async Task DiscoverApi_NonHttpUrl_Refused(string url)
    {
        var result = await DiscoverApiTool.DiscoverApi(
            url: url, budgetAware: false, cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.TryGetProperty("tried_urls", out _).Should().BeFalse();
    }

    #endregion

    #region FIX C — discover_api fallback fetch path has NO unguarded client

    // Before FIX C, when no IHttpClientFactory was injected, the manifest fetch fell back
    // to a static, UNGUARDED HttpClient (no ConnectCallback) — an unguarded SSRF path that
    // a DNS-rebind target could ride. The fallback now uses an INLINE client carrying the
    // SAME connect-time SsrfConnectValidator as the DI-registered client. This proves that
    // exact fallback client (the one FetchAndFormatManifestAsync builds when the factory is
    // null) blocks a private/metadata target at connect time. An IP literal makes it
    // deterministic — the socket rejects before any DNS.

    [Theory]
    [InlineData("http://10.0.0.5/")]              // RFC1918
    [InlineData("http://169.254.169.254/")]       // cloud metadata (link-local)
    [InlineData("http://127.0.0.1/")]             // loopback
    public async Task FallbackManifestClient_BlocksPrivateTargetAtConnect(string url)
    {
        using var client = DiscoverApiTool.CreateGuardedManifestClient();

        var act = async () => await client.GetAsync(url);

        var ex = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        var chain = ex.Message + " " + (ex.InnerException?.Message ?? string.Empty);
        chain.Should().Contain("private/reserved",
            because: "the fallback manifest client must carry the connect-time SSRF guard, never be unguarded");
    }

    #endregion

    #region FIX A — discover_api surfaces an unfollowed 3xx as an actionable redirect

    /// <summary>Fake handler returning a canned response and recording every URL fetched.</summary>
    private sealed class StubHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly Func<Uri, HttpResponseMessage> _responder;
        public List<Uri> Received { get; } = new();
        public StubHandler(Func<Uri, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Received.Add(request.RequestUri!);
            return Task.FromResult(_responder(request.RequestUri!));
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly System.Net.Http.HttpMessageHandler _handler;
        public StubHttpClientFactory(System.Net.Http.HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    [Fact]
    public async Task DiscoverApi_ManifestUrlRedirects_ReturnsActionableRedirect_NotFollowed()
    {
        // The manifest URL returns a 302 to a different host. With AllowAutoRedirect off the
        // client does not follow — the tool must surface the target as actionable, not chase it.
        var handler = new StubHandler(_ =>
        {
            var r = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://canonical.example.com/.well-known/l402-manifest.json");
            r.Content = new StringContent("moved");
            return r;
        });
        var factory = new StubHttpClientFactory(handler);

        var result = await DiscoverApiTool.DiscoverApi(
            url: "https://api.example.com",
            budgetAware: false,
            httpClientFactory: factory,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("redirect_location").GetString()
            .Should().Be("https://canonical.example.com/.well-known/l402-manifest.json");
        json.RootElement.GetProperty("error").GetString().Should().Contain("redirected to");

        // Every request went to the ORIGINAL host — the redirect target was never fetched.
        handler.Received.Should().OnlyContain(u => u.Host == "api.example.com");
    }

    #endregion

    #region FIX 2 — a redirect on a SYNTHESIZED well-known probe is NOT promoted to actionable

    // A 3xx encountered while probing a synthesized /.well-known/ path is NOT the user's URL.
    // Promoting it would send the agent chasing a catch-all/login page and suppress the
    // honest "no manifest here" result. So a well-known-probe redirect is treated as "no
    // manifest at this path" (tried_urls), while a redirect on the USER's explicit url IS
    // surfaced as an actionable redirect_location.

    [Fact]
    public async Task DiscoverApi_WellKnownProbeRedirects_ButUserUrlDoesNot_ReturnsNotFound_NotRedirect()
    {
        // The user's URL (fetched last for a non-.json url, at path "/") 404s; every
        // synthesized /.well-known/ probe 302s. The tool must return the not-found +
        // tried_urls shape, NOT an actionable redirect chasing the probe's target.
        var handler = new StubHandler(uri =>
        {
            if (uri.AbsolutePath == "/")
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new StringContent("nope") };

            var r = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://login.example.com/sso"); // catch-all/login trap
            r.Content = new StringContent("moved");
            return r;
        });
        var factory = new StubHttpClientFactory(handler);

        var result = await DiscoverApiTool.DiscoverApi(
            url: "https://api.example.com",
            budgetAware: false,
            httpClientFactory: factory,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        // NOT promoted to an actionable redirect — the probe's Location must not leak here.
        json.RootElement.TryGetProperty("redirect_location", out _).Should()
            .BeFalse("a redirect on a synthesized well-known probe must not be surfaced as actionable");
        json.RootElement.TryGetProperty("tried_urls", out var tried).Should().BeTrue();
        tried.GetArrayLength().Should().BeGreaterThan(0);
        json.RootElement.GetProperty("error").GetString().Should().Contain("Could not find");
    }

    [Fact]
    public async Task DiscoverApi_UserJsonUrlRedirects_IsStillSurfacedAsActionableRedirect()
    {
        // A redirect on the USER's EXPLICIT url (a .json manifest URL, tried first) IS the
        // meaningful signal and stays surfaced — only the well-known-probe case is suppressed.
        var handler = new StubHandler(uri =>
        {
            if (uri.AbsolutePath == "/manifest.json")
            {
                var r = new HttpResponseMessage(System.Net.HttpStatusCode.MovedPermanently);
                r.Headers.Location = new Uri("https://cdn.example.com/manifest.json");
                r.Content = new StringContent("moved");
                return r;
            }
            // well-known probes off the .json base 404
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new StringContent("nope") };
        });
        var factory = new StubHttpClientFactory(handler);

        var result = await DiscoverApiTool.DiscoverApi(
            url: "https://api.example.com/manifest.json",
            budgetAware: false,
            httpClientFactory: factory,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("redirect_location").GetString()
            .Should().Be("https://cdn.example.com/manifest.json");
        json.RootElement.TryGetProperty("tried_urls", out _).Should()
            .BeFalse("a redirect on the user's explicit URL is surfaced as actionable, not a not-found");
    }

    #endregion

    #region FIX 4 — registry-search client does not auto-follow a 3xx

    [Fact]
    public void RegistrySearchHandler_DoesNotAutoFollowRedirects()
    {
        // Locks the config the reviewer flagged: the registry client was a plain
        // new HttpClient() (AllowAutoRedirect defaults TRUE) that would silently follow a
        // 302 → internal/metadata host. It must be pinned off (parity with Python).
        DiscoverApiTool.CreateRegistrySearchHandler().AllowAutoRedirect.Should().BeFalse();
    }

    [Fact]
    public async Task SearchRegistry_RegistryReturns302_ReturnsCleanRedirectError_DoesNotFollow()
    {
        // With auto-follow off, a registry 3xx is surfaced as a clean error and the tool
        // makes exactly ONE request — it never chases the Location.
        var handler = new StubHandler(_ =>
        {
            var r = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
            r.Headers.Location = new Uri("http://169.254.169.254/latest/meta-data/");
            r.Content = new StringContent("moved");
            return r;
        });
        using var stubClient = new HttpClient(handler, disposeHandler: false);

        var result = await DiscoverApiTool.SearchRegistryAsync(
            query: "weather", category: null, budgetAware: false,
            budgetService: null, priceService: null, ct: CancellationToken.None,
            httpClient: stubClient);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("redirect");
        // The internal metadata target is never echoed back nor fetched.
        json.RootElement.GetProperty("error").GetString().Should().NotContain("169.254.169.254");
        handler.Received.Should().ContainSingle("the tool must not follow the registry's redirect");
    }

    #endregion
}
