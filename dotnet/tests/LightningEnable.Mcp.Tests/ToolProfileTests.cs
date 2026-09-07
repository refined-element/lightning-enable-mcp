using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Tools;

namespace LightningEnable.Mcp.Tests;

/// <summary>
/// End-to-end guards for the consolidated tool surface, driven through a real MCP
/// client/server pair (see <see cref="McpToolHost"/>).
///
/// The 2026-09 consolidation folded 16 single-purpose tools into five action-style verbs
/// and added <c>LIGHTNING_ENABLE_TOOL_PROFILE</c>. Three things must stay true:
///
/// <list type="number">
/// <item><description><b>No caller is stranded.</b> Every pre-consolidation tool name still
/// dispatches, and says so with a <c>deprecated</c> marker.</description></item>
/// <item><description><b>The profile picks the advertised surface</b> and nothing else — a
/// narrower profile hides schemas, it never removes capability.</description></item>
/// <item><description><b>The surface actually got smaller</b>, which was the point: every
/// advertised schema is re-sent into the agent's context each session.</description></item>
/// </list>
/// </summary>
public class ToolProfileResolutionTests
{
    [Theory]
    [InlineData(null, ToolProfile.Standard)]
    [InlineData("", ToolProfile.Standard)]
    [InlineData("   ", ToolProfile.Standard)]
    [InlineData("lite", ToolProfile.Lite)]
    [InlineData("LITE", ToolProfile.Lite)]
    [InlineData("  Standard  ", ToolProfile.Standard)]
    [InlineData("full", ToolProfile.Full)]
    public void Resolve_RecognizedValues(string? raw, ToolProfile expected)
        => ToolProfiles.Resolve(raw).Should().Be(expected);

    [Theory]
    [InlineData("minimal")]
    [InlineData("standrd")]
    [InlineData("everything")]
    public void Resolve_UnknownValue_FallsBackToStandard_AndWarns(string raw)
    {
        var warnings = new List<string>();
        ToolProfiles.Resolve(raw, warnings.Add).Should().Be(ToolProfile.Standard,
            "a typo must never silently produce an empty or surprise tool surface");
        warnings.Should().ContainSingle().Which.Should().Contain(ToolProfiles.EnvironmentVariable);
    }

    [Fact]
    public void Resolve_RecognizedValue_DoesNotWarn()
    {
        var warnings = new List<string>();
        ToolProfiles.Resolve("lite", warnings.Add);
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void LiteIsASubsetOfStandard()
        => ToolProfiles.StandardToolNames.Should().Contain(ToolProfiles.LiteToolNames);

    [Fact]
    public void FullIsStandardPlusEveryLegacyName()
    {
        var full = ToolProfiles.AdvertisedNames(ToolProfile.Full);
        full.Should().HaveCount(31);
        full.Should().Contain(ToolProfiles.StandardToolNames);
        full.Should().Contain(ToolProfiles.LegacyToolNames);
    }

    [Fact]
    public void EveryLegacyNameIsADispatchableAlias()
    {
        // `full` may only re-advertise names the dispatcher actually accepts.
        foreach (var name in ToolProfiles.LegacyToolNames)
        {
            DeprecatedAliasDispatcher.IsAlias(name).Should().BeTrue($"'{name}' is re-advertised by the full profile");
        }
    }

    [Fact]
    public void V1AliasesAreNeverAdvertised()
    {
        // These were already unadvertised before profiles existed; `full` is not a reason
        // to start advertising them again.
        var v1 = new[] { "confirm_payment", "check_wallet_balance", "get_all_balances" };
        foreach (var profile in new[] { ToolProfile.Lite, ToolProfile.Standard, ToolProfile.Full })
        {
            ToolProfiles.AdvertisedNames(profile).Should().NotContain(v1);
        }
    }
}

/// <summary>The env var selects what <c>tools/list</c> advertises, over the real protocol.</summary>
public class ToolProfileListingTests
{
    [Theory]
    [InlineData(ToolProfile.Lite, 5)]
    [InlineData(ToolProfile.Standard, 15)]
    [InlineData(ToolProfile.Full, 31)]
    public async Task ProfileSelectsTheAdvertisedToolCount(ToolProfile profile, int expected)
    {
        await using var host = await McpToolHost.StartAsync(profile);
        var names = await host.AdvertisedNamesAsync();
        names.Should().HaveCount(expected);
        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task StandardAdvertisesExactlyTheConsolidatedSet()
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        (await host.AdvertisedNamesAsync()).Should().BeEquivalentTo(ToolProfiles.StandardToolNames);
    }

