using System.Security.Cryptography;
using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// Tests for renew_lightning_enable_subscription. Mirrors CreateAccountToolTests one-for-one
/// (budget gate, out-of-band confirmation, paid-but-failed signalling, scrubbing) plus the
/// two things this tool adds: it needs the merchant API key, and that key must never reach
/// a model-visible result.
/// </summary>
public class RenewSubscriptionToolTests
{
    private readonly Mock<IL402HttpClient> _l402ClientMock = new();
    private readonly Mock<IBudgetService> _budgetServiceMock = new();
    private readonly Mock<ILightningEnableApiService> _apiServiceMock = new();

    // Neutral fixture names (gitleaks generic-api-key): not shaped like a real credential,
    // which also means CreateAccountTool.Scrub's le_live_ pattern can NOT catch it — the
    // tool has to redact the configured key itself.
    private const string FixtureApiKey = "fixture-string-merchant-key-renew";
    private const string FixturePreimage = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0";
    private const string FixtureMacaroon = "fixture-string-macaroon";
    private const string ServerPaymentHash = "fixture-string-payment-hash";

    private const string RenewedJson =
        "{\"success\":true,\"alreadyCredited\":false,\"planTier\":\"individual\"," +
        "\"currentPeriodEnd\":\"2026-11-10T00:00:00Z\",\"paidSats\":25000," +
        "\"paymentHash\":\"" + ServerPaymentHash + "\"}";

    public RenewSubscriptionToolTests()
    {
        _apiServiceMock.Setup(a => a.IsConfigured).Returns(true);
        _apiServiceMock.Setup(a => a.ApiKey).Returns(FixtureApiKey);
    }

    private static string ExpectedPaymentHash() =>
        Convert.ToHexString(SHA256.HashData(Convert.FromHexString(FixturePreimage))).ToLowerInvariant();

