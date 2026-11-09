using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// Ownership verification on <c>l402_producer</c>: <c>verify</c> with a <c>proxyId</c>, and
/// <c>verification_token</c>, against a mocked <c>ProxyVerificationController</c>.
///
/// <para>The method table, error codes, polling bounds and reason hints are pinned against
/// <c>shared/l402-producer-verify.json</c>. Mirrors
/// <c>python/lightning-enable-mcp/tests/test_producer_verify.py</c>.</para>
/// </summary>
public sealed class ProducerVerifyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "le-verify-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public ProducerVerifyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private const string ApiBase = "https://api.example.test";
    private const string ProxyId = "forecast-api-a1b2";
    private const string StatePath = "/api/proxy/forecast-api-a1b2/verification";
    private const string TokenPath = "/api/proxy/forecast-api-a1b2/verification/token";
    private const string VerifyPath = "/api/proxy/forecast-api-a1b2/verify";

    // Shaped like the server's token: 43 url-safe characters. Fake, and named so the gitleaks
    // generic-api-key rule does not read it as a credential.
    private const string FixtureVerificationValue = "fixture-verification-value-0000000000000000";

    // ── Scripted API ─────────────────────────────────────────────────────────

    private sealed record Reply(HttpStatusCode Status, string Body, int? RetryAfter = null);

    /// <summary>Answers each (method, path) from a queue; the last reply repeats.</summary>
    private sealed class ScriptedApi : HttpMessageHandler
    {
        private readonly Dictionary<(string, string), Queue<Reply>> _routes = new();

        public List<(string Method, string Path)> Requests { get; } = new();

        public Exception? Throws { get; set; }

        public ScriptedApi On(string method, string path, params Reply[] replies)
        {
            _routes[(method, path)] = new Queue<Reply>(replies);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method.Method, path));
            if (Throws is not null)
            {
                throw Throws;
            }

            if (!_routes.TryGetValue((request.Method.Method, path), out var queue))
            {
                // What a server without the route answers: a bare 404.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });
            }

            var reply = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            var response = new HttpResponseMessage(reply.Status)
            {
                Content = new StringContent(reply.Body, Encoding.UTF8, "application/json"),
            };
            if (reply.RetryAfter is { } seconds)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }
            return Task.FromResult(response);
        }

        public int Count(string method, string path) => Requests.Count(r => r.Method == method && r.Path == path);
    }

    private sealed class StubConfig : IBudgetConfigurationService
    {
        public UserBudgetConfiguration Configuration { get; } = new() { LightningEnableApiKey = "fixture-key-1" };
        public string ConfigFilePath => "/tmp/fixture-config.json";
        public bool ConfigFileExists => false;
        public void Reload() { }
    }

    private static LightningEnableApiService Service(ScriptedApi api)
    {
        var previousUrl = Environment.GetEnvironmentVariable("LIGHTNING_ENABLE_API_URL");
        try
        {
            Environment.SetEnvironmentVariable("LIGHTNING_ENABLE_API_URL", ApiBase);
            return new LightningEnableApiService(new HttpClient(api), new StubConfig());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIGHTNING_ENABLE_API_URL", previousUrl);
        }
    }

    private static readonly JsonObject Instructions = JsonNode.Parse("""
        {
          "tokenPlaceholder": "<token>",
          "methods": [
            {"method":"well-known","order":1,"summary":"Serve this JSON.","url":"https://api.example.com/.well-known/lightning-enable.json","recordName":null,"recordType":null,"headerName":null,"body":"{\"verify\":\"<token>\"}","value":"<token>"},
            {"method":"dns-txt","order":2,"summary":"Add a DNS TXT record.","url":null,"recordName":"_lightning-enable.api.example.com","recordType":"TXT","headerName":null,"body":null,"value":"<token>"},
            {"method":"header","order":3,"summary":"Return the header.","url":"https://api.example.com/v1/","recordName":null,"recordType":null,"headerName":"X-Lightning-Enable-Verify","body":null,"value":"<token>"}
          ],
          "tokenEndpoint": "/api/proxy/forecast-api-a1b2/verification/token",
          "verifyEndpoint": "/api/proxy/forecast-api-a1b2/verify",
          "note": "Publish the token by any one method."
        }
        """)!.AsObject();

    private static string State(
        bool verified = false, string? method = null, bool tokenIssued = true, bool listed = false,
        string? reason = null, bool? pending = null, int? retryAfterSeconds = null, string? token = null)
    {
        var state = new JsonObject
        {
            ["proxyId"] = ProxyId,
            ["verified"] = verified,
            ["method"] = method,
            ["verifiedAt"] = verified ? "2026-11-08T18:02:11Z" : null,
            ["lastCheckedAt"] = "2026-11-08T18:02:11Z",
            ["failures"] = verified ? 0 : 1,
            ["tokenIssued"] = tokenIssued,
            ["token"] = token,
            ["listed"] = listed,
            ["nextCheckAt"] = verified ? "2026-11-09T04:00:00Z" : null,
            ["checkedAt"] = "2026-11-08T18:02:11Z",
            ["reason"] = reason,
            ["pending"] = pending,
            ["retryAfterSeconds"] = retryAfterSeconds,
            ["instructions"] = Instructions.DeepClone(),
        };
        return state.ToJsonString();
    }

    private static Reply Ok(string body, int? retryAfter = null) => new(HttpStatusCode.OK, body, retryAfter);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static List<string> Strings(JsonElement element, string name)
        => element.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToList();

    private static readonly List<TimeSpan> NoDelays = new();

    private static Func<TimeSpan, CancellationToken, Task> Recording(List<TimeSpan> waits)
        => (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        };

    private Task<string> Verify(ScriptedApi api, string? method = null, List<TimeSpan>? waits = null)
        => ProducerVerification.VerifyAsync(
            ProxyId, method, Service(api), Recording(waits ?? new()), CancellationToken.None, ConfigPath);

    private Task<string> Token(ScriptedApi api, bool reissue = false, string? method = null)
        => ProducerVerification.TokenAsync(ProxyId, method, reissue, Service(api), CancellationToken.None, ConfigPath);

    // ── Shared fixture parity ────────────────────────────────────────────────

    private static JsonDocument Fixture() =>
        JsonDocument.Parse(File.ReadAllText(SharedFixture.Path("l402-producer-verify.json")));

    [Fact]
    public void Tables_MatchTheSharedFixture()
    {
        using var doc = Fixture();
        var root = doc.RootElement;

        root.GetProperty("errorCodes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetBoolean())
            .Should().Equal(ProducerVerification.ErrorCodes);
        root.GetProperty("methods").EnumerateArray()
            .Select(m => new VerificationMethodInfo(
                m.GetProperty("method").GetString()!, m.GetProperty("proves").GetString()!,
                m.GetProperty("chooseWhen").GetString()!))
            .Should().Equal(ProducerVerification.Methods);
        Strings(root, "liveMethods").Should().Equal(ProducerVerification.LiveMethods);
        root.GetProperty("maxPendingAttempts").GetInt32().Should().Be(ProducerVerification.MaxPendingAttempts);
        root.GetProperty("maxRetryAfterSeconds").GetInt32().Should().Be(ProducerVerification.MaxRetryAfterSeconds);
        root.GetProperty("reasonHints").EnumerateArray()
            .Select(h => new VerificationReasonHint(
                h.GetProperty("contains").GetString()!,
                h.GetProperty("method").ValueKind == JsonValueKind.Null ? null : h.GetProperty("method").GetString(),
                h.GetProperty("hint").GetString()!))
            .Should().Equal(ProducerVerification.ReasonHints);
        root.GetProperty("reasonHintFallback").GetString().Should().Be(ProducerVerification.ReasonHintFallback);
        var storedSteps = root.GetProperty("storedTokenSteps");
        storedSteps.GetProperty("staleWhen").GetString().Should().Be(ProducerVerification.StaleWhen);
        storedSteps.GetProperty("default").GetString().Should().Be(ProducerVerification.StoredTokenStep);
        storedSteps.GetProperty("stale").GetString().Should().Be(ProducerVerification.StaleStoredTokenStep);
        root.GetProperty("unprovenMethodSteps").EnumerateObject().Where(p => p.Name != "_comment")
            .ToDictionary(p => p.Name, p => p.Value.GetString()!)
            .Should().Equal(ProducerVerification.UnprovenMethodSteps);
        root.GetProperty("sellUrlVerification").GetProperty("noTokenNote").GetString()
            .Should().Be(ProducerSellUrl.NoVerificationTokenNote);

        var store = root.GetProperty("tokenStore");
        store.GetProperty("filePrefix").GetString().Should().Be(ProducerVerification.TokenFilePrefix);
        foreach (var example in store.GetProperty("examples").EnumerateArray())
        {
            Path.GetFileName(ProducerVerification.TokenFilePath("/tmp/x/config.json", example.GetProperty("proxyId").GetString()!))
                .Should().Be(example.GetProperty("fileName").GetString());
        }
    }

    [Fact]
    public void StoredTokenFile_HasTheFixtureFields()
    {
        using var doc = Fixture();
        var storage = ProducerVerification.StoreToken(ConfigPath, ProxyId, FixtureVerificationValue, "verification_token");

        storage.Written.Should().BeTrue();
        Path.GetDirectoryName(storage.Path).Should().Be(_dir);
        JsonNode.Parse(File.ReadAllText(storage.Path))!.AsObject().Select(p => p.Key)
            .Should().BeEquivalentTo(Strings(doc.RootElement.GetProperty("tokenStore"), "fields"));
    }

    [Fact]
    public async Task Verify_UsesAStoredToken_BeforePointingAtVerificationToken()
    {
        ProducerVerification.StoreToken(ConfigPath, ProxyId, FixtureVerificationValue, "sell_url");
        var api = new ScriptedApi().On("POST", VerifyPath, Ok(State(reason: "dns-txt: no TXT record at x")));

        var result = Parse(await Verify(api));

        result.GetProperty("storedToken").GetProperty("token").GetString().Should().Be(FixtureVerificationValue);
        result.GetProperty("storedToken").GetProperty("source").GetString().Should().Be("sell_url");
        Strings(result, "nextSteps")[0].Should().Be(
            ProducerVerification.StoredTokenStep.Replace("{path}", ProducerVerification.LoadToken(ConfigPath, ProxyId)!.Path));
    }

    [Fact]
    public async Task Verify_WhenTheReasonSaysNotCurrent_CallsTheStoredTokenStale_AndPointsAtReissue()
    {
        ProducerVerification.StoreToken(ConfigPath, ProxyId, FixtureVerificationValue, "sell_url");
        var api = new ScriptedApi().On("POST", VerifyPath,
            Ok(State(reason: "well-known: \"verify\" does not contain this proxy's current token")));

        var first = Strings(Parse(await Verify(api)), "nextSteps")[0];

        first.Should().Contain("stale").And.Contain($"action=verification_token proxy_id={ProxyId}");
        first.Should().NotContain("Publish it unless");
    }

    [Fact]
    public async Task Verify_GrandfatheredProxy_ReportedUnverified_LeadsWithItsStanding()
    {
        var api = new ScriptedApi().On("POST", VerifyPath,
            Ok(State(verified: false, method: "grandfathered", listed: true)));

        Strings(Parse(await Verify(api)), "nextSteps")[0]
            .Should().Be(ProducerVerification.UnprovenMethodSteps["grandfathered"]);
    }

    [Fact]
    public void ExactServerReasons_SelectTheirHint()
    {
        using var doc = Fixture();
        foreach (var sample in doc.RootElement.GetProperty("serverReasonSamples").EnumerateObject()
                     .Where(p => p.Name != "_comment"))
        {
            ProducerVerification.HintsFor(sample.Name, null)[0].Should().Be(sample.Value.GetString(), sample.Name);
        }
    }

    [Fact]
    public async Task Verify_DoesNotEchoAStoredToken_OnceVerified()
    {
        ProducerVerification.StoreToken(ConfigPath, ProxyId, FixtureVerificationValue, "sell_url");
        var api = new ScriptedApi().On("POST", VerifyPath, Ok(State(verified: true, method: "header")));

        (await Verify(api)).Should().NotContain(FixtureVerificationValue);
    }

    [Fact]
    public async Task AdvertisedSchema_TagsTheVerificationArguments_AndTheDescriptionIsFindable()
    {
        using var fixture = Fixture();
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var tool = (await host.AdvertisedToolsAsync()).Single(t => t.Name == "l402_producer");
        var properties = tool.JsonSchema.GetProperty("properties");

        static string Camel(string snake) => System.Text.RegularExpressions.Regex.Replace(
            snake, "_([a-z])", m => m.Groups[1].Value.ToUpperInvariant());

        foreach (var argument in Strings(fixture.RootElement, "verifyArguments"))
        {
            properties.GetProperty(Camel(argument)).GetProperty("description").GetString().Should().Contain("verify");
        }
        foreach (var argument in Strings(fixture.RootElement, "tokenArguments"))
        {
            var description = properties.GetProperty(Camel(argument)).GetProperty("description").GetString()!;
            (description.Contains("verification_token") || description.Contains("verify*"))
                .Should().BeTrue(argument);
        }

        var action = properties.GetProperty("action").GetProperty("description").GetString()!;
        action.Should().Contain("verification_token").And.Contain("proxyId");

        var text = tool.Description!;
        text.Should().Contain("ownership").And.Contain("domain").And.Contain("listed");
        text.Should().NotMatchRegex(@"\d+\s*(sats?|USD|\$)");
        foreach (var word in new[] { "non-custodial", "self-custody", "trustless", "decentralized" })
        {
            text.ToLowerInvariant().Should().NotContain(word);
            action.ToLowerInvariant().Should().NotContain(word);
        }
    }

    // ── verify ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Verify_ReturnsTheState_WhatEachMethodProves_AndPointsAtPublish()
    {
        var api = new ScriptedApi().On("POST", VerifyPath, Ok(State(verified: true, method: "dns-txt")));

        var result = Parse(await Verify(api));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("verified").GetBoolean().Should().BeTrue();
        result.GetProperty("method").GetString().Should().Be("dns-txt");
        result.GetProperty("attempts").GetInt32().Should().Be(1);
        result.TryGetProperty("token", out _).Should().BeFalse("the state never carries the token");
        result.GetProperty("methods").EnumerateArray().Select(m => m.GetProperty("proves").GetString())
            .Should().Equal(ProducerVerification.Methods.Select(m => m.Proves));
        var steps = string.Join(" ", Strings(result, "nextSteps"));
        steps.Should().Contain("DNS zone").And.Contain($"action=publish proxy_id={ProxyId}");
        api.Requests.Should().Equal(("POST", VerifyPath));
    }

    [Fact]
    public async Task Verify_WhenAlreadyListed_SaysSoInsteadOfPointingAtPublish()
    {
        var api = new ScriptedApi().On("POST", VerifyPath, Ok(State(verified: true, method: "header", listed: true)));

        var steps = string.Join(" ", Strings(Parse(await Verify(api)), "nextSteps"));

        steps.Should().Contain("listed publicly").And.NotContain("action=publish");
    }

    [Fact]
    public async Task Verify_MethodOnlyFiltersTheInstructionsShown()
    {
        var api = new ScriptedApi().On("POST", VerifyPath, Ok(State()));

        var result = Parse(await Verify(api, method: "DNS_TXT"));

        var methods = result.GetProperty("methods").EnumerateArray().ToList();
        methods.Should().ContainSingle();
        methods[0].GetProperty("recordName").GetString().Should().Be("_lightning-enable.api.example.com");
        methods[0].GetProperty("value").GetString().Should().Be("<token>");
    }

    [Fact]
    public async Task Verify_PollsPendingUntilVerified_HonoringRetryAfterCappedAtTenSeconds()
    {
        var api = new ScriptedApi().On("POST", VerifyPath,
            Ok(State(pending: true, retryAfterSeconds: 30, reason: "the check did not finish within 15s"), 30),
            Ok(State(pending: true, retryAfterSeconds: 4), 4),
            Ok(State(verified: true, method: "well-known")));
        var waits = new List<TimeSpan>();

        var result = Parse(await Verify(api, waits: waits));

        result.GetProperty("verified").GetBoolean().Should().BeTrue();
        result.GetProperty("attempts").GetInt32().Should().Be(3);
        waits.Should().Equal(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(4));
        api.Count("POST", VerifyPath).Should().Be(3);
    }

    [Fact]
    public async Task Verify_StopsAfterFourPendingChecks_AndSaysWhenToCallAgain()
    {
        var api = new ScriptedApi().On("POST", VerifyPath, Ok(State(pending: true, retryAfterSeconds: 3), 3));
        var waits = new List<TimeSpan>();

        var result = Parse(await Verify(api, waits: waits));

        api.Count("POST", VerifyPath).Should().Be(ProducerVerification.MaxPendingAttempts);
        waits.Should().HaveCount(ProducerVerification.MaxPendingAttempts - 1).And.OnlyContain(w => w == TimeSpan.FromSeconds(3));
        result.GetProperty("pending").GetBoolean().Should().BeTrue();
        string.Join(" ", Strings(result, "nextSteps")).Should().Contain("Call verify again in 3 seconds");
    }

    [Fact]
    public async Task Verify_FailedCheck_TurnsTheReasonIntoNextSteps()
    {
        const string reason =
            "well-known: /.well-known/lightning-enable.json answered HTTP 404; dns-txt: no TXT record at "
            + "_lightning-enable.api.example.com; header: HEAD answered HTTP 401 without X-Lightning-Enable-Verify; "
            + "GET answered HTTP 401 without X-Lightning-Enable-Verify";
        var api = new ScriptedApi().On("POST", VerifyPath, Ok(State(reason: reason)));

        var all = Strings(Parse(await Verify(api)), "nextSteps");
        all.Should().HaveCount(4);
        all.Last().Should().Be(ProducerVerification.ReasonHintFallback);

        var dnsOnly = Strings(Parse(await Verify(api, method: "dns-txt")), "nextSteps");
        dnsOnly.Should().HaveCount(2);
        dnsOnly[0].Should().Contain("dig +short TXT");
    }

    [Fact]
    public void ReasonHints_GiveEachMethodItsNarrowestMatch()
    {
        var hints = ProducerVerification.HintsFor(
            "dns-txt: no TXT record at _lightning-enable.api.example.com contains this proxy's current token", null);

        hints.Should().HaveCount(2);
        hints[0].Should().Contain("Remove quotes");
    }

    // ── verification_token ───────────────────────────────────────────────────

    [Fact]
    public async Task Token_IsReturnedOnce_WithTheStorageNote_AndPlaceholderInstructions()
    {
        var api = new ScriptedApi()
            .On("GET", StatePath, Ok(State(tokenIssued: false)))
            .On("POST", TokenPath, Ok(State(tokenIssued: true, token: FixtureVerificationValue)));

        var raw = await Token(api);
        var result = Parse(raw);

        result.GetProperty("token").GetString().Should().Be(FixtureVerificationValue);
        raw.Split(FixtureVerificationValue).Length.Should().Be(2, "the token appears exactly once");
        result.GetProperty("tokenNote").GetString().Should().Contain("shown only this once");
        result.GetProperty("methods").EnumerateArray().Should().OnlyContain(m => m.GetProperty("value").GetString() == "<token>");
        result.TryGetProperty("warnings", out _).Should().BeFalse();
        api.Requests.Should().Equal(("GET", StatePath), ("POST", TokenPath));
        result.GetProperty("tokenStorage").GetProperty("written").GetBoolean().Should().BeTrue();
        result.GetProperty("tokenNote").GetString().Should().Contain(result.GetProperty("tokenStorage").GetProperty("path").GetString());
        ProducerVerification.LoadToken(ConfigPath, ProxyId)!.Token.Should().Be(FixtureVerificationValue);
    }

    [Fact]
    public async Task Token_RefusesToUnverifyALiveVerification_UnlessReissue()
    {
        var api = new ScriptedApi()
            .On("GET", StatePath, Ok(State(verified: true, method: "well-known", listed: true)))
            .On("POST", TokenPath, Ok(State(token: FixtureVerificationValue)));

        var refused = Parse(await Token(api));
        refused.GetProperty("errorCode").GetString().Should().Be(ProducerVerification.ReissueRequired);
        refused.GetProperty("error").GetString().Should().Contain("unlisted").And.Contain("reissue=true");
        api.Count("POST", TokenPath).Should().Be(0);

        var rotated = Parse(await Token(api, reissue: true));
        rotated.GetProperty("token").GetString().Should().Be(FixtureVerificationValue);
        string.Join(" ", Strings(rotated, "warnings")).Should().Contain("unverified and unlisted");
    }

    [Fact]
    public async Task Token_ForAGrandfatheredProxy_NeedsNoReissue()
    {
        var api = new ScriptedApi()
            .On("GET", StatePath, Ok(State(verified: true, method: "grandfathered", listed: true)))
            .On("POST", TokenPath, Ok(State(verified: true, method: "grandfathered", token: FixtureVerificationValue)));

        var result = Parse(await Token(api));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        string.Join(" ", Strings(result, "warnings")).Should().Contain("grandfathered standing");
    }

    // ── Errors ───────────────────────────────────────────────────────────────

    public static IEnumerable<object?[]> ErrorCases() => new[]
    {
        new object?[] { HttpStatusCode.Unauthorized, "{\"error\":\"Merchant authentication required\"}", null, ProducerVerification.Unauthorized },
        new object?[] { HttpStatusCode.NotFound, "{\"error\":\"Proxy not found\"}", null, ProducerVerification.ProxyNotFound },
        new object?[] { HttpStatusCode.NotFound, "", null, ProducerVerification.FeatureNotEnabled },
        new object?[]
        {
            HttpStatusCode.Conflict,
            "{\"type\":\"https://lightningenable.com/problems/verification_token_required\",\"title\":\"No verification token\",\"status\":409,\"detail\":\"This proxy has no verification token yet.\",\"error\":\"verification_token_required\",\"message\":\"This proxy has no verification token yet.\",\"tokenEndpoint\":\"/api/proxy/forecast-api-a1b2/verification/token\"}",
            null, ProducerVerification.TokenRequired,
        },
        new object?[] { HttpStatusCode.TooManyRequests, "{\"error\":\"Too many requests\",\"message\":\"Rate limit exceeded. Please try again later.\",\"retryAfter\":42.0}", 42, ProducerVerification.RateLimited },
        new object?[] { HttpStatusCode.InternalServerError, "{}", null, ProducerVerification.UpstreamError },
        new object?[] { HttpStatusCode.BadRequest, "{\"error\":\"bad\"}", null, ProducerVerification.RequestRejected },
    };

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public async Task Verify_MapsEachServerError_ToItsCode(HttpStatusCode status, string body, int? retryAfter, string code)
    {
        var api = new ScriptedApi().On("POST", VerifyPath, new Reply(status, body, retryAfter));

        var result = Parse(await Verify(api));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("errorCode").GetString().Should().Be(code);
        result.GetProperty("retryable").GetBoolean().Should().Be(ProducerVerification.ErrorCodes[code]);
        result.GetProperty("httpStatus").GetInt32().Should().Be((int)status);
        if (retryAfter is not null)
        {
            result.GetProperty("retryAfterSeconds").GetInt32().Should().Be(retryAfter);
        }
        if (code == ProducerVerification.TokenRequired)
        {
            string.Join(" ", Strings(result, "nextSteps")).Should().Contain("action=verification_token");
        }
        api.Count("POST", VerifyPath).Should().Be(1, "an error is never polled");
    }

    [Fact]
    public async Task ServerWithoutTheRoutes_IsReportedAsFeatureNotEnabled_ForBothActions()
    {
        var api = new ScriptedApi(); // every route answers a bare 404

        Parse(await Verify(api)).GetProperty("errorCode").GetString().Should().Be(ProducerVerification.FeatureNotEnabled);
        var token = Parse(await Token(api));
        token.GetProperty("errorCode").GetString().Should().Be(ProducerVerification.FeatureNotEnabled);
        token.GetProperty("error").GetString().Should().Contain(ApiBase);
        api.Count("POST", TokenPath).Should().Be(0);
    }

    [Fact]
    public async Task TransportFailure_IsARetryableUpstreamError()
    {
        var api = new ScriptedApi { Throws = new HttpRequestException("connection refused") };

        var result = Parse(await Verify(api));

        result.GetProperty("errorCode").GetString().Should().Be(ProducerVerification.UpstreamError);
        result.GetProperty("retryable").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(null, null, "proxy_id_required")]
    [InlineData(ProxyId, "smoke-signal", "invalid_method")]
    public async Task LocalValidation_HappensBeforeAnyRequest(string? proxyId, string? method, string code)
    {
        var api = new ScriptedApi();

        var verify = await ProducerVerification.VerifyAsync(proxyId, method, Service(api), null, CancellationToken.None, ConfigPath);
        var token = await ProducerVerification.TokenAsync(proxyId, method, false, Service(api), CancellationToken.None, ConfigPath);

        Parse(verify).GetProperty("errorCode").GetString().Should().Be(code);
        Parse(token).GetProperty("errorCode").GetString().Should().Be(code);
        api.Requests.Should().BeEmpty();
    }

    // ── Dispatch ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAction_WithProxyId_ChecksOwnership_AndWithBothIsRefused()
    {
        var api = new ScriptedApi().On("POST", VerifyPath, Ok(State(verified: true, method: "header")));

        var ownership = Parse(await L402ProducerTool.L402Producer(
            L402ProducerAction.verify, proxyId: ProxyId, apiService: Service(api), configService: Config()));
        ownership.GetProperty("verified").GetBoolean().Should().BeTrue();

        var both = Parse(await L402ProducerTool.L402Producer(
            L402ProducerAction.verify, proxyId: ProxyId, macaroon: "AgEL", preimage: "00", apiService: Service(api)));
        both.GetProperty("errorCode").GetString().Should().Be(ProducerVerification.AmbiguousVerify);

        var token = Parse(await L402ProducerTool.L402Producer(
            L402ProducerAction.verification_token, apiService: Service(api), configService: Config()));
        token.GetProperty("errorCode").GetString().Should().Be(ProducerVerification.ProxyIdRequired);
    }

    public static IEnumerable<object[]> RoutingCases()
    {
        using var doc = Fixture();
        return doc.RootElement.GetProperty("verifyRouting").GetProperty("cases").EnumerateArray()
            .Select(c => new object[]
            {
                string.Join(",", c.GetProperty("args").EnumerateArray().Select(a => a.GetString())),
                c.GetProperty("route").GetString()!,
            })
            .ToList();
    }

    [Theory]
    [MemberData(nameof(RoutingCases))]
    public async Task VerifyRouting_MatchesTheSharedTable(string args, string route)
    {
        var given = args.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var api = new Mock<ILightningEnableApiService>();
        api.SetupGet(a => a.IsConfigured).Returns(true);
        api.SetupGet(a => a.BaseUrl).Returns(ApiBase);
        api.Setup(a => a.VerifyTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VerifyTokenResult { Success = true, Valid = true, Resource = "r" });
        api.Setup(a => a.VerifyProxyOwnershipAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiCallResult { Success = true, Data = JsonNode.Parse(State(verified: true, method: "header"))!.AsObject() });

        var result = Parse(await L402ProducerTool.L402Producer(
            L402ProducerAction.verify,
            proxyId: given.Contains("proxy_id") ? ProxyId : null,
            macaroon: given.Contains("macaroon") ? "AgEL" : null,
            preimage: given.Contains("preimage") ? "00" : null,
            apiService: api.Object,
            configService: Config()));

        var ownershipCalls = api.Invocations.Count(i => i.Method.Name == nameof(ILightningEnableApiService.VerifyProxyOwnershipAsync));
        var paymentCalls = api.Invocations.Count(i => i.Method.Name == nameof(ILightningEnableApiService.VerifyTokenAsync));
        var ambiguous = result.TryGetProperty("errorCode", out var code) && code.GetString() == ProducerVerification.AmbiguousVerify;
        var actual = ambiguous ? "ambiguous" : ownershipCalls > 0 ? "ownership" : "payment";

        actual.Should().Be(route, args);
        if (route == "payment")
        {
            ownershipCalls.Should().Be(0);
            // Bare verify fails the payment tool's own argument check; with both it reaches the API.
            if (given.Count == 0)
                result.GetProperty("error").GetString().Should().Contain("Macaroon is required");
            else
                paymentCalls.Should().Be(1);
        }
        else
        {
            paymentCalls.Should().Be(0);
        }
    }

    [Fact]
    public async Task Publish_RefusedForOwnership_PointsAtVerify()
    {
        var api = new ScriptedApi().On("PUT", "/api/proxy/forecast-api-a1b2/manifest/settings",
            new Reply(HttpStatusCode.UnprocessableEntity,
                "{\"error\":\"ownership_verification_required\",\"message\":\"Verify ownership first.\",\"verify_endpoint\":\"/api/proxy/forecast-api-a1b2/verify\"}"));

        var result = Parse(await ProducerSetupTools.PublishAsync(ProxyId, null, null, null, Service(api), CancellationToken.None));

        result.GetProperty("errorCode").GetString().Should().Be("ownership_verification_required");
        result.GetProperty("hint").GetString().Should().Contain($"action=verify proxy_id={ProxyId}");
    }

    // ── publish_agent_capability ─────────────────────────────────────────────

    private IBudgetConfigurationService Config()
    {
        var config = new Mock<IBudgetConfigurationService>();
        config.SetupGet(c => c.ConfigFilePath).Returns(ConfigPath);
        return config.Object;
    }

    private static Mock<IAgentService> Agent(AgentPublishResult result)
    {
        var agent = new Mock<IAgentService>();
        agent.SetupGet(a => a.IsConfigured).Returns(true);
        agent.Setup(a => a.PublishCapabilityAsync(
                It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string[]?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return agent;
    }

    [Fact]
    public async Task Capability_NotPublished_SaysSo_ReturnsTheTokenOnce_AndPointsAtVerify()
    {
        var agent = Agent(new AgentPublishResult
        {
            Success = true,
            L402Endpoint = $"{ApiBase}/l402/proxy/{ProxyId}",
            Published = false,
            Reason = "ownership_verification_required",
            ProxyId = ProxyId,
            VerifyEndpoint = $"/api/proxy/{ProxyId}/verify",
            VerificationToken = FixtureVerificationValue,
            VerificationInstructions = Instructions.DeepClone().AsObject(),
        });

        var raw = await AgentPublishTool.PublishAgentCapability(
            "forecast", new[] { "weather" }, "Forecasts", 10, targetUrl: "https://api.example.com/v1",
            agentService: agent.Object, configService: Config());
        var result = Parse(raw);

        result.GetProperty("published").GetBoolean().Should().BeFalse();
        result.GetProperty("reason").GetString().Should().Be("ownership_verification_required");
        result.GetProperty("message").GetString().Should().Contain("NOT published").And.NotContain("successfully");
        raw.Split(FixtureVerificationValue).Length.Should().Be(2, "the token appears exactly once");
        result.GetProperty("verification").GetProperty("tokenNote").GetString().Should().Contain("shown only this once");
        result.GetProperty("verification").GetProperty("methods").GetArrayLength().Should().Be(3);
        string.Join(" ", Strings(result, "nextSteps"))
            .Should().Contain($"l402_producer(action=\"verify\", proxy_id=\"{ProxyId}\")");
        result.GetProperty("verification").GetProperty("tokenStorage").GetProperty("written").GetBoolean().Should().BeTrue();
        ProducerVerification.LoadToken(ConfigPath, ProxyId)!.Source.Should().Be("publish_agent_capability");
    }

    [Fact]
    public async Task Capability_Published_KeepsTheOldSuccessPath()
    {
        var agent = Agent(new AgentPublishResult
        {
            Success = true, EventId = "evt1", L402Endpoint = $"{ApiBase}/l402/proxy/{ProxyId}", Published = true,
        });

        var result = Parse(await AgentPublishTool.PublishAgentCapability(
            "forecast", new[] { "weather" }, "Forecasts", 10, agentService: agent.Object));

        result.GetProperty("eventId").GetString().Should().Be("evt1");
        result.GetProperty("message").GetString().Should().Contain("published successfully");
    }

    [Fact]
    public async Task Capability_FromAServerWithoutThePublishedField_KeepsTheOldSuccessPath()
    {
        var agent = Agent(new AgentPublishResult
        {
            Success = true, EventId = "evt1", L402Endpoint = $"{ApiBase}/l402/proxy/{ProxyId}", Published = null,
        });

        var result = Parse(await AgentPublishTool.PublishAgentCapability(
            "forecast", new[] { "weather" }, "Forecasts", 10, agentService: agent.Object));

        result.GetProperty("eventId").GetString().Should().Be("evt1");
        result.GetProperty("message").GetString().Should().Contain("published successfully");
    }

    [Fact]
    public async Task AgentService_ParsesTheUnpublishedShape()
    {
        var body = new JsonObject
        {
            ["serviceId"] = "forecast",
            ["proxyId"] = ProxyId,
            ["l402Endpoint"] = $"{ApiBase}/l402/proxy/{ProxyId}",
            ["categories"] = new JsonArray("weather"),
            ["priceSats"] = 10,
            ["published"] = false,
            ["reason"] = "ownership_verification_required",
            ["verifyEndpoint"] = $"/api/proxy/{ProxyId}/verify",
            ["verification"] = new JsonObject
            {
                ["token"] = FixtureVerificationValue,
                ["instructions"] = Instructions.DeepClone(),
            },
        }.ToJsonString();
        var api = new ScriptedApi().On("POST", "/api/agents/capabilities", new Reply(HttpStatusCode.Created, body));

        var previousUrl = Environment.GetEnvironmentVariable("LIGHTNING_ENABLE_API_URL");
        AgentService service;
        try
        {
            Environment.SetEnvironmentVariable("LIGHTNING_ENABLE_API_URL", ApiBase);
            service = new AgentService(new HttpClient(api), new StubConfig());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIGHTNING_ENABLE_API_URL", previousUrl);
        }

        var result = await service.PublishCapabilityAsync(
            "forecast", new[] { "weather" }, "Forecasts", 10, null, "https://api.example.com/v1", null, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Published.Should().BeFalse();
        result.Reason.Should().Be("ownership_verification_required");
        result.ProxyId.Should().Be(ProxyId);
        result.VerifyEndpoint.Should().Be($"/api/proxy/{ProxyId}/verify");
        result.VerificationToken.Should().Be(FixtureVerificationValue);
        result.VerificationInstructions!["methods"]!.AsArray().Should().HaveCount(3);
        result.EventId.Should().BeNull();
    }
    // ── unverified publish: the shared field contract ────────────────────────

    private static JsonElement Unverified(JsonDocument doc) => doc.RootElement.GetProperty("unverifiedPublish");

    private static string? TokenAtPath(JsonElement result, JsonElement unverified)
    {
        var node = result;
        foreach (var key in unverified.GetProperty("tokenPath").EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key.GetString()!, out node))
            {
                return null;
            }
        }
        return node.GetString();
    }

    private static void AssertUnverifiedFields(JsonElement result, JsonElement unverified)
    {
        foreach (var field in unverified.GetProperty("fields").EnumerateObject())
        {
            result.TryGetProperty(field.Name, out var actual).Should().BeTrue($"{field.Name} must be a field");
            JsonNode.DeepEquals(JsonNode.Parse(actual.GetRawText()), JsonNode.Parse(field.Value.GetRawText()))
                .Should().BeTrue($"{field.Name} is {actual.GetRawText()}, expected {field.Value.GetRawText()}");
        }
    }

    private static AgentService AgentServiceFor(ScriptedApi api)
    {
        var previousUrl = Environment.GetEnvironmentVariable("LIGHTNING_ENABLE_API_URL");
        try
        {
            Environment.SetEnvironmentVariable("LIGHTNING_ENABLE_API_URL", ApiBase);
            return new AgentService(new HttpClient(api), new StubConfig());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIGHTNING_ENABLE_API_URL", previousUrl);
        }
    }

    private async Task<string> PublishCapability(JsonObject body)
    {
        var api = new ScriptedApi().On("POST", "/api/agents/capabilities",
            new Reply(HttpStatusCode.Created, body.ToJsonString()));
        return await AgentPublishTool.PublishAgentCapability(
            "forecast", new[] { "weather" }, "Forecasts", 10, targetUrl: "https://api.example.com/v1",
            agentService: AgentServiceFor(api), configService: Config());
    }

    [Fact]
    public async Task Unverified_CapabilityPublish_CarriesTheSharedFields()
    {
        using var doc = Fixture();
        var unverified = Unverified(doc);
        var body = JsonNode.Parse(unverified.GetProperty("capabilityResponse").GetProperty("body").GetRawText())!.AsObject();

        var result = Parse(await PublishCapability(body));

        AssertUnverifiedFields(result, unverified);
        TokenAtPath(result, unverified).Should().Be(body["verification"]!["token"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unverified_CapabilityPublish_WithoutAToken_InventsNone()
    {
        using var doc = Fixture();
        var unverified = Unverified(doc);
        var body = JsonNode.Parse(unverified.GetProperty("capabilityResponse").GetProperty("body").GetRawText())!.AsObject();
        body["verification"]!.AsObject().Remove("token");

        var raw = await PublishCapability(body);
        var result = Parse(raw);

        AssertUnverifiedFields(result, unverified);
        TokenAtPath(result, unverified).Should().BeNull();
        raw.Should().NotContain(FixtureVerificationValue);
    }

    [Fact]
    public async Task Unverified_ListingRefusal_CarriesTheSharedFields()
    {
        using var doc = Fixture();
        var unverified = Unverified(doc);
        var refusal = unverified.GetProperty("listingRefusal");
        var api = new ScriptedApi().On("PUT", "/api/proxy/forecast-api-a1b2/manifest/settings",
            new Reply((HttpStatusCode)refusal.GetProperty("status").GetInt32(), refusal.GetProperty("body").GetRawText()));

        var result = Parse(await ProducerSetupTools.PublishAsync(ProxyId, null, null, null, Service(api), CancellationToken.None));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("errorCode").GetString().Should().Be("ownership_verification_required");
        AssertUnverifiedFields(result, unverified);
        TokenAtPath(result, unverified).Should().BeNull();
    }
}