    [Fact]
    public async Task LiteAdvertisesTheFiveSpendAndStayInBudgetTools()
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Lite);
        (await host.AdvertisedNamesAsync()).Should().BeEquivalentTo(
            "access_l402_resource", "pay_invoice", "get_balance", "budget", "receipts");
    }

    [Fact]
    public async Task FullAlsoAdvertisesThePreConsolidationNames()
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Full);
        var names = await host.AdvertisedNamesAsync();
        names.Should().Contain(ToolProfiles.StandardToolNames);
        names.Should().Contain(ToolProfiles.LegacyToolNames);
    }

    [Fact]
    public async Task NoProfileAdvertisesADeprecatedV1Alias()
    {
        foreach (var profile in new[] { ToolProfile.Lite, ToolProfile.Standard, ToolProfile.Full })
        {
            await using var host = await McpToolHost.StartAsync(profile);
            var names = await host.AdvertisedNamesAsync();
            names.Should().NotContain(new[] { "confirm_payment", "check_wallet_balance", "get_all_balances" });
        }
    }

    [Fact]
    public async Task AHiddenToolIsStillCallable()
    {
        // Profiles are listing-only: `lite` drops check_invoice_status from the schema
        // budget, but an agent that knows the name can still call it.
        await using var host = await McpToolHost.StartAsync(ToolProfile.Lite);
        (await host.AdvertisedNamesAsync()).Should().NotContain("check_invoice_status");

        var text = await host.CallTextAsync("check_invoice_status",
            new Dictionary<string, object?> { ["invoiceId"] = "inv-1" });

        text.Should().NotBeEmpty();
        JsonDocument.Parse(text).RootElement.TryGetProperty("success", out _).Should().BeTrue(
            "a hidden tool must run normally, not fall through to the unknown-tool branch");
        text.Should().NotContain("Unknown tool");
    }

    [Fact]
    public async Task AGenuinelyUnknownToolReportsAnActionableError()
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var text = await host.CallTextAsync("no_such_tool");
        text.Should().Contain("Unknown tool: no_such_tool");
        text.Should().Contain(ToolProfiles.EnvironmentVariable,
            "the error must tell the caller how to see the tools that do exist");
    }
}

/// <summary>Every pre-consolidation name still dispatches, and says it is deprecated.</summary>
public class DeprecatedAliasDispatchTests
{
    /// <summary>
    /// Every deprecated name, with arguments plausible enough to reach the tool body.
    /// Parametrized over the WHOLE alias map (see <see cref="AliasMapIsFullyCovered"/>), so
    /// adding an alias without wiring its dispatch fails here rather than in production.
    /// </summary>
    public static TheoryData<string, Dictionary<string, object?>> AllAliases()
    {
        var data = new TheoryData<string, Dictionary<string, object?>>();
        foreach (var (name, arguments) in AliasArguments)
        {
            data.Add(name, arguments);
        }
        return data;
    }

    private static readonly Dictionary<string, Dictionary<string, object?>> AliasArguments = new()
    {
        ["confirm_payment"] = new() { ["nonce"] = "ABC123" },
        ["check_wallet_balance"] = new(),
        ["get_all_balances"] = new(),
        ["get_budget_status"] = new(),
        ["configure_budget"] = new() { ["perRequest"] = 100, ["perSession"] = 1000 },
        ["get_receipts"] = new() { ["limit"] = 5 },
        ["get_payment_history"] = new() { ["limit"] = 5 },
        ["get_btc_price"] = new(),
        ["exchange_currency"] = new() { ["sourceCurrency"] = "USD", ["targetCurrency"] = "BTC", ["amount"] = 1 },
        ["send_onchain"] = new() { ["address"] = "bc1qexampleaddress", ["amountSats"] = 1000 },
        ["create_l402_challenge"] = new() { ["resource"] = "https://example.test/x", ["priceSats"] = 10 },
        ["verify_l402_payment"] = new() { ["macaroon"] = "fixture-macaroon", ["preimage"] = "fixture-preimage" },
        ["discover_agent_services"] = new() { ["category"] = "ai" },
        ["publish_agent_capability"] = new()
        {
            ["serviceId"] = "svc-1",
            ["categories"] = new[] { "ai" },
            ["content"] = "a service",
            ["priceSats"] = 10,
        },
        ["unpublish_agent_capability"] = new() { ["serviceId"] = "svc-1" },
        ["request_agent_service"] = new() { ["capabilityEventId"] = "evt-1", ["budgetSats"] = 10 },
        ["publish_agent_attestation"] = new()
        {
            ["subjectPubkey"] = "pk-1",
            ["agreementId"] = "evt-1",
            ["rating"] = 5,
            ["content"] = "good",
        },
        ["get_agent_reputation"] = new() { ["pubkey"] = "pk-1" },
        ["settle_agent_service"] = new() { ["l402Endpoint"] = "https://example.test/paid" },
    };

