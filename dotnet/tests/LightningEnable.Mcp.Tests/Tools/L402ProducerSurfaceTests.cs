using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Tools;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// What <c>l402_producer</c> advertises after growing the seller-setup actions, and where
/// each action routes.
///
/// <para>The point of adding actions instead of tools is that the advertised inventory does
/// not grow — no new schema is pushed into the model's context, and no prompt written
/// against the old surface breaks. That claim is only worth something if it is pinned, so
/// this holds the tool count, the alias table, the annotations and the enum together.</para>
///
/// <para>Mirrors the ``TestAdvertisedSurface`` / ``TestDispatch`` classes in
/// <c>python/lightning-enable-mcp/tests/test_producer_setup.py</c>.</para>
/// </summary>
public class L402ProducerSurfaceTests
{
    /// <summary>Every action, in the order the enum declares them.</summary>
    private static readonly string[] AllActions =
    {
        "create", "verify", "configure_receive", "status",
        "create_proxy", "add_endpoint", "publish", "list_challenges",
    };

    /// <summary>
    /// Actions that only read. Annotations are per TOOL, so these do not change the tool's
    /// own <c>readOnlyHint</c> — the widest action decides that — but the split is recorded
    /// here and reflected in the action description the model reads.
    /// </summary>
    private static readonly string[] ReadOnlyActions = { "status", "list_challenges" };

    private static readonly string[] WriteActions =
    {
        "create", "verify", "configure_receive", "create_proxy", "add_endpoint", "publish",
    };

