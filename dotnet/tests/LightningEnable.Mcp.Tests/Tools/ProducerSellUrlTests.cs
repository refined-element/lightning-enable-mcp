using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// <c>l402_producer(action="sell_url")</c> against a scripted claim endpoint: quote, phase 1
/// (402), payment through the pay_l402_challenge path, phase 2, key storage, error mapping,
/// phase-2 retries, crash recovery, the money-safety rules for ambiguous payments, and the
/// redeemed marker. No network: the claim client is a fake and the wallet is a mock.
///
/// <para>Mirrors <c>python/lightning-enable-mcp/tests/test_producer_sell_url.py</c>; both
/// read <c>shared/l402-producer-sell-url.json</c>.</para>
/// </summary>
public sealed class ProducerSellUrlTests : IDisposable
{
    private const string TargetUrl = "https://api.example-weather.dev/v1";
    private const string Address = "alice@wallet.example.com";
    private const string Terms = "2026-11";
    private const string Macaroon = "MDAyZGxvY2F0aW9uIGZpeHR1cmU";
    private const string Preimage = "ea195ec92c7e00000000000000000000000000000000000000000000000000aa";
    private const string FreshKey = "le_fixture_claim_key_1";
    private const string PricingNote = "Server pricing note, verbatim.";
    private const string ServerBodyRule = "Server body rule, verbatim.";
    private static readonly string Invoice = TestInvoices.Build("lnbc1500n"); // 150 sats

    private readonly string _dir;
    private readonly string _configPath;
    private readonly FakeClaimClient _claim = new();
    private readonly Mock<IL402HttpClient> _l402 = new();
    private readonly Mock<IWalletService> _wallet = new();
    private readonly Mock<IBudgetService> _budget = new();
    private readonly List<TimeSpan> _delays = new();
    private IOperationLedger? _ledger;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private string? _envKey;

    public ProducerSellUrlTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "le-sell-url-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "config.json");

        _wallet.SetupGet(w => w.IsConfigured).Returns(true);
        _l402.Setup(c => c.PayChallengeAsync(Macaroon, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync($"{Macaroon}:{Preimage}");
        SetApproval(ApprovalLevel.AutoApprove);
        _budget.Setup(b => b.GetUserConfiguration()).Returns(new UserBudgetConfiguration());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────

    private void SetApproval(ApprovalLevel level, string? denial = null) =>
        _budget.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long sats, CancellationToken _) => new ApprovalCheckResult
            {
                Level = level,
                AmountSats = sats,
                AmountUsd = 0.10m,
                DenialReason = denial,
                RemainingSessionBudgetUsd = 1.00m,
            });

    private static string QuoteJson(long sats = 150, string terms = Terms) => new JsonObject
    {
        ["enabled"] = true,
        ["price"] = new JsonObject { ["sats"] = sats, ["usdCents"] = 10, ["validForSeconds"] = 900 },
        ["covers"] = new JsonObject { ["days"] = 30, ["plan"] = "individual" },
        ["renewal"] = new JsonObject { ["endpoint"] = "/api/merchant/subscription/renew", ["baseSats"] = 30000, ["periodDays"] = 30 },
        ["limits"] = new JsonObject { ["maxProxyPriceSats"] = 1000000, ["minProxyPriceSats"] = 1 },
        ["terms"] = new JsonObject { ["version"] = terms, ["url"] = "https://lightningenable.com/terms" },
        ["listing"] = new JsonObject { ["available"] = false, ["note"] = "pending verification" },
        ["pricingNote"] = PricingNote,
        ["bodyRule"] = ServerBodyRule,
    }.ToJsonString();

    private string ChallengeJson(long sats = 150) => new JsonObject
    {
        ["invoice"] = Invoice,
        ["macaroon"] = Macaroon,
        ["paymentHash"] = "2859e1e143bc0000",
        ["expiresAt"] = _now.AddMinutes(10).UtcDateTime.ToString("O"),
        ["priceSats"] = sats,
        ["instructions"] = "Pay the Lightning invoice, then retry.",
    }.ToJsonString();

    private static string ClaimedJson(bool replayed = false, string? apiKey = FreshKey, bool keyRotated = false,
        string recovery = "none")
    {
        var o = new JsonObject
        {
            ["merchantId"] = 3,
            ["proxy"] = new JsonObject
            {
                ["proxyId"] = "claimed-weather-api",
                ["url"] = "https://api.example.test/l402/proxy/claimed-weather-api/",
                ["manifest"] = "https://api.example.test/l402/proxy/claimed-weather-api/.well-known/l402-manifest.json",
                ["openapi"] = "https://api.example.test/l402/proxy/claimed-weather-api/openapi.json",
                ["active"] = true,
            },
            ["receive"] = new JsonObject { ["lane"] = "lightningAddress", ["status"] = "live" },
            ["connectUrl"] = null,
            ["paidThrough"] = "2026-12-08T21:00:30Z",
            ["renew"] = new JsonObject { ["endpoint"] = "/api/merchant/subscription/renew", ["quoteSats"] = 30000, ["periodDays"] = 30 },
            ["recovery"] = recovery,
            ["listPublicly"] = false,
            ["listing"] = new JsonObject { ["requested"] = false, ["listed"] = false, ["reason"] = "verification_required" },
            ["warnings"] = new JsonArray("No recovery binding: this API key is the only credential. Store it securely."),
            ["replayed"] = replayed,
            ["keyRotated"] = keyRotated,
        };
        if (apiKey != null) o["apiKey"] = apiKey;
        return o.ToJsonString();
    }

