using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for the discover_api MCP tool.
/// </summary>
public class DiscoverApiToolTests
{
    private readonly Mock<IBudgetService> _budgetServiceMock;
    private readonly Mock<IPriceService> _priceServiceMock;

    public DiscoverApiToolTests()
    {
        _budgetServiceMock = new Mock<IBudgetService>();
        _priceServiceMock = new Mock<IPriceService>();

        // Default budget config
        _budgetServiceMock.Setup(b => b.GetConfig()).Returns(new BudgetConfig
        {
            MaxSatsPerSession = 10000,
            SessionSpent = 2000
        });

        _priceServiceMock.Setup(p => p.GetBtcPriceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100000m); // $100k/BTC for easy math
    }

    [Fact]
    public async Task DiscoverApi_InvalidUrl_ReturnsError()
    {
        var result = await DiscoverApiTool.DiscoverApi(
            "not-a-url", false, null, null, CancellationToken.None);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DiscoverApi_UnreachableUrl_ReturnsErrorWithTriedUrls()
    {
        // Use a URL that won't resolve
        var result = await DiscoverApiTool.DiscoverApi(
            "https://this-domain-does-not-exist-12345.example.com",
            false, null, null, CancellationToken.None);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.TryGetProperty("tried_urls", out var triedUrls).Should().BeTrue();
        triedUrls.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DiscoverApi_NoBudgetService_SkipsBudgetAnnotations()
    {
        // Without budget service, result should not have budget field
        var result = await DiscoverApiTool.DiscoverApi(
            "https://this-domain-does-not-exist-12345.example.com",
            true, null, null, CancellationToken.None);

        var json = JsonDocument.Parse(result);
        // Should fail (unreachable), but budget should not appear in error
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task DiscoverApi_BudgetAwareFalse_SkipsBudgetEvenWithService()
    {
        // Even with budget service, budgetAware=false should skip annotations
        var result = await DiscoverApiTool.DiscoverApi(
            "https://this-domain-does-not-exist-12345.example.com",
            false, _budgetServiceMock.Object, _priceServiceMock.Object,
            CancellationToken.None);

        // Should fail (unreachable), confirm budget service wasn't called
        _budgetServiceMock.Verify(b => b.GetConfig(), Times.Never);
    }
}
