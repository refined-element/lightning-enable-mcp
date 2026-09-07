using System.IO.Pipelines;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tests;

/// <summary>
/// A real MCP server and client talking to each other over in-memory pipes, wired exactly
/// the way <c>Program</c> wires them: <c>WithToolsFromAssembly()</c> then
/// <c>WithToolSurface(profile)</c>.
///
/// <para>This runs the actual protocol, so a test here proves the things a unit test of
/// <see cref="ToolSurface"/> cannot: that <c>tools/list</c> really advertises what the
/// profile says, and that the SDK really falls through to the call-tool handler for a name
/// that is not in the advertised collection — which is the entire mechanism keeping the
/// deprecated aliases callable.</para>
///
/// <para>The application services are registered as <c>null</c> on purpose. Registration is
/// what makes the SDK treat a parameter as an injected dependency rather than a tool
/// argument (so it stays out of the JSON schema), while a null instance makes every tool
/// take its own "service not available" branch — a deterministic, well-formed JSON result
/// with no network, no wallet and no clock. Tool behaviour itself is covered by the
/// per-tool test classes; what is under test here is the routing.</para>
/// </summary>
internal sealed class McpToolHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly McpServer _server;
    private readonly Task _serverTask;

    private McpToolHost(McpServer server, Task serverTask, McpClient client, ToolSurface surface)
    {
        _server = server;
        _serverTask = serverTask;
        Client = client;
        Surface = surface;
    }

    /// <summary>The connected client — use it to call <c>tools/list</c> and <c>tools/call</c>.</summary>
    public McpClient Client { get; }

    /// <summary>The surface the server was built with.</summary>
    public ToolSurface Surface { get; }

    /// <summary>Starts a server for <paramref name="profile"/> and connects a client to it.</summary>
    public static async Task<McpToolHost> StartAsync(ToolProfile profile)
    {
        var services = new ServiceCollection();
        RegisterApplicationServices(services);

        var surface = services
            .AddMcpServer(options => options.ServerInfo = new Implementation
            {
                Name = "lightning-enable-test",
                Version = "0.0.0",
            })
            .WithToolsFromAssembly(typeof(PayInvoiceTool).Assembly)
            .WithToolSurface(profile);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            serverName: "lightning-enable-test");

        var server = McpServer.Create(serverTransport, options, loggerFactory: null, serviceProvider: provider);
        var cts = new CancellationTokenSource();
        var serverTask = server.RunAsync(cts.Token);

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream());

        var client = await McpClient.CreateAsync(clientTransport);

        var host = new McpToolHost(server, serverTask, client, surface);
        host._cts.Dispose();
        return host;
    }

    /// <summary>Names the server advertises in <c>tools/list</c>, in listing order.</summary>
    public async Task<IReadOnlyList<string>> AdvertisedNamesAsync()
    {
        var tools = await Client.ListToolsAsync();
        return tools.Select(t => t.Name).ToList();
    }

    /// <summary>The advertised tool definitions.</summary>
    public async Task<IReadOnlyList<McpClientTool>> AdvertisedToolsAsync()
        => (await Client.ListToolsAsync()).ToList();

    /// <summary>Calls a tool and returns the text of its first content block.</summary>
    public async Task<string> CallTextAsync(string name, IReadOnlyDictionary<string, object?>? arguments = null)
    {
        var result = await Client.CallToolAsync(name, arguments ?? new Dictionary<string, object?>());
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault();
        return text?.Text ?? string.Empty;
    }

    /// <summary>
    /// Registers every service a tool injects, as <c>null</c>. See the class remarks — the
    /// registration excludes the parameter from the tool's JSON schema; the null value
    /// routes each tool to its own "service not available" branch.
    /// </summary>
    private static void RegisterApplicationServices(IServiceCollection services)
    {
        // Mirrors Program.cs. A tool that injects something NOT listed here would leak the
        // parameter into its JSON schema — ToolSchemaTests.NoAdvertisedTool_ExposesAnInjectedService
        // fails loudly if that happens.
        services.AddSingleton<IWalletService>(_ => null!);
        services.AddSingleton<IBudgetService>(_ => null!);
        services.AddSingleton<IPriceService>(_ => null!);
        services.AddSingleton<IBudgetConfigurationService>(_ => null!);
        services.AddSingleton<IPaymentHistoryService>(_ => null!);
        services.AddSingleton<IReceiptService>(_ => null!);
        services.AddSingleton<IL402HttpClient>(_ => null!);
        services.AddSingleton<ILightningEnableApiService>(_ => null!);
        services.AddSingleton<IAgentService>(_ => null!);
        services.AddSingleton<IRateLimiter>(_ => null!);
        services.AddSingleton<IOperationLedger>(_ => null!);
        services.AddSingleton<IWalletOnboardingService>(_ => null!);
        services.AddHttpClient();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _server.DisposeAsync();
        try
        {
            await _serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // The transport is torn down under the running server; a cancellation or
            // closed-pipe exception here is expected and irrelevant to the test result.
        }
    }
}
