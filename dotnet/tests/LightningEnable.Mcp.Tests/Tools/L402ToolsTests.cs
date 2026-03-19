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
    public async Task PayL402Challenge_WithMissingMacaroon_ReturnsInputError()
    {
        // Act
        var result = await PayL402ChallengeTool.PayL402Challenge(
            invoice: "lnbc100n1...",
            macaroon: "",
            l402Client: _l402ClientMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Macaroon is required");
    }

    #endregion
}
