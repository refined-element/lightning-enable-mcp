using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// One-command self-test for the Lightning wallet: pays the public 1-sat L402
/// test endpoint end to end, proving the wallet is connected, returns a preimage,
/// and can complete an L402 payment.
///
/// It adds NO new payment logic — it delegates to the same proven
/// <c>access_l402_resource</c> path (budget checks, recording, everything) against
/// a <b>hardcoded</b> endpoint, so there is no user-supplied URL and therefore no
/// SSRF surface. It then translates the raw result into a plain pass/fail verdict
/// with a fix hint, which is what makes it a better onboarding primitive than
/// telling a beginner to curl a magic URL.
/// </summary>
[McpServerToolType]
public static class TestL402PaymentTool
{
    // The public 1-sat L402 test resource. Hardcoded on purpose so this tool can
    // never be repurposed into an arbitrary-URL payer. Only the base host is
    // overridable, via the same env var the rest of the MCP uses to point at a
    // non-prod Lightning Enable instance.
    internal const string TestPath = "/l402/test/ping";

    // The endpoint charges 1 sat; small headroom, hard-capped so the self-test can
    // never spend more than a rounding error regardless of what the endpoint returns.
    private const int MaxTestSats = 10;

    [McpServerTool(Name = "test_l402_payment"), Description(
        "Self-test the Lightning wallet by paying the public 1-sat L402 test endpoint end to end. " +
        "Proves the wallet is connected, returns a preimage, and can complete an L402 payment. " +
        "Costs about 1 satoshi. Use this to verify setup or answer 'is my wallet actually working?'.")]
    public static async Task<string> TestL402Payment(
        McpServer? server = null,
        IL402HttpClient? l402Client = null,
        IBudgetService? budgetService = null,
        IPriceService? priceService = null,
        IPaymentHistoryService? paymentHistory = null,
        IRateLimiter? rateLimiter = null,
        CancellationToken cancellationToken = default)
    {
        var endpoint = ResolveTestEndpoint();

        // Delegate to the existing, proven L402 payment path. No new payment code,
        // no bypass of budget/confirmation. maxSats hard-caps the spend.
        var raw = await AccessL402ResourceTool.AccessL402Resource(
            url: endpoint,
            method: "GET",
            headers: null,
            body: null,
            maxSats: MaxTestSats,
            confirmationNonce: null,
            server: server,
            l402Client: l402Client,
            budgetService: budgetService,
            priceService: priceService,
            paymentHistory: paymentHistory,
            rateLimiter: rateLimiter,
            cancellationToken: cancellationToken);

        return Interpret(raw, endpoint);
    }

    /// <summary>
    /// Turns the raw access_l402_resource result into a plain self-test verdict.
    /// Kept internal + pure so the diagnostics are unit-testable without a wallet.
    /// </summary>
    internal static string Interpret(string rawJson, string endpoint)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var success = root.TryGetProperty("success", out var s) && s.GetBoolean();

            if (success)
            {
                var paid = root.TryGetProperty("payment", out var p)
                    && p.TryGetProperty("paid", out var pd) && pd.GetBoolean();
                long sats = paid && p.TryGetProperty("amountSats", out var amt) ? amt.GetInt64() : 0;
                int status = root.TryGetProperty("statusCode", out var sc) ? sc.GetInt32() : 200;

                if (paid)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        test = "passed",
                        message = $"✅ L402 works end to end. Paid {sats} sat(s), preimage verified, endpoint returned {status}. " +
                                  "Your wallet is ready to pay L402 resources anywhere.",
                        endpoint,
                        amountSats = sats,
                        statusCode = status,
                        walletWorking = true
                    });
                }

                // 200 without a payment: the test endpoint should always issue a 402,
                // so this means the L402 challenge was not exercised. Inconclusive.
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    test = "inconclusive",
                    message = $"Endpoint returned {status} without requiring payment, so the L402 flow was not exercised. " +
                              "Retry, or verify the test endpoint.",
                    endpoint,
                    statusCode = status
                });
            }

            // Failure — interpret the error into a specific reason + fix.
            var error = root.TryGetProperty("error", out var e) ? (e.GetString() ?? "") : "";
            var (reason, fix) = Diagnose(error);
            return JsonSerializer.Serialize(new
            {
                success = false,
                test = "failed",
                reason,
                message = $"❌ L402 self-test failed: {error}",
                howToFix = fix,
                endpoint,
                walletWorking = false
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                test = "failed",
                reason = "unexpected",
                message = $"❌ L402 self-test result could not be interpreted: {ex.Message}",
                endpoint
            });
        }
    }

    /// <summary>Maps a raw error string to a stable reason code + a plain-language fix.</summary>
    internal static (string reason, string fix) Diagnose(string error)
    {
        var e = (error ?? string.Empty).ToLowerInvariant();
        if (e.Contains("not configured") || e.Contains("no wallet"))
            return ("no_wallet",
                "No Lightning wallet is configured. Set NWC_CONNECTION_STRING (CoinOS/Alby Hub/CLINK), STRIKE_API_KEY, or LND " +
                "credentials — or add a wallet to ~/.lightning-enable/config.json. NWC and Strike both work for L402.");
        if (e.Contains("preimage") || e.Contains("opennode"))
            return ("no_preimage",
                "The wallet paid but did not return a preimage, which L402 requires. OpenNode cannot do L402 — switch to " +
                "NWC (CoinOS, Alby Hub, CLINK), LND, or Strike.");
        if (e.Contains("insufficient") || e.Contains("balance") || e.Contains("funds"))
            return ("insufficient_funds",
                "The wallet has too little balance to pay ~1 sat. Fund it with a small amount and retry.");
        if (e.Contains("budget") || e.Contains("limit") || e.Contains("exceed"))
            return ("budget_block",
                "The MCP budget blocked even this 1-sat payment. Loosen the auto-approve tier / per-payment limit in " +
                "~/.lightning-enable/config.json.");
        if (e.Contains("timeout") || e.Contains("timed out") || e.Contains("connection") || e.Contains("resolve"))
            return ("network",
                "Could not reach the wallet or the test endpoint. Check the wallet connection (is the NWC relay reachable?) " +
                "and your network, then retry.");
        return ("unknown",
            "Check the error message above. Confirm a preimage-returning wallet (NWC, LND, or Strike) is connected and funded, then retry.");
    }

    /// <summary>Resolves the hardcoded test path against the configured LE API base.</summary>
    internal static string ResolveTestEndpoint()
    {
        var baseUrl = Environment.GetEnvironmentVariable("LIGHTNING_ENABLE_API_URL");
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.StartsWith("${", StringComparison.Ordinal))
            baseUrl = "https://api.lightningenable.com";
        return baseUrl.TrimEnd('/') + TestPath;
    }
}