    private static async Task<JsonElement> ProducerSchemaAsync()
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var tool = (await host.AdvertisedToolsAsync()).Single(t => t.Name == "l402_producer");
        return tool.JsonSchema;
    }

    [Fact]
    public void EnumDeclaresEveryAction()
    {
        Enum.GetNames<L402ProducerAction>().Should().Equal(AllActions);
    }

    [Fact]
    public void EveryActionIsClassifiedAsAReadOrAWrite()
    {
        ReadOnlyActions.Concat(WriteActions).Should().BeEquivalentTo(AllActions);
        ReadOnlyActions.Intersect(WriteActions).Should().BeEmpty();
    }

    [Fact]
    public async Task SchemaAdvertisesEveryAction()
    {
        var schema = await ProducerSchemaAsync();
        var action = schema.GetProperty("properties").GetProperty("action");

        action.GetProperty("enum").EnumerateArray().Select(v => v.GetString())
            .Should().BeEquivalentTo(AllActions);
        schema.GetProperty("required").EnumerateArray().Select(v => v.GetString())
            .Should().Contain("action");
    }

    [Fact]
    public async Task ActionDescriptionMarksTheReadOnlyActions()
    {
        var schema = await ProducerSchemaAsync();
        var description = schema.GetProperty("properties").GetProperty("action")
            .GetProperty("description").GetString()!;

        foreach (var action in AllActions)
        {
            description.Should().Contain(action);
        }

        // One "(read-only)" tag per read-only action, so a model can tell which calls are
        // safe to make speculatively without reading the whole per-argument list.
        var tags = description.Split("(read-only)").Length - 1;
        tags.Should().Be(ReadOnlyActions.Length);
    }

    [Fact]
    public async Task CreateAndVerifyArgumentsAreUntouched()
    {
        var properties = (await ProducerSchemaAsync()).GetProperty("properties");

        foreach (var argument in new[] { "resource", "priceSats", "description", "macaroon", "preimage" })
        {
            properties.TryGetProperty(argument, out _).Should().BeTrue(argument);
        }
    }

    [Fact]
    public async Task EveryNewArgumentIsAdvertised()
    {
        var properties = (await ProducerSchemaAsync()).GetProperty("properties");

        foreach (var argument in new[]
                 {
                     "nwcConnectionString", "name", "targetBaseUrl", "defaultPriceSats",
                     "proxyId", "endpointId", "path", "httpMethod", "summary",
                     "serviceName", "serviceDescription", "categories",
                     "challengeStatus", "limit", "offset",
                 })
        {
            properties.TryGetProperty(argument, out _).Should().BeTrue(argument);
        }

        properties.GetProperty("categories").GetProperty("type").GetString().Should().Be("array");
    }

    [Fact]
    public async Task TheToolStaysAWriteAnnotatedForItsWidestAction()
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var annotations = (await host.AdvertisedToolsAsync())
            .Single(t => t.Name == "l402_producer").ProtocolTool.Annotations!;

        annotations.Title.Should().Be("L402 producer");
        annotations.ReadOnlyHint.Should().BeFalse();
        annotations.DestructiveHint.Should().BeFalse("no producer action can spend the wallet");
    }

    [Fact]
    public async Task TheToolCountIsUnchanged()
    {
        // New actions, not new tools — the advertised inventory must not grow.
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        (await host.AdvertisedNamesAsync()).Should().HaveCount(16);
    }

    [Fact]
    public void NoNewDeprecatedAliasWasIntroduced()
    {
        var producerAliases = DeprecatedAliasDispatcher.Aliases
            .Where(a => a.Value.Tool == "l402_producer")
            .ToDictionary(a => a.Key, a => a.Value.Use);

        producerAliases.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["create_l402_challenge"] = "l402_producer(action=\"create\")",
            ["verify_l402_payment"] = "l402_producer(action=\"verify\")",
        });
    }

    // ── Dispatch ─────────────────────────────────────────────────────────────
    //
    // Each action is routed by calling the tool with NO service and asserting the branch it
    // lands in. The two original actions and the six new ones take different "not available"
    // paths, and each new action validates its own required arguments, so a mis-wired switch
    // arm shows up as the wrong message rather than passing silently.

    [Theory]
    [InlineData(L402ProducerAction.create, "Resource identifier is required")]
    [InlineData(L402ProducerAction.verify, "Macaroon is required")]
    public async Task OriginalActionsStillReachTheirOwnImplementations(
        L402ProducerAction action, string expected)
    {
        var result = await L402ProducerTool.L402Producer(action);

        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain(expected);
    }

    [Theory]
    [InlineData(L402ProducerAction.create_proxy, "A name is required")]
    [InlineData(L402ProducerAction.add_endpoint, "proxyId is required")]
    [InlineData(L402ProducerAction.publish, "proxyId is required")]
    public async Task NewActionsValidateTheirOwnArgumentsBeforeAnythingElse(
        L402ProducerAction action, string expected)
    {
        // A configured stub proves the routing reached the right handler: the message comes
        // from that action's own argument checks, not from the shared API-key gate.
        var result = await L402ProducerTool.L402Producer(action, apiService: new ConfiguredStub());

        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain(expected);
    }

    [Fact]
    public async Task ListChallengesTakesItsFilterFromChallengeStatus()
    {
        // `status` is an ACTION name, so the filter argument has its own name.
        var result = await L402ProducerTool.L402Producer(
            L402ProducerAction.list_challenges,
            challengeStatus: "settled",
            apiService: new ConfiguredStub());

        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("paid, unpaid, expired");
    }

    [Fact]
    public async Task ConfigureReceiveRoutesToTheWalletFallback()
    {
        var result = await L402ProducerTool.L402Producer(
            L402ProducerAction.configure_receive, apiService: new ConfiguredStub());

        // No connection string and no onboarding service: the fallback branch, not a crash.
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("nwcConnectionString");
    }

    /// <summary>An API service that passes the key gate and never gets called.</summary>
    private sealed class ConfiguredStub : LightningEnable.Mcp.Services.ILightningEnableApiService
    {
        public bool IsConfigured => true;
        public string BaseUrl => "https://api.example.test";

        private static Task<T> Unreachable<T>() =>
            Task.FromException<T>(new InvalidOperationException(
                "the action should have failed its own argument validation first"));

        public Task<LightningEnable.Mcp.Services.CreateChallengeResult> CreateChallengeAsync(
            string resource, long priceSats, string? description, CancellationToken ct)
            => Unreachable<LightningEnable.Mcp.Services.CreateChallengeResult>();

        public Task<LightningEnable.Mcp.Services.VerifyTokenResult> VerifyTokenAsync(
            string macaroon, string preimage, CancellationToken ct)
            => Unreachable<LightningEnable.Mcp.Services.VerifyTokenResult>();

        public Task<LightningEnable.Mcp.Services.ApiCallResult> SaveNwcConnectionAsync(
            string nwcConnectionString, CancellationToken ct)
            => Unreachable<LightningEnable.Mcp.Services.ApiCallResult>();

        public Task<LightningEnable.Mcp.Services.ApiCallResult> SetPaymentProviderAsync(
            string provider, CancellationToken ct)
            => Unreachable<LightningEnable.Mcp.Services.ApiCallResult>();

        public Task<LightningEnable.Mcp.Services.ApiCallResult> GetMerchantAccountAsync(CancellationToken ct)
            => Unreachable<LightningEnable.Mcp.Services.ApiCallResult>();

        public Task<LightningEnable.Mcp.Services.ApiCallResult> GetQuickStartAsync(CancellationToken ct)
            => Unreachable<LightningEnable.Mcp.Services.ApiCallResult>();

        public Task<LightningEnable.Mcp.Services.ApiCallResult> ListChallengesAsync(
            string? status, int limit, int offset, CancellationToken ct)
            => Unreachable<LightningEnable.Mcp.Services.ApiCallResult>();

        public Task<LightningEnable.Mcp.Services.ApiCallResult> CreateProxyAsync(
            string name, string targetBaseUrl, string? description, int defaultPriceSats, CancellationToken ct)
            => Unreachable<LightningEnable.Mcp.Services.ApiCallResult>();

        public Task<LightningEnable.Mcp.Services.ApiCallResult> RenameProxyAsync(
            string proxyId, string name, CancellationToken ct)
            => Unreachable<LightningEnable.Mcp.Services.ApiCallResult>();

        public Task<LightningEnable.Mcp.Services.ApiCallResult> CreateManifestEndpointAsync(
            string proxyId, string endpointId, string path, string httpMethod, string? summary,
            int basePriceSats, CancellationToken ct)
            => Unreachable<LightningEnable.Mcp.Services.ApiCallResult>();

        public Task<LightningEnable.Mcp.Services.ApiCallResult> UpdateManifestSettingsAsync(
            string proxyId, string? serviceDescription, IReadOnlyList<string>? categories, CancellationToken ct)
            => Unreachable<LightningEnable.Mcp.Services.ApiCallResult>();
    }
}