    private static string Problem(int status, string code, string detail, JsonObject? extra = null)
    {
        var o = new JsonObject
        {
            ["type"] = "https://lightningenable.com/problems/" + code,
            ["title"] = code,
            ["status"] = status,
            ["detail"] = detail,
            ["error"] = code,
            ["message"] = detail,
        };
        if (extra != null)
            foreach (var (k, v) in extra) o[k] = v?.DeepClone();
        return o.ToJsonString();
    }

    private Task<string> SellAsync(string? acceptTerms = Terms, string? confirmationNonce = null, string? name = null,
        string? recoveryEmail = null, IL402HttpClient? l402 = null, IWalletService? wallet = null,
        CancellationToken ct = default) =>
        ProducerSellUrl.SellUrlAsync(
            TargetUrl, Address, defaultPriceSats: 21, name: name, description: "Weather for agents",
            recoveryEmail: recoveryEmail, recoveryNostrPubkey: null, listPublicly: false, acceptTerms: acceptTerms,
            confirmationNonce: confirmationNonce, claimClient: _claim,
            l402Client: l402 ?? _l402.Object, walletService: wallet ?? _wallet.Object, ledger: _ledger,
            budgetService: _budget.Object, priceService: null, paymentHistory: null, configService: null, server: null,
            options: new SellUrlOptions
            {
                ConfigPath = _configPath,
                EnvironmentApiKey = () => _envKey,
                Delay = (d, _) => { _delays.Add(d); return Task.CompletedTask; },
                UtcNow = () => _now,
            },
            cancellationToken: ct);

    private string StateDir => Path.Combine(_dir, "claim");

    private string[] StateFiles() =>
        Directory.Exists(StateDir) ? Directory.GetFiles(StateDir, "sell-*.json") : Array.Empty<string>();

    private JsonNode OnlyState() => JsonNode.Parse(File.ReadAllText(StateFiles().Should().ContainSingle().Subject))!;

