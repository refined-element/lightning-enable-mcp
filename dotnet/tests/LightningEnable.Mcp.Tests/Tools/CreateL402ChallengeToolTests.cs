using System.Text.Json;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Tools;

public class CreateL402ChallengeToolTests
{
    private readonly Mock<ILightningEnableApiService> _apiServiceMock;

    public CreateL402ChallengeToolTests()
    {
        _apiServiceMock = new Mock<ILightningEnableApiService>();
        _apiServiceMock.Setup(s => s.IsConfigured).Returns(true);
    }

    #region Input Validation Tests

    [Fact]
    public async Task CreateL402Challenge_EmptyResource_ReturnsError()
    {
        var result = await CreateL402ChallengeTool.CreateL402Challenge(
            resource: "",
            priceSats: 100,
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Resource");
    }

    [Fact]
    public async Task CreateL402Challenge_NullResource_ReturnsError()
    {
        var result = await CreateL402ChallengeTool.CreateL402Challenge(
            resource: null!,
            priceSats: 100,
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Resource");
    }

    [Fact]
    public async Task CreateL402Challenge_ZeroPrice_ReturnsError()
    {
        var result = await CreateL402ChallengeTool.CreateL402Challenge(
            resource: "https://api.example.com/data",
            priceSats: 0,
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("greater than 0");
    }

    [Fact]
    public async Task CreateL402Challenge_NegativePrice_ReturnsError()
    {
        var result = await CreateL402ChallengeTool.CreateL402Challenge(
            resource: "https://api.example.com/data",
            priceSats: -10,
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("greater than 0");
    }

    #endregion

    #region Service Availability Tests

    [Fact]
    public async Task CreateL402Challenge_NullApiService_ReturnsError()
    {
        var result = await CreateL402ChallengeTool.CreateL402Challenge(
            resource: "https://api.example.com/data",
            priceSats: 100,
            apiService: null);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("not available");
    }

    [Fact]
    public async Task CreateL402Challenge_ApiKeyNotConfigured_ReturnsError()
    {
        _apiServiceMock.Setup(s => s.IsConfigured).Returns(false);

        var result = await CreateL402ChallengeTool.CreateL402Challenge(
            resource: "https://api.example.com/data",
            priceSats: 100,
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("API key not configured");
    }

    #endregion

    #region Success Tests

    [Fact]
    public async Task CreateL402Challenge_Success_ReturnsChallengeDetails()
    {
        _apiServiceMock.Setup(s => s.CreateChallengeAsync(
                "https://api.example.com/data", 100, "Test data access", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateChallengeResult
            {
                Success = true,
                Invoice = "lnbc1000n1p3testinvoice",
                Macaroon = "dGVzdG1hY2Fyb29u",
                PaymentHash = "abc123def456",
                ExpiresAt = "2026-03-14T00:00:00Z"
            });

        var result = await CreateL402ChallengeTool.CreateL402Challenge(
            resource: "https://api.example.com/data",
            priceSats: 100,
            description: "Test data access",
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var challenge = json.RootElement.GetProperty("challenge");
        challenge.GetProperty("invoice").GetString().Should().Be("lnbc1000n1p3testinvoice");
        challenge.GetProperty("macaroon").GetString().Should().Be("dGVzdG1hY2Fyb29u");
        challenge.GetProperty("paymentHash").GetString().Should().Be("abc123def456");
        challenge.GetProperty("expiresAt").GetString().Should().Be("2026-03-14T00:00:00Z");

        json.RootElement.GetProperty("resource").GetString().Should().Be("https://api.example.com/data");
        json.RootElement.GetProperty("priceSats").GetInt64().Should().Be(100);
    }

    [Fact]
    public async Task CreateL402Challenge_Success_IncludesInstructions()
    {
        _apiServiceMock.Setup(s => s.CreateChallengeAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateChallengeResult
            {
                Success = true,
                Invoice = "lnbc1000n1p3test",
                Macaroon = "dGVzdA==",
                PaymentHash = "hash123"
            });

        var result = await CreateL402ChallengeTool.CreateL402Challenge(
            resource: "test-resource",
            priceSats: 50,
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.TryGetProperty("instructions", out _).Should().BeTrue();

        var instructions = json.RootElement.GetProperty("instructions");
        instructions.GetProperty("tokenFormat").GetString().Should().Be("L402 {macaroon}:{preimage}");
        instructions.GetProperty("verifyWith").GetString().Should().Contain("verify_l402_payment");
    }

    [Fact]
    public async Task CreateL402Challenge_NullDescription_Succeeds()
    {
        _apiServiceMock.Setup(s => s.CreateChallengeAsync(
                "resource", 100, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateChallengeResult
            {
                Success = true,
                Invoice = "lnbc1000n1p3test",
                Macaroon = "mac",
                PaymentHash = "hash"
            });

        var result = await CreateL402ChallengeTool.CreateL402Challenge(
            resource: "resource",
            priceSats: 100,
            description: null,
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    #endregion

    #region API Error Tests

    [Fact]
    public async Task CreateL402Challenge_ApiReturnsError_ReturnsError()
    {
        _apiServiceMock.Setup(s => s.CreateChallengeAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateChallengeResult
            {
                Success = false,
                ErrorMessage = "Subscription required for L402 challenges"
            });

        var result = await CreateL402ChallengeTool.CreateL402Challenge(
            resource: "test",
            priceSats: 100,
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Subscription required");
    }

    [Fact]
    public async Task CreateL402Challenge_ApiThrowsException_ReturnsError()
    {
        _apiServiceMock.Setup(s => s.CreateChallengeAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Network timeout"));

        var result = await CreateL402ChallengeTool.CreateL402Challenge(
            resource: "test",
            priceSats: 100,
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Network timeout");
    }

    #endregion

    #region JSON Response Tests

    [Fact]
    public async Task CreateL402Challenge_AllResponses_ReturnValidJson()
    {
        // Error response
        var errorResult = await CreateL402ChallengeTool.CreateL402Challenge(
            resource: "",
            priceSats: 100,
            apiService: _apiServiceMock.Object);

        var act = () => JsonDocument.Parse(errorResult);
        act.Should().NotThrow();

        // Success response
        _apiServiceMock.Setup(s => s.CreateChallengeAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateChallengeResult
            {
                Success = true,
                Invoice = "inv",
                Macaroon = "mac",
                PaymentHash = "hash"
            });

        var successResult = await CreateL402ChallengeTool.CreateL402Challenge(
            resource: "test",
            priceSats: 100,
            apiService: _apiServiceMock.Object);

        act = () => JsonDocument.Parse(successResult);
        act.Should().NotThrow();
    }

    #endregion
}
