using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// Tests for L402 tools (access_l402_resource and pay_l402_challenge).
/// </summary>
public class L402ToolsTests
{
    private readonly Mock<IL402HttpClient> _l402ClientMock;

    public L402ToolsTests()
    {
        _l402ClientMock = new Mock<IL402HttpClient>();
    }

    #region AccessL402ResourceTool Tests

    [Fact]
    public async Task AccessL402Resource_CallsL402Client()
    {
        // Arrange
        _l402ClientMock.Setup(c => c.FetchWithL402Async(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new L402FetchResult
            {
                Success = true,
                Url = "https://api.example.com/data",
                StatusCode = 200,
                Content = "{\"data\": \"test\"}",
                ContentType = "application/json"
            });

        // Act
        var result = await AccessL402ResourceTool.AccessL402Resource(
            url: "https://api.example.com/data",
            l402Client: _l402ClientMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        // Verify L402 client was called
        _l402ClientMock.Verify(c => c.FetchWithL402Async(
            "https://api.example.com/data", "GET", null, null, 1000L, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AccessL402Resource_WhenRetryFailsAfterPayment_SurfacesL402Token()
    {
        // Arrange — payment succeeded but retry returned 402 (e.g., store split-flow)
        _l402ClientMock.Setup(c => c.FetchWithL402Async(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(L402FetchResult.Failed(
                "https://store.example.com/checkout",
                "Request failed after payment: HTTP 402",
                402,
                paidAmountSats: 5000,
                l402Token: "macaroon123:preimage456"));

        // Act
        var result = await AccessL402ResourceTool.AccessL402Resource(
            url: "https://store.example.com/checkout",
            l402Client: _l402ClientMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("statusCode").GetInt32().Should().Be(402);

        var payment = json.RootElement.GetProperty("payment");
        payment.GetProperty("paid").GetBoolean().Should().BeTrue();
        payment.GetProperty("amountSats").GetInt64().Should().Be(5000);
        payment.GetProperty("l402Token").GetString().Should().Be("macaroon123:preimage456");
        payment.GetProperty("note").GetString().Should().Contain("valid");
    }

    #endregion

    #region PayL402ChallengeTool Tests

    [Fact]
    public async Task PayL402Challenge_CallsL402Client()
    {
        // Arrange
        _l402ClientMock.Setup(c => c.PayChallengeAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("macaroon:preimage");

        // Act
        var result = await PayL402ChallengeTool.PayL402Challenge(
            invoice: "lnbc100n1...",
            macaroon: "base64macaroon",
            l402Client: _l402ClientMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("l402Token").GetString().Should().Be("macaroon:preimage");

        // Verify L402 client was called
        _l402ClientMock.Verify(c => c.PayChallengeAsync(
            "base64macaroon", "lnbc100n1...", 1000L, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PayL402Challenge_WithMissingInvoice_ReturnsInputError()
    {
        // Act
        var result = await PayL402ChallengeTool.PayL402Challenge(
            invoice: "",
            macaroon: "base64macaroon",
            l402Client: _l402ClientMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Invoice is required");
    }

    [Fact]
    public async Task PayL402Challenge_WithNullMacaroon_MppMode_ReturnsPreimageOnly()
    {
        // Arrange
        _l402ClientMock.Setup(c => c.PayChallengeAsync(
            null, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("abcdef1234567890");

        // Act
        var result = await PayL402ChallengeTool.PayL402Challenge(
            invoice: "lnbc100n1pjtest",
            macaroon: null,
            l402Client: _l402ClientMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("l402Token").GetString().Should().Be("abcdef1234567890");
        json.RootElement.GetProperty("usage").GetProperty("protocol").GetString().Should().Be("MPP");
        json.RootElement.GetProperty("usage").GetProperty("headerValue").GetString()
            .Should().Contain("Payment method=\"lightning\"");
    }

    [Fact]
    public async Task PayL402Challenge_WithMacaroon_L402Mode_ReturnsFullToken()
    {
        // Arrange
        _l402ClientMock.Setup(c => c.PayChallengeAsync(
            "base64macaroon", It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("base64macaroon:preimage123");

        // Act
        var result = await PayL402ChallengeTool.PayL402Challenge(
            invoice: "lnbc100n1pjtest",
            macaroon: "base64macaroon",
            l402Client: _l402ClientMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("l402Token").GetString().Should().Be("base64macaroon:preimage123");
        json.RootElement.GetProperty("usage").GetProperty("protocol").GetString().Should().Be("L402");
        json.RootElement.GetProperty("usage").GetProperty("headerValue").GetString()
            .Should().StartWith("L402 ");
    }

    #endregion

    #region Nonce Fallback Tests

    [Fact]
    public async Task AccessL402Resource_RequiresConfirmation_ElicitationFails_ReturnsNonceFallback()
    {
        // Arrange — budget says RequiresConfirmation, no server (elicitation unavailable)
        var budgetServiceMock = new Mock<IBudgetService>();
        var priceServiceMock = new Mock<IPriceService>();

        budgetServiceMock.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalCheckResult
            {
                Level = ApprovalLevel.FormConfirm,
                AmountSats = 1000,
                AmountUsd = 5.00m,
                RemainingSessionBudgetUsd = 95.00m
            });
        budgetServiceMock.Setup(b => b.CreatePendingConfirmation(
                It.IsAny<long>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new PendingConfirmation
            {
                Nonce = "L4C123",
                AmountSats = 1000,
                AmountUsd = 5.00m,
                ToolName = "access_l402_resource",
                Description = "https://api.example.com/data",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(2)
            });
        budgetServiceMock.Setup(b => b.GetUserConfiguration())
            .Returns(new UserBudgetConfiguration());

        // Act — no McpServer, so elicitation can't work
        var result = await AccessL402ResourceTool.AccessL402Resource(
            url: "https://api.example.com/data",
            l402Client: _l402ClientMock.Object,
            budgetService: budgetServiceMock.Object,
            priceService: priceServiceMock.Object);

        // Assert — should return nonce-based fallback
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("requiresConfirmation").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("nonce").GetString().Should().Be("L4C123");
        json.RootElement.TryGetProperty("howToConfirm", out _).Should().BeTrue();
        json.RootElement.GetProperty("expiresInSeconds").GetInt32().Should().Be(120);
        json.RootElement.GetProperty("amount").GetProperty("maxSats").GetInt32().Should().Be(1000);
        json.RootElement.GetProperty("amount").GetProperty("usd").GetDecimal().Should().Be(5.00m);
    }

    [Fact]
    public async Task PayL402Challenge_RequiresConfirmation_ElicitationFails_ReturnsNonceFallback()
    {
        // Arrange — budget says RequiresConfirmation, no server (elicitation unavailable)
        var budgetServiceMock = new Mock<IBudgetService>();
        var priceServiceMock = new Mock<IPriceService>();

        budgetServiceMock.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalCheckResult
            {
                Level = ApprovalLevel.FormConfirm,
                AmountSats = 500,
                AmountUsd = 2.50m,
                RemainingSessionBudgetUsd = 97.50m
            });
        budgetServiceMock.Setup(b => b.CreatePendingConfirmation(
                It.IsAny<long>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new PendingConfirmation
            {
                Nonce = "PLC456",
                AmountSats = 500,
                AmountUsd = 2.50m,
                ToolName = "pay_l402_challenge",
                Description = "lnbc500n1pjtest...",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(2)
            });
        budgetServiceMock.Setup(b => b.GetUserConfiguration())
            .Returns(new UserBudgetConfiguration());

        // Act — no McpServer, so elicitation can't work
        var result = await PayL402ChallengeTool.PayL402Challenge(
            invoice: "lnbc500n1pjtest",
            macaroon: "base64macaroon",
            l402Client: _l402ClientMock.Object,
            budgetService: budgetServiceMock.Object,
            priceService: priceServiceMock.Object);

        // Assert — should return nonce-based fallback
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("requiresConfirmation").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("nonce").GetString().Should().Be("PLC456");
        json.RootElement.TryGetProperty("howToConfirm", out _).Should().BeTrue();
        json.RootElement.GetProperty("expiresInSeconds").GetInt32().Should().Be(120);
        json.RootElement.GetProperty("amount").GetProperty("sats").GetInt64().Should().Be(50); // lnbc500n = 50 sats
        json.RootElement.GetProperty("amount").GetProperty("usd").GetDecimal().Should().Be(2.50m);
    }

    #endregion
}
