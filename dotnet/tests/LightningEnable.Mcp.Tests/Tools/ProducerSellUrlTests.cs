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
/// phase-2 retries and crash recovery. No network: the claim client is a fake and the wallet
/// is a mock.
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
    private static readonly string Invoice = TestInvoices.Build("lnbc1500n"); // 150 sats

    private readonly string _dir;
    private readonly string _configPath;
    private readonly FakeClaimClient _claim = new();
    private readonly Mock<IL402HttpClient> _l402 = new();
    private readonly Mock<IBudgetService> _budget = new();
    private readonly List<TimeSpan> _delays = new();
    private string? _envKey;

    public ProducerSellUrlTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "le-sell-url-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "config.json");

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
    }.ToJsonString();

    private static string ChallengeJson(long sats = 150) => new JsonObject
    {
        ["invoice"] = Invoice,
        ["macaroon"] = Macaroon,
        ["paymentHash"] = "2859e1e143bc0000",
        ["expiresAt"] = DateTime.UtcNow.AddMinutes(10).ToString("O"),
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
        };
        if (keyRotated) o["keyRotated"] = true;
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
        string? recoveryEmail = null, IL402HttpClient? l402 = null, bool noWallet = false) =>
        ProducerSellUrl.SellUrlAsync(
            TargetUrl, Address, priceSats: 21, name: name, description: "Weather for agents",
            recoveryEmail: recoveryEmail, recoveryNostrPubkey: null, listPublicly: false, acceptTerms: acceptTerms,
            confirmationNonce: confirmationNonce, claimClient: _claim,
            l402Client: noWallet ? null : (l402 ?? _l402.Object), budgetService: _budget.Object, priceService: null,
            paymentHistory: null, configService: null, server: null,
            options: new SellUrlOptions
            {
                ConfigPath = _configPath,
                EnvironmentApiKey = () => _envKey,
                Delay = (d, _) => { _delays.Add(d); return Task.CompletedTask; },
            },
            cancellationToken: CancellationToken.None);

    private string StateDir => Path.Combine(_dir, "claim");

    private string[] StateFiles() =>
        Directory.Exists(StateDir) ? Directory.GetFiles(StateDir, "sell-*.json") : Array.Empty<string>();

    private void VerifyNeverPaid() =>
        _l402.Verify(c => c.PayChallengeAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<CancellationToken>()), Times.Never);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static void AssertNoSecrets(string result)
    {
        result.Should().NotContain(Macaroon, "the macaroon never enters a tool result");
        result.Should().NotContain(Preimage, "the preimage never enters a tool result");
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
        result.GetProperty("terms").GetProperty("url").GetString().Should().Contain("terms");
        result.GetProperty("covers").GetProperty("days").GetInt32().Should().Be(30);
        result.GetProperty("bodyRule").GetString().Should().Contain("written once and sent twice");
        result.GetProperty("nextStep").GetString().Should().Contain($"acceptTerms=\"{Terms}\"");
        result.GetProperty("name").GetString().Should().Be("api.example-weather.dev", "name defaults to the hostname");

        _claim.Posts.Should().BeEmpty();
        VerifyNeverPaid();
        StateFiles().Should().BeEmpty();
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

    // ── Happy path ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_QuotePaysRedeemsAndStoresTheKeyOnce()
    {
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson()));
        _claim.Responses.Enqueue(new ClaimHttpResponse(402, ChallengeJson()));
        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson()));

        var raw = await SellAsync();
        var result = Parse(raw);

        result.GetProperty("success").GetBoolean().Should().BeTrue(raw);
        result.GetProperty("proxyUrl").GetString().Should().EndWith("/l402/proxy/claimed-weather-api/");
        result.GetProperty("manifestUrl").GetString().Should().EndWith("l402-manifest.json");
        result.GetProperty("proxyId").GetString().Should().Be("claimed-weather-api");
        result.GetProperty("paidThrough").GetString().Should().Be("2026-12-08T21:00:30Z");
        result.GetProperty("renew").GetProperty("endpoint").GetString().Should().Contain("renew");
        result.GetProperty("recovery").GetString().Should().Be("none");
        result.GetProperty("listing").GetProperty("listed").GetBoolean().Should().BeFalse();
        result.GetProperty("warnings").GetArrayLength().Should().Be(1);
        result.GetProperty("apiKeyStorage").GetProperty("location").GetString().Should().Be("config");
        result.GetProperty("apiKeyNote").GetString().Should().Contain(_configPath);

        // The key appears exactly once in the result, and is stored.
        raw.Split(FreshKey).Length.Should().Be(2);
        JsonNode.Parse(File.ReadAllText(_configPath))!["lightningEnableApiKey"]!.GetValue<string>().Should().Be(FreshKey);
        AssertNoSecrets(raw);

        // Phase 1 had no Authorization; phase 2 sent the byte-identical body with the token.
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
        body["receive"]!["connect"].Should().BeNull();

        // Paid once, through the budget gate, capped at the 402's priceSats.
        _budget.Verify(b => b.CheckApprovalLevelAsync(150, It.IsAny<CancellationToken>()), Times.Once);
        _l402.Verify(c => c.PayChallengeAsync(Macaroon, Invoice.ToLowerInvariant(), 150, It.IsAny<CancellationToken>()), Times.Once);
        StateFiles().Should().BeEmpty("the scratch file is deleted once the claim answers 200");
    }

    [Fact]
    public async Task Replay_WithKeyRotated_StoresTheFreshKeyBesideAnExistingConfigKey()
    {
        File.WriteAllText(_configPath, "{\"lightningEnableApiKey\":\"le_other_account\"}");
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson()));
        _claim.Responses.Enqueue(new ClaimHttpResponse(402, ChallengeJson()));
        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson(replayed: true, keyRotated: true)));

        var raw = await SellAsync();
        var result = Parse(raw);

        result.GetProperty("success").GetBoolean().Should().BeTrue(raw);
        result.GetProperty("replayed").GetBoolean().Should().BeTrue();
        result.GetProperty("keyRotated").GetBoolean().Should().BeTrue();
        result.GetProperty("apiKey").GetString().Should().Be(FreshKey);
        result.GetProperty("apiKeyStorage").GetProperty("location").GetString().Should().Be("key_file");
        result.GetProperty("apiKeyNote").GetString().Should().Contain("fresh key");

        var keyFile = Path.Combine(_dir, "claim-claimed-weather-api.key");
        File.ReadAllText(keyFile).Trim().Should().Be(FreshKey);
        JsonNode.Parse(File.ReadAllText(_configPath))!["lightningEnableApiKey"]!.GetValue<string>()
            .Should().Be("le_other_account", "another account's key is never overwritten");
        AssertNoSecrets(raw);
    }

    [Fact]
    public async Task Replay_WithoutAKey_SaysTheKeyIsNotReturnedAgain()
    {
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson()));
        _claim.Responses.Enqueue(new ClaimHttpResponse(402, ChallengeJson()));
        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson(replayed: true, apiKey: null)));

        var result = Parse(await SellAsync());

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.TryGetProperty("apiKey", out _).Should().BeFalse();
        result.GetProperty("apiKeyNote").GetString().Should().Contain("only the first answer");
        File.Exists(_configPath).Should().BeFalse();
    }

    // ── Errors ───────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> FixtureCodes()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SharedFixture.Path("l402-producer-sell-url.json")));
        foreach (var p in doc.RootElement.GetProperty("errorCodes").EnumerateObject())
            yield return new object[] { p.Name, p.Value.GetBoolean() };
    }

    [Fact]
    public void ErrorTable_MatchesTheSharedFixture()
    {
        var expected = FixtureCodes().ToDictionary(r => (string)r[0], r => (bool)r[1]);
        ProducerClaimErrors.Table.ToDictionary(kv => kv.Key, kv => kv.Value.Retryable)
            .Should().BeEquivalentTo(expected);

        using var doc = JsonDocument.Parse(File.ReadAllText(SharedFixture.Path("l402-producer-sell-url.json")));
        ProducerClaimErrors.RetryAfterPaymentAlso.Should().BeEquivalentTo(
            doc.RootElement.GetProperty("retryAfterPaymentAlso").EnumerateArray().Select(e => e.GetString()));
        ProducerSellUrl.MaxRedeemAttempts.Should().Be(doc.RootElement.GetProperty("maxRedeemAttempts").GetInt32());
        foreach (var info in ProducerClaimErrors.Table.Values)
            info.Message.Should().NotBeNullOrWhiteSpace();
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
        result.GetProperty("detail").GetString().Should().Contain("outside");
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
        var raw = await ProducerSellUrl.SellUrlAsync(TargetUrl, Address, 0, null, null, "a@example.com", new string('a', 64),
            false, Terms, null, _claim, _l402.Object, _budget.Object, null, null, null, null,
            new SellUrlOptions { ConfigPath = _configPath }, CancellationToken.None);

        Parse(raw).GetProperty("error").GetString().Should().Be("invalid_recovery");
        _claim.QuoteCalls.Should().Be(0);
        _claim.Posts.Should().BeEmpty();
    }

    // ── Budget and confirmation ──────────────────────────────────────────────────────

    [Fact]
    public async Task BudgetRefusal_WhenThePriceExceedsTheCeiling_PaysNothing()
    {
        SetApproval(ApprovalLevel.Deny, denial: "Payment of 150 sats exceeds the per-payment limit");
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson()));
        _claim.Responses.Enqueue(new ClaimHttpResponse(402, ChallengeJson()));

        var raw = await SellAsync();
        var result = Parse(raw);

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Be("payment_refused");
        result.GetProperty("message").GetString().Should().Contain("per-payment limit");
        result.GetProperty("paid").GetBoolean().Should().BeFalse();
        _claim.Posts.Should().HaveCount(1, "no phase 2 without a payment");
        VerifyNeverPaid();
        AssertNoSecrets(raw);
    }

    [Fact]
    public async Task OverThreshold_RequestsTheOperatorsCode_ThenTheSameArgumentsPlusTheCodePay()
    {
        SetApproval(ApprovalLevel.FormConfirm);
        var pending = ConfirmationTestSetup.SetupDelivered(_budget, ConfirmationChannelKind.Stderr, "ZZZ999", 150, 0.10m,
            "pay_l402_challenge");
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson()));
        _claim.Responses.Enqueue(new ClaimHttpResponse(402, ChallengeJson()));

        var first = await SellAsync();
        var firstJson = Parse(first);

        firstJson.GetProperty("requiresConfirmation").GetBoolean().Should().BeTrue();
        firstJson.GetProperty("howToConfirm").GetString().Should().Contain("sell_url").And.Contain("confirmationNonce");
        first.Should().NotContain("ZZZ999");
        _budget.Verify(b => b.RequestConfirmationAsync(It.IsAny<ConfirmationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        VerifyNeverPaid();
        StateFiles().Should().HaveCount(1, "the challenge is saved so the code binds to the same invoice");

        _budget.Setup(b => b.ValidateAndConsumeConfirmation("ZZZ999", 150, "pay_l402_challenge", Invoice.ToLowerInvariant()))
            .Returns(pending);
        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson()));

        var second = Parse(await SellAsync(confirmationNonce: "zzz999"));

        second.GetProperty("success").GetBoolean().Should().BeTrue();
        _claim.Posts.Should().HaveCount(2, "the second call skipped phase 1 and redeemed");
        _claim.QuoteCalls.Should().Be(1);
        _l402.Verify(c => c.PayChallengeAsync(Macaroon, It.IsAny<string>(), 150, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Phase 2 retries and recovery ─────────────────────────────────────────────────

    [Fact]
    public async Task TransientPhase2Failure_IsRetriedWithTheSameBodyAndToken()
    {
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson()));
        _claim.Responses.Enqueue(new ClaimHttpResponse(402, ChallengeJson()));
        _claim.Responses.Enqueue(new ClaimHttpResponse(503, Problem(503, "claim_contended", "Try again"), RetryAfterSeconds: 1));
        _claim.Responses.Enqueue(new ClaimHttpResponse(400, Problem(400, "invalid_lightning_address", "LNURL lookup failed")));
        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson()));

        var result = Parse(await SellAsync());

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        _claim.Posts.Should().HaveCount(4);
        _claim.Posts.Skip(1).Select(p => p.Body).Distinct().Should().ContainSingle().Which.Should().Be(_claim.Posts[0].Body);
        _claim.Posts.Skip(1).Select(p => p.Authorization).Distinct().Should().ContainSingle();
        _delays.Should().HaveCount(2);
        _delays[0].Should().Be(TimeSpan.FromSeconds(1), "Retry-After is honoured");
        _l402.Verify(c => c.PayChallengeAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Phase2GivingUp_ReturnsRecoveryInstructionsWithoutSecrets_AndKeepsTheToken()
    {
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson()));
        _claim.Responses.Enqueue(new ClaimHttpResponse(402, ChallengeJson()));
        for (var i = 0; i < ProducerSellUrl.MaxRedeemAttempts; i++)
            _claim.Responses.Enqueue(new ClaimHttpResponse(503, Problem(503, "upstream_unavailable", "down")));

        var raw = await SellAsync();
        var result = Parse(raw);

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("paid").GetBoolean().Should().BeTrue();
        result.GetProperty("error").GetString().Should().Be("upstream_unavailable");
        result.GetProperty("redeemAttempts").GetInt32().Should().Be(ProducerSellUrl.MaxRedeemAttempts);
        var recovery = result.GetProperty("recovery");
        recovery.GetProperty("paymentSpent").GetBoolean().Should().BeTrue();
        recovery.GetProperty("paymentHash").GetString().Should().Be("2859e1e143bc0000");
        recovery.GetProperty("instructions").GetString().Should().Contain("same arguments").And.Contain("do not pay again");
        var stateFile = recovery.GetProperty("stateFile").GetString()!;
        File.Exists(stateFile).Should().BeTrue();
        var saved = JsonNode.Parse(File.ReadAllText(stateFile))!;
        saved["macaroon"]!.GetValue<string>().Should().Be(Macaroon);
        saved["preimage"]!.GetValue<string>().Should().Be(Preimage);
        saved["body"]!.GetValue<string>().Should().Be(_claim.Posts[0].Body);
        _claim.Posts.Should().HaveCount(1 + ProducerSellUrl.MaxRedeemAttempts);
        AssertNoSecrets(raw);
    }

    [Fact]
    public async Task NonRetryablePhase2Refusal_GivesUpAtOnceWithRecovery()
    {
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson()));
        _claim.Responses.Enqueue(new ClaimHttpResponse(402, ChallengeJson()));
        _claim.Responses.Enqueue(new ClaimHttpResponse(409, Problem(409, "merchant_exists", "Account exists")));

        var result = Parse(await SellAsync(recoveryEmail: "op@example.com"));

        result.GetProperty("error").GetString().Should().Be("merchant_exists");
        result.GetProperty("paid").GetBoolean().Should().BeTrue();
        result.GetProperty("recovery").GetProperty("paymentSpent").GetBoolean().Should().BeTrue();
        _claim.Posts.Should().HaveCount(2);
        _delays.Should().BeEmpty();
    }

    [Fact]
    public async Task CrashAfterPaying_IsRecoveredFromTheSavedToken_WithoutPayingAgain()
    {
        // First run: paid, then every redeem fails (the process "crashes" from the operator's view).
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson()));
        _claim.Responses.Enqueue(new ClaimHttpResponse(402, ChallengeJson()));
        for (var i = 0; i < ProducerSellUrl.MaxRedeemAttempts; i++)
            _claim.Responses.Enqueue(new ClaimHttpResponse(503, Problem(503, "upstream_unavailable", "down")));
        await SellAsync();
        var postsBefore = _claim.Posts.Count;

        // Second run with the same arguments: no quote, no phase 1, no payment.
        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson()));
        var raw = await SellAsync();
        var result = Parse(raw);

        result.GetProperty("success").GetBoolean().Should().BeTrue(raw);
        result.GetProperty("apiKey").GetString().Should().Be(FreshKey);
        _claim.QuoteCalls.Should().Be(1);
        _claim.Posts.Should().HaveCount(postsBefore + 1);
        _claim.Posts[^1].Authorization.Should().Be($"L402 {Macaroon}:{Preimage}");
        _claim.Posts[^1].Body.Should().Be(_claim.Posts[0].Body);
        _l402.Verify(c => c.PayChallengeAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<CancellationToken>()), Times.Once);
        StateFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task CrashBeforePaying_ResumesFromTheSavedChallenge_WithoutAnotherMint()
    {
        // A crash between saving the challenge and paying leaves phase "challenge" on disk.
        var body = ProducerSellUrl.BuildBody(TargetUrl, "api.example-weather.dev", "Weather for agents", 21, Address,
            null, null, false, Terms);
        new ProducerSellUrl.ClaimState(StateDir, body)
        {
            ClaimUrl = "https://api.example.test/api/producer/claim",
            Invoice = Invoice,
            Macaroon = Macaroon,
            PaymentHash = "2859e1e143bc0000",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5).ToString("O"),
            PriceSats = 150,
            Phase = ProducerSellUrl.ClaimState.Challenge,
        }.Save();
        _claim.Responses.Enqueue(new ClaimHttpResponse(200, ClaimedJson()));

        var result = Parse(await SellAsync());

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        _claim.QuoteCalls.Should().Be(0);
        _claim.Posts.Should().ContainSingle().Which.Authorization.Should().StartWith("L402 ");
        _l402.Verify(c => c.PayChallengeAsync(Macaroon, It.IsAny<string>(), 150, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoWallet_RefusesBeforeMinting()
    {
        _claim.Quotes.Enqueue(new ClaimHttpResponse(200, QuoteJson()));

        var result = Parse(await SellAsync(noWallet: true));

        result.GetProperty("error").GetString().Should().Be("no_wallet");
        _claim.Posts.Should().BeEmpty();
    }

    // ── Advertised surface ───────────────────────────────────────────────────────────

    private static string ToSnake(string camel) =>
        string.Concat(camel.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));

    [Fact]
    public async Task AdvertisedSchema_MatchesTheSharedFixture_SoBothPortsAgree()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(SharedFixture.Path("l402-producer-sell-url.json")));
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
        public Queue<ClaimHttpResponse> Quotes { get; } = new();
        public Queue<ClaimHttpResponse> Responses { get; } = new();
        public List<(string Body, string? Authorization)> Posts { get; } = new();
        public int QuoteCalls { get; private set; }
        public Exception? PostThrows { get; set; }

        public string ClaimUrl => "https://api.example.test/api/producer/claim";

        public Task<ClaimHttpResponse> GetQuoteAsync(CancellationToken cancellationToken)
        {
            QuoteCalls++;
            return Task.FromResult(Quotes.Dequeue());
        }

        public Task<ClaimHttpResponse> PostAsync(string jsonBody, string? authorization, CancellationToken cancellationToken)
        {
            Posts.Add((jsonBody, authorization));
            if (PostThrows != null) throw PostThrows;
            return Task.FromResult(Responses.Dequeue());
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
