using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Tools;

public class AgentDiscoveryToolTests
{
    private readonly Mock<IAgentService> _agentServiceMock;
    private readonly Mock<IBudgetService> _budgetServiceMock;

    public AgentDiscoveryToolTests()
    {
        _agentServiceMock = new Mock<IAgentService>();
        _budgetServiceMock = new Mock<IBudgetService>();

        _budgetServiceMock.Setup(b => b.GetConfig()).Returns(new BudgetConfig
        {
            MaxSatsPerSession = 10000,
            SessionSpent = 2000
        });
    }

    [Fact]
    public async Task DiscoverAgentServices_WithCategory_ReturnsResults()
    {
        // Arrange
        var capabilities = new List<AgentCapability>
        {
            new()
            {
                EventId = "event123",
                ServiceId = "translate-svc",
                Content = "AI translation service",
                Categories = new List<string> { "ai", "translation" },
                PriceSats = 100,
                L402Endpoint = "https://api.example.com/translate"
            },
            new()
            {
                EventId = "event456",
                ServiceId = "summarize-svc",
                Content = "Text summarization",
                Categories = new List<string> { "ai" },
                PriceSats = 50
            }
        };

        _agentServiceMock.Setup(a => a.DiscoverCapabilitiesAsync(
                "ai", It.IsAny<string[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentDiscoveryResult
            {
                Success = true,
                Capabilities = capabilities,
                Total = 2
            });

        // Act
        var result = await AgentDiscoveryTool.DiscoverAgentServices(
            category: "ai",
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("total").GetInt32().Should().Be(2);
        json.RootElement.GetProperty("results").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task DiscoverAgentServices_NoParams_ReturnsUsageError()
    {
        // Act
        var result = await AgentDiscoveryTool.DiscoverAgentServices(
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("category");
        json.RootElement.TryGetProperty("examples", out var examples).Should().BeTrue();
        examples.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DiscoverAgentServices_WithBudget_AnnotatesAffordability()
    {
        // Arrange
        var capabilities = new List<AgentCapability>
        {
            new()
            {
                EventId = "event123",
                ServiceId = "expensive-svc",
                Content = "Expensive service",
                Categories = new List<string> { "ai" },
                PriceSats = 500
            }
        };

        _agentServiceMock.Setup(a => a.DiscoverCapabilitiesAsync(
                It.IsAny<string?>(), It.IsAny<string[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentDiscoveryResult
            {
                Success = true,
                Capabilities = capabilities,
                Total = 1
            });

        // Act
        var result = await AgentDiscoveryTool.DiscoverAgentServices(
            category: "ai",
            agentService: _agentServiceMock.Object,
            budgetService: _budgetServiceMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.TryGetProperty("budget", out var budget).Should().BeTrue();
        budget.GetProperty("remaining_sats").GetInt64().Should().Be(8000);

        // Check affordable_calls annotation on the result
        var firstResult = json.RootElement.GetProperty("results")[0];
        firstResult.TryGetProperty("affordable_calls", out _).Should().BeTrue();
    }

    [Fact]
    public async Task DiscoverAgentServices_ServiceUnavailable_ReturnsError()
    {
        // Arrange
        _agentServiceMock.Setup(a => a.DiscoverCapabilitiesAsync(
                It.IsAny<string?>(), It.IsAny<string[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentDiscoveryResult
            {
                Success = false,
                ErrorMessage = "Discovery failed: Connection refused"
            });

        // Act
        var result = await AgentDiscoveryTool.DiscoverAgentServices(
            category: "ai",
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Connection refused");
        json.RootElement.TryGetProperty("hint", out _).Should().BeTrue();
    }
}