    [Fact]
    public void AliasMapIsFullyCovered()
    {
        AliasArguments.Keys.Should().BeEquivalentTo(DeprecatedAliasDispatcher.Aliases.Keys);
        DeprecatedAliasDispatcher.Aliases.Should().HaveCount(19);
    }

    [Theory]
    [MemberData(nameof(AllAliases))]
    public async Task EveryLegacyNameDispatchesWithADeprecationNote(
        string alias, Dictionary<string, object?> arguments)
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);

        var text = await host.CallTextAsync(alias, arguments);

        text.Should().NotBeEmpty($"'{alias}' must still be callable");
        text.Should().NotContain("Unknown tool", $"'{alias}' must still route to its replacement");

        var json = JsonDocument.Parse(text).RootElement;
        var target = DeprecatedAliasDispatcher.Aliases[alias];
        json.GetProperty("deprecated").GetProperty("replaced_by").GetString().Should().Be(target.Tool);
        json.GetProperty("deprecated").GetProperty("use").GetString().Should().Be(target.Use);
        json.GetProperty("deprecated").GetProperty("removal").GetString().Should().Be("v2.0.0");
    }

    [Theory]
    [MemberData(nameof(AllAliases))]
    public async Task NoLegacyNameIsAdvertisedByDefault(string alias, Dictionary<string, object?> arguments)
    {
        _ = arguments;
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        (await host.AdvertisedNamesAsync()).Should().NotContain(alias);
    }

    [Theory]
    [MemberData(nameof(AllAliases))]
    public void EveryAliasTargetsAnAdvertisedTool(string alias, Dictionary<string, object?> arguments)
    {
        _ = arguments;
        ToolProfiles.StandardToolNames.Should().Contain(DeprecatedAliasDispatcher.Aliases[alias].Tool);
    }

    [Fact]
    public async Task ALegacyNameStillCarriesTheNoteUnderTheFullProfile()
    {
        // `full` re-advertises the old name, but the call still routes through the alias
        // path, so the caller is still told the name is going away.
        await using var host = await McpToolHost.StartAsync(ToolProfile.Full);
        (await host.AdvertisedNamesAsync()).Should().Contain("get_btc_price");

        var json = JsonDocument.Parse(await host.CallTextAsync("get_btc_price")).RootElement;
        json.GetProperty("deprecated").GetProperty("replaced_by").GetString().Should().Be("wallet_ops");
    }

    [Fact]
    public async Task ANewNameCarriesNoDeprecationMarker()
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var text = await host.CallTextAsync("wallet_ops", new Dictionary<string, object?> { ["action"] = "price" });
        text.Should().NotContain("deprecated");
    }

    [Fact]
    public void UseHintNamesTheActionForAConsolidatedTool()
    {
        DeprecatedAliasDispatcher.Aliases["get_budget_status"].Use.Should().Be("budget(action=\"status\")");
        DeprecatedAliasDispatcher.Aliases["get_receipts"].Use.Should().Be("receipts(source=\"durable\")");
        DeprecatedAliasDispatcher.Aliases["check_wallet_balance"].Use.Should().Be("get_balance",
            "an alias with no action reads as the bare tool name");
        DeprecatedAliasDispatcher.IsAlias("get_balance").Should().BeFalse("get_balance is a current tool, not an alias");
    }
}

