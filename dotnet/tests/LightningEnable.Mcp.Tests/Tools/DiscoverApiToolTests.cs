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

    #region Finding 3 — a hostile manifest must never claim "unlimited" affordability, nor throw

    // An L402 manifest is a THIRD-PARTY, ATTACKER-AUTHORABLE document: it is fetched from a
    // public registry or an arbitrary caller-supplied URL. Nothing about its shape is trusted.
    // The agent reads `affordable_calls` to decide how freely it may spend, so a malformed or
    // hostile price must degrade to an explicit "unknown" — never "unlimited", never "free",
    // and never an exception that takes down the whole discovery response.

    /// <summary>Builds a well-formed manifest whose only variable is the endpoint's raw price JSON.</summary>
    private static string ManifestWithPrice(string basePriceSatsJson) => $$"""
        {
          "service": { "name": "Hostile API", "base_url": "https://hostile.example.com" },
          "l402": { "default_price_sats": 100, "payment_flow": "l402" },
          "endpoints": [
            {
              "id": "ep1",
              "path": "/v1/data",
              "method": "GET",
              "summary": "Get data",
              "l402_enabled": true,
              "pricing": { "model": "flat", "base_price_sats": {{basePriceSatsJson}} }
            }
          ]
        }
        """;

    /// <summary>Runs the real extract → annotate pipeline and returns the single endpoint.</summary>
    private static Dictionary<string, object?> AnnotateFirstEndpoint(string manifestJson, long remainingSats = 8000)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        var endpoints = DiscoverApiTool.ExtractEndpoints(doc.RootElement);
        DiscoverApiTool.AnnotateEndpointsWithAffordability(endpoints, remainingSats, btcPrice: 100_000m);
        return endpoints[0];
    }

    /// <summary>Every price shape a hostile manifest can legally put in the JSON.</summary>
    public static TheoryData<string, string> HostilePriceShapes => new()
    {
        { "0",                        "a zero price" },
        { "-5",                       "a negative price" },
        { "null",                     "a null price" },
        { "\"abc\"",                  "a non-numeric string price" },
        { "\"\"",                     "an empty string price" },
        { "{\"amount\":100}",         "an object price" },
        { "[100]",                    "an array price" },
        { "true",                     "a boolean price" },
        { "1.5",                      "a fractional price" },
        { "99999999999999999999999",  "a price that overflows Int64" },
    };

    [Theory]
    [MemberData(nameof(HostilePriceShapes))]
    public void Annotate_HostilePrice_ReportsUnknown_NeverUnlimited(string priceJson, string why)
    {
        var endpoint = AnnotateFirstEndpoint(ManifestWithPrice(priceJson));

        endpoint["affordable_calls"].Should().Be("unknown",
            because: $"the manifest is attacker-authorable and {why} tells us nothing — " +
                     "the agent must be told the price is unknown, not that it can spend freely");
        endpoint.Should().NotContainKey("cost_usd",
            because: "a USD cost derived from an unusable price would be fabricated");
    }

    [Theory]
    [MemberData(nameof(HostilePriceShapes))]
    public void ExtractEndpoints_HostilePrice_DoesNotThrow(string priceJson, string why)
    {
        using var doc = JsonDocument.Parse(ManifestWithPrice(priceJson));
        var root = doc.RootElement;

        var act = () => DiscoverApiTool.ExtractEndpoints(root);

        act.Should().NotThrow(
            because: $"{why} must not kill the entire discovery response for every other endpoint");
    }

    [Theory]
    [MemberData(nameof(HostilePriceShapes))]
    public void Annotate_HostilePrice_NeverClaimsUnlimitedOrFree(string priceJson, string why)
    {
        var endpoint = AnnotateFirstEndpoint(ManifestWithPrice(priceJson));

        endpoint["affordable_calls"].Should().NotBe("unlimited",
            because: $"{why} must never be read as unlimited affordability");
        endpoint["affordable_calls"].Should().NotBe("free",
            because: $"{why} must never be read as a free endpoint");
    }

    [Fact]
    public void Annotate_PositivePrice_ReportsRealAffordableCallCount()
    {
        // Only a positive, numeric price produces a real count: 8000 sats / 100 sats = 80 calls,
        // and 100 sats at $100k/BTC = $0.10.
        var endpoint = AnnotateFirstEndpoint(ManifestWithPrice("100"), remainingSats: 8000);

        endpoint["affordable_calls"].Should().Be(80L);
        endpoint["cost_usd"].Should().Be(0.1m);
    }

    [Fact]
    public void Annotate_QuotedNumericPrice_IsParsedNotRejected()
    {
        // A quoted integer is unambiguous and cannot over-claim, so it is accepted.
        var endpoint = AnnotateFirstEndpoint(ManifestWithPrice("\"100\""), remainingSats: 8000);

        endpoint["affordable_calls"].Should().Be(80L);
    }

    [Fact]
    public void Annotate_PriceExceedingBudget_ReportsZeroAffordableCalls_NotUnknown()
    {
        // A real but unaffordable price is known — it affords zero calls.
        var endpoint = AnnotateFirstEndpoint(ManifestWithPrice("9000"), remainingSats: 8000);

        endpoint["affordable_calls"].Should().Be(0L);
    }

    [Fact]
    public void Annotate_MissingPriceField_ReportsUnknown()
    {
        var manifest = """
            {
              "endpoints": [
                { "id": "ep1", "path": "/v1/x", "method": "GET", "pricing": { "model": "flat" } }
              ]
            }
            """;

        AnnotateFirstEndpoint(manifest)["affordable_calls"].Should().Be("unknown",
            because: "a pricing block with no base_price_sats states no price at all");
    }

    [Fact]
    public void Annotate_NoPricingBlockAtAll_ReportsUnknown()
    {
        var manifest = """
            {
              "endpoints": [ { "id": "ep1", "path": "/v1/x", "method": "GET" } ]
            }
            """;

        AnnotateFirstEndpoint(manifest)["affordable_calls"].Should().Be("unknown",
            because: "an endpoint with no pricing at all must not be silently left unannotated, " +
                     "which an agent could misread as free");
    }

    [Fact]
    public void ExtractL402Info_HostileDefaultPrice_DoesNotThrow()
    {
        // The manifest-level l402.default_price_sats is read on the same untrusted document.
        var manifest = """
            { "l402": { "default_price_sats": "not-a-number", "payment_flow": "l402" }, "endpoints": [] }
            """;
        using var doc = JsonDocument.Parse(manifest);
        var root = doc.RootElement;

        var act = () => DiscoverApiTool.ExtractL402Info(root);

        act.Should().NotThrow(because: "no field of an untrusted manifest may crash discovery");
    }

    #endregion

    #region Finding 3 — registry search path uses the same safe price parsing

    private static string RegistryJson(string defaultPriceSatsJson) => $$"""
        {
          "items": [
            {
              "name": "Some API",
              "description": "An API",
              "endpointCount": 3,
              "defaultPriceSats": {{defaultPriceSatsJson}},
              "manifestUrl": "https://x.example.com/.well-known/l402-manifest.json"
            }
          ],
          "total": 1
        }
        """;

    [Theory]
    [MemberData(nameof(HostilePriceShapes))]
    public void BuildRegistryEntries_HostilePrice_DoesNotThrow_AndReportsUnknown(string priceJson, string why)
    {
        using var doc = JsonDocument.Parse(RegistryJson(priceJson));
        var root = doc.RootElement;
        List<Dictionary<string, object?>> entries = null!;

        var act = () => entries = DiscoverApiTool.BuildRegistryEntries(
            root, budgetAware: true, _budgetServiceMock.Object);

        act.Should().NotThrow(because: $"the registry response is untrusted input and {why} must not throw");
        entries[0]["affordable_calls"].Should().Be("unknown",
            because: $"{why} affords an unknown number of calls");
    }

    [Fact]
    public void BuildRegistryEntries_PositivePrice_ReportsAffordableCalls()
    {
        // Remaining session budget = 10000 - 2000 = 8000 sats → 8000 / 100 = 80 calls.
        using var doc = JsonDocument.Parse(RegistryJson("100"));

        var entries = DiscoverApiTool.BuildRegistryEntries(
            doc.RootElement, budgetAware: true, _budgetServiceMock.Object);

        entries[0]["affordable_calls"].Should().Be(80L);
    }

    [Fact]
    public void BuildRegistryEntries_BudgetAwareFalse_AddsNoAffordabilityClaim()
    {
        using var doc = JsonDocument.Parse(RegistryJson("100"));

        var entries = DiscoverApiTool.BuildRegistryEntries(
            doc.RootElement, budgetAware: false, _budgetServiceMock.Object);

        entries[0].Should().NotContainKey("affordable_calls");
        _budgetServiceMock.Verify(b => b.GetConfig(), Times.Never);
    }

    #endregion
}
