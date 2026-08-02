using System.Text.Json;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Tools;

public class AgentAttestationToolTests
{
    private readonly Mock<IAgentService> _agentServiceMock;

    public AgentAttestationToolTests()
    {
        _agentServiceMock = new Mock<IAgentService>();
        _agentServiceMock.Setup(a => a.IsConfigured).Returns(true);
    }

    [Fact]
    public async Task PublishAgentAttestation_NotConfigured_ReturnsError()
    {
        // Arrange
        _agentServiceMock.Setup(a => a.IsConfigured).Returns(false);

        // Act
        var result = await AgentAttestationTool.PublishAgentAttestation(
            subjectPubkey: "pk",
            agreementId: "agr",
            rating: 4,
            content: "Great service",
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("API key not configured");
        // GTM upsell: 30-day trial link + in-MCP signup tool hint
        json.RootElement.GetProperty("error").GetString().Should().Contain("https://api.lightningenable.com/Checkout?plan=individual&utm_source=mcp&utm_medium=tool-hint&utm_campaign=gtm-aug-2026");
        json.RootElement.GetProperty("error").GetString().Should().Contain("create_lightning_enable_account");
    }

    [Fact]
    public async Task PublishAgentAttestation_InvalidRating_ReturnsError()
    {
        // Act
        var result = await AgentAttestationTool.PublishAgentAttestation(
            subjectPubkey: "pk",
            agreementId: "agr",
            rating: 6,
            content: "Great service",
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("between 1 and 5");
    }
}
