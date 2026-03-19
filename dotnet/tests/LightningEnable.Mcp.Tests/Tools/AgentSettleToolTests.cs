using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Tools;

public class AgentSettleToolTests
{
    private readonly Mock<IL402HttpClient> _l402ClientMock;
    private readonly Mock<IBudgetService> _budgetServiceMock;
    private readonly Mock<IPaymentHistoryService> _paymentHistoryMock;

    private const string TestEndpoint = "https://api.example.com/l402/translate";

    public AgentSettleToolTests()
    {
        _l402ClientMock = new Mock<IL402HttpClient>();
        _budgetServiceMock = new Mock<IBudgetService>();
        _paymentHistoryMock = new Mock<IPaymentHistoryService>();
    }

    [Fact]
    public async Task SettleAgentService_ValidL402_ReturnsSuccess()
    {
        // Arrange
        _budgetServiceMock.Setup(b => b.CheckBudget(1000))
            .Returns(BudgetCheckResult.Allow(8000, 1000));

        _l402ClientMock.Setup(c => c.FetchWithL402Async(
                TestEndpoint, "GET", null, null, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(L402FetchResult.Succeeded(
                TestEndpoint,
                "{\"translation\": \"Hola mundo\"}",
                200,
                "application/json",
                paidAmountSats: 100,
                l402Token: "macaroon123:preimage456"));

        // Act
        var result = await AgentSettleTool.SettleAgentService(
            l402Endpoint: TestEndpoint,
            l402Client: _l402ClientMock.Object,
            budgetService: _budgetServiceMock.Object,
            paymentHistoryService: _paymentHistoryMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("settlement").GetProperty("paid").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("settlement").GetProperty("amountSats").GetInt64().Should().Be(100);
        json.RootElement.GetProperty("response").GetProperty("statusCode").GetInt32().Should().Be(200);
        json.RootElement.GetProperty("response").GetProperty("content").GetString().Should().Contain("Hola mundo");

        // Verify budget was recorded
        _budgetServiceMock.Verify(b => b.RecordSpend(100), Times.Once);
        _budgetServiceMock.Verify(b => b.RecordPaymentTime(), Times.Once);
    }

    [Fact]
    public async Task SettleAgentService_MissingEndpoint_ReturnsError()
    {
        // Act
        var result = await AgentSettleTool.SettleAgentService(
            l402Endpoint: "",
            l402Client: _l402ClientMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("L402 endpoint URL is required");
    }

    [Fact]
    public async Task SettleAgentService_BudgetExceeded_ReturnsError()
    {
        // Arrange
        _budgetServiceMock.Setup(b => b.CheckBudget(1000))
            .Returns(BudgetCheckResult.Deny("Would exceed session budget", 500, 1000));

        // Act
        var result = await AgentSettleTool.SettleAgentService(
            l402Endpoint: TestEndpoint,
            l402Client: _l402ClientMock.Object,
            budgetService: _budgetServiceMock.Object,
            paymentHistoryService: _paymentHistoryMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Budget limit exceeded");
        json.RootElement.TryGetProperty("details", out var details).Should().BeTrue();
        details.GetProperty("remainingSats").GetInt64().Should().Be(500);

        // Verify failed payment was recorded
        _paymentHistoryMock.Verify(h => h.RecordFailedPayment(
            TestEndpoint,
            "ASA-Settlement",
            1000,
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Once);
    }
}
