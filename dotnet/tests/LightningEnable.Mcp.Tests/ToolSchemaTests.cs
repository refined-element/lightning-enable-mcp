using System.Text;
using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Tools;
using ModelContextProtocol.Client;

namespace LightningEnable.Mcp.Tests;

/// <summary>
/// Guards on what the advertised tool schemas cost and claim.
///
/// Every advertised schema is loaded into the agent's context at the start of each session,
/// so schema bytes are a per-turn tax; and MCP directory review requires a human-readable
/// title plus an explicit read-only hint on every listed tool.
/// </summary>
public class ToolSchemaTests
{
    // ── Schema-size baseline ─────────────────────────────────────────────────
    // Bytes of the pre-consolidation 26-tool list, measured as the compact JSON of
    // [{name, description, inputSchema}, ...] at commit 10d6c2f (the last commit before
    // this change), through the same McpToolHost harness. That is the schema payload a
    // client receives on tools/list — the thing that lands in the model's context.
    //
    // `annotations` are excluded on BOTH sides of the comparison: the baseline predates
    // them (no tool carried any), so including them would compare a schema against a
    // schema-plus-metadata and understate the reduction. Their own cost is asserted below.
    private const int PreConsolidationSchemaBytes = 17_864;

    /// <summary>
    /// The consolidated surface must be well under the old one, not marginally under.
    ///
    /// <para>Raised 0.60 → 0.65 when <c>setup_wallet</c> was added. That tool is not
    /// sprawl — nothing else in the surface works without a wallet, and an agent had no way
    /// to discover or fix that — but it is ~300 bytes the 0.60 budget did not allow for.
    /// The consolidation still delivers a ~39% reduction, and the guard keeps roughly 750
    /// bytes of headroom, so it fails again on the next couple of verbose additions.</para>
    /// </summary>
    private const double MaxStandardFraction = 0.65;

    private static readonly IReadOnlySet<string> MoneyMovingTools = new HashSet<string>
    {
        "pay_invoice", "access_l402_resource", "pay_l402_challenge", "test_l402_payment",
        "create_lightning_enable_account", "wallet_ops", "agent_services",
    };

    private static readonly IReadOnlySet<string> PureReadTools = new HashSet<string>
    {
        "get_balance", "receipts", "discover_api", "check_invoice_status",
        "verify_confirmation_code",
    };

