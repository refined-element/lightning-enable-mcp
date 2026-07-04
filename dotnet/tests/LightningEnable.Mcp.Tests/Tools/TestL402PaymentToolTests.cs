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
    public async Task TestL402Payment_PaysHardcodedTestEndpoint_ReturnsPassed()
    {
        // Arrange — the wallet pays 1 sat and the endpoint returns 200.
        string? calledUrl = null;
        _l402ClientMock.Setup(c => c.FetchWithL402Async(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, long, CancellationToken>(
                (url, _, _, _, _, _) => calledUrl = url)
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

    // ---- Pure interpretation tests (no wallet, no mocks) ----

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
    public void Interpret_Success200ButNotPaid_IsInconclusive()
    {
        var raw = JsonSerializer.Serialize(new
        {
            success = true,
            statusCode = 200,
            payment = new { paid = false }
        });

        var verdict = JsonDocument.Parse(
            TestL402PaymentTool.Interpret(raw, "e")).RootElement;

        verdict.GetProperty("test").GetString().Should().Be("inconclusive");
    }

    [Theory]
    [InlineData("Wallet not configured. Set STRIKE_API_KEY...", "no_wallet")]
    [InlineData("Payment succeeded but no preimage was returned", "no_preimage")]
    [InlineData("OpenNode does not return preimages", "no_preimage")]
    [InlineData("Insufficient balance in wallet", "insufficient_funds")]
    [InlineData("Payment exceeds per-session budget limit", "budget_block")]
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
