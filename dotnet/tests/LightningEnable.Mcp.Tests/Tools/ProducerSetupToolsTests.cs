using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// The seller-side <c>l402_producer</c> actions: from an API key to a paid endpoint.
///
/// <para><c>create</c> / <c>verify</c> mint and check ONE challenge. These six actions are
/// the setup that has to happen before either is worth calling — pointing the merchant's
/// payouts at their own wallet, registering an API, pricing its endpoints, listing it, and
/// reading back what was minted — and every one of them used to require raw REST.</para>
///
/// <para>What these tests hold:</para>
/// <list type="number">
/// <item><description><b>Request shape.</b> Each action hits the exact route and body the
/// Lightning Enable API defines (<c>MerchantSettingsController</c> /
/// <c>ProxyManagementController</c> / <c>ManifestController</c> on the branch this targets).
/// A stub handler records every request, so a renamed field fails here rather than in
/// production.</description></item>
/// <item><description><b>The NWC connection string never comes back.</b> It authorises live
/// calls against the merchant's wallet, including when the API's own error body quotes
/// it.</description></item>
/// <item><description><b>The MCP wallet is only reused when it IS an NWC wallet.</b></description></item>
/// <item><description><b>Errors are surfaced, not swallowed</b> — RFC 9457 members and the
/// legacy <c>error</c>/<c>message</c> both reach the agent, and the API key never
/// does.</description></item>
/// </list>
///
/// <para>Mirrors <c>python/lightning-enable-mcp/tests/test_producer_setup.py</c>.</para>
/// </summary>
public class ProducerSetupToolsTests
{
    private const string ApiBase = "https://api.example.test";

    // A syntactically valid NWC string with obviously fake key material. Named so it cannot
    // trip the gitleaks generic-api-key rule.
    private const string FixtureNwc =
        "nostr+walletconnect://abababababababababababababababababababababababababababababababab"
        + "?relay=wss://relay.example.test"
        + "&secret=cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd";

    private const string FixtureSecret =
        "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd";

    private const string FixtureApiKey = "fixture-key-1";

    // ── Test doubles ─────────────────────────────────────────────────────────

    /// <summary>Records every request and answers from a <c>(method, path)</c> table.</summary>
    private sealed class StubApi : HttpMessageHandler
    {
        private readonly Dictionary<(string Method, string Path), (HttpStatusCode Status, string Body)> _routes;

        public StubApi(Dictionary<(string, string), (HttpStatusCode, string)> routes)
            => _routes = routes.ToDictionary(r => (r.Key.Item1, r.Key.Item2), r => (r.Value.Item1, r.Value.Item2));

        public List<(string Method, string Path, string Query, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method.Method, path, request.RequestUri.Query, body));

