using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Tools;

public class AgentNegotiateToolTests
{
    private readonly Mock<IAgentService> _agentServiceMock;
    private readonly Mock<IBudgetService> _budgetServiceMock;

    public AgentNegotiateToolTests()
    {
        _agentServiceMock = new Mock<IAgentService>();
        _budgetServiceMock = new Mock<IBudgetService>();
    }

    [Fact]
    public async Task RequestAgentService_ValidInput_ReturnsSuccess()
    {
        // Arrange
        _agentServiceMock.Setup(a => a.RequestServiceAsync(
                "event123", 500, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRequestResult
            {
                Success = true,
                RequestEventId = "req-event-456",
                L402Endpoint = "https://api.example.com/l402/translate"
            });

        _budgetServiceMock.Setup(b => b.CheckBudget(500))
            .Returns(BudgetCheckResult.Allow(8000, 1000));

        // Act
        var result = await AgentNegotiateTool.RequestAgentService(
            capabilityEventId: "event123",
            budgetSats: 500,
            agentService: _agentServiceMock.Object,
            budgetService: _budgetServiceMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("requestEventId").GetString().Should().Be("req-event-456");
        json.RootElement.GetProperty("capabilityEventId").GetString().Should().Be("event123");
        json.RootElement.GetProperty("budgetSats").GetInt32().Should().Be(500);
        json.RootElement.GetProperty("l402Endpoint").GetString().Should().Be("https://api.example.com/l402/translate");
    }

    [Fact]
    public async Task RequestAgentService_ExceedsBudget_ReturnsError()
    {
        // Arrange
        _budgetServiceMock.Setup(b => b.CheckBudget(5000))
            .Returns(BudgetCheckResult.Deny("Exceeds session limit", 1000, 1000));

        // Act
        var result = await AgentNegotiateTool.RequestAgentService(
            capabilityEventId: "event123",
            budgetSats: 5000,
            agentService: _agentServiceMock.Object,
            budgetService: _budgetServiceMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Budget limit exceeded");
        json.RootElement.TryGetProperty("details", out var details).Should().BeTrue();
        details.GetProperty("requestedSats").GetInt32().Should().Be(5000);
    }

    [Fact]
    public async Task RequestAgentService_MissingCapabilityId_ReturnsError()
    {
        // Act
        var result = await AgentNegotiateTool.RequestAgentService(
            capabilityEventId: "",
            budgetSats: 500,
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Capability event ID");
    }
}
