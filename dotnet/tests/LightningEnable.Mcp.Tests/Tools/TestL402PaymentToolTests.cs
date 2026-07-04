using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// Tests for the test_l402_payment self-test tool. The tool delegates to the proven
/// access_l402_resource path against a hardcoded 1-sat endpoint, then interprets the
/// result into a plain pass/fail verdict.
/// </summary>
public class TestL402PaymentToolTests
{
    private readonly Mock<IL402HttpClient> _l402ClientMock = new();

    [Fact]
    public async Task TestL402Payment_PaysHardcodedEndpoint_UnderHardCap_ReturnsPassed()
    {
        // Arrange — the wallet pays 1 sat and the endpoint returns 200.
        string? calledUrl = null;
        long calledMaxSats = -1;
        _l402ClientMock.Setup(c => c.FetchWithL402Async(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, long, CancellationToken>(
                (url, _, _, _, maxSats, _) => { calledUrl = url; calledMaxSats = maxSats; })
            .ReturnsAsync(new L402FetchResult
            {
                Success = true,
                Url = "https://api.lightningenable.com/l402/test/ping",
                StatusCode = 200,
                Content = "pong",
                ContentType = "text/plain",
                PaidAmountSats = 1
            });

        // Act
        var result = await TestL402PaymentTool.TestL402Payment(l402Client: _l402ClientMock.Object);

        // Assert
        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("test").GetString().Should().Be("passed");
        json.GetProperty("walletWorking").GetBoolean().Should().BeTrue();
        json.GetProperty("amountSats").GetInt64().Should().Be(1);

        // It must hit the hardcoded 1-sat test endpoint — never a caller-supplied URL.
        calledUrl.Should().EndWith("/l402/test/ping");
        // ...and it must forward the hard cap — the tool's core funds-safety guarantee.
        calledMaxSats.Should().Be(TestL402PaymentTool.MaxTestSats);
        calledMaxSats.Should().BeLessThanOrEqualTo(10);
    }

    [Fact]
    public async Task TestL402Payment_NoWalletConfigured_ReturnsNoWalletDiagnosis()
    {
        // Arrange — L402 client reports the wallet isn't configured (its real message).
        _l402ClientMock.Setup(c => c.FetchWithL402Async(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(L402FetchResult.Failed(
                "https://api.lightningenable.com/l402/test/ping",
                "NWC wallet not configured. Set NWC_CONNECTION_STRING environment variable.",
                402));

        // Act
        var result = await TestL402PaymentTool.TestL402Payment(l402Client: _l402ClientMock.Object);

        // Assert
        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("test").GetString().Should().Be("failed");
        json.GetProperty("reason").GetString().Should().Be("no_wallet");
        json.GetProperty("howToFix").GetString().Should().Contain("NWC_CONNECTION_STRING");
    }

    // ---- Pure interpretation tests over the REAL shapes access_l402_resource emits ----

    [Fact]
    public void Interpret_PaidSuccess_IsPassed()
    {
        var raw = JsonSerializer.Serialize(new
        {
            success = true,
            statusCode = 200,
            payment = new { paid = true, amountSats = 1 }
        });

        var verdict = JsonDocument.Parse(
            TestL402PaymentTool.Interpret(raw, "https://api.lightningenable.com/l402/test/ping")).RootElement;

        verdict.GetProperty("test").GetString().Should().Be("passed");
        verdict.GetProperty("walletWorking").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Interpret_Success200ButNotPaid_IsInconclusive_AndNotASuccess()
    {
        var raw = JsonSerializer.Serialize(new
        {
            success = true,
            statusCode = 200,
            payment = new { paid = false }
        });

        var verdict = JsonDocument.Parse(TestL402PaymentTool.Interpret(raw, "e")).RootElement;

        verdict.GetProperty("test").GetString().Should().Be("inconclusive");
        // An unproven wallet must NOT read as a passing self-test.
        verdict.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Interpret_SplitFlow_PaidButRetryFailed_IsPassed_NotBrokenWallet()
    {
        // access_l402_resource's store-split-flow shape: success:false with a real
        // completed payment. The wallet worked; the endpoint retry didn't.
        var raw = JsonSerializer.Serialize(new
        {
            success = false,
            statusCode = 402,
            error = "Request failed after payment: HTTP 402",
            payment = new { paid = true, amountSats = 1, l402Token = "mac:preimage" }
        });

        var verdict = JsonDocument.Parse(TestL402PaymentTool.Interpret(raw, "e")).RootElement;

        verdict.GetProperty("test").GetString().Should().Be("passed");
        verdict.GetProperty("success").GetBoolean().Should().BeTrue();
        verdict.GetProperty("walletWorking").GetBoolean().Should().BeTrue();
        verdict.GetProperty("amountSats").GetInt64().Should().Be(1);
    }

    [Fact]
    public void Interpret_RequiresConfirmation_IsNeedsConfirmation_NotFailed()
    {
        var raw = JsonSerializer.Serialize(new
        {
            success = false,
            requiresConfirmation = true,
            error = "L402 payment requires human confirmation",
            howToConfirm = "Ask the human for the code shown in the server console."
        });

        var verdict = JsonDocument.Parse(TestL402PaymentTool.Interpret(raw, "e")).RootElement;

        verdict.GetProperty("test").GetString().Should().Be("needs_confirmation");
        // A healthy wallet awaiting a code must not be labeled a failure.
        verdict.TryGetProperty("reason", out _).Should().BeFalse();
        verdict.GetProperty("message").GetString().Should().Contain("confirmationNonce");
    }

    [Fact]
    public void Interpret_BudgetDenyShape_SurfacesSpecificReason()
    {
        // access_l402_resource's deny shape: a "budget" object beside the error,
        // where the error IS the specific denial reason.
        var raw = JsonSerializer.Serialize(new
        {
            success = false,
            error = "Payment amount exceeds maximum per-payment limit of $5.00",
            budget = new { maxSats = 10, remainingSessionUsd = 3.0 }
        });

        var verdict = JsonDocument.Parse(TestL402PaymentTool.Interpret(raw, "e")).RootElement;

        verdict.GetProperty("test").GetString().Should().Be("failed");
        verdict.GetProperty("reason").GetString().Should().Be("budget_block");
        // The specific cap that was hit must be surfaced, not a generic message.
        verdict.GetProperty("message").GetString().Should().Contain("per-payment limit of $5.00");
    }

    [Fact]
    public void Interpret_NonJson_DegradesGracefully()
    {
        var verdict = JsonDocument.Parse(
            TestL402PaymentTool.Interpret("not json at all", "e")).RootElement;

        verdict.GetProperty("success").GetBoolean().Should().BeFalse();
        verdict.GetProperty("test").GetString().Should().Be("failed");
        verdict.GetProperty("reason").GetString().Should().Be("unexpected");
    }

    [Fact]
    public void Interpret_AlreadyPaid_IsInconclusive_NotBrokenWallet()
    {
        // L402 idempotency: re-testing within ~60s reuses the same invoice, which
        // the wallet refuses to double-pay. The wallet is fine — not a failure.
        var raw = JsonSerializer.Serialize(new
        {
            success = false,
            error = "Payment failed: Invoice has already been paid"
        });

        var verdict = JsonDocument.Parse(TestL402PaymentTool.Interpret(raw, "e")).RootElement;

        verdict.GetProperty("test").GetString().Should().Be("inconclusive");
        verdict.GetProperty("message").GetString().Should().Contain("60 second");
        // must NOT claim the wallet is broken
        verdict.TryGetProperty("walletWorking", out var ww).Should().BeTrue();
        (ww.ValueKind == JsonValueKind.Null).Should().BeTrue();
    }

    [Theory]
    [InlineData("Wallet not configured. Set STRIKE_API_KEY...", "no_wallet")]
    [InlineData("", "network")]                                                  // blank (e.g. ReadTimeout) -> network, not unknown
    [InlineData("   ", "network")]
    [InlineData("Rate limit exceeded", "rate_limited")]                          // must NOT be budget_block
    [InlineData("timed out waiting for preimage from relay", "network")]         // network before preimage
    [InlineData("Payment succeeded but no preimage was returned", "no_preimage")]
    [InlineData("OpenNode does not return preimages", "no_preimage")]
    [InlineData("Insufficient balance in wallet", "insufficient_funds")]
    [InlineData("Unable to retrieve balance", "unknown")]                        // balance-FETCH is not insufficient_funds
    [InlineData("Payment exceeds per-session budget of $20", "budget_block")]
    [InlineData("Connection timed out reaching relay", "network")]
    [InlineData("Something totally unexpected happened", "unknown")]
    public void Diagnose_MapsErrorsToStableReasonCodes(string error, string expectedReason)
    {
        var (reason, fix) = TestL402PaymentTool.Diagnose(error);
        reason.Should().Be(expectedReason);
        fix.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ResolveTestEndpoint_TargetsTheTestPingPath()
    {
        // Invariant regardless of the configured base host: it always targets the
        // hardcoded 1-sat test path. (Non-mutating so it can't race parallel tests
        // that also read LIGHTNING_ENABLE_API_URL.)
        TestL402PaymentTool.ResolveTestEndpoint().Should().EndWith("/l402/test/ping");
    }
}
