using System.Text.Json;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Tools;

public class VerifyL402PaymentToolTests
{
    private readonly Mock<ILightningEnableApiService> _apiServiceMock;

    public VerifyL402PaymentToolTests()
    {
        _apiServiceMock = new Mock<ILightningEnableApiService>();
        _apiServiceMock.Setup(s => s.IsConfigured).Returns(true);
    }

    #region Input Validation Tests

    [Fact]
    public async Task VerifyL402Payment_EmptyMacaroon_ReturnsError()
    {
        var result = await VerifyL402PaymentTool.VerifyL402Payment(
            macaroon: "",
            preimage: "abc123",
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Macaroon is required");
    }

    [Fact]
    public async Task VerifyL402Payment_NullMacaroon_ReturnsError()
    {
        var result = await VerifyL402PaymentTool.VerifyL402Payment(
            macaroon: null!,
            preimage: "abc123",
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Macaroon is required");
    }

    [Fact]
    public async Task VerifyL402Payment_EmptyPreimage_ReturnsError()
    {
        var result = await VerifyL402PaymentTool.VerifyL402Payment(
            macaroon: "dGVzdA==",
            preimage: "",
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Preimage is required");
    }

    [Fact]
    public async Task VerifyL402Payment_NullPreimage_ReturnsError()
    {
        var result = await VerifyL402PaymentTool.VerifyL402Payment(
            macaroon: "dGVzdA==",
            preimage: null!,
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Preimage is required");
    }

    [Fact]
    public async Task VerifyL402Payment_WhitespaceMacaroon_ReturnsError()
    {
        var result = await VerifyL402PaymentTool.VerifyL402Payment(
            macaroon: "   ",
            preimage: "abc123",
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task VerifyL402Payment_WhitespacePreimage_ReturnsError()
    {
        var result = await VerifyL402PaymentTool.VerifyL402Payment(
            macaroon: "dGVzdA==",
            preimage: "   ",
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    #endregion

    #region Service Availability Tests

    [Fact]
    public async Task VerifyL402Payment_NullApiService_ReturnsError()
    {
        var result = await VerifyL402PaymentTool.VerifyL402Payment(
            macaroon: "dGVzdA==",
            preimage: "abc123",
            apiService: null);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("not available");
    }

    [Fact]
    public async Task VerifyL402Payment_ApiKeyNotConfigured_ReturnsError()
    {
        _apiServiceMock.Setup(s => s.IsConfigured).Returns(false);

        var result = await VerifyL402PaymentTool.VerifyL402Payment(
            macaroon: "dGVzdA==",
            preimage: "abc123",
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("API key not configured");
    }

    #endregion

    #region Verification Tests

    [Fact]
    public async Task VerifyL402Payment_ValidToken_ReturnsValid()
    {
        _apiServiceMock.Setup(s => s.VerifyTokenAsync(
                "dGVzdG1hY2Fyb29u", "abc123def456", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VerifyTokenResult
            {
                Success = true,
                Valid = true,
                Resource = "https://api.example.com/data"
            });

        var result = await VerifyL402PaymentTool.VerifyL402Payment(
            macaroon: "dGVzdG1hY2Fyb29u",
            preimage: "abc123def456",
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("valid").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("resource").GetString().Should().Be("https://api.example.com/data");
        json.RootElement.GetProperty("message").GetString().Should().Contain("grant access");
    }

    [Fact]
    public async Task VerifyL402Payment_InvalidToken_ReturnsInvalid()
    {
        _apiServiceMock.Setup(s => s.VerifyTokenAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VerifyTokenResult
            {
                Success = true,
                Valid = false
            });

        var result = await VerifyL402PaymentTool.VerifyL402Payment(
            macaroon: "invalidmac",
            preimage: "invalidpreimage",
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("valid").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("message").GetString().Should().Contain("NOT grant access");
    }

    [Fact]
    public async Task VerifyL402Payment_TrimsWhitespace_FromInputs()
    {
        _apiServiceMock.Setup(s => s.VerifyTokenAsync(
                "dGVzdA==", "abc123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VerifyTokenResult
            {
                Success = true,
                Valid = true
            });

        await VerifyL402PaymentTool.VerifyL402Payment(
            macaroon: "  dGVzdA==  ",
            preimage: "  abc123  ",
            apiService: _apiServiceMock.Object);

        _apiServiceMock.Verify(s => s.VerifyTokenAsync(
            "dGVzdA==", "abc123", It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region API Error Tests

    [Fact]
    public async Task VerifyL402Payment_ApiReturnsError_ReturnsError()
    {
        _apiServiceMock.Setup(s => s.VerifyTokenAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VerifyTokenResult
            {
                Success = false,
                ErrorMessage = "Invalid macaroon format"
            });

        var result = await VerifyL402PaymentTool.VerifyL402Payment(
            macaroon: "bad-macaroon",
            preimage: "abc123",
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Invalid macaroon format");
    }

    [Fact]
    public async Task VerifyL402Payment_ApiThrowsException_ReturnsError()
    {
        _apiServiceMock.Setup(s => s.VerifyTokenAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection refused"));

        var result = await VerifyL402PaymentTool.VerifyL402Payment(
            macaroon: "dGVzdA==",
            preimage: "abc123",
            apiService: _apiServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Connection refused");
    }

    #endregion

    #region JSON Response Tests

    [Fact]
    public async Task VerifyL402Payment_AllResponses_ReturnValidJson()
    {
        // Error response
        var errorResult = await VerifyL402PaymentTool.VerifyL402Payment(
            macaroon: "",
            preimage: "abc123",
            apiService: _apiServiceMock.Object);

        var act = () => JsonDocument.Parse(errorResult);
        act.Should().NotThrow();

        // Valid response
        _apiServiceMock.Setup(s => s.VerifyTokenAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VerifyTokenResult { Success = true, Valid = true });

        var validResult = await VerifyL402PaymentTool.VerifyL402Payment(
            macaroon: "mac",
            preimage: "pre",
            apiService: _apiServiceMock.Object);

        act = () => JsonDocument.Parse(validResult);
        act.Should().NotThrow();

        // Invalid response
        _apiServiceMock.Setup(s => s.VerifyTokenAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VerifyTokenResult { Success = true, Valid = false });

        var invalidResult = await VerifyL402PaymentTool.VerifyL402Payment(
            macaroon: "mac",
            preimage: "pre",
            apiService: _apiServiceMock.Object);

        act = () => JsonDocument.Parse(invalidResult);
        act.Should().NotThrow();
    }

    #endregion
}