/// <summary>The consolidated verbs route each action to the tool it replaced.</summary>
public class ConsolidatedActionDispatchTests
{
    [Theory]
    [InlineData("budget", "action", "status")]
    [InlineData("budget", "action", "tighten")]
    [InlineData("receipts", "source", "durable")]
    [InlineData("receipts", "source", "session")]
    [InlineData("wallet_ops", "action", "price")]
    [InlineData("wallet_ops", "action", "exchange")]
    [InlineData("wallet_ops", "action", "send_onchain")]
    [InlineData("l402_producer", "action", "create")]
    [InlineData("l402_producer", "action", "verify")]
    [InlineData("agent_services", "action", "discover")]
    [InlineData("agent_services", "action", "request")]
    [InlineData("agent_services", "action", "settle")]
    [InlineData("agent_services", "action", "publish")]
    [InlineData("agent_services", "action", "unpublish")]
    [InlineData("agent_services", "action", "attest")]
    [InlineData("agent_services", "action", "reputation")]
    public async Task EveryActionRunsAndReturnsJson(string tool, string key, string action)
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var text = await host.CallTextAsync(tool, new Dictionary<string, object?> { [key] = action });

        text.Should().NotBeEmpty();
        var json = JsonDocument.Parse(text).RootElement;
        json.TryGetProperty("success", out _).Should().BeTrue(
            $"{tool}({key}={action}) must reach a real handler, not a routing dead end");
    }

    [Theory]
    [InlineData("budget", "action", "status", "tighten")]
    [InlineData("receipts", "source", "durable", "session")]
    [InlineData("wallet_ops", "action", "price", "send_onchain")]
    [InlineData("l402_producer", "action", "create", "verify")]
    [InlineData("agent_services", "action", "discover", "settle")]
    public async Task TheDiscriminatorIsRequiredAndEnumeratedInTheSchema(
        string tool, string key, string first, string second)
    {
        // The contract that stops a wrong call is the schema, not a runtime string match:
        // the discriminator is `required` and carries the full `enum`, so a client can
        // reject a bad call before it is ever sent.
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var advertised = await host.AdvertisedToolsAsync();
        var schema = advertised.Single(t => t.Name == tool).JsonSchema;

        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().Contain(key);

        var discriminator = schema.GetProperty("properties").GetProperty(key);
        var values = discriminator.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        values.Should().Contain(first).And.Contain(second);
        discriminator.GetProperty("description").GetString()
            .Should().Contain(first).And.Contain(second, "each action must be described");
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("receipts")]
    [InlineData("wallet_ops")]
    [InlineData("l402_producer")]
    [InlineData("agent_services")]
    public async Task AMissingDiscriminatorIsAnError_NotASilentNoOp(string tool)
    {
        // The .NET MCP SDK rejects the call during argument binding and reports a generic,
        // non-leaking message naming the tool — it never reaches the handler and never
        // silently succeeds. (The Python port surfaces the SDK's own validation text.)
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var result = await host.Client.CallToolAsync(tool, new Dictionary<string, object?>());
        var text = string.Join(" ", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));

        result.IsError.Should().BeTrue();
        text.Should().NotBeEmpty().And.Contain(tool);
    }

    [Fact]
    public async Task AnUnknownActionIsRejected()
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var result = await host.Client.CallToolAsync("budget",
            new Dictionary<string, object?> { ["action"] = "loosen" });

        result.IsError.Should().BeTrue("'loosen' is not in the schema enum");
    }

    [Fact]
    public void HandlerLevelUnknownActionErrorIsDescriptive()
    {
        // Defence behind the SDK's schema validation: a client that skips validation must
        // still get a named tool, the offending value and the valid choices.
        var json = JsonDocument.Parse(ActionErrors.Unknown(
            "budget", "action", (BudgetAction)99, BudgetAction.status, BudgetAction.tighten)).RootElement;

        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("tool").GetString().Should().Be("budget");
        json.GetProperty("error").GetString().Should().Contain("action").And.Contain("status").And.Contain("tighten");
        json.GetProperty("validActions").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void ReceiptsKeepsEachSourcesOwnDefaultPageSize()
    {
        // get_receipts defaulted to 20 and get_payment_history to 10; merging them must not
        // silently change either.
        ReceiptsTool.DurableDefaultLimit.Should().Be(20);
        ReceiptsTool.SessionDefaultLimit.Should().Be(10);
    }
}
