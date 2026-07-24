using System.Text.Json;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Tools;

public class AgentUnpublishToolTests
{
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
                "my-svc", "done", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentUnpublishResult
            {
                Success = true,
                ProxyId = "my-svc",
                Retired = true
            });

        var result = await AgentUnpublishTool.UnpublishAgentCapability(
            serviceId: "my-svc",
            reason: "done",
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("retired").GetBoolean().Should().BeTrue();
        json.GetProperty("proxyId").GetString().Should().Be("my-svc");
        _agentServiceMock.Verify(a => a.UnpublishCapabilityAsync(
            "my-svc", "done", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnpublishAgentCapability_AlreadyRetired_ReportsIt()
    {
        _agentServiceMock.Setup(a => a.UnpublishCapabilityAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentUnpublishResult
            {
                Success = true, ProxyId = "my-svc", Retired = true, AlreadyRetired = true
            });

        var result = await AgentUnpublishTool.UnpublishAgentCapability(
            serviceId: "my-svc", agentService: _agentServiceMock.Object, cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("alreadyRetired").GetBoolean().Should().BeTrue();
        json.GetProperty("message").GetString().Should().Contain("already retired");
    }

    [Fact]
    public async Task UnpublishAgentCapability_MissingServiceId_ReturnsError()
    {
        var result = await AgentUnpublishTool.UnpublishAgentCapability(
            serviceId: "",
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("Service ID");
    }

    [Fact]
    public async Task UnpublishAgentCapability_NotConfigured_ReturnsError()
    {
        _agentServiceMock.Setup(a => a.IsConfigured).Returns(false);

        var result = await AgentUnpublishTool.UnpublishAgentCapability(
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
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentUnpublishResult
            {
                Success = false,
                ErrorMessage = "Proxy not found"
            });

        var result = await AgentUnpublishTool.UnpublishAgentCapability(
            serviceId: "missing",
            agentService: _agentServiceMock.Object,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("not found");
    }
}