    private void SetupFetch(L402FetchResult result) =>
        _l402ClientMock
            .Setup(c => c.PostFirstPartyAuthenticatedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private void SetupSuccessfulFetch(long paidSats = 25_000, string body = RenewedJson) =>
        SetupFetch(L402FetchResult.Succeeded("https://x/api/merchant/subscription/renew", body, 200,
            "application/json", paidAmountSats: paidSats,
            l402Token: $"{FixtureMacaroon}:{FixturePreimage}", protocol: "L402"));

    private void VerifyNeverFetched()
    {
        _l402ClientMock.Verify(c => c.PostFirstPartyAuthenticatedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _l402ClientMock.Verify(c => c.PostFirstPartyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ApprovalCheckResult Approval(ApprovalLevel level, long sats = 50_000, decimal usd = 25.00m, string? denial = null) =>
        new()
        {
            Level = level,
            AmountSats = sats,
            AmountUsd = usd,
            DenialReason = denial,
            RemainingSessionBudgetUsd = 50.00m
        };

    private Task<string> Renew(int maxSats = 50_000, string? confirmationNonce = null, bool withBudget = false) =>
        RenewSubscriptionTool.RenewLightningEnableSubscription(
            maxSats: maxSats,
            confirmationNonce: confirmationNonce,
            l402Client: _l402ClientMock.Object,
            budgetService: withBudget ? _budgetServiceMock.Object : null,
            apiService: _apiServiceMock.Object);

    // ----- API key -----

    [Fact]
    public async Task Renew_NoApiService_ReturnsKeyRequiredError_PointingAtCreateAccount_DoesNotFetch()
    {
        var result = await RenewSubscriptionTool.RenewLightningEnableSubscription(
            l402Client: _l402ClientMock.Object, apiService: null);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("create_lightning_enable_account");
        VerifyNeverFetched();
    }

    [Theory]
    [InlineData(false, FixtureApiKey)]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, "   ")]
    public async Task Renew_UnconfiguredOrBlankKey_ReturnsKeyRequiredError_DoesNotFetch(bool isConfigured, string? key)
    {
        _apiServiceMock.Setup(a => a.IsConfigured).Returns(isConfigured);
        _apiServiceMock.Setup(a => a.ApiKey).Returns(key);

        var result = await Renew();

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Be(LightningEnableApiService.ProducerApiKeyRequired);
        VerifyNeverFetched();
    }

    // ----- Wallet -----

    [Fact]
    public async Task Renew_NoClient_ReturnsError()
    {
        var result = await RenewSubscriptionTool.RenewLightningEnableSubscription(
            l402Client: null, apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("No wallet configured");
    }

    // ----- Budget gating -----

    [Fact]
    public async Task Renew_BudgetDenied_ReturnsError_DoesNotFetch()
    {
        _budgetServiceMock.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Approval(ApprovalLevel.Deny, denial: "over session limit"));

        var result = await Renew(withBudget: true);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("denied by budget policy");
        _budgetServiceMock.Verify(b => b.CheckApprovalLevelAsync(50_000, It.IsAny<CancellationToken>()), Times.Once);
        VerifyNeverFetched();
    }

    [Theory]
    [InlineData(ConfirmationChannelKind.Stderr)]
    [InlineData(ConfirmationChannelKind.Webhook)]
    [InlineData(ConfirmationChannelKind.File)]
    public async Task Renew_RequiresConfirmation_DoesNotLeakCode_DoesNotFetch(ConfirmationChannelKind channel)
    {
        _budgetServiceMock.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Approval(ApprovalLevel.FormConfirm));
        ConfirmationTestSetup.SetupDelivered(
            _budgetServiceMock, channel, "ABC123", 50_000, 25.00m, "renew_lightning_enable_subscription");

        // No McpServer → elicitation unavailable → out-of-band approval-channel path.
        var result = await Renew(withBudget: true);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("requiresConfirmation").GetBoolean().Should().BeTrue();
        json.GetProperty("confirmationChannel").GetString().Should().Be(channel.ToString().ToLowerInvariant());
        json.TryGetProperty("nonce", out _).Should().BeFalse("the code must never be in the result");
        result.Should().NotContain("ABC123", "the confirmation code must not leak into the model-visible result");
        result.Should().NotContain(FixtureApiKey);
        json.GetProperty("expiresInSeconds").GetInt32().Should().Be(120);

        // The request names this tool, the renewal, and the renew URL as the destination.
        _budgetServiceMock.Verify(b => b.RequestConfirmationAsync(
            It.Is<ConfirmationRequest>(r =>
                r.AmountSats == 50_000 &&
                r.ToolName == "renew_lightning_enable_subscription" &&
                r.Title == "SUBSCRIPTION RENEWAL CONFIRMATION REQUIRED" &&
                r.Description == "Lightning Enable subscription renewal" &&
                r.Destination.EndsWith("/api/merchant/subscription/renew")),
            It.IsAny<CancellationToken>()), Times.Once);
        VerifyNeverFetched();
    }

    [Fact]
    public async Task Renew_RefuseChannel_RefusesWithoutPaying()
    {
        _budgetServiceMock.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Approval(ApprovalLevel.FormConfirm));
        ConfirmationTestSetup.SetupRefused(_budgetServiceMock);

        var result = await Renew(withBudget: true);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("requiresConfirmation").GetBoolean().Should().BeFalse();
        json.GetProperty("confirmationChannel").GetString().Should().Be("refuse");
        VerifyNeverFetched();
    }

    [Fact]
    public async Task Renew_WithConfirmationNonce_ProceedsAndConsumesBoundToToolAndDestination()
    {
        _budgetServiceMock.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Approval(ApprovalLevel.FormConfirm));
        _budgetServiceMock.Setup(b => b.ValidateAndConsumeConfirmation(
                "ABC123", 30_000, "renew_lightning_enable_subscription",
                It.Is<string>(u => u.EndsWith("/api/merchant/subscription/renew"))))
            .Returns(new PendingConfirmation { Nonce = "ABC123", AmountSats = 30_000, ToolName = "renew_lightning_enable_subscription" });
        SetupSuccessfulFetch();

