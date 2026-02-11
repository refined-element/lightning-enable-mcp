using LightningEnable.Mcp.Services;
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

        var builder = Host.CreateApplicationBuilder(args);

        // Register budget configuration FIRST (needed by wallet services for config file fallback)
        builder.Services.AddSingleton<IBudgetConfigurationService, BudgetConfigurationService>();

        // Load config to check for wallet settings
        var configService = new BudgetConfigurationService();
        var config = configService.Configuration;

        // Register HTTP client for L402
        builder.Services.AddHttpClient<IL402HttpClient, L402HttpClient>();

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
                builder.Services.AddHttpClient<IWalletService, NwcWalletService>();
            }
        }

        // Register price service for USD/sats conversion
        builder.Services.AddHttpClient<IPriceService, PriceService>();

        // Register singleton services
        builder.Services.AddSingleton<IBudgetService, BudgetService>();
        builder.Services.AddSingleton<IPaymentHistoryService, PaymentHistoryService>();
        builder.Services.AddSingleton<IRateLimiter, RateLimiter>();

        // Configure MCP server with stdio transport
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        var host = builder.Build();
        await host.RunAsync();
    }
}
