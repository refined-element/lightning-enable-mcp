using LightningEnable.Mcp.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
            // Streamable HTTP — for remote hosting (self-host remote / mobile).
            // Listen address via ASPNETCORE_URLS (defaults to http://localhost:5000).
            var app = webBuilder!.Build();

            // Auth gate. These tools move money, so a network-exposed endpoint must
            // not be open. If MCP_AUTH_TOKEN is set, require a matching Bearer token
            // (constant-time compare — engineering standard #7). If it's NOT set, run
            // open but log a loud local/dev-only warning. This Bearer gate is the
            // safety floor (never ship an open money endpoint; usable behind an auth
            // proxy / for controlled clients); full OAuth — what Claude's connector UI
            // uses for a polished remote connect — is the next increment.
            var authToken = Environment.GetEnvironmentVariable("MCP_AUTH_TOKEN");
            // Treat a whitespace-only token as NOT set — otherwise a value like " "
            // would satisfy the fail-closed check below and enable the auth middleware
            // with a trivially guessable token. Trim so an env var carrying a stray
            // trailing newline still matches the token the client sends.
            authToken = string.IsNullOrWhiteSpace(authToken) ? null : authToken.Trim();

            // FAIL-CLOSED. The single most important guard against a misconfigured
            // deploy draining a wallet: if the server is set to listen on a
            // non-loopback address (reachable beyond this machine) AND no
            // MCP_AUTH_TOKEN is set, REFUSE to start rather than silently run an
            // open, money-moving endpoint. A warning is not enough.
            // Resolve the listen address from ALL of ASP.NET Core's binding sources, not
            // just the ASPNETCORE_URLS env var. A non-loopback bind can also arrive via the
            // --urls command-line arg or the "urls" config key (both surface as
            // Configuration["urls"]), or a Kestrel:Endpoints config section. Gather them all
            // so the fail-closed check below can't be bypassed by a non-env binding source.
            var urlSources = new List<string?>
            {
                app.Configuration["urls"],
                Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
            };
            foreach (var ep in app.Configuration.GetSection("Kestrel:Endpoints").GetChildren())
                urlSources.Add(ep["Url"]);
            var listenUrls = string.Join(';', urlSources.Where(u => !string.IsNullOrWhiteSpace(u)));

            if (string.IsNullOrEmpty(authToken) && IsNonLoopbackBind(listenUrls))
            {
                Console.Error.WriteLine(
                    $"[Lightning Enable MCP] FATAL: refusing to start. HTTP transport is set to listen on a " +
                    $"non-loopback address (ASPNETCORE_URLS={listenUrls}) with NO authentication.");
                Console.Error.WriteLine(
                    "[Lightning Enable MCP] This would expose money-moving tools (pay_invoice, send_onchain, " +
                    "settle_agent_service, ...) to anyone who can reach the endpoint.");
                Console.Error.WriteLine(
                    "[Lightning Enable MCP] Fix: set MCP_AUTH_TOKEN to a strong secret AND terminate TLS in front; " +
                    "or bind to localhost only (unset ASPNETCORE_URLS / use 127.0.0.1) for local-only use.");
                Environment.Exit(78); // EX_CONFIG
                return;
            }
            if (!string.IsNullOrEmpty(authToken) && IsNonLoopbackBind(listenUrls))
            {
                Console.Error.WriteLine(
                    "[Lightning Enable MCP] NOTE: serving on a non-loopback address. Ensure TLS terminates in " +
                    "front of this endpoint — the Bearer token and all traffic must never cross the network in cleartext.");
            }

            if (!string.IsNullOrEmpty(authToken))
            {
                app.Use(async (context, next) =>
                {
                    var header = context.Request.Headers.Authorization.ToString();
                    const string prefix = "Bearer ";
                    // RFC 9110: the auth scheme is case-insensitive. Accept Bearer/bearer/…
                    // and trim the token so a trailing space/newline in the header doesn't
                    // cause a spurious 401.
                    var presented = header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        ? header[prefix.Length..].Trim()
                        : null;
                    if (presented is null || !ConstantTimeEquals(presented, authToken))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.Headers.WWWAuthenticate = "Bearer";
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = "unauthorized",
                            message = "A valid Bearer token is required. Set MCP_AUTH_TOKEN on the server and send 'Authorization: Bearer <token>'."
                        });
                        return;
                    }
                    await next();
                });
                Console.Error.WriteLine("[Lightning Enable MCP] HTTP auth: Bearer token required (MCP_AUTH_TOKEN set).");
            }
            else
            {
                Console.Error.WriteLine(
                    "[Lightning Enable MCP] WARNING: HTTP transport is UNAUTHENTICATED (MCP_AUTH_TOKEN not set). " +
                    "These tools move money — local/dev ONLY; never expose this to a network without auth.");
            }

            app.MapMcp();
            Console.Error.WriteLine("[Lightning Enable MCP] Transport: Streamable HTTP (MapMcp).");
            await app.RunAsync();
        }
        else
        {
            var host = hostBuilder!.Build();
            await host.RunAsync();
        }
    }

    /// <summary>
    /// Constant-time comparison for the HTTP auth token. Uses
    /// <see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals"/>
    /// so a wrong token cannot be reconstructed via timing (engineering standard #7).
    /// Returns false on a length mismatch without leaking via content comparison.
    /// </summary>
    private static bool ConstantTimeEquals(string a, string b)
    {
        var ba = System.Text.Encoding.UTF8.GetBytes(a);
        var bb = System.Text.Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    /// <summary>
    /// Returns true if any configured listen address (gathered from ASPNETCORE_URLS,
    /// the --urls / "urls" config key, and Kestrel:Endpoints — joined with ';') is
    /// reachable beyond this machine (any host other than localhost / 127.0.0.1 / ::1
    /// — e.g. 0.0.0.0, +, *, a real IP, or a hostname). An empty value means the
    /// framework's default localhost bind, which is loopback-only (safe). Biased
    /// fail-closed: anything not provably loopback is treated as non-loopback.
    /// </summary>
    private static bool IsNonLoopbackBind(string? aspnetcoreUrls)
    {
        if (string.IsNullOrWhiteSpace(aspnetcoreUrls))
            return false; // default bind is localhost (loopback)

        foreach (var raw in aspnetcoreUrls.Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var s = raw;
            var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx >= 0) s = s[(schemeIdx + 3)..];

            string host;
            if (s.StartsWith('['))
            {
                // IPv6 literal, e.g. [::1]:5000
                var end = s.IndexOf(']');
                host = end > 0 ? s[1..end] : s;
            }
            else
            {
                var cut = s.IndexOfAny(new[] { ':', '/' });
                host = cut >= 0 ? s[..cut] : s;
            }
            host = host.Trim().ToLowerInvariant();

            var loopback = host is "localhost" or "127.0.0.1" or "::1";
            if (!loopback)
                return true; // any non-loopback entry in the list = network-exposed
        }
        return false;
    }
}
