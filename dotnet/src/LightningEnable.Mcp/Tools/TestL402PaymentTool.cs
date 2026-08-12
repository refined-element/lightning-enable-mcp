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
/// SSRF surface. It then translates the raw result into a plain pass/fail verdict.
///
/// Interpretation checks the <b>structured</b> signals the delegated path emits
/// (a completed payment, a confirmation requirement, a budget denial) before
/// falling back to matching the error string, so a real payment is never reported
/// as a broken wallet and a rate-limit is never mistaken for a budget block.
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
    internal const int MaxTestSats = 10;

    [McpServerTool(Name = "test_l402_payment"), Description(
        "Self-test the Lightning wallet by paying the public 1-sat L402 test endpoint end to end. "
        + "Proves the wallet is connected, returns a preimage, and can complete an L402 payment. "
        + "Costs about 1 satoshi. Use this to verify setup or answer 'is my wallet actually working?'. "
        + "If your budget config requires confirmation for this amount, the verdict is 'needs_confirmation' "
        + "and the server prints a code to its console — re-run with confirmationNonce set to that code.")]
    public static async Task<string> TestL402Payment(
        [Description("Confirmation code the human read from the server console, if a prior call returned "
            + "test='needs_confirmation'. Omit on the first call.")] string? confirmationNonce = null,
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
            confirmationNonce: confirmationNonce,
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
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            root = doc.RootElement.Clone();
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

        var success = root.TryGetProperty("success", out var s) && s.GetBoolean();

        // Underlying access_l402_resource result reports whether the durable receipt
        // landed; pass it through on the paid verdicts (a self-test moves real sats).
        bool? receiptWritten = root.TryGetProperty("receipt_written", out var rw)
            && rw.ValueKind != JsonValueKind.Null ? rw.GetBoolean() : null;

        if (success)
        {
            var paidOk = root.TryGetProperty("payment", out var p)
                && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("paid", out var pd) && pd.GetBoolean();
            long sats = paidOk && p.TryGetProperty("amountSats", out var amt) ? amt.GetInt64() : 0;
            int status = root.TryGetProperty("statusCode", out var sc) ? sc.GetInt32() : 200;

            if (paidOk)
            {
                return Passed(sats, status, endpoint, receiptWritten);
            }

            // 200 without a payment: the test endpoint should always issue a 402,
            // so the L402 flow was NOT exercised — this is not a proven pass.
            return JsonSerializer.Serialize(new
            {
                success = false,
                test = "inconclusive",
                message = $"Endpoint returned {status} without requiring payment, so the L402 flow was not "
                    + "exercised — the wallet is unproven. Retry, or verify the test endpoint.",
                endpoint,
                statusCode = status,
                walletWorking = (bool?)null
            });
        }

        // ---- Failure branch: read STRUCTURED signals before the error string. ----

        // (1) The payment needs human confirmation — a healthy wallet, not a failure.
        if (root.TryGetProperty("requiresConfirmation", out var rc) && rc.GetBoolean())
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                test = "needs_confirmation",
                message = "Your budget config requires human confirmation for this payment. A confirmation "
                    + "code was printed to the server console/logs (visible to the operator, not to the agent). "
                    + "Re-run test_l402_payment with confirmationNonce set to that code to finish the test.",
                howToConfirm = root.TryGetProperty("howToConfirm", out var hc) ? hc.GetString() : null,
                endpoint,
                walletWorking = (bool?)null
            });
        }

        // (2) Split-flow: the payment SUCCEEDED (preimage minted) but the post-payment
        // retry returned non-200. For a wallet self-test that IS a pass — the wallet
        // completed an L402 payment, which is exactly what this checks.
        if (root.TryGetProperty("payment", out var fp)
            && fp.ValueKind == JsonValueKind.Object
            && fp.TryGetProperty("paid", out var fpd) && fpd.GetBoolean())
        {
            long sats = fp.TryGetProperty("amountSats", out var fa) ? fa.GetInt64() : 0;
            return JsonSerializer.Serialize(new
            {
                success = true,
                receipt_written = receiptWritten,
                test = "passed",
                message = $"✅ Wallet works — paid {sats} sat(s) and the preimage verified. (The test "
                    + "endpoint's post-payment response wasn't a clean 200, but your wallet completed the "
                    + "L402 payment, which is what this checks.)",
                endpoint,
                amountSats = sats,
                walletWorking = true
            });
        }

        // (3) Budget denial has a dedicated shape (a "budget" object beside the error);
        // detect it structurally so the specific denial reason is surfaced verbatim.
        if (root.TryGetProperty("budget", out _))
        {
            var denyMsg = root.TryGetProperty("error", out var be) ? be.GetString() : "budget policy";
            return JsonSerializer.Serialize(new
            {
                success = false,
                test = "failed",
                reason = "budget_block",
                message = $"❌ L402 self-test blocked by budget: {denyMsg}",
                howToFix = "The MCP budget denied even this ~1-sat payment. Raise the relevant cap in "
                    + "~/.lightning-enable/config.json (the message above names which limit was hit).",
                endpoint,
                walletWorking = false
            });
        }

        // (3.5) Idempotency: the same 1-sat invoice was already paid moments ago
        // (L402 reuses it for ~60s to prevent double-charges). NOT a wallet failure —
        // it's evidence a prior payment succeeded; the wallet is fine.
        var errPeek = (root.TryGetProperty("error", out var epk) ? epk.GetString() : "") ?? "";
        var errLow = errPeek.ToLowerInvariant();
        if (errLow.Contains("already") && errLow.Contains("paid"))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                test = "inconclusive",
                message = "The 1-sat test invoice was already paid moments ago — L402 reuses the same "
                    + "invoice for ~60 seconds to prevent double-charges. Your wallet is fine; wait a "
                    + "minute and run the test again for a fresh payment.",
                endpoint,
                walletWorking = (bool?)null
            });
        }

        // (4) Otherwise diagnose from the error string.
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

    private static string Passed(long sats, int status, string endpoint, bool? receiptWritten = null) =>
        JsonSerializer.Serialize(new
        {
            success = true,
            test = "passed",
            message = $"✅ L402 works end to end. Paid {sats} sat(s), preimage verified, endpoint returned "
                + $"{status}. Your wallet is ready to pay L402 resources anywhere.",
            endpoint,
            amountSats = sats,
            statusCode = status,
            receipt_written = receiptWritten,
            walletWorking = true
        });

    /// <summary>
    /// Maps a raw error string to a stable reason code + a plain-language fix.
    /// Order matters: the most specific / most-often-confused buckets are checked
    /// first (rate-limit before budget; network before preimage) so an error that
    /// contains an overlapping word isn't misclassified. Budget denials and
    /// confirmation requirements are handled structurally in Interpret, not here.
    /// </summary>
    internal static (string reason, string fix) Diagnose(string error)
    {
        var e = (error ?? string.Empty).ToLowerInvariant();

        // A blank error usually means a client-side timeout / dropped connection
        // (e.g. an HTTP ReadTimeout stringifies to empty) — surface that, not "unknown".
        if (string.IsNullOrWhiteSpace(e))
            return ("network",
                "The request failed without a specific error — usually a timeout or a dropped connection. "
                + "Check your network / the NWC relay is reachable, then retry.");

        if (e.Contains("not configured") || e.Contains("no wallet") || e.Contains("not initialized"))
            return ("no_wallet",
                "No Lightning wallet is configured. Set NWC_CONNECTION_STRING (CoinOS/Alby Hub/CLINK), STRIKE_API_KEY, or LND "
                + "credentials — or add a wallet to ~/.lightning-enable/config.json. NWC and Strike both work for L402.");

        // Before budget: "Rate limit exceeded" contains "limit"/"exceed" but is NOT a budget issue.
        if (e.Contains("rate limit"))
            return ("rate_limited",
                "You've hit the request rate limit (shared with access_l402_resource). Wait for the window to reset "
                + "(about a minute) and retry — this is not a budget or wallet problem.");

        // Before preimage: a network blip whose text mentions "preimage" is still a network issue.
        if (e.Contains("timeout") || e.Contains("timed out") || e.Contains("unreachable")
            || e.Contains("network") || e.Contains("resolve") || e.Contains("connect"))
            return ("network",
                "Could not reach the wallet or the test endpoint. Check the wallet connection (is the NWC relay reachable?) "
                + "and your network, then retry.");

        if (e.Contains("opennode") || e.Contains("preimage"))
            return ("no_preimage",
                "The wallet paid but did not return a preimage, which L402 requires. OpenNode cannot do L402 — switch to "
                + "NWC (CoinOS, Alby Hub, CLINK), LND, or Strike.");

        // Only "insufficient" — not bare "balance"/"funds", which also appear in
        // balance-*fetch* failures ("unable to retrieve balance").
        if (e.Contains("insufficient"))
            return ("insufficient_funds",
                "The wallet has too little balance to pay ~1 sat. Fund it with a small amount and retry.");

        // Fallback for any budget wording that reaches here (the structured path in
        // Interpret handles the normal budget-deny shape).
        if (e.Contains("budget"))
            return ("budget_block",
                "The MCP budget blocked even this 1-sat payment. Loosen the auto-approve tier / per-payment limit in "
                + "~/.lightning-enable/config.json.");

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
