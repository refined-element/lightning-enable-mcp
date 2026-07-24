using System.Text.Json;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Tools;

public class AgentUnpublishToolTests
{
    private const string ValidPubkey = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2";

    private readonly Mock<IAgentService> _agentServiceMock;

    public AgentUnpublishToolTests()
    {
        _agentServiceMock = new Mock<IAgentService>();
        _agentServiceMock.Setup(a => a.IsConfigured).Returns(true);
    }

    [Fact]
    public async Task UnpublishAgentCapability_Remove_ReturnsSuccessAndRetired()
    {
        _agentServiceMock.Setup(a => a.UnpublishCapabilityAsync(
                ValidPubkey, "my-svc", "remove", "done", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentUnpublishResult
            {
                Success = true,
                ServiceId = "my-svc",
                ProxyId = "agent-my-svc-ab12",
                Mode = "remove",
                Retired = true
            });

        var result = await AgentUnpublishTool.UnpublishAgentCapability(
            pubkey: ValidPubkey,
            serviceId: "my-svc",
            reason: "done",
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("retired").GetBoolean().Should().BeTrue();
        json.GetProperty("proxyId").GetString().Should().Be("agent-my-svc-ab12");
        _agentServiceMock.Verify(a => a.UnpublishCapabilityAsync(
            ValidPubkey, "my-svc", "remove", "done", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnpublishAgentCapability_InvalidPubkey_ReturnsError()
    {
        var result = await AgentUnpublishTool.UnpublishAgentCapability(
            pubkey: "not-hex",
            serviceId: "my-svc",
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("pubkey");
    }

    [Fact]
    public async Task UnpublishAgentCapability_MissingServiceId_ReturnsError()
    {
        var result = await AgentUnpublishTool.UnpublishAgentCapability(
            pubkey: ValidPubkey,
            serviceId: "",
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("Service ID");
    }

    [Fact]
    public async Task UnpublishAgentCapability_InvalidMode_ReturnsError()
    {
        var result = await AgentUnpublishTool.UnpublishAgentCapability(
            pubkey: ValidPubkey,
            serviceId: "my-svc",
            mode: "delete",
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("Mode must be");
    }

    [Fact]
    public async Task UnpublishAgentCapability_NotConfigured_ReturnsError()
    {
        _agentServiceMock.Setup(a => a.IsConfigured).Returns(false);

        var result = await AgentUnpublishTool.UnpublishAgentCapability(
            pubkey: ValidPubkey,
            serviceId: "my-svc",
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("API key not configured");
    }

    [Fact]
    public async Task UnpublishAgentCapability_ServiceError_IsSurfaced()
    {
        _agentServiceMock.Setup(a => a.UnpublishCapabilityAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentUnpublishResult
            {
                Success = false,
                ErrorMessage = "Capability not found for this agent"
            });

        var result = await AgentUnpublishTool.UnpublishAgentCapability(
            pubkey: ValidPubkey,
            serviceId: "missing",
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("not found");
    }
}
