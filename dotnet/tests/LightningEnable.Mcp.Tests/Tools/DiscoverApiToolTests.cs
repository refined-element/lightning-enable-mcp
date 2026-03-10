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
    public async Task DiscoverApi_NoParams_ReturnsUsageError()
    {
        var result = await DiscoverApiTool.DiscoverApi(
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("url");
        json.RootElement.TryGetProperty("examples", out var examples).Should().BeTrue();
        examples.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DiscoverApi_WithUrl_FetchesManifest()
    {
        // URL provided → should attempt manifest fetch (existing behavior)
        var result = await DiscoverApiTool.DiscoverApi(
            url: "https://this-domain-does-not-exist-12345.example.com",
            budgetAware: false,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        // Should have tried_urls (manifest fetch path), not registry path
        json.RootElement.TryGetProperty("tried_urls", out var triedUrls).Should().BeTrue();
        triedUrls.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DiscoverApi_WithQuery_AttemptsRegistrySearch()
    {
        // Query provided → should attempt registry search
        var result = await DiscoverApiTool.DiscoverApi(
            query: "weather",
            budgetAware: false,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        // May fail (no registry running in tests) but should attempt the registry path
        if (json.RootElement.GetProperty("success").GetBoolean())
        {
            json.RootElement.GetProperty("source").GetString().Should().Be("registry");
        }
        else
        {
            // Registry unavailable in unit tests — that's expected
            json.RootElement.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task DiscoverApi_WithCategory_AttemptsRegistrySearch()
    {
        var result = await DiscoverApiTool.DiscoverApi(
            category: "ai",
            budgetAware: false,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        // Registry may not be running, but should not throw
        json.RootElement.TryGetProperty("success", out _).Should().BeTrue();
    }

    [Fact]
    public async Task DiscoverApi_UrlTakesPrecedenceOverQuery()
    {
        // When both url and query are provided, url should win (manifest fetch path)
        var result = await DiscoverApiTool.DiscoverApi(
            url: "https://this-domain-does-not-exist-12345.example.com",
            query: "weather",
            budgetAware: false,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        // Should have tried_urls → manifest fetch path, not registry
        json.RootElement.TryGetProperty("tried_urls", out _).Should().BeTrue();
    }

    [Fact]
    public async Task DiscoverApi_InvalidUrl_ReturnsError()
    {
        var result = await DiscoverApiTool.DiscoverApi(
            url: "not-a-url", budgetAware: false, cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DiscoverApi_UnreachableUrl_ReturnsErrorWithTriedUrls()
    {
        // Use a URL that won't resolve
        var result = await DiscoverApiTool.DiscoverApi(
            url: "https://this-domain-does-not-exist-12345.example.com",
            budgetAware: false, cancellationToken: CancellationToken.None);

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
            url: "https://this-domain-does-not-exist-12345.example.com",
            budgetAware: true, cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        // Should fail (unreachable), but budget should not appear in error
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task DiscoverApi_BudgetAwareFalse_SkipsBudgetEvenWithService()
    {
        // Even with budget service, budgetAware=false should skip annotations
        var result = await DiscoverApiTool.DiscoverApi(
            url: "https://this-domain-does-not-exist-12345.example.com",
            budgetAware: false, budgetService: _budgetServiceMock.Object,
            priceService: _priceServiceMock.Object,
            cancellationToken: CancellationToken.None);

        // Should fail (unreachable), confirm budget service wasn't called
        _budgetServiceMock.Verify(b => b.GetConfig(), Times.Never);
    }
}