    private void VerifyPaid(Times times) =>
        _l402.Verify(c => c.PayChallengeAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<CancellationToken>()), times);

    private void VerifyNeverPaid() => VerifyPaid(Times.Never());

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static void AssertNoSecrets(string result)
    {
        result.Should().NotContain(Macaroon, "the macaroon never enters a tool result");
        result.Should().NotContain(Preimage, "the preimage never enters a tool result");
    }

    private void QueueQuoteAndChallenge()
    {
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson()));
        _claim.Responses.Enqueue(new ClaimHttpResponse(402, ChallengeJson()));
    }

    private void SaveState(string phase, string? preimage = null)
    {
        var body = ProducerSellUrl.BuildBody(TargetUrl, "api.example-weather.dev", "Weather for agents", 21, Address,
            null, null, false, Terms);
        new ProducerSellUrl.ClaimState(StateDir, body)
        {
            ClaimUrl = "https://api.example.test/api/producer/claim",
            Invoice = Invoice,
            Macaroon = Macaroon,
            PaymentHash = "2859e1e143bc0000",
            ExpiresAt = _now.AddMinutes(5).UtcDateTime.ToString("O"),
            PriceSats = 150,
            Phase = phase,
            Preimage = preimage,
        }.Save();
    }

    private static Mock<IOperationLedger> LedgerWithRecord()
    {
        var ledger = new Mock<IOperationLedger>();
        ledger.Setup(l => l.Lookup(It.IsAny<string>()))
            .Returns(new OperationRecord("ln:x", OperationState.Submitted, 150, null));
        return ledger;
    }

    // ── Quote ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("2025-01")]
    public async Task WithoutAcceptedTerms_ReturnsTheQuoteAndPostsNothing(string? acceptTerms)
    {
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson()));

        var result = Parse(await SellAsync(acceptTerms: acceptTerms));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("stage").GetString().Should().Be("quote");
        result.GetProperty("requiresTermsAcceptance").GetBoolean().Should().BeTrue();
        result.GetProperty("price").GetProperty("sats").GetInt64().Should().Be(150);
        result.GetProperty("terms").GetProperty("version").GetString().Should().Be(Terms);
        result.GetProperty("covers").GetProperty("days").GetInt32().Should().Be(30);
        result.GetProperty("nextStep").GetString().Should().Contain($"acceptTerms=\"{Terms}\"");
        result.GetProperty("name").GetString().Should().Be("api.example-weather.dev", "name defaults to the hostname");
        result.TryGetProperty("recoveryEmailWarning", out _).Should().BeFalse();

        _claim.Posts.Should().BeEmpty();
        VerifyNeverPaid();
        StateFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Quote_CarriesTheServersPricingNoteAndBodyRuleVerbatim()
    {
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson()));

        var result = Parse(await SellAsync(acceptTerms: null));

        result.GetProperty("pricingNote").GetString().Should().Be(PricingNote);
        result.GetProperty("bodyRule").GetString().Should().Be(ServerBodyRule);
    }

    [Fact]
    public async Task Quote_WithARecoveryEmail_WarnsThatARegisteredEmailIsRefusedOnlyAfterPayment()
    {
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson()));

        var result = Parse(await SellAsync(acceptTerms: null, recoveryEmail: "op@example.com"));

        result.GetProperty("recoveryEmailWarning").GetString().Should().Contain("only after payment");
    }

    [Fact]
    public async Task Bare404_MeansTheFeatureIsNotEnabled()
    {
        _claim.Quotes.Enqueue(new ClaimHttpResponse(404, ""));

        var result = Parse(await SellAsync());

        result.GetProperty("error").GetString().Should().Be("feature_not_enabled");
        result.GetProperty("retryable").GetBoolean().Should().BeFalse();
        result.GetProperty("paid").GetBoolean().Should().BeFalse();
        _claim.Posts.Should().BeEmpty();
    }

    [Fact]
    public async Task HttpTarget_IsRefusedLocally()
    {
        var raw = await ProducerSellUrl.SellUrlAsync("http://api.example.com", Address, null, null, null, null, null,
            false, Terms, null, _claim, _l402.Object, _wallet.Object, null, _budget.Object, null, null, null, null,
            new SellUrlOptions { ConfigPath = _configPath }, CancellationToken.None);

        Parse(raw).GetProperty("error").GetString().Should().Be("invalid_target_url");
        _claim.QuoteCalls.Should().Be(0);
    }

    // ── Happy path ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_QuotePaysRedeemsStoresTheKeyOnce_AndLeavesASecretFreeMarker()
    {
        QueueQuoteAndChallenge();
        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson()));

        var raw = await SellAsync();
        var result = Parse(raw);

        result.GetProperty("success").GetBoolean().Should().BeTrue(raw);
        result.GetProperty("proxyUrl").GetString().Should().EndWith("/l402/proxy/claimed-weather-api/");
        result.GetProperty("manifestUrl").GetString().Should().EndWith("l402-manifest.json");
        result.GetProperty("proxyId").GetString().Should().Be("claimed-weather-api");
        result.GetProperty("paidThrough").GetString().Should().Be("2026-12-08T21:00:30Z");
        result.GetProperty("recovery").GetString().Should().Be("none");
        result.GetProperty("listing").GetProperty("listed").GetBoolean().Should().BeFalse();
        result.GetProperty("apiKeyStorage").GetProperty("location").GetString().Should().Be("config");
        result.GetProperty("apiKeyNote").GetString().Should().Contain(_configPath).And.Contain("shown once");

        raw.Split(FreshKey).Length.Should().Be(2);
        JsonNode.Parse(File.ReadAllText(_configPath))!["lightningEnableApiKey"]!.GetValue<string>().Should().Be(FreshKey);
        AssertNoSecrets(raw);

        _claim.Posts.Should().HaveCount(2);
        _claim.Posts[0].Authorization.Should().BeNull();
        _claim.Posts[1].Authorization.Should().Be($"L402 {Macaroon}:{Preimage}");
        _claim.Posts[1].Body.Should().Be(_claim.Posts[0].Body);

        var body = JsonNode.Parse(_claim.Posts[0].Body)!;
        body["targetUrl"]!.GetValue<string>().Should().Be(TargetUrl);
        body["name"]!.GetValue<string>().Should().Be("api.example-weather.dev");
        body["defaultPriceSats"]!.GetValue<long>().Should().Be(21);
        body["receive"]!["lightningAddress"]!.GetValue<string>().Should().Be(Address);
        body["acceptTerms"]!.GetValue<string>().Should().Be(Terms);
        body["recovery"].Should().BeNull();

        _budget.Verify(b => b.CheckApprovalLevelAsync(150, It.IsAny<CancellationToken>()), Times.Once);
        _l402.Verify(c => c.PayChallengeAsync(Macaroon, Invoice.ToLowerInvariant(), 150, It.IsAny<CancellationToken>()), Times.Once);

        var marker = OnlyState();
        marker["phase"]!.GetValue<string>().Should().Be("redeemed");
        marker["proxyId"]!.GetValue<string>().Should().Be("claimed-weather-api");
        marker["keyLocation"]!.GetValue<string>().Should().Be("config");
        var markerText = marker.ToJsonString();
        markerText.Should().NotContain(Macaroon).And.NotContain(Preimage).And.NotContain(Invoice);
    }

    [Fact]
    public async Task ReCallAfterSuccess_AnswersAlreadyClaimed_WithoutMintingOrPaying()
    {
        QueueQuoteAndChallenge();
        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson()));
        await SellAsync();

        var raw = await SellAsync();
        var result = Parse(raw);

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("alreadyClaimed").GetBoolean().Should().BeTrue();
        result.GetProperty("paidThisCall").GetBoolean().Should().BeFalse();
        result.GetProperty("proxyUrl").GetString().Should().EndWith("/claimed-weather-api/");
        result.GetProperty("apiKeyStorage").GetProperty("path").GetString().Should().Be(_configPath);
        raw.Should().NotContain(FreshKey);
        _claim.QuoteCalls.Should().Be(1);
        _claim.Posts.Should().HaveCount(2);
        VerifyPaid(Times.Once());
    }

    [Fact]
    public async Task Replay_WithKeyRotated_StoresTheFreshKeyBesideAnotherAccountsConfigKey()
    {
        File.WriteAllText(_configPath, "{\"lightningEnableApiKey\":\"le_other_account\"}");
        QueueQuoteAndChallenge();
        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson(replayed: true, keyRotated: true)));

        var raw = await SellAsync();
        var result = Parse(raw);

        result.GetProperty("success").GetBoolean().Should().BeTrue(raw);
        result.GetProperty("keyRotated").GetBoolean().Should().BeTrue();
        result.GetProperty("apiKey").GetString().Should().Be(FreshKey);
        result.GetProperty("apiKeyStorage").GetProperty("location").GetString().Should().Be("key_file");
        var note = result.GetProperty("apiKeyNote").GetString()!;
        note.Should().Contain("rotated").And.Contain("only valid one");
        note.Should().NotContain("not return the API key again").And.NotContain("never");

        File.ReadAllText(Path.Combine(_dir, "claim-claimed-weather-api.key")).Trim().Should().Be(FreshKey);
        JsonNode.Parse(File.ReadAllText(_configPath))!["lightningEnableApiKey"]!.GetValue<string>()
            .Should().Be("le_other_account", "another account's key is never overwritten");
        AssertNoSecrets(raw);
    }

    [Fact]
    public async Task Replay_WithKeyRotated_ReplacesThisClaimsOwnDeadKeyInConfig()
    {
        // The first redeem stored this claim's key in config, then the result was lost before the
        // marker landed: the state file still says 'paid', and records where the key went.
        File.WriteAllText(_configPath, "{\"lightningEnableApiKey\":\"le_this_claims_old_key\"}");
        SaveState(ProducerSellUrl.ClaimState.Paid, Preimage);
        var path = StateFiles().Single();
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["keyLocation"] = "config";
        node["keyPath"] = _configPath;
        File.WriteAllText(path, node.ToJsonString());
        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson(replayed: true, keyRotated: true)));

        var result = Parse(await SellAsync());

        result.GetProperty("apiKeyStorage").GetProperty("location").GetString().Should().Be("config");
        result.GetProperty("apiKeyStorage").GetProperty("replacedRotatedKey").GetBoolean().Should().BeTrue();
        JsonNode.Parse(File.ReadAllText(_configPath))!["lightningEnableApiKey"]!.GetValue<string>().Should().Be(FreshKey);
        File.Exists(Path.Combine(_dir, "claim-claimed-weather-api.key")).Should().BeFalse();
        VerifyNeverPaid();
    }

    [Fact]
    public async Task Replay_WithoutAKey_DoesNotClaimTheServerNeverReturnsIt()
    {
        QueueQuoteAndChallenge();
        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson(replayed: true, apiKey: null)));

        var result = Parse(await SellAsync());

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.TryGetProperty("apiKey", out _).Should().BeFalse();
        var note = result.GetProperty("apiKeyNote").GetString()!;
        note.Should().Contain("without an API key").And.Contain("rotates");
        note.Should().NotContain("only the first answer");
    }

    // ── Errors ───────────────────────────────────────────────────────────────────────

    private static JsonDocument Fixture() =>
        JsonDocument.Parse(File.ReadAllText(SharedFixture.Path("l402-producer-sell-url.json")));

    public static IEnumerable<object[]> FixtureCodes()
    {
        using var doc = Fixture();
        foreach (var p in doc.RootElement.GetProperty("errorCodes").EnumerateObject())
            yield return new object[] { p.Name, p.Value.GetBoolean() };
    }

    [Fact]
    public void ErrorTable_MatchesTheSharedFixture()
    {
        var expected = FixtureCodes().ToDictionary(r => (string)r[0], r => (bool)r[1]);
        ProducerClaimErrors.Table.ToDictionary(kv => kv.Key, kv => kv.Value.Retryable)
            .Should().BeEquivalentTo(expected);

        using var doc = Fixture();
        ProducerClaimErrors.RetryAfterPaymentAlso.Should().BeEquivalentTo(
            doc.RootElement.GetProperty("retryAfterPaymentAlso").EnumerateArray().Select(e => e.GetString()));
        ProducerSellUrl.MaxRedeemAttempts.Should().Be(doc.RootElement.GetProperty("maxRedeemAttempts").GetInt32());
    }

    [Fact]
    public void CanonicalBody_MatchesTheSharedVector_BytesAndFileName()
    {
        using var doc = Fixture();
        var vector = doc.RootElement.GetProperty("canonicalBody");
        var i = vector.GetProperty("inputs");

        var body = ProducerSellUrl.BuildBody(
            i.GetProperty("targetUrl").GetString()!, i.GetProperty("name").GetString()!,
            i.GetProperty("description").GetString(), i.GetProperty("defaultPriceSats").GetInt64(),
            i.GetProperty("lightningAddress").GetString()!, i.GetProperty("recoveryEmail").GetString(),
            null, i.GetProperty("listPublicly").GetBoolean(), i.GetProperty("acceptTerms").GetString()!);

        body.Should().Be(vector.GetProperty("expectedBody").GetString());
        ProducerSellUrl.ClaimState.FileNameFor(body).Should().Be(vector.GetProperty("expectedFileName").GetString());
    }

    [Theory]
    [MemberData(nameof(FixtureCodes))]
    public async Task EveryPhase1Code_MapsToItsMessageAndRetryFlag_AndCostsNothing(string code, bool retryable)
    {
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson()));
        switch (code)
        {
            case "feature_not_enabled":
                _claim.Responses.Enqueue(new ClaimHttpResponse(404, ""));
                break;
            case "rate_limited":
                _claim.Responses.Enqueue(new ClaimHttpResponse(429, "", RetryAfterSeconds: 7));
                break;
            case "network_error":
                _claim.PostThrows = new HttpRequestException("connection refused");
                break;
            default:
                _claim.Responses.Enqueue(new ClaimHttpResponse(code == "connect_not_available" ? 501 : 400,
                    Problem(400, code, "server detail for " + code)));
                break;
        }

        var result = Parse(await SellAsync());

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Be(code);
        result.GetProperty("retryable").GetBoolean().Should().Be(retryable);
        result.GetProperty("message").GetString().Should().Be(ProducerClaimErrors.Table[code].Message);
        result.GetProperty("paid").GetBoolean().Should().BeFalse();
        result.GetProperty("stage").GetString().Should().Be("phase1");
        VerifyNeverPaid();
        StateFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task PrePaymentRefusal_MintsNothingSpendsNothing_AndSurfacesTheProviderBounds()
    {
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson()));
        _claim.Responses.Enqueue(new ClaimHttpResponse(400, Problem(400, "invalid_lightning_address",
            "The price per call is outside what this Lightning Address accepts.",
            new JsonObject { ["minSendableSats"] = 1, ["maxSendableSats"] = 10 })));

        var result = Parse(await SellAsync());

        result.GetProperty("error").GetString().Should().Be("invalid_lightning_address");
        result.GetProperty("paid").GetBoolean().Should().BeFalse();
        result.GetProperty("note").GetString().Should().Contain("costs nothing");
        result.GetProperty("problem").GetProperty("maxSendableSats").GetInt32().Should().Be(10);
        _claim.Posts.Should().HaveCount(1, "one phase-1 attempt, no retry before payment");
        VerifyNeverPaid();
        _budget.Verify(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        StateFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task InvoiceAboveTheQuote_IsRefusedBeforePaying()
    {
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson(sats: 100)));
        _claim.Responses.Enqueue(new ClaimHttpResponse(402, ChallengeJson(sats: 150)));

        var result = Parse(await SellAsync());

        result.GetProperty("error").GetString().Should().Be("price_changed");
        result.GetProperty("paid").GetBoolean().Should().BeFalse();
        VerifyNeverPaid();
    }

    [Fact]
    public async Task BothRecoveryKinds_AreRefusedLocally()
    {
        var raw = await ProducerSellUrl.SellUrlAsync(TargetUrl, Address, null, null, null, "a@example.com", new string('a', 64),
            false, Terms, null, _claim, _l402.Object, _wallet.Object, null, _budget.Object, null, null, null, null,
            new SellUrlOptions { ConfigPath = _configPath }, CancellationToken.None);

        Parse(raw).GetProperty("error").GetString().Should().Be("invalid_recovery");
        _claim.QuoteCalls.Should().Be(0);
        _claim.Posts.Should().BeEmpty();
    }

    [Fact]
    public async Task NoWallet_WithTheRealClientAndAnUnconfiguredWallet_RefusesBeforeMinting()
    {
        var unconfigured = new Mock<IWalletService>();
        unconfigured.SetupGet(w => w.IsConfigured).Returns(false);
        var realClient = new L402HttpClient(new HttpClient(), unconfigured.Object, _budget.Object,
            new Mock<IPaymentHistoryService>().Object);
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson()));

        var result = Parse(await SellAsync(l402: realClient, wallet: unconfigured.Object));

        result.GetProperty("error").GetString().Should().Be("no_wallet");
        result.GetProperty("paid").GetBoolean().Should().BeFalse();
        _claim.Posts.Should().BeEmpty("no challenge is minted without a wallet that can pay it");
        StateFiles().Should().BeEmpty();
    }

    // ── Budget and confirmation ──────────────────────────────────────────────────────

    [Fact]
    public async Task BudgetRefusal_WhenThePriceExceedsTheCeiling_PaysNothing_AndSaysSo()
    {
        SetApproval(ApprovalLevel.Deny, denial: "Payment of 150 sats exceeds the per-payment limit");
        QueueQuoteAndChallenge();

        var raw = await SellAsync();
        var result = Parse(raw);

        result.GetProperty("error").GetString().Should().Be("payment_refused");
        result.GetProperty("message").GetString().Should().Contain("per-payment limit").And.Contain("Nothing was charged");
        result.GetProperty("paid").GetBoolean().Should().BeFalse();
        _claim.Posts.Should().HaveCount(1, "no phase 2 without a payment");
        VerifyNeverPaid();
        OnlyState()["phase"]!.GetValue<string>().Should().Be("challenge");
        AssertNoSecrets(raw);
    }

    [Fact]
    public async Task OverThreshold_RequestsTheOperatorsCode_ThenTheSameArgumentsPlusTheCodePay()
    {
        SetApproval(ApprovalLevel.FormConfirm);
        var pending = ConfirmationTestSetup.SetupDelivered(_budget, ConfirmationChannelKind.Stderr, "ZZZ999", 150, 0.10m,
            "pay_l402_challenge");
        QueueQuoteAndChallenge();

        var first = await SellAsync();
        var firstJson = Parse(first);

        firstJson.GetProperty("requiresConfirmation").GetBoolean().Should().BeTrue();
        firstJson.GetProperty("howToConfirm").GetString().Should().Contain("sell_url").And.Contain("confirmationNonce");
        first.Should().NotContain("ZZZ999");
        VerifyNeverPaid();
        OnlyState()["phase"]!.GetValue<string>().Should().Be("challenge");

        _budget.Setup(b => b.ValidateAndConsumeConfirmation("ZZZ999", 150, "pay_l402_challenge", Invoice.ToLowerInvariant()))
            .Returns(pending);
        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson()));

        var second = Parse(await SellAsync(confirmationNonce: "zzz999"));

        second.GetProperty("success").GetBoolean().Should().BeTrue();
        _claim.Posts.Should().HaveCount(2);
        _claim.QuoteCalls.Should().Be(1);
        VerifyPaid(Times.Once());
    }

    // ── C1: ambiguous payments are never reported as unpaid ─────────────────────────

    [Fact]
    public async Task WalletFailureAfterSubmit_IsPaymentStateUnknown_AndKeepsPaying()
    {
        _ledger = LedgerWithRecord().Object;
        _l402.Setup(c => c.PayChallengeAsync(Macaroon, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Payment failed: NO_RESPONSE"));
        QueueQuoteAndChallenge();

        var raw = await SellAsync();
        var result = Parse(raw);

        result.GetProperty("error").GetString().Should().Be("payment_state_unknown");
        result.GetProperty("paid").GetString().Should().Be("unknown");
        result.GetProperty("message").GetString().Should().Contain("Do NOT pay again");
        raw.Should().NotContain("Nothing was charged");
        result.GetProperty("recovery").GetProperty("paymentHash").GetString().Should().Be("2859e1e143bc0000");
        OnlyState()["phase"]!.GetValue<string>().Should().Be("paying");
        AssertNoSecrets(raw);
    }

    [Fact]
    public async Task KnownPreWalletShape_ButTheLedgerSawTheInvoice_IsStillUnknown()
    {
        _ledger = LedgerWithRecord().Object;
        _l402.Setup(c => c.PayChallengeAsync(Macaroon, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Budget check failed: session cap"));
        QueueQuoteAndChallenge();

        var result = Parse(await SellAsync());

        result.GetProperty("error").GetString().Should().Be("payment_state_unknown");
        OnlyState()["phase"]!.GetValue<string>().Should().Be("paying");
    }

    [Fact]
    public async Task PaymentUnproven_PendingOrNoPreimage_IsPaidTrue()
    {
        _l402.Setup(c => c.PayChallengeAsync(Macaroon, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                PaymentReceiptScope.Current!.RecordWrite(true); // the wallet seam saw money move
                throw new InvalidOperationException("Payment has not settled yet — it may still succeed or fail.");
            });
        QueueQuoteAndChallenge();

        var raw = await SellAsync();
        var result = Parse(raw);

        result.GetProperty("error").GetString().Should().Be("payment_unproven");
        result.GetProperty("paid").GetBoolean().Should().BeTrue();
        raw.Should().NotContain("Nothing was charged");
        OnlyState()["phase"]!.GetValue<string>().Should().Be("paying");
    }

    [Fact]
    public async Task ResumeFromPaying_ReturnsPaymentStateUnknown_WithoutPayingOrMinting()
    {
        SaveState(ProducerSellUrl.ClaimState.Paying);

        var result = Parse(await SellAsync());

        result.GetProperty("error").GetString().Should().Be("payment_state_unknown");
        result.GetProperty("paid").GetString().Should().Be("unknown");
        _claim.QuoteCalls.Should().Be(0);
        _claim.Posts.Should().BeEmpty();
        VerifyNeverPaid();
    }

    [Fact]
    public async Task ResumeFromPaying_WhenTheLedgerNeverSawTheInvoice_Pays()
    {
        var ledger = new Mock<IOperationLedger>();
        ledger.Setup(l => l.Lookup(It.IsAny<string>())).Returns((OperationRecord?)null);
        _ledger = ledger.Object;
        SaveState(ProducerSellUrl.ClaimState.Paying);
        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson()));

        var result = Parse(await SellAsync());

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        VerifyPaid(Times.Once());
    }

    [Fact]
    public async Task ExpiredAfterAmbiguousFailure_NeverMintsOrPaysAgain()
    {
        _l402.Setup(c => c.PayChallengeAsync(Macaroon, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Payment failed: NO_RESPONSE"));
        QueueQuoteAndChallenge();
        await SellAsync();

        _now = _now.AddHours(2); // well past the invoice's expiresAt
        var result = Parse(await SellAsync());

        result.GetProperty("error").GetString().Should().Be("payment_state_unknown");
        _claim.QuoteCalls.Should().Be(1);
        _claim.Posts.Should().HaveCount(1, "no second mint");
        VerifyPaid(Times.Once());
        OnlyState()["phase"]!.GetValue<string>().Should().Be("paying");
    }

    [Fact]
    public async Task TwoConcurrentIdenticalCalls_PayOnce_AndNeverDowngradeThePhase()
    {
        var gate = new TaskCompletionSource();
        _l402.Setup(c => c.PayChallengeAsync(Macaroon, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { await gate.Task; return $"{Macaroon}:{Preimage}"; });
        QueueQuoteAndChallenge();
        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson()));

        var first = SellAsync();
        var second = SellAsync();
        await Task.Delay(50);
        gate.SetResult();
        var results = (await Task.WhenAll(first, second)).Select(Parse).ToList();

        results.Count(r => r.TryGetProperty("apiKey", out _)).Should().Be(1);
        results.Count(r => r.TryGetProperty("alreadyClaimed", out _)).Should().Be(1);
        VerifyPaid(Times.Once());
        _claim.QuoteCalls.Should().Be(1);
        OnlyState()["phase"]!.GetValue<string>().Should().Be("redeemed");
    }

    [Fact]
    public void StaleSave_CannotMoveTheStateBackwards()
    {
        SaveState(ProducerSellUrl.ClaimState.Paid, Preimage);
        var body = ProducerSellUrl.BuildBody(TargetUrl, "api.example-weather.dev", "Weather for agents", 21, Address,
            null, null, false, Terms);
        var stale = new ProducerSellUrl.ClaimState(StateDir, body) { Phase = ProducerSellUrl.ClaimState.Paying };

        var act = () => stale.Save();
        var downgrade = () => new ProducerSellUrl.ClaimState(StateDir, body) { Phase = ProducerSellUrl.ClaimState.Challenge }
            .Save(allowDowngradeFromPaying: true);

        act.Should().Throw<InvalidOperationException>();
        downgrade.Should().Throw<InvalidOperationException>("only a file that still says 'paying' may step back");
        OnlyState()["preimage"]!.GetValue<string>().Should().Be(Preimage);
    }

    // ── Phase 2 retries and recovery ─────────────────────────────────────────────────

    [Fact]
    public async Task TransientPhase2Failure_IsRetriedWithTheSameBodyAndToken()
    {
        QueueQuoteAndChallenge();
        _claim.Responses.Enqueue(new ClaimHttpResponse(503, Problem(503, "claim_contended", "Try again"), RetryAfterSeconds: 1));
        _claim.Responses.Enqueue(new ClaimHttpResponse(400, Problem(400, "invalid_lightning_address", "LNURL lookup failed")));
        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson()));

        var result = Parse(await SellAsync());

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        _claim.Posts.Should().HaveCount(4);
        _claim.Posts.Skip(1).Select(p => p.Body).Distinct().Should().ContainSingle().Which.Should().Be(_claim.Posts[0].Body);
        _delays.Should().HaveCount(2);
        _delays[0].Should().Be(TimeSpan.FromSeconds(1), "Retry-After is honoured");
        VerifyPaid(Times.Once());
    }

    [Fact]
    public async Task Phase2GivingUp_ReturnsRecoveryInstructionsWithoutSecrets_AndKeepsTheToken()
    {
        QueueQuoteAndChallenge();
        for (var i = 0; i < ProducerSellUrl.MaxRedeemAttempts; i++)
            _claim.Responses.Enqueue(new ClaimHttpResponse(503, Problem(503, "upstream_unavailable", "down")));

        var raw = await SellAsync();
        var result = Parse(raw);

        result.GetProperty("paid").GetBoolean().Should().BeTrue();
        result.GetProperty("error").GetString().Should().Be("upstream_unavailable");
        result.GetProperty("redeemAttempts").GetInt32().Should().Be(ProducerSellUrl.MaxRedeemAttempts);
        var recovery = result.GetProperty("recovery");
        recovery.GetProperty("paymentSpent").GetBoolean().Should().BeTrue();
        recovery.GetProperty("instructions").GetString().Should().Contain("same arguments").And.Contain("do not pay again");
        var saved = JsonNode.Parse(File.ReadAllText(recovery.GetProperty("stateFile").GetString()!))!;
        saved["macaroon"]!.GetValue<string>().Should().Be(Macaroon);
        saved["preimage"]!.GetValue<string>().Should().Be(Preimage);
        AssertNoSecrets(raw);
    }

    [Fact]
    public async Task MerchantExistsAfterPayment_SaysDoNotChangeTheArguments_AndContactSupport()
    {
        QueueQuoteAndChallenge();
        _claim.Responses.Enqueue(new ClaimHttpResponse(409, Problem(409, "merchant_exists", "Account exists")));

        var result = Parse(await SellAsync(recoveryEmail: "op@example.com"));

        result.GetProperty("error").GetString().Should().Be("merchant_exists");
        result.GetProperty("paid").GetBoolean().Should().BeTrue();
        var message = result.GetProperty("message").GetString()!;
        message.Should().Contain("Do not change the").And.Contain("payment hash");
        message.Should().NotContain("use a different recovery");
        _claim.Posts.Should().HaveCount(2);
        _delays.Should().BeEmpty();
    }

    [Fact]
    public async Task UnexpectedExceptionAfterPayment_ReportsPaidWithRecovery()
    {
        QueueQuoteAndChallenge();
        _claim.ThrowOnPost = 2; // phase 2 blows up with a non-HTTP error
        _claim.PostThrows = new InvalidOperationException("boom");

        var raw = await SellAsync();
        var result = Parse(raw);

        result.GetProperty("paid").GetBoolean().Should().BeTrue();
        result.GetProperty("recovery").GetProperty("paymentSpent").GetBoolean().Should().BeTrue();
        AssertNoSecrets(raw);
    }

    [Fact]
    public async Task CrashAfterPaying_IsRecoveredFromTheSavedToken_WithoutPayingAgain()
    {
        QueueQuoteAndChallenge();
        for (var i = 0; i < ProducerSellUrl.MaxRedeemAttempts; i++)
            _claim.Responses.Enqueue(new ClaimHttpResponse(503, Problem(503, "upstream_unavailable", "down")));
        await SellAsync();
        var postsBefore = _claim.Posts.Count;

        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson()));
        var raw = await SellAsync();
        var result = Parse(raw);

        result.GetProperty("success").GetBoolean().Should().BeTrue(raw);
        result.GetProperty("apiKey").GetString().Should().Be(FreshKey);
        _claim.QuoteCalls.Should().Be(1);
        _claim.Posts.Should().HaveCount(postsBefore + 1);
        _claim.Posts[^1].Authorization.Should().Be($"L402 {Macaroon}:{Preimage}");
        VerifyPaid(Times.Once());
        OnlyState()["phase"]!.GetValue<string>().Should().Be("redeemed");
    }

    [Fact]
    public async Task CrashBeforePaying_ResumesFromTheSavedChallenge_WithoutAnotherMint()
    {
        SaveState(ProducerSellUrl.ClaimState.Challenge);
        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson()));

        var result = Parse(await SellAsync());

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        _claim.QuoteCalls.Should().Be(0);
        _claim.Posts.Should().ContainSingle().Which.Authorization.Should().StartWith("L402 ");
        _l402.Verify(c => c.PayChallengeAsync(Macaroon, It.IsAny<string>(), 150, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Advertised surface ───────────────────────────────────────────────────────────

    private static string ToSnake(string camel) =>
        string.Concat(camel.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));

    [Fact]
    public async Task AdvertisedSchema_MatchesTheSharedFixture_SoBothPortsAgree()
    {
        using var fixture = Fixture();
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var schema = (await host.AdvertisedToolsAsync()).Single(t => t.Name == "l402_producer").JsonSchema;
        var properties = schema.GetProperty("properties");

        properties.EnumerateObject().Select(p => ToSnake(p.Name))
            .Should().BeEquivalentTo(fixture.RootElement.GetProperty("arguments").EnumerateArray().Select(e => e.GetString()));
        properties.GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(fixture.RootElement.GetProperty("actions").EnumerateArray().Select(e => e.GetString()));

        foreach (var argument in fixture.RootElement.GetProperty("sellUrlArguments").EnumerateArray())
        {
            var camel = System.Text.RegularExpressions.Regex.Replace(argument.GetString()!, "_([a-z])",
                m => m.Groups[1].Value.ToUpperInvariant());
            properties.GetProperty(camel).GetProperty("description").GetString().Should().Contain("sell_url", camel);
        }
        properties.GetProperty("priceSats").GetProperty("description").GetString().Should().NotContain("sell_url");
        properties.GetProperty("defaultPriceSats").GetProperty("description").GetString().Should().Contain("buyers pay");
    }

    [Fact]
    public async Task ToolDescription_LandsOnTheOperatorsWords_WithoutPricesOrCustodyClaims()
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var description = (await host.AdvertisedToolsAsync()).Single(t => t.Name == "l402_producer").Description!;

        description.Should().Contain("charge for your API").And.Contain("monetize an endpoint");
        description.Should().NotMatchRegex(@"\d+\s*(sats?|USD|\$)");
        foreach (var word in new[] { "non-custodial", "self-custody", "trustless", "decentralized" })
            description.ToLowerInvariant().Should().NotContain(word);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────────────

    private sealed class FakeClaimClient : IProducerClaimClient
    {
        private readonly object _gate = new();
        public Queue<ClaimHttpResponse> Quotes { get; } = new();
        public Queue<ClaimHttpResponse> Responses { get; } = new();
        public List<(string Body, string? Authorization)> Posts { get; } = new();
        public int QuoteCalls { get; private set; }
        public Exception? PostThrows { get; set; }

        /// <summary>Throw <see cref="PostThrows"/> only on this 1-based POST; 0 means on every POST.</summary>
        public int ThrowOnPost { get; set; }

        public string ClaimUrl => "https://api.example.test/api/producer/claim";

        public Task<ClaimHttpResponse> GetQuoteAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                QuoteCalls++;
                return Task.FromResult(Quotes.Dequeue());
            }
        }

        public Task<ClaimHttpResponse> PostAsync(string jsonBody, string? authorization, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Posts.Add((jsonBody, authorization));
                if (PostThrows != null && (ThrowOnPost == 0 || ThrowOnPost == Posts.Count)) throw PostThrows;
                return Task.FromResult(Responses.Dequeue());
            }
        }
    }
}

/// <summary>Finds a file under the repo's <c>shared/</c> directory from the test output folder.</summary>
internal static class SharedFixture
{
    public static string Path(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "shared", name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"shared/{name} not found above the test output directory.");
    }
}
