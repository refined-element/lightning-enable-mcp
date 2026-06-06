using LightningEnable.Mcp.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

        // Transport selection. Default is stdio (local / Claude Desktop / Claude Code).
        // Opt into Streamable HTTP — for remote hosting (mobile, Connectors Directory) —
        // with MCP_TRANSPORT=http. Both transports register the SAME services; only the
        // host type, the MCP transport, and the run call differ. We register through a
        // shared IServiceCollection so the wallet/L402/agent/budget wiring below is
        // byte-identical for both paths.
        var transport = (Environment.GetEnvironmentVariable("MCP_TRANSPORT") ?? "stdio")
            .Trim().ToLowerInvariant();

        // Fail fast on a typo'd transport rather than silently running stdio. Setting
        // MCP_TRANSPORT=htt (meaning http) would otherwise start a local-only server
        // with no error — a confusing, hard-to-detect misconfiguration for someone
        // who intended a remote endpoint.
        if (transport != "stdio" && transport != "http")
        {
            Console.Error.WriteLine(
                $"[Lightning Enable MCP] FATAL: unrecognized MCP_TRANSPORT='{transport}'. " +
                "Valid values: 'stdio' (default, local) or 'http' (Streamable HTTP, remote). " +
                "Refusing to start rather than silently falling back to a transport you did not request.");
            Environment.Exit(78); // EX_CONFIG
            return;
        }

        WebApplicationBuilder? webBuilder = null;
        HostApplicationBuilder? hostBuilder = null;
        IServiceCollection services;
        if (transport == "http")
        {
            webBuilder = WebApplication.CreateBuilder(args);
            services = webBuilder.Services;
        }
        else
        {
            hostBuilder = Host.CreateApplicationBuilder(args);
            services = hostBuilder.Services;
        }

        // Register budget configuration FIRST (needed by wallet services for config file fallback)
        services.AddSingleton<IBudgetConfigurationService, BudgetConfigurationService>();

        // Load config to check for wallet settings
        var configService = new BudgetConfigurationService();
        var config = configService.Configuration;

        // Register HTTP client for L402
        services.AddHttpClient<IL402HttpClient, L402HttpClient>();

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
            services.AddHttpClient<IWalletService, LndWalletService>();
            walletRegistered = true;
        }
        else if (walletPriority == "nwc" && !string.IsNullOrEmpty(nwcConnection))
        {
            Console.Error.WriteLine("Using NWC wallet backend (priority override)");
            Console.Error.WriteLine("NWC returns preimage - L402 fully supported");
            services.AddHttpClient<IWalletService, NwcWalletService>();
            walletRegistered = true;
        }
        else if (walletPriority == "strike" && !string.IsNullOrEmpty(strikeApiKey))
        {
            Console.Error.WriteLine("Using Strike wallet backend (priority override)");
            Console.Error.WriteLine("Strike returns preimage - L402 fully supported");
            services.AddHttpClient<IWalletService, StrikeWalletService>();
            walletRegistered = true;
        }
        else if (walletPriority == "opennode" && !string.IsNullOrEmpty(openNodeApiKey))
        {
            var environment = Environment.GetEnvironmentVariable("OPENNODE_ENVIRONMENT") ?? "production";
            Console.Error.WriteLine($"Using OpenNode wallet backend ({environment}) (priority override)");
            Console.Error.WriteLine("WARNING: OpenNode does NOT return preimage - L402 will not work");
            services.AddHttpClient<IWalletService, OpenNodeWalletService>();
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
                services.AddHttpClient<IWalletService, LndWalletService>();
            }
            else if (!string.IsNullOrEmpty(nwcConnection))
            {
                Console.Error.WriteLine("Using NWC wallet backend");
                Console.Error.WriteLine("NWC returns preimage - L402 fully supported");
                services.AddHttpClient<IWalletService, NwcWalletService>();
            }
            else if (!string.IsNullOrEmpty(strikeApiKey))
            {
                Console.Error.WriteLine("Using Strike wallet backend");
                Console.Error.WriteLine("Strike returns preimage - L402 fully supported");
                services.AddHttpClient<IWalletService, StrikeWalletService>();
            }
            else if (!string.IsNullOrEmpty(openNodeApiKey))
            {
                var environment = Environment.GetEnvironmentVariable("OPENNODE_ENVIRONMENT") ?? "production";
                Console.Error.WriteLine($"Using OpenNode wallet backend ({environment})");
                Console.Error.WriteLine("WARNING: OpenNode does NOT return preimage - L402 will not work");
                services.AddHttpClient<IWalletService, OpenNodeWalletService>();
            }
            else
            {
                Console.Error.WriteLine("WARNING: No wallet configured.");
                Console.Error.WriteLine("");
                Console.Error.WriteLine("Configure a wallet using environment variables or config file:");
                Console.Error.WriteLine("  STRIKE_API_KEY        - Strike wallet (recommended, multi-currency)");
                Console.Error.WriteLine("  OPENNODE_API_KEY      - OpenNode wallet (+ optional OPENNODE_ENVIRONMENT)");
                Console.Error.WriteLine("  NWC_CONNECTION_STRING - Nostr Wallet Connect");
                Console.Error.WriteLine("  LND_REST_HOST + LND_MACAROON_HEX - LND node");
                Console.Error.WriteLine("");
                Console.Error.WriteLine("Or add credentials to ~/.lightning-enable/config.json under 'wallets'");
                Console.Error.WriteLine("");
                Console.Error.WriteLine("Note: For L402 auto-pay, use LND, NWC, or Strike (they return preimage).");
                Console.Error.WriteLine("      OpenNode works for direct payments but not L402.");
                // Register a default that will report "not configured" errors
                services.AddHttpClient<IWalletService, NwcWalletService>();
            }
        }

        // Register Lightning Enable API service for L402 producer tools
        services.AddHttpClient<ILightningEnableApiService, LightningEnableApiService>();

        // Register price service for USD/sats conversion
        services.AddHttpClient<IPriceService, PriceService>();

        // Register agent service for ASA (Agent Service Agreement) operations
        services.AddHttpClient<IAgentService, AgentService>();

        // Register singleton services
        services.AddSingleton<IBudgetService, BudgetService>();
        services.AddSingleton<IPaymentHistoryService, PaymentHistoryService>();
        services.AddSingleton<IRateLimiter, RateLimiter>();

        // Configure the MCP server. Tools are identical across transports; only the
        // transport differs (stdio for local, Streamable HTTP for remote).
        var mcp = services.AddMcpServer().WithToolsFromAssembly();
        if (transport == "http")
            mcp.WithHttpTransport();
        else
            mcp.WithStdioServerTransport();

        if (transport == "http")
        {
            // Streamable HTTP — for remote hosting (mobile, Connectors Directory).
            // Listen address via ASPNETCORE_URLS (defaults to http://localhost:5000).
            // WARNING: there is NO authentication here yet. This is for local/dev use
            // only — OAuth + per-user wallet connection must land before any networked
            // deployment, since these tools move money.
            var app = webBuilder!.Build();
            app.MapMcp();
            Console.Error.WriteLine(
                "[Lightning Enable MCP] Transport: Streamable HTTP (MapMcp). " +
                "No auth configured — local/dev use only until auth lands.");
            await app.RunAsync();
        }
        else
        {
            var host = hostBuilder!.Build();
            await host.RunAsync();
        }
    }
}
