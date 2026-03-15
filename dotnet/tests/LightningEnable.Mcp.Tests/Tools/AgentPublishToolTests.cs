using System.Text.Json;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Tools;

public class AgentPublishToolTests
{
    private readonly Mock<IAgentService> _agentServiceMock;
    private readonly Mock<ILightningEnableApiService> _apiServiceMock;

    public AgentPublishToolTests()
    {
        _agentServiceMock = new Mock<IAgentService>();
        _apiServiceMock = new Mock<ILightningEnableApiService>();
        _agentServiceMock.Setup(a => a.IsConfigured).Returns(true);
    }

    [Fact]
    public async Task PublishAgentCapability_ValidInput_ReturnsSuccess()
    {
        // Arrange
        _agentServiceMock.Setup(a => a.PublishCapabilityAsync(
                "my-translate-svc",
                It.IsAny<string[]>(),
                It.IsAny<string>(),
                100,
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentPublishResult
            {
                Success = true,
                EventId = "event789",
                L402Endpoint = "https://api.example.com/l402/translate"
            });

        // Act
        var result = await AgentPublishTool.PublishAgentCapability(
            serviceId: "my-translate-svc",
            categories: new[] { "ai", "translation" },
            content: "Translates text between languages",
            priceSats: 100,
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("eventId").GetString().Should().Be("event789");
        json.RootElement.GetProperty("serviceId").GetString().Should().Be("my-translate-svc");
        json.RootElement.GetProperty("priceSats").GetInt32().Should().Be(100);
        json.RootElement.GetProperty("l402Endpoint").GetString().Should().Be("https://api.example.com/l402/translate");
        json.RootElement.GetProperty("message").GetString().Should().Contain("published successfully");
    }

    [Fact]
    public async Task PublishAgentCapability_MissingServiceId_ReturnsError()
    {
        // Act
        var result = await AgentPublishTool.PublishAgentCapability(
            serviceId: "",
            categories: new[] { "ai" },
            content: "Some service",
            priceSats: 100,
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Service ID");
    }

    [Fact]
    public async Task PublishAgentCapability_NotConfigured_ReturnsError()
    {
        // Arrange
        _agentServiceMock.Setup(a => a.IsConfigured).Returns(false);

        // Act
        var result = await AgentPublishTool.PublishAgentCapability(
            serviceId: "my-svc",
            categories: new[] { "ai" },
            content: "Some service",
            priceSats: 100,
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("API key not configured");
    }

    [Fact]
    public async Task PublishAgentCapability_EmptyCategories_ReturnsError()
    {
        // Act
        var result = await AgentPublishTool.PublishAgentCapability(
            serviceId: "my-svc",
            categories: Array.Empty<string>(),
            content: "Some service",
            priceSats: 100,
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("category");
    }
}