    // Parameter names the tools take by dependency injection. None may ever surface as a
    // tool ARGUMENT: it would be noise the model has to read past, and the config service
    // in particular expands into the shape of the wallet-credential file.
    private static readonly IReadOnlySet<string> InjectedServiceParameters = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "walletService", "budgetService", "priceService", "configService", "paymentHistory",
        "paymentHistoryService", "historyService", "receiptService", "l402Client",
        "apiService", "agentService", "rateLimiter", "operationLedger", "httpClientFactory",
        "onboarding",
        "server", "cancellationToken",
    };

    private static int SchemaBytes(IEnumerable<McpClientTool> tools)
    {
        var payload = tools.Select(t => new
        {
            name = t.Name,
            description = t.Description ?? string.Empty,
            inputSchema = t.JsonSchema,
        });
        return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload));
    }

    private static int AnnotationBytes(IEnumerable<McpClientTool> tools)
        => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(tools.Select(t => t.ProtocolTool.Annotations)));

    [Fact]
    public async Task StandardProfileIsWellUnderTheOld26ToolSurface()
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var actual = SchemaBytes(await host.AdvertisedToolsAsync());
        var budget = (int)(PreConsolidationSchemaBytes * MaxStandardFraction);

        actual.Should().BeLessThan(budget,
            $"the standard-profile schema is {actual} bytes "
            + $"({100.0 * actual / PreConsolidationSchemaBytes:F1}% of the pre-consolidation "
            + $"{PreConsolidationSchemaBytes}); the budget is {budget} ({MaxStandardFraction:P0}). "
            + "Adding a tool or a verbose description costs every session's context - trim, "
            + "or consolidate.");
    }

    [Fact]
    public async Task LiteProfileIsFarSmallerStill()
    {
        await using var lite = await McpToolHost.StartAsync(ToolProfile.Lite);
        await using var standard = await McpToolHost.StartAsync(ToolProfile.Standard);

        SchemaBytes(await lite.AdvertisedToolsAsync())
            .Should().BeLessThan(SchemaBytes(await standard.AdvertisedToolsAsync()) / 3);
    }

    [Fact]
    public async Task FullProfileIsNotSmallerThanStandard()
    {
        // `full` is an escape hatch for old prompts, not a recommendation.
        await using var full = await McpToolHost.StartAsync(ToolProfile.Full);
        await using var standard = await McpToolHost.StartAsync(ToolProfile.Standard);

        SchemaBytes(await full.AdvertisedToolsAsync())
            .Should().BeGreaterThan(SchemaBytes(await standard.AdvertisedToolsAsync()));
    }

    [Fact]
    public async Task AnnotationsStayASmallFractionOfThePayload()
    {
        // Annotations earn their bytes, but must not undo the consolidation.
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var tools = await host.AdvertisedToolsAsync();
        var overhead = AnnotationBytes(tools);

        overhead.Should().BeGreaterThan(0).And.BeLessThan((int)(SchemaBytes(tools) * 0.20));
    }

    [Theory]
    [InlineData(ToolProfile.Lite)]
    [InlineData(ToolProfile.Standard)]
    [InlineData(ToolProfile.Full)]
    public async Task EveryListedToolHasATitleAndAnExplicitReadOnlyHint(ToolProfile profile)
    {
        await using var host = await McpToolHost.StartAsync(profile);

        foreach (var tool in await host.AdvertisedToolsAsync())
        {
            var annotations = tool.ProtocolTool.Annotations;
            annotations.Should().NotBeNull($"'{tool.Name}' must carry MCP tool annotations");
            annotations!.Title.Should().NotBeNullOrWhiteSpace($"'{tool.Name}' must have a human-readable title");
            annotations.ReadOnlyHint.Should().NotBeNull(
                $"'{tool.Name}' must declare readOnlyHint explicitly, so a client never has to guess "
                + "whether calling it changes anything");
        }
    }

    [Fact]
    public async Task MoneyMovingToolsAreDestructiveAndNotReadOnly()
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var byName = (await host.AdvertisedToolsAsync()).ToDictionary(t => t.Name);

        foreach (var name in MoneyMovingTools)
        {
            var annotations = byName[name].ProtocolTool.Annotations!;
            annotations.ReadOnlyHint.Should().BeFalse(name);
            annotations.DestructiveHint.Should().BeTrue($"'{name}' can spend the wallet");
        }
    }

    [Fact]
    public async Task PureReadsAreReadOnly()
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var byName = (await host.AdvertisedToolsAsync()).ToDictionary(t => t.Name);

        foreach (var name in PureReadTools)
        {
            byName[name].ProtocolTool.Annotations!.ReadOnlyHint.Should().BeTrue(name);
        }
    }

    [Fact]
    public async Task BudgetIsIdempotentAndNotReadOnly()
    {
        // `budget` mixes a read (status) with a write (tighten). Annotations are per tool,
        // so the widest action decides: not read-only, but not destructive either.
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var annotations = (await host.AdvertisedToolsAsync()).Single(t => t.Name == "budget")
            .ProtocolTool.Annotations!;

        annotations.ReadOnlyHint.Should().BeFalse();
        annotations.DestructiveHint.Should().BeFalse();
        annotations.IdempotentHint.Should().BeTrue("tightening to the same caps twice is the same state");
    }

    [Fact]
    public async Task EveryStandardToolIsClassifiedAsAReadOrAWrite()
    {
        // No tool may fall outside both lists: an unclassified tool is one nobody decided
        // the safety posture of.
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var names = (await host.AdvertisedToolsAsync()).Select(t => t.Name).ToHashSet();

        // budget, create_invoice, l402_producer and setup_wallet are writes that are not
        // destructive: none of them can spend the wallet.
        var classifiedElsewhere = new[]
        {
            "budget", "create_invoice", "l402_producer", "setup_wallet",
        };
        names.Except(MoneyMovingTools).Except(PureReadTools).Except(classifiedElsewhere)
            .Should().BeEmpty("every advertised tool needs a deliberate read/write classification");
    }

    [Fact]
    public async Task DeprecatedNamesAreTitledAsDeprecated()
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Full);
        var byName = (await host.AdvertisedToolsAsync()).ToDictionary(t => t.Name);

        foreach (var name in ToolProfiles.LegacyToolNames)
        {
            byName[name].ProtocolTool.Annotations!.Title!.ToLowerInvariant()
                .Should().Contain("deprecated", $"'{name}' is only advertised for compatibility");
        }
    }

    [Theory]
    [InlineData(ToolProfile.Standard)]
    [InlineData(ToolProfile.Full)]
    public async Task NoAdvertisedTool_ExposesAnInjectedService(ToolProfile profile)
    {
        // The SDK drops a parameter from the schema only when its type is registered in DI.
        // If a tool starts injecting something the composition root does not register, the
        // service leaks into the tool's arguments — bytes the model must read past, and for
        // the config service, the shape of the wallet-credential file.
        await using var host = await McpToolHost.StartAsync(profile);

        foreach (var tool in await host.AdvertisedToolsAsync())
        {
            if (!tool.JsonSchema.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            foreach (var property in properties.EnumerateObject())
            {
                InjectedServiceParameters.Should().NotContain(property.Name,
                    $"'{tool.Name}' exposes the injected service '{property.Name}' as a tool argument - "
                    + "register its type in the composition root (and in McpToolHost)");
            }
        }
    }
}
