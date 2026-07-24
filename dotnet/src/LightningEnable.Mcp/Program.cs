using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp;

/// <summary>
/// Entry point for the Lightning Enable MCP server.
/// Provides Lightning payment capabilities to AI agents via Model Context Protocol.
///
/// Available tools:
/// - pay_invoice - Pay any Lightning invoice
/// - check_wallet_balance - Check wallet balance
/// - get_payment_history - View payment history
/// - get_budget_status - View current budget limits (read-only)
/// - access_l402_resource - Auto-pay L402 challenges
/// - pay_l402_challenge - Manual L402 payment
///
/// Wallet Configuration (in priority order):
/// - Set STRIKE_API_KEY for Strike wallet (https://dashboard.strike.me/)
/// - Set OPENNODE_API_KEY for OpenNode wallet (with optional OPENNODE_ENVIRONMENT)
/// - Set NWC_CONNECTION_STRING for Nostr Wallet Connect
/// - First configured wallet takes precedence
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        // Version banner for debugging
        var currentVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        Console.Error.WriteLine($"[Lightning Enable MCP] Version {currentVersion} starting...");
        Console.Error.WriteLine($"[Lightning Enable MCP] Config dir: ~/.lightning-enable/");

        // Check for updates (fire-and-forget, don't block startup)
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var apiUrl = Environment.GetEnvironmentVariable("LIGHTNING_ENABLE_API_URL")
                    ?? "https://api.lightningenable.com";
                var response = await http.GetStringAsync($"{apiUrl}/api/mcp/version-check?currentVersion={currentVersion}");
                var doc = System.Text.Json.JsonDocument.Parse(response);
                var root = doc.RootElement;

                if (root.TryGetProperty("updateRequired", out var required) && required.GetBoolean())
                {
                    var msg = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                    var notes = root.TryGetProperty("releaseNotes", out var n) ? n.GetString() : null;
                    Console.Error.WriteLine("");
                    Console.Error.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                    Console.Error.WriteLine($"[Lightning Enable MCP] CRITICAL UPDATE REQUIRED");
                    if (msg != null) Console.Error.WriteLine($"[Lightning Enable MCP] {msg}");
                    if (notes != null) Console.Error.WriteLine($"[Lightning Enable MCP] What's fixed: {notes}");
                    Console.Error.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                    Console.Error.WriteLine("");
                }
                else if (root.TryGetProperty("updateRecommended", out var recommended) && recommended.GetBoolean())
                {
                    var latest = root.TryGetProperty("latestVersion", out var v) ? v.GetString() : "unknown";
                    Console.Error.WriteLine($"[Lightning Enable MCP] Update available: v{latest}. Run: dotnet tool update -g LightningEnable.Mcp");
                }
            }
            catch
            {
                // Silently ignore - don't break MCP server if version check fails
            }
        });

        var builder = Host.CreateApplicationBuilder(args);

        // MCP stdio uses stdout EXCLUSIVELY for JSON-RPC frames. The default Generic Host
        // console logger (and the Hosting.Lifetime "Application started / Content root path"
        // banner) write to stdout, which interleaves non-JSON lines into the protocol stream
        // and desyncs strict MCP clients. Force ALL console log output to stderr, and drop the
        // lifetime status banner entirely so stdout carries nothing but JSON-RPC.
        builder.Logging.AddConsole(consoleOptions =>
        {
            consoleOptions.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);

        // Register budget configuration FIRST (needed by wallet services for config file fallback)
        builder.Services.AddSingleton<IBudgetConfigurationService, BudgetConfigurationService>();

        // Load config to check for wallet settings
        var configService = new BudgetConfigurationService();
        var config = configService.Configuration;

        // Register HTTP client for L402.
        //
        // SSRF hardening (F-10d/F-10e): the URL is attacker-influenceable and only a
        // cheap pre-check runs in AccessL402ResourceTool.ValidateUrl. The definitive
        // guard is the ConnectCallback below.
        //  - ConnectCallback: validates the ACTUAL connect-time IP and connects to
        //    exactly that validated set (no re-resolution), closing the DNS-rebind
        //    TOCTOU window between the initial-URL check and the socket connect.
        //  - AllowAutoRedirect = false: the SAFE posture, and NOT the same thing as
        //    "the ConnectCallback makes redirects safe" — it does not, for two reasons
        //    the callback cannot see:
        //      (1) HEADER LEAK. .NET's redirect handler only auto-strips the
        //          Authorization header on a cross-origin redirect; arbitrary custom
        //          headers the agent supplied via L402HttpClient.CreateRequest
        //          (X-Api-Key, Cookie, ...) are RE-SENT to the redirect target. A
        //          302 → attacker host would exfiltrate them. The IP guard fires per
        //          hop but says nothing about which headers travel.
        //      (2) L402 MID-PAYMENT HOST CHANGE. HandleL402ChallengeAsync retries the
        //          ORIGINAL url. A provider that host-redirects BEFORE its 402 would,
        //          with auto-redirect on, get paid (sats debited via RecordSpend) and
        //          then the paid retry — following the redirect to a new host — drops
        //          its Authorization: L402 header on the host change and 402s again:
        //          the agent pays and receives nothing.
        //    With redirects unfollowed the initial URL is the ONLY fetch and it is
        //    fully validated (pre-check + connect-time IP guard); there is no
        //    cross-origin header leak, no L402 host change mid-payment, and no
        //    unvalidated redirect-hop. A 3xx is surfaced to the agent as an actionable
        //    "call again with the redirect target" result (see L402HttpClient), never
        //    followed.
        builder.Services.AddHttpClient<IL402HttpClient, L402HttpClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = SsrfConnectValidator.ConnectAsync,
            });

        // Register the guarded HTTP client for discover_api's manifest fetch (F-10e).
        // The manifest URL is agent-supplied, so this client carries the SAME
        // connect-time SSRF guard as the L402 client — an agent cannot use discover_api
        // to reach 169.254.169.254 / a private range. AllowAutoRedirect = false for the
        // same header-leak / redirect-hop reasons as the L402 client above; a 3xx is
        // surfaced as an actionable redirect result (see DiscoverApiTool), never followed.
        builder.Services.AddHttpClient(DiscoverApiTool.ManifestHttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("User-Agent", "LightningEnable-MCP/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = SsrfConnectValidator.ConnectAsync,
            });

        // Register wallet service
        // Default priority for L402: LND > NWC > Strike > OpenNode
        // (LND, NWC, and Strike return preimage; OpenNode does not)
        // Override with WALLET_PRIORITY env var or config file wallets.priority
        var lndRestHost = Environment.GetEnvironmentVariable("LND_REST_HOST");
        var lndMacaroonHex = Environment.GetEnvironmentVariable("LND_MACAROON_HEX");
        var nwcConnection = Environment.GetEnvironmentVariable("NWC_CONNECTION_STRING");
        var strikeApiKey = Environment.GetEnvironmentVariable("STRIKE_API_KEY");
        var openNodeApiKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        var walletPriority = Environment.GetEnvironmentVariable("WALLET_PRIORITY")?.ToLowerInvariant();

        // Fall back to config file for credentials if env vars not set
        if (string.IsNullOrEmpty(lndRestHost) || lndRestHost.StartsWith("${"))
            lndRestHost = config?.Wallets?.LndRestHost;
        if (string.IsNullOrEmpty(lndMacaroonHex) || lndMacaroonHex.StartsWith("${"))
            lndMacaroonHex = config?.Wallets?.LndMacaroonHex;
        if (string.IsNullOrEmpty(nwcConnection) || nwcConnection.StartsWith("${"))
            nwcConnection = config?.Wallets?.NwcConnectionString;
        if (string.IsNullOrEmpty(strikeApiKey) || strikeApiKey.StartsWith("${"))
            strikeApiKey = config?.Wallets?.StrikeApiKey;
        if (string.IsNullOrEmpty(openNodeApiKey) || openNodeApiKey.StartsWith("${"))
            openNodeApiKey = config?.Wallets?.OpenNodeApiKey;
        if (string.IsNullOrEmpty(walletPriority) || walletPriority.StartsWith("${"))
            walletPriority = config?.Wallets?.Priority?.ToLowerInvariant();

        bool walletRegistered = false;
        bool lndConfigured = !string.IsNullOrEmpty(lndRestHost) && !string.IsNullOrEmpty(lndMacaroonHex);

        // If priority is set, try that wallet first
        if (walletPriority == "lnd" && lndConfigured)
        {
            Console.Error.WriteLine("Using LND wallet backend (priority override)");
            Console.Error.WriteLine("LND always returns preimage - L402 fully supported");
            builder.Services.AddHttpClient<IWalletService, LndWalletService>();
            walletRegistered = true;
        }
        else if (walletPriority == "nwc" && !string.IsNullOrEmpty(nwcConnection))
        {
            Console.Error.WriteLine("Using NWC wallet backend (priority override)");
            Console.Error.WriteLine("NWC returns preimage - L402 fully supported");
            builder.Services.AddHttpClient<IWalletService, NwcWalletService>();
            walletRegistered = true;
        }
        else if (walletPriority == "strike" && !string.IsNullOrEmpty(strikeApiKey))
        {
            Console.Error.WriteLine("Using Strike wallet backend (priority override)");
            Console.Error.WriteLine("Strike returns preimage - L402 fully supported");
            builder.Services.AddHttpClient<IWalletService, StrikeWalletService>();
            walletRegistered = true;
        }
        else if (walletPriority == "opennode" && !string.IsNullOrEmpty(openNodeApiKey))
        {
            var environment = Environment.GetEnvironmentVariable("OPENNODE_ENVIRONMENT") ?? "production";
            Console.Error.WriteLine($"Using OpenNode wallet backend ({environment}) (priority override)");
            Console.Error.WriteLine("WARNING: OpenNode does NOT return preimage - L402 will not work");
            builder.Services.AddHttpClient<IWalletService, OpenNodeWalletService>();
            walletRegistered = true;
        }

        // Fall back to default priority: LND > NWC > Strike > OpenNode
        // This order prioritizes wallets that return preimage for L402
        if (!walletRegistered)
        {
            if (lndConfigured)
            {
                Console.Error.WriteLine("Using LND wallet backend");
                Console.Error.WriteLine("LND always returns preimage - L402 fully supported");
                builder.Services.AddHttpClient<IWalletService, LndWalletService>();
            }
            else if (!string.IsNullOrEmpty(nwcConnection))
            {
                Console.Error.WriteLine("Using NWC wallet backend");
                Console.Error.WriteLine("NWC returns preimage - L402 fully supported");
                builder.Services.AddHttpClient<IWalletService, NwcWalletService>();
            }
            else if (!string.IsNullOrEmpty(strikeApiKey))
            {
                Console.Error.WriteLine("Using Strike wallet backend");
                Console.Error.WriteLine("Strike returns preimage - L402 fully supported");
                builder.Services.AddHttpClient<IWalletService, StrikeWalletService>();
            }
            else if (!string.IsNullOrEmpty(openNodeApiKey))
            {
                var environment = Environment.GetEnvironmentVariable("OPENNODE_ENVIRONMENT") ?? "production";
                Console.Error.WriteLine($"Using OpenNode wallet backend ({environment})");
                Console.Error.WriteLine("WARNING: OpenNode does NOT return preimage - L402 will not work");
                builder.Services.AddHttpClient<IWalletService, OpenNodeWalletService>();
            }
            else
            {
                Console.Error.WriteLine("WARNING: No wallet configured.");
                Console.Error.WriteLine("");
                Console.Error.WriteLine("Configure one L402-capable wallet (env var or ~/.lightning-enable/config.json).");
                Console.Error.WriteLine("L402 — the core use case — needs a wallet that returns a preimage:");
                Console.Error.WriteLine("  STRIKE_API_KEY                   - Strike (easiest to start, multi-currency)");
                Console.Error.WriteLine("  NWC_CONNECTION_STRING            - Nostr Wallet Connect (CoinOS / CLINK / Alby Hub)");
                Console.Error.WriteLine("  LND_REST_HOST + LND_MACAROON_HEX - LND node (always returns a preimage)");
                Console.Error.WriteLine("");
                Console.Error.WriteLine("  OPENNODE_API_KEY                 - OpenNode: receiving/invoicing only — CANNOT pay L402 challenges");
                Console.Error.WriteLine("                                     (+ optional OPENNODE_ENVIRONMENT)");
                Console.Error.WriteLine("");
                Console.Error.WriteLine("Priority when several are set: LND > NWC > Strike > OpenNode.");
                Console.Error.WriteLine("Or add credentials to ~/.lightning-enable/config.json under the \"wallets\" key.");
                Console.Error.WriteLine("After configuring, run the test_l402_payment tool to confirm the wallet works end to end (~1 sat).");
                // Register a default that will report "not configured" errors
                builder.Services.AddHttpClient<IWalletService, NwcWalletService>();
            }
        }

        // Register Lightning Enable API service for L402 producer tools
        builder.Services.AddHttpClient<ILightningEnableApiService, LightningEnableApiService>();

        // Register price service for USD/sats conversion
        builder.Services.AddHttpClient<IPriceService, PriceService>();

        // Register agent service for ASA (Agent Service Agreement) operations
        builder.Services.AddHttpClient<IAgentService, AgentService>();

        // Register singleton services
        builder.Services.AddSingleton<IBudgetService, BudgetService>();
        builder.Services.AddSingleton<IPaymentHistoryService, PaymentHistoryService>();
        builder.Services.AddSingleton<IRateLimiter, RateLimiter>();
        // Durable, append-only spend receipts (~/.lightning-enable/receipts.jsonl).
        builder.Services.AddSingleton<IReceiptService, ReceiptService>();

        // Configure MCP server with stdio transport.
        //
        // WithToolsFromAssembly() populates the advertised ToolCollection (the 25
        // canonical tools). The custom CallToolHandler below is consulted by the SDK
        // ONLY for tool names absent from that collection — i.e. the deprecated
        // forwarding aliases (confirm_payment, check_wallet_balance, get_all_balances).
        // Because they are not [McpServerTool] and no ListToolsHandler adds them, they
        // never appear in list_tools yet remain callable — true hidden aliases, matching
        // the Python port.
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly()
            .WithCallToolHandler(async (context, cancellationToken) =>
            {
                var name = context.Params?.Name ?? string.Empty;

                if (!Tools.DeprecatedAliasDispatcher.IsAlias(name))
                {
                    // The 25 advertised tools are served from the ToolCollection before
                    // this handler runs, so anything reaching here that is not an alias
                    // is a genuinely unknown tool.
                    return new ModelContextProtocol.Protocol.CallToolResult
                    {
                        IsError = true,
                        Content = { new ModelContextProtocol.Protocol.TextContentBlock { Text = $"Unknown tool: {name}" } },
                    };
                }

                var json = await Tools.DeprecatedAliasDispatcher.DispatchAsync(
                    name,
                    context.Params?.Arguments as IReadOnlyDictionary<string, System.Text.Json.JsonElement>,
                    context.Services!,
                    cancellationToken);

                return new ModelContextProtocol.Protocol.CallToolResult
                {
                    Content = { new ModelContextProtocol.Protocol.TextContentBlock { Text = json } },
                };
            });

        var host = builder.Build();
        await host.RunAsync();
    }
}