        var result = await Renew(maxSats: 30_000, confirmationNonce: "abc123", withBudget: true);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeTrue();

        // Code upcased; bound to amount + tool + renew destination.
        _budgetServiceMock.Verify(b => b.ValidateAndConsumeConfirmation(
            "ABC123", 30_000, "renew_lightning_enable_subscription",
            It.Is<string>(u => u.EndsWith("/api/merchant/subscription/renew"))), Times.Once);
        _l402ClientMock.Verify(c => c.PostFirstPartyAuthenticatedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), 30_000L, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Renew_WithInvalidConfirmationNonce_RefusesWithoutPaying()
    {
        _budgetServiceMock.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Approval(ApprovalLevel.FormConfirm));
        _budgetServiceMock.Setup(b => b.ValidateAndConsumeConfirmation(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((PendingConfirmation?)null);

        var result = await Renew(confirmationNonce: "WRONG1", withBudget: true);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("Confirmation code is invalid");
        VerifyNeverFetched();
    }

    // ----- Renewal flow -----

    [Fact]
    public async Task Renew_PostsToRenewEndpoint_WithTheApiKey_AndDefaultCeiling()
    {
        SetupSuccessfulFetch();

        await Renew();

        _l402ClientMock.Verify(c => c.PostFirstPartyAuthenticatedAsync(
            It.Is<string>(u => u.EndsWith("/api/merchant/subscription/renew")),
            "{}",
            FixtureApiKey,
            50_000L,
            It.IsAny<CancellationToken>()), Times.Once);

        // Never the unauthenticated POST, never the generic GET/HEAD-only path.
        _l402ClientMock.Verify(c => c.PostFirstPartyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _l402ClientMock.Verify(c => c.FetchWithL402Async(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Renew_Success_ReturnsPeriodAndPaymentDetails()
    {
        SetupSuccessfulFetch();

        var result = await Renew();

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("currentPeriodEnd").GetString().Should().Be("2026-11-10T00:00:00Z");
        json.GetProperty("paidSats").GetInt64().Should().Be(25_000);
        json.GetProperty("paymentHash").GetString().Should().Be(ServerPaymentHash);
        json.GetProperty("alreadyCredited").GetBoolean().Should().BeFalse();
        json.GetProperty("planTier").GetString().Should().Be("individual");
        json.GetProperty("activation").GetProperty("paid").GetBoolean().Should().BeTrue();
        json.GetProperty("activation").GetProperty("amountSats").GetInt64().Should().Be(25_000);
        json.TryGetProperty("receipt_written", out _).Should().BeTrue();
        json.GetProperty("message").GetString().Should().Contain("2026-11-10T00:00:00Z");
    }

    [Fact]
    public async Task Renew_AlreadyCredited_SaysSo()
    {
        SetupSuccessfulFetch(body: RenewedJson.Replace("\"alreadyCredited\":false", "\"alreadyCredited\":true"));

        var result = await Renew();

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("alreadyCredited").GetBoolean().Should().BeTrue();
        json.GetProperty("message").GetString().Should().Contain("already credited");
    }

    [Fact]
    public async Task Renew_Success_NeverEchoesTheApiKeyOrThePreimage()
    {
        SetupSuccessfulFetch();

        var result = await Renew();

        result.Should().NotContain(FixtureApiKey, "the merchant API key must never reach a tool result");
        result.Should().NotContain(FixturePreimage, "the preimage is a bearer credential");
        result.Should().NotContain(FixtureMacaroon);
    }

    // ----- Paid-but-failed (money already left the wallet) -----

    [Fact]
    public async Task Renew_PaidButRefused409_SurfacesPaidSignal_ErrorCode_AndSaysDoNotRetry()
    {
        SetupFetch(L402FetchResult.Failed(
            "https://x/api/merchant/subscription/renew",
            "Request failed after payment: HTTP 409: {\"error\":\"prepaid_cap_reached\",\"message\":\"already prepaid\"}",
            409, paidAmountSats: 25_000, l402Token: $"{FixtureMacaroon}:{FixturePreimage}", protocol: "L402"));

        var result = await Renew();

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("errorCode").GetString().Should().Be("prepaid_cap_reached");
        json.GetProperty("statusCode").GetInt32().Should().Be(409);

        var activation = json.GetProperty("activation");
        activation.GetProperty("paid").GetBoolean().Should().BeTrue();
        activation.GetProperty("amountSats").GetInt64().Should().Be(25_000);

        json.GetProperty("warning").GetString().Should().Contain("Do NOT re-run")
            .And.Contain("support@lightningenable.com");

        // The payment hash support needs is derived from the preimage; the preimage itself stays out.
        json.GetProperty("paymentHash").GetString().Should().Be(ExpectedPaymentHash());
        result.Should().NotContain(FixturePreimage);
        result.Should().NotContain(FixtureMacaroon);
        result.Should().NotContain(FixtureApiKey);
    }

    [Fact]
    public async Task Renew_PaidButPending_SurfacesPaidSignal_AndDoesNotBlameWallet()
    {
        SetupFetch(L402FetchResult.Failed(
            "https://x/api/merchant/subscription/renew",
            "Payment has not settled yet (tracking ID: strike-123) — it may still succeed or fail. " +
            "Do not retry it; check its status with the provider.",
            402, paidAmountSats: 25_000));

        var result = await Renew();

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("activation").GetProperty("paid").GetBoolean().Should().BeTrue();
        json.GetProperty("activation").GetProperty("amountSats").GetInt64().Should().Be(25_000);
        json.GetProperty("warning").GetString().Should().Contain("Do NOT re-run");
        json.GetProperty("hint").GetString().Should().Contain("do not re-pay");

        // Direct contradiction of the client's own "do not retry" guidance.
        result.Should().NotContain("Check wallet balance");
    }

    [Fact]
    public async Task Renew_PaidButNoPreimage_SurfacesPaidSignal()
    {
        SetupFetch(L402FetchResult.Failed(
            "https://x/api/merchant/subscription/renew",
            "Payment succeeded (tracking ID: strike-456) but wallet did not return a usable preimage. " +
            "L402 requires a preimage for verification.",
            402, paidAmountSats: 25_000));

        var result = await Renew();

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("activation").GetProperty("paid").GetBoolean().Should().BeTrue();
        result.Should().NotContain("Check wallet balance");
    }

    [Fact]
    public async Task Renew_UnpaidChallengeFailure_DoesNotClaimPaid()
    {
        SetupFetch(L402FetchResult.Failed(
            "https://x/api/merchant/subscription/renew",
            "Invoice amount 25000 sats exceeds maximum 1000 sats",
            402, paidAmountSats: 0));

        var result = await Renew(maxSats: 1000);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("activation").GetProperty("paid").GetBoolean().Should().BeFalse();
        json.TryGetProperty("warning", out _).Should().BeFalse();
        json.GetProperty("hint").GetString().Should().Contain("no payment was made");
    }

    [Fact]
    public async Task Renew_Unpaid409_SurfacesServerErrorCodeVerbatim()
    {
        SetupFetch(L402FetchResult.Failed(
            "https://x/api/merchant/subscription/renew",
            "HTTP 409: {\"error\":\"stripe_subscription_active\",\"message\":\"This account is billed by card.\"}",
            409));

        var result = await Renew();

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("errorCode").GetString().Should().Be("stripe_subscription_active");
        json.GetProperty("error").GetString().Should().Contain("billed by card");
        json.GetProperty("activation").GetProperty("paid").GetBoolean().Should().BeFalse();
        json.TryGetProperty("warning", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Renew_NonJsonResponse_ReturnsError_KeepsPaidSignal()
    {
        SetupSuccessfulFetch(body: "<html>gateway error</html>");

        var result = await Renew();

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("not valid JSON");
        json.GetProperty("activation").GetProperty("paid").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Renew_NoPeriodEndInResponse_ReturnsError_KeepsPaidSignal()
    {
        SetupSuccessfulFetch(body: "{\"success\":true,\"planTier\":\"individual\"}");

        var result = await Renew();

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("currentPeriodEnd");
        json.GetProperty("activation").GetProperty("paid").GetBoolean().Should().BeTrue();
        json.GetProperty("warning").GetString().Should().Contain("Do NOT re-run");
    }

    // ----- Error scrubbing / key redaction -----

    [Fact]
    public async Task Renew_FetchFails_ScrubsAndTruncatesServerBody()
    {
        // Key-shaped token appears early so we prove redaction runs BEFORE truncation.
        var leaky = "le_live_leakedkey :: " + new string('y', 400);
        SetupFetch(L402FetchResult.Failed("https://x/api/merchant/subscription/renew", leaky, 500));

        var result = await Renew();

        var error = JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()!;
        error.Should().NotContain("leakedkey", "the API-key-shaped token must be redacted");
        error.Length.Should().BeLessThanOrEqualTo(203, "the server body must be truncated");
    }

    [Fact]
    public async Task Renew_ServerEchoesTheConfiguredKey_KeyIsRedacted()
    {
        SetupFetch(L402FetchResult.Failed(
            "https://x/api/merchant/subscription/renew",
            $"HTTP 401: {{\"error\":\"invalid_api_key\",\"message\":\"key {FixtureApiKey} is not valid\"}}",
            401));

        var result = await Renew();

        result.Should().NotContain(FixtureApiKey, "the merchant API key must never reach a tool result");
        JsonDocument.Parse(result).RootElement.GetProperty("errorCode").GetString().Should().Be("invalid_api_key");
    }

    [Fact]
    public async Task Renew_ClientThrows_ErrorIsScrubbed_AndKeyIsRedacted()
    {
        _l402ClientMock
            .Setup(c => c.PostFirstPartyAuthenticatedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException($"boom with {FixtureApiKey} and le_live_otherkey"));

        var result = await Renew();

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        result.Should().NotContain(FixtureApiKey);
        result.Should().NotContain("otherkey");

        // Nothing was paid (no receipt observed) — the plain error shape, no paid claim.
        json.TryGetProperty("activation", out _).Should().BeFalse();
        json.TryGetProperty("warning", out _).Should().BeFalse();
        json.GetProperty("receipt_written").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// The client throws AFTER the wallet paid. The receipt seam
    /// (ReceiptRecordingWalletService) records its write on the ambient scope during the
    /// payment — reproduced here through the same internal <c>RecordWrite</c> it calls — so
    /// the tool must report the payment and tell the agent not to pay again.
    /// </summary>
    [Fact]
    public async Task Renew_ClientThrowsAfterPayment_SurfacesPaidSignal_AndSaysDoNotRetry()
    {
        _l402ClientMock
            .Setup(c => c.PostFirstPartyAuthenticatedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Callback(() => PaymentReceiptScope.Current!.RecordWrite(true))
            .ThrowsAsync(new HttpRequestException($"connection reset after paying with {FixtureApiKey}"));

        var result = await Renew();

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("receipt_written").GetBoolean().Should().BeTrue();
        json.GetProperty("activation").GetProperty("paid").GetBoolean().Should().BeTrue();
        json.GetProperty("warning").GetString().Should().Contain("Do NOT re-run")
            .And.Contain("support@lightningenable.com")
            .And.Contain("time of this call");
        result.Should().NotContain(FixtureApiKey);
    }

    // ----- Paid, then the retry failed in transport (the shape L402HttpClient now returns) -----

    [Fact]
    public async Task Renew_PaidThenRetryTransportFailure_SurfacesPaidSignal_AndSaysDoNotRetry()
    {
        SetupFetch(L402FetchResult.Failed(
            "https://x/api/merchant/subscription/renew",
            "Payment succeeded (25000 sats) but the request failed after payment: request failed (connection reset). Do NOT pay again.",
            statusCode: 0, paidAmountSats: 25_000, l402Token: $"{FixtureMacaroon}:{FixturePreimage}", protocol: "L402"));

        var result = await Renew();

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("activation").GetProperty("paid").GetBoolean().Should().BeTrue();
        json.GetProperty("activation").GetProperty("amountSats").GetInt64().Should().Be(25_000);
        json.GetProperty("warning").GetString().Should().Contain("Do NOT re-run");
        json.GetProperty("paymentHash").GetString().Should().Be(ExpectedPaymentHash());
        result.Should().NotContain("Nothing was charged").And.NotContain("refused");
        result.Should().NotContain(FixturePreimage);
    }

    /// <summary>
    /// The client reported no amount, but the wallet seam recorded a receipt under this call —
    /// independent evidence of a payment. The tool must flag it paid without inventing an amount.
    /// </summary>
    [Fact]
    public async Task Renew_FailedWithNoAmountButReceiptRecorded_FlagsPaid_WithoutClaimingAnAmount()
    {
        _l402ClientMock
            .Setup(c => c.PostFirstPartyAuthenticatedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Callback(() => PaymentReceiptScope.Current!.RecordWrite(true))
            .ReturnsAsync(L402FetchResult.Failed("https://x/api/merchant/subscription/renew", "Request timed out", 0, paidAmountSats: 0));

        var result = await Renew();

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("receipt_written").GetBoolean().Should().BeTrue();
        var activation = json.GetProperty("activation");
        activation.GetProperty("paid").GetBoolean().Should().BeTrue();
        activation.TryGetProperty("amountSats", out _).Should().BeFalse("the amount is unknown, so none is claimed");
        json.GetProperty("warning").GetString().Should().Contain("may have been taken").And.Contain("Do NOT re-run");
        result.Should().NotContain("Nothing was charged");
    }

    [Fact]
    public async Task Renew_UnpaidTransportError_HintIsNeutral_NeverSaysNothingWasCharged()
    {
        SetupFetch(L402FetchResult.Failed("https://x/api/merchant/subscription/renew", "Request timed out"));

        var result = await Renew();

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("activation").GetProperty("paid").GetBoolean().Should().BeFalse();
        json.GetProperty("hint").GetString().Should().Contain("transport or wallet error")
            .And.Contain("payment history")
            .And.NotContain("refused")
            .And.NotContain("Nothing was charged");
    }

    [Fact]
    public async Task Renew_PaidRedirect_ReplacesTheRetryWithTokenAdvice()
    {
        SetupFetch(new L402FetchResult
        {
            Success = false,
            Url = "https://x/api/merchant/subscription/renew",
            StatusCode = 302,
            RedirectLocation = "https://x/elsewhere",
            PaidAmountSats = 25_000,
            L402Token = $"{FixtureMacaroon}:{FixturePreimage}",
            Protocol = "L402",
            ErrorMessage = "Payment succeeded (25000 sats). The resource redirected to https://x/elsewhere. " +
                           "You have ALREADY PAID — do NOT pay again. Retry the redirect target with the returned L402 token."
        });

        var result = await Renew();

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("activation").GetProperty("paid").GetBoolean().Should().BeTrue();
        json.GetProperty("error").GetString().Should().Contain("redirect")
            .And.Contain("Do NOT pay again")
            .And.Contain("payment hash")
            .And.NotContain("Retry the redirect target");
        json.GetProperty("hint").GetString().Should().Contain("redirected").And.Contain("Do not re-pay");
        json.GetProperty("warning").GetString().Should().Contain("Do NOT re-run");
        result.Should().NotContain(FixturePreimage);
    }
}