            if (!_routes.TryGetValue((request.Method.Method, path), out var route))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        $"{{\"error\":\"stub has no route for {request.Method.Method} {path}\"}}",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(route.Status)
            {
                Content = new StringContent(route.Body, Encoding.UTF8, "application/json"),
            };
        }

        public (string Method, string Path, string Query, string Body) RequestFor(string method, string path)
        {
            var match = Requests.FirstOrDefault(r => r.Method == method && r.Path == path);
            match.Method.Should().NotBeNull(
                $"{method} {path} was never called; saw "
                + string.Join(", ", Requests.Select(r => $"{r.Method} {r.Path}")));
            return match;
        }

        public JsonElement BodyOf(string method, string path)
            => JsonDocument.Parse(RequestFor(method, path).Body).RootElement;

        public IReadOnlyList<(string Method, string Path)> Paths
            => Requests.Select(r => (r.Method, r.Path)).ToList();
    }

    /// <summary>A scriptable wallet-onboarding seam: no relay, no home directory.</summary>
    private sealed class StubOnboarding : IWalletOnboardingService
    {
        public WalletSetupState State { get; set; } = new(
            false, null, null, false, Array.Empty<string>(), "/tmp/fixture-config.json");

        public string? OwnNwc { get; set; }

        public WalletSetupState Describe() => State;

        public string? ResolveOwnNwcConnectionString() => OwnNwc;

        public Task<NwcProbeResult> ProbeNwcAsync(string connectionString, CancellationToken ct = default)
            => Task.FromResult(new NwcProbeResult(true, null, Array.Empty<string>(), null, null));

        public void SaveNwcConnectionString(string connectionString) { }
    }

    private static StubOnboarding WalletIs(string provider, string? ownNwc = null) => new()
    {
        State = new(true, provider, $"environment (FIXTURE_{provider.ToUpperInvariant()})",
            true, new[] { provider }, "/tmp/fixture-config.json"),
        OwnNwc = ownNwc,
    };

    private static (LightningEnableApiService Service, StubApi Stub) MakeService(
        Dictionary<(string, string), (HttpStatusCode, string)> routes, string? apiKey = FixtureApiKey)
    {
        var stub = new StubApi(routes);
        var previousKey = Environment.GetEnvironmentVariable("LIGHTNING_ENABLE_API_KEY");
        var previousUrl = Environment.GetEnvironmentVariable("LIGHTNING_ENABLE_API_URL");
        try
        {
            Environment.SetEnvironmentVariable("LIGHTNING_ENABLE_API_KEY", apiKey);
            Environment.SetEnvironmentVariable("LIGHTNING_ENABLE_API_URL", ApiBase);
            var config = new StubConfig();
            return (new LightningEnableApiService(new HttpClient(stub), config), stub);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIGHTNING_ENABLE_API_KEY", previousKey);
            Environment.SetEnvironmentVariable("LIGHTNING_ENABLE_API_URL", previousUrl);
        }
    }

    private sealed class StubConfig : IBudgetConfigurationService
    {
        public UserBudgetConfiguration Configuration { get; } = new();
        public string ConfigFilePath => "/tmp/fixture-config.json";
        public bool ConfigFileExists => false;
        public void Reload() { }
    }

    private static Dictionary<(string, string), (HttpStatusCode, string)> Routes(
        params ((string Method, string Path) Key, (HttpStatusCode Status, string Body) Value)[] entries)
        => entries.ToDictionary(e => (e.Key.Method, e.Key.Path), e => (e.Value.Status, e.Value.Body));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ── configure_receive ────────────────────────────────────────────────────

    [Fact]
    public async Task ConfigureReceive_SavesTheConnectionThenSwitchesTheProvider()
    {
        var (service, stub) = MakeService(Routes(
            (("PUT", "/api/merchant/nwc-connection"), (HttpStatusCode.OK, "{\"success\":true}")),
            (("PUT", "/api/merchant/payment-provider"), (HttpStatusCode.OK, "{\"success\":true}"))));

        var result = Parse(await ProducerSetupTools.ConfigureReceiveAsync(
            FixtureNwc, service, new StubOnboarding(), CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        stub.Paths.Should().Equal(
            ("PUT", "/api/merchant/nwc-connection"),
            ("PUT", "/api/merchant/payment-provider"));
        Str(stub.BodyOf("PUT", "/api/merchant/nwc-connection"), "nwcConnectionString")
            .Should().Be(FixtureNwc);
        Str(stub.BodyOf("PUT", "/api/merchant/payment-provider"), "provider").Should().Be("nwc");
        Str(result, "provider").Should().Be("nwc");
        Str(result, "nwcConnectionString").Should().Be("<set>");
    }

    [Fact]
    public async Task ConfigureReceive_NeverEchoesTheConnectionString()
    {
        var (service, _) = MakeService(Routes(
            (("PUT", "/api/merchant/nwc-connection"), (HttpStatusCode.OK, "{\"success\":true}")),
            (("PUT", "/api/merchant/payment-provider"), (HttpStatusCode.OK, "{\"success\":true}"))));

        var raw = await ProducerSetupTools.ConfigureReceiveAsync(
            FixtureNwc, service, new StubOnboarding(), CancellationToken.None);

        raw.Should().NotContain(FixtureNwc);
        raw.Should().NotContain(FixtureSecret);
    }

    [Fact]
    public async Task ConfigureReceive_ScrubsTheStringOutOfAnApiErrorBody()
    {
        // Defence in depth: the API promises not to quote it, we make sure anyway.
        var (service, _) = MakeService(Routes(
            (("PUT", "/api/merchant/nwc-connection"),
                (HttpStatusCode.BadRequest, $"{{\"error\":\"could not parse {FixtureNwc}\"}}"))));

        var raw = await ProducerSetupTools.ConfigureReceiveAsync(
            FixtureNwc, service, new StubOnboarding(), CancellationToken.None);

        raw.Should().NotContain(FixtureNwc);
        raw.Should().NotContain(FixtureSecret);
        Parse(raw).GetProperty("success").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ConfigureReceive_DoesNotSwitchProviderWhenTheSaveFails()
    {
        var (service, stub) = MakeService(Routes(
            (("PUT", "/api/merchant/nwc-connection"),
                (HttpStatusCode.BadRequest,
                    "{\"error\":\"The 'secret' parameter must be 64 hex characters.\"}")),
            (("PUT", "/api/merchant/payment-provider"), (HttpStatusCode.OK, "{\"success\":true}"))));

        var result = Parse(await ProducerSetupTools.ConfigureReceiveAsync(
            FixtureNwc, service, new StubOnboarding(), CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        Str(result, "error").Should().Contain("secret");
        stub.Paths.Should().NotContain(("PUT", "/api/merchant/payment-provider"));
    }

    [Fact]
    public async Task ConfigureReceive_SaysTheConnectionIsStoredWhenOnlyTheProviderSwitchFails()
    {
        var (service, _) = MakeService(Routes(
            (("PUT", "/api/merchant/nwc-connection"), (HttpStatusCode.OK, "{\"success\":true}")),
            (("PUT", "/api/merchant/payment-provider"),
                (HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}"))));

        var result = Parse(await ProducerSetupTools.ConfigureReceiveAsync(
            FixtureNwc, service, new StubOnboarding(), CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        Str(result, "nwcConnectionString").Should().Be("<set>");
        Str(result, "hint").Should().Contain("WAS stored");
    }

    [Fact]
    public async Task ConfigureReceive_RejectsAMalformedStringBeforeSendingIt()
    {
        var (service, stub) = MakeService(Routes());

        var result = Parse(await ProducerSetupTools.ConfigureReceiveAsync(
            "not-a-connection", service, new StubOnboarding(), CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        Str(result, "error").Should().Contain("nostr+walletconnect://");
        stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfigureReceive_FallsBackToTheMcpWalletWhenItIsNwc()
    {
        var (service, stub) = MakeService(Routes(
            (("PUT", "/api/merchant/nwc-connection"), (HttpStatusCode.OK, "{\"success\":true}")),
            (("PUT", "/api/merchant/payment-provider"), (HttpStatusCode.OK, "{\"success\":true}"))));

        var result = Parse(await ProducerSetupTools.ConfigureReceiveAsync(
            null, service, WalletIs("NWC", FixtureNwc), CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("usedMcpWallet").GetBoolean().Should().BeTrue();
        Str(result, "source").Should().Contain("NWC");
        Str(stub.BodyOf("PUT", "/api/merchant/nwc-connection"), "nwcConnectionString")
            .Should().Be(FixtureNwc);
    }

    [Theory]
    [InlineData("LND")]
    [InlineData("Strike")]
    [InlineData("OpenNode")]
    public async Task ConfigureReceive_RefusesWhenTheMcpWalletIsNotNwc(string provider)
    {
        var (service, stub) = MakeService(Routes());

        var result = Parse(await ProducerSetupTools.ConfigureReceiveAsync(
            null, service, WalletIs(provider), CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        Str(result, "error").Should().Contain(provider).And.Contain("nwcConnectionString");
        stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfigureReceive_RefusesWhenNoWalletIsConfiguredAtAll()
    {
        var (service, stub) = MakeService(Routes());

        var result = Parse(await ProducerSetupTools.ConfigureReceiveAsync(
            null, service, new StubOnboarding(), CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        Str(result, "error")!.ToLowerInvariant().Should().Contain("no wallet");
        stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfigureReceive_RequiresAnApiKey()
    {
        var (service, _) = MakeService(Routes(), apiKey: null);

        var result = Parse(await ProducerSetupTools.ConfigureReceiveAsync(
            FixtureNwc, service, new StubOnboarding(), CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        Str(result, "error").Should().Contain("LIGHTNING_ENABLE_API_KEY");
    }

    // ── status ───────────────────────────────────────────────────────────────

    private const string MerchantMe = """
        {
          "merchantId": 42,
          "name": "Fixture Merchant",
          "planTier": "individual",
          "subscriptionStatus": "active",
          "isActive": true,
          "features": { "l402Enabled": true },
          "onboarding": {
            "hasOpenNodeKey": false,
            "hasStrikeKey": false,
            "hasNwcConnection": true,
            "hasActiveProxy": true,
            "proxyCount": 2,
            "isFullyConfigured": true
          }
        }
        """;

    private const string QuickStart = """
        {
          "completedSteps": 2,
          "totalSteps": 4,
          "requiredStepsCompleted": 2,
          "requiredStepsTotal": 3,
          "isReadyForProduction": false,
          "steps": [
            { "stepNumber": 1, "title": "Connect a wallet", "isCompleted": true, "isRequired": true }
          ]
        }
        """;

    private const string Challenges = """
        {
          "challenges": [
            {
              "paymentHash": "aa11",
              "resource": "/weather",
              "amountSats": 25,
              "status": "paid",
              "createdAt": "2026-09-01T10:00:00Z",
              "paidAt": "2026-09-01T10:01:00Z",
              "expiresAt": "2026-09-01T11:00:00Z"
            }
          ],
          "total": 1,
          "limit": 5,
          "offset": 0
        }
        """;

    [Fact]
    public async Task Status_SummarisesAccountChecklistAndRecentChallenges()
    {
        var (service, stub) = MakeService(Routes(
            (("GET", "/api/merchant/me"), (HttpStatusCode.OK, MerchantMe)),
            (("GET", "/api/merchant/quickstart"), (HttpStatusCode.OK, QuickStart)),
            (("GET", "/api/l402/challenges"), (HttpStatusCode.OK, Challenges))));

        var result = Parse(await ProducerSetupTools.StatusAsync(5, service, CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        Str(result.GetProperty("merchant"), "planTier").Should().Be("individual");
        Str(result.GetProperty("receive"), "provider").Should().Be("nwc");
        result.GetProperty("receive").GetProperty("configured").GetBoolean().Should().BeTrue();
        Str(result.GetProperty("receive"), "nwcConnectionString").Should().Be("<set>");
        result.GetProperty("proxies").GetProperty("count").GetInt32().Should().Be(2);
        result.GetProperty("checklist").GetProperty("completedSteps").GetInt32().Should().Be(2);
        Str(result.GetProperty("recentChallenges")[0], "paymentHash").Should().Be("aa11");
        stub.RequestFor("GET", "/api/l402/challenges").Query.Should().Contain("limit=5");
    }

    [Fact]
    public async Task Status_NeverReportsAMacaroonOrPreimage()
    {
        const string leaky = """
            {
              "challenges": [
                {
                  "paymentHash": "aa11",
                  "resource": "/weather",
                  "amountSats": 25,
                  "status": "paid",
                  "macaroon": "fixture-macaroon-value",
                  "preimage": "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
                }
              ],
              "total": 1
            }
            """;
        var (service, _) = MakeService(Routes(
            (("GET", "/api/merchant/me"), (HttpStatusCode.OK, MerchantMe)),
            (("GET", "/api/merchant/quickstart"), (HttpStatusCode.OK, QuickStart)),
            (("GET", "/api/l402/challenges"), (HttpStatusCode.OK, leaky))));

        var raw = await ProducerSetupTools.StatusAsync(5, service, CancellationToken.None);

        raw.Should().NotContain("macaroon");
        raw.Should().NotContain("preimage");
    }

    [Fact]
    public async Task Status_StillAnswersWhenTheChecklistIsUnavailable()
    {
        var (service, _) = MakeService(Routes(
            (("GET", "/api/merchant/me"), (HttpStatusCode.OK, MerchantMe)),
            (("GET", "/api/merchant/quickstart"),
                (HttpStatusCode.NotFound, "{\"error\":\"Merchant not found\"}")),
            (("GET", "/api/l402/challenges"), (HttpStatusCode.OK, Challenges))));

        var result = Parse(await ProducerSetupTools.StatusAsync(5, service, CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("checklist").ValueKind.Should().Be(JsonValueKind.Null);
        Str(result, "checklistError").Should().Contain("Merchant not found");
    }

    [Fact]
    public async Task Status_FailsWhenTheAccountLookupFails()
    {
        var (service, _) = MakeService(Routes(
            (("GET", "/api/merchant/me"),
                (HttpStatusCode.Unauthorized, "{\"error\":\"Authentication required\"}"))));

        var result = Parse(await ProducerSetupTools.StatusAsync(5, service, CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        Str(result, "error").Should().Contain("Authentication required");
    }

    [Fact]
    public async Task Status_DerivesTheProviderFromTheOnboardingFlags()
    {
        var strikeOnly = MerchantMe
            .Replace("\"hasStrikeKey\": false", "\"hasStrikeKey\": true")
            .Replace("\"hasNwcConnection\": true", "\"hasNwcConnection\": false");
        var (service, _) = MakeService(Routes(
            (("GET", "/api/merchant/me"), (HttpStatusCode.OK, strikeOnly)),
            (("GET", "/api/merchant/quickstart"), (HttpStatusCode.OK, QuickStart)),
            (("GET", "/api/l402/challenges"), (HttpStatusCode.OK, Challenges))));

        var result = Parse(await ProducerSetupTools.StatusAsync(5, service, CancellationToken.None));

        Str(result.GetProperty("receive"), "provider").Should().Be("strike");
        Str(result.GetProperty("receive"), "nwcConnectionString").Should().Be("<unset>");
    }

    [Fact]
    public async Task Status_PrefersAnExplicitPaymentProviderFieldWhenTheApiAddsOne()
    {
        var explicitProvider = MerchantMe.Replace(
            "\"merchantId\": 42", "\"merchantId\": 42, \"paymentProvider\": \"opennode\"");
        var (service, _) = MakeService(Routes(
            (("GET", "/api/merchant/me"), (HttpStatusCode.OK, explicitProvider)),
            (("GET", "/api/merchant/quickstart"), (HttpStatusCode.OK, QuickStart)),
            (("GET", "/api/l402/challenges"), (HttpStatusCode.OK, Challenges))));

        var result = Parse(await ProducerSetupTools.StatusAsync(5, service, CancellationToken.None));

        Str(result.GetProperty("receive"), "provider").Should().Be("opennode");
    }

    // ── create_proxy ─────────────────────────────────────────────────────────

    private const string ProxyCreated = """
        {
          "proxyId": "weather-api",
          "name": "Weather API",
          "description": "Forecasts",
          "targetBaseUrl": "https://upstream.example.test",
          "defaultPriceSats": 25,
          "proxyUrl": "/l402/proxy/weather-api"
        }
        """;

    [Fact]
    public async Task CreateProxy_PostsTheDtoAndReturnsThePublicBaseUrl()
    {
        var (service, stub) = MakeService(Routes(
            (("POST", "/api/proxy"), (HttpStatusCode.Created, ProxyCreated))));

        var result = Parse(await ProducerSetupTools.CreateProxyAsync(
            "Weather API", "https://upstream.example.test", "Forecasts", 25,
            service, CancellationToken.None));

        var body = stub.BodyOf("POST", "/api/proxy");
        Str(body, "name").Should().Be("Weather API");
        Str(body, "targetBaseUrl").Should().Be("https://upstream.example.test");
        Str(body, "description").Should().Be("Forecasts");
        body.GetProperty("defaultPriceSats").GetInt32().Should().Be(25);

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        Str(result, "proxyId").Should().Be("weather-api");
        Str(result, "proxyUrl").Should().Be("/l402/proxy/weather-api");
        Str(result, "publicBaseUrl").Should().Be($"{ApiBase}/l402/proxy/weather-api");
    }

    [Theory]
    [InlineData("", "https://upstream.example.test")]
    [InlineData("Weather API", "   ")]
    public async Task CreateProxy_RequiresANameAndTarget(string name, string target)
    {
        var (service, stub) = MakeService(Routes());

        var result = Parse(await ProducerSetupTools.CreateProxyAsync(
            name, target, null, 10, service, CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateProxy_SurfacesAProblemJsonPlanLimit()
    {
        const string problem = """
            {
              "type": "https://lightningenable.com/problems/plan_proxy_limit",
              "title": "Proxy limit reached",
              "status": 402,
              "detail": "Plan 'free' caps proxy configs at 1. Upgrade for unlimited.",
              "error": "plan_proxy_limit"
            }
            """;
        var (service, _) = MakeService(Routes(
            (("POST", "/api/proxy"), (HttpStatusCode.PaymentRequired, problem))));

        var result = Parse(await ProducerSetupTools.CreateProxyAsync(
            "Weather API", "https://upstream.example.test", null, 10,
            service, CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        Str(result, "error").Should().Contain("caps proxy configs at 1");
        Str(result, "errorType").Should().EndWith("plan_proxy_limit");
        Str(result, "errorCode").Should().Be("plan_proxy_limit");
        result.GetProperty("httpStatus").GetInt32().Should().Be(402);
    }

    // ── add_endpoint ─────────────────────────────────────────────────────────

    private const string EndpointCreated = """
        {
          "endpointId": "forecast",
          "path": "/forecast",
          "httpMethod": "GET",
          "summary": "Five-day forecast",
          "basePriceSats": 10
        }
        """;

    [Fact]
    public async Task AddEndpoint_PostsTheManifestEndpointDto()
    {
        var (service, stub) = MakeService(Routes(
            (("POST", "/api/proxy/weather-api/manifest/endpoints"),
                (HttpStatusCode.Created, EndpointCreated))));

        var result = Parse(await ProducerSetupTools.AddEndpointAsync(
            "weather-api", "forecast", "/forecast", "get", "Five-day forecast", 10,
            service, CancellationToken.None));

        var body = stub.BodyOf("POST", "/api/proxy/weather-api/manifest/endpoints");
        Str(body, "endpointId").Should().Be("forecast");
        Str(body, "path").Should().Be("/forecast");
        Str(body, "httpMethod").Should().Be("GET", "the API compares the method upper-cased");
        Str(body, "summary").Should().Be("Five-day forecast");
        body.GetProperty("basePriceSats").GetInt32().Should().Be(10);

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        Str(result, "httpMethod").Should().Be("GET");
        Str(result, "url").Should().Be($"{ApiBase}/l402/proxy/weather-api/forecast");
    }

    [Fact]
    public async Task AddEndpoint_UrlEncodesTheProxyIdInThePath()
    {
        // A slug with a slash must not walk out of its own route segment.
        var (service, stub) = MakeService(Routes());

        await ProducerSetupTools.AddEndpointAsync(
            "a/b", "e", "/x", null, null, 1, service, CancellationToken.None);

        stub.Requests.Should().ContainSingle();
        stub.Requests[0].Path.Should().Be("/api/proxy/a%2Fb/manifest/endpoints");
    }

    [Theory]
    [InlineData("", "e", "/x")]
    [InlineData("p", "", "/x")]
    [InlineData("p", "e", "")]
    public async Task AddEndpoint_RequiresProxyEndpointAndPath(string proxy, string endpoint, string path)
    {
        var (service, stub) = MakeService(Routes());

        var result = Parse(await ProducerSetupTools.AddEndpointAsync(
            proxy, endpoint, path, null, null, 1, service, CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AddEndpoint_SurfacesADuplicateEndpointError()
    {
        var (service, _) = MakeService(Routes(
            (("POST", "/api/proxy/weather-api/manifest/endpoints"),
                (HttpStatusCode.BadRequest,
                    "{\"error\":\"Endpoint with ID 'forecast' already exists\"}"))));

        var result = Parse(await ProducerSetupTools.AddEndpointAsync(
            "weather-api", "forecast", "/forecast", null, null, 10,
            service, CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        Str(result, "error").Should().Contain("already exists");
    }

    // ── publish ──────────────────────────────────────────────────────────────

    private const string ManifestSettings = """
        {
          "manifestEnabled": true,
          "serviceDescription": "Forecasts for agents",
          "categories": ["weather", "data"],
          "manifestPubliclyListed": true,
          "manifestUrl": "/l402/proxy/weather-api/.well-known/l402-manifest.json"
        }
        """;

    [Fact]
    public async Task Publish_EnablesTheManifestAndListsItPublicly()
    {
        var (service, stub) = MakeService(Routes(
            (("PUT", "/api/proxy/weather-api/manifest/settings"),
                (HttpStatusCode.OK, ManifestSettings))));

        var result = Parse(await ProducerSetupTools.PublishAsync(
            "weather-api", null, "Forecasts for agents", new[] { "weather", "data" },
            service, CancellationToken.None));

        var body = stub.BodyOf("PUT", "/api/proxy/weather-api/manifest/settings");
        body.GetProperty("manifestEnabled").GetBoolean().Should().BeTrue();
        body.GetProperty("manifestPubliclyListed").GetBoolean().Should().BeTrue();
        Str(body, "serviceDescription").Should().Be("Forecasts for agents");
        body.GetProperty("categories").EnumerateArray().Select(c => c.GetString())
            .Should().Equal("weather", "data");

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        Str(result, "openapiUrl").Should().Be($"{ApiBase}/l402/proxy/weather-api/openapi.json");
        Str(result, "manifestUrl")
            .Should().Be($"{ApiBase}/l402/proxy/weather-api/.well-known/l402-manifest.json");
    }

    [Fact]
    public async Task Publish_RenamesTheProxyFirstWhenAServiceNameIsGiven()
    {
        // `service.name` in the manifest is the proxy's own name, not a manifest field.
        var (service, stub) = MakeService(Routes(
            (("PUT", "/api/proxy/weather-api"), (HttpStatusCode.OK, ProxyCreated)),
            (("PUT", "/api/proxy/weather-api/manifest/settings"),
                (HttpStatusCode.OK, ManifestSettings))));

        await ProducerSetupTools.PublishAsync(
            "weather-api", "Weather for Agents", "Forecasts", null,
            service, CancellationToken.None);

        stub.Paths.Should().Equal(
            ("PUT", "/api/proxy/weather-api"),
            ("PUT", "/api/proxy/weather-api/manifest/settings"));
        Str(stub.BodyOf("PUT", "/api/proxy/weather-api"), "name").Should().Be("Weather for Agents");
    }

    [Fact]
    public async Task Publish_DoesNotPublishWhenTheRenameFails()
    {
        var (service, stub) = MakeService(Routes(
            (("PUT", "/api/proxy/weather-api"),
                (HttpStatusCode.NotFound, "{\"error\":\"Proxy not found\"}")),
            (("PUT", "/api/proxy/weather-api/manifest/settings"),
                (HttpStatusCode.OK, ManifestSettings))));

        var result = Parse(await ProducerSetupTools.PublishAsync(
            "weather-api", "Renamed", null, null, service, CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        Str(result, "error").Should().Contain("Proxy not found");
        stub.Paths.Should().NotContain(("PUT", "/api/proxy/weather-api/manifest/settings"));
    }

    [Fact]
    public async Task Publish_SurfacesThePlanGateOnPublicListing()
    {
        const string gate = """
            {
              "error": "plan_feature_disabled",
              "feature": "registry_listing",
              "message": "Public registry listing needs a paid plan.",
              "current_plan": "free"
            }
            """;
        var (service, _) = MakeService(Routes(
            (("PUT", "/api/proxy/weather-api/manifest/settings"),
                (HttpStatusCode.PaymentRequired, gate))));

        var result = Parse(await ProducerSetupTools.PublishAsync(
            "weather-api", null, null, null, service, CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        Str(result, "error").Should().Contain("paid plan");
        Str(result, "errorCode").Should().Be("plan_feature_disabled");
    }

    [Fact]
    public async Task Publish_RequiresAProxyId()
    {
        var (service, stub) = MakeService(Routes());

        var result = Parse(await ProducerSetupTools.PublishAsync(
            "", null, null, null, service, CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        stub.Requests.Should().BeEmpty();
    }

    // ── list_challenges ──────────────────────────────────────────────────────

    [Fact]
    public async Task ListChallenges_PassesTheStatusFilterAndPaging()
    {
        var (service, stub) = MakeService(Routes(
            (("GET", "/api/l402/challenges"), (HttpStatusCode.OK, Challenges))));

        var result = Parse(await ProducerSetupTools.ListChallengesAsync(
            "paid", 10, 20, service, CancellationToken.None));

        var query = stub.RequestFor("GET", "/api/l402/challenges").Query;
        query.Should().Contain("status=paid").And.Contain("limit=10").And.Contain("offset=20");
        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("total").GetInt32().Should().Be(1);
        Str(result.GetProperty("challenges")[0], "resource").Should().Be("/weather");
    }

    [Fact]
    public async Task ListChallenges_OmitsTheStatusParameterWhenUnfiltered()
    {
        var (service, stub) = MakeService(Routes(
            (("GET", "/api/l402/challenges"), (HttpStatusCode.OK, Challenges))));

        await ProducerSetupTools.ListChallengesAsync(null, 20, 0, service, CancellationToken.None);

        stub.RequestFor("GET", "/api/l402/challenges").Query.Should().NotContain("status=");
    }

    [Fact]
    public async Task ListChallenges_RejectsAnUnknownStatusBeforeSending()
    {
        var (service, stub) = MakeService(Routes());

        var result = Parse(await ProducerSetupTools.ListChallengesAsync(
            "settled", 20, 0, service, CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        Str(result, "error").Should().Contain("paid");
        stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ListChallenges_SurfacesTheApisInvalidFilterProblem()
    {
        const string problem = """
            {
              "type": "https://lightningenable.com/problems/invalid_status_filter",
              "title": "Invalid status filter",
              "detail": "status must be one of: paid, unpaid, expired.",
              "error": "invalid_status_filter"
            }
            """;
        var (service, _) = MakeService(Routes(
            (("GET", "/api/l402/challenges"), (HttpStatusCode.BadRequest, problem))));

        var result = Parse(await ProducerSetupTools.ListChallengesAsync(
            null, 20, 0, service, CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        Str(result, "error").Should().Contain("paid, unpaid, expired");
        Str(result, "errorType").Should().EndWith("invalid_status_filter");
    }

    // ── Shared gating ────────────────────────────────────────────────────────

    public static TheoryData<string> ProducerActions => new()
    {
        "status", "create_proxy", "add_endpoint", "publish", "list_challenges",
    };

    private static Task<string> Invoke(string action, ILightningEnableApiService? service)
        => action switch
        {
            "status" => ProducerSetupTools.StatusAsync(5, service, CancellationToken.None),
            "create_proxy" => ProducerSetupTools.CreateProxyAsync(
                "n", "https://x.example.test", null, 10, service, CancellationToken.None),
            "add_endpoint" => ProducerSetupTools.AddEndpointAsync(
                "p", "e", "/x", null, null, 1, service, CancellationToken.None),
            "publish" => ProducerSetupTools.PublishAsync(
                "p", null, null, null, service, CancellationToken.None),
            _ => ProducerSetupTools.ListChallengesAsync(null, 20, 0, service, CancellationToken.None),
        };

    [Theory]
    [MemberData(nameof(ProducerActions))]
    public async Task EveryActionRequiresTheMerchantApiKey(string action)
    {
        var (service, _) = MakeService(Routes(), apiKey: null);

        var result = Parse(await Invoke(action, service));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        Str(result, "error").Should().Contain("LIGHTNING_ENABLE_API_KEY");
    }

    [Theory]
    [MemberData(nameof(ProducerActions))]
    public async Task MissingServiceIsADescriptiveErrorNotACrash(string action)
    {
        var result = Parse(await Invoke(action, null));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        Str(result, "error").Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ConfigureReceive_MissingServiceIsADescriptiveErrorNotACrash()
    {
        var result = Parse(await ProducerSetupTools.ConfigureReceiveAsync(
            FixtureNwc, null, new StubOnboarding(), CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        Str(result, "error").Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task NoActionEverEchoesTheApiKey()
    {
        var (service, _) = MakeService(Routes(
            (("GET", "/api/l402/challenges"), (HttpStatusCode.OK, Challenges))));

        var raw = await ProducerSetupTools.ListChallengesAsync(
            null, 20, 0, service, CancellationToken.None);

        raw.Should().NotContain(FixtureApiKey);
    }
}
