using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for self-bootstrapping Lightning Enable signup.
///
/// An agent with a connected wallet calls this with an email; it activates a
/// Lightning Enable account via the L402 "Fast Lane" and returns the merchant API
/// key. It is an OUT-OF-THE-BOX tool: it requires NO Lightning Enable API key (it
/// CREATES one) — only a wallet that can pay the tiny activation fee (~100 sats).
///
/// The activation POST is routed through the existing <see cref="IL402HttpClient"/>
/// (which handles the 402 → pay → retry-with-Authorization flow) and gated through
/// <see cref="IBudgetService"/> exactly like pay_l402_challenge: above the
/// auto-approve threshold the tool returns requiresConfirmation and prints an
/// out-of-band code to the server console (stderr) that the human — not the model —
/// must relay back. On success the returned apiKey is merged into
/// ~/.lightning-enable/config.json (without clobbering other keys) so the
/// API-key-gated producer/ASA tools unlock.
/// </summary>
[McpServerToolType]
public static class CreateAccountTool
{
    private const string SignupPath = "/api/signup/l402";
    private const string ConfigKey = "lightningEnableApiKey";
    private const string ToolName = "create_lightning_enable_account";

    // Reasonable email shape check (not RFC-perfect) — fail fast before minting an invoice.
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    /// <summary>
    /// Activates a Lightning Enable account with a Lightning micropayment and returns the merchant API key.
    /// </summary>
    [McpServerTool(
        Name = ToolName,
        Title = "Create Lightning Enable account",
        ReadOnly = false,
        Destructive = true)]
    [Description(
        "Self-bootstrapping signup: pay a ~100-sat activation fee for a Lightning Enable "
        + "merchant API key. Needs only a wallet; the key is saved to "
        + "~/.lightning-enable/config.json.")]
    public static async Task<string> CreateLightningEnableAccount(
        [Description("Email to register")] string email,
        [Description("Max sats for the ~100-sat fee")] int maxSats = 1000,
        [Description("Code the human reads off the server console (never returned to you). Omit to request one.")] string? confirmationNonce = null,
        McpServer? server = null,
        IL402HttpClient? l402Client = null,
        IBudgetService? budgetService = null,
        IPaymentHistoryService? paymentHistory = null,
        IBudgetConfigurationService? configService = null,
        CancellationToken cancellationToken = default)
    {
        // Declared outside the try so the catch can report whether a durable receipt
        // landed when the client throws AFTER the activation fee settled.
        PaymentReceiptScope? receiptScope = null;
        try
        {
            // Validate email BEFORE minting any invoice.
            if (string.IsNullOrWhiteSpace(email))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Email is required to create a Lightning Enable account."
                });
            }

            email = email.Trim();
            if (!EmailRegex.IsMatch(email))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"'{email}' is not a valid email address. Provide a real email — the account and API key are tied to it."
                });
            }

            if (l402Client == null)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "No wallet configured. Account activation pays a tiny Lightning fee (~100 sats), so a " +
                            "preimage-capable wallet (LND, NWC, or Strike) is required. Set LND_REST_HOST+LND_MACAROON_HEX, " +
                            "NWC_CONNECTION_STRING, or STRIKE_API_KEY."
                });
            }

            var signupUrl = BuildSignupUrl();

            // Budget gating BEFORE payment (mirrors pay_l402_challenge / settle_agent_service).
            // We gate on maxSats (the ceiling) because the exact fee isn't known until the 402
            // challenge is minted inside FetchWithL402Async; the confirmation is bound to maxSats,
            // this tool, and the signup URL, so a code can't be redirected or reused.
            var (blocked, activationPolicy) = await FirstPartyPaymentGate.CheckAsync(
                new FirstPartyPaymentGateOptions
                {
                    ToolName = ToolName,
                    Destination = signupUrl,
                    MaxSats = maxSats,
                    Label = "Account activation",
                    Noun = "activation",
                    ConfirmationTitle = "ACCOUNT ACTIVATION CONFIRMATION REQUIRED",
                    ConfirmationDescription = $"activation for {email}",
                    ConfirmationSummary = approval =>
                        $"{ToolName} — {approval.AmountUsd:C} ({maxSats:N0} sats), email {email}",
                    HowToConfirm = "Ask the human operator for the confirmation code shown in the server console, then call " +
                                   "create_lightning_enable_account(email=\"...\", confirmationNonce=\"<code-from-human>\").",
                    ElicitationMessage = approval =>
                        "Lightning Enable Account Activation\n\n" +
                        $"Email: {email}\n" +
                        $"Fee: up to {approval.AmountUsd:C} ({approval.AmountSats:N0} sats)\n\n" +
                        "Authorize this activation payment?",
                    AutoApproveLogSuffix = $" for {email}"
                },
                confirmationNonce, server, budgetService, cancellationToken);
            if (blocked != null)
            {
                return blocked;
            }

            // Ambient payment intent: the durable receipt is written at the wallet seam
            // (ReceiptRecordingWalletService) when the activation fee is paid.
            receiptScope = PaymentReceiptScope.Begin(
                "l402", context: "l402_fastlane_signup", policy: activationPolicy);

            // Execute the L402 signup flow: POST {email} -> 402 -> pay -> retry POST.
            // Spend + payment history are recorded inside FetchWithL402Async (the client),
            // so the tool does NOT record again (avoids double-counting — same delegation
            // pattern as pay_l402_challenge).
            // This is the ONE POST in the paid-HTTP surface. It goes through the explicit,
            // internal first-party path (fixed operator-configured origin, never a tool
            // argument) — NOT the generic FetchWithL402Async, which is GET/HEAD only (A3).
            var body = JsonSerializer.Serialize(new { email });
            var fetch = await l402Client.PostFirstPartyAsync(
                signupUrl,
                body, // L402HttpClient sends it as application/json
                maxSats,
                cancellationToken);

            if (!fetch.Success)
            {
                // Scrub any credential-shaped tokens and cap the length — the L402 client's
                // error can embed the full (untruncated) server response body.
                var scrubbedError = Scrub(fetch.ErrorMessage ?? "Account activation failed.");

                // PAID BUT ACTIVATION FAILED — the activation fee left the wallet (or is in
                // flight) yet no account came back. Mirrors Python's paid-but-retry-failed
                // branch and this repo's own AccessL402ResourceTool.
                //
                // The spend is ALREADY recorded inside L402HttpClient, so — unlike Python,
                // where the tool records it — we must NOT record again here or the budget
                // double-counts. Budget accounting was never the gap: the AGENT-facing signal
                // was. Without `paid`/`amountSats` and an explicit do-not-retry, the agent
                // reads a retryable-sounding hint and pays the fee again on every attempt.
                //
                // This fires for EVERY failure status, not just 409: a duplicate email (409),
                // a pending payment, and a settled-without-preimage payment (both 402) all
                // spend real sats.
                //
                // Belt-and-braces: the wallet seam's receipt write is independent evidence of a
                // payment, so it counts even when the client reported no amount — then no amount
                // is claimed, only that a payment may have been taken.
                if (fetch.PaidAmountSats > 0 || receiptScope.ReceiptWritten != null)
                {
                    var amountKnown = fetch.PaidAmountSats > 0;
                    var paidRedirect = fetch.RedirectLocation != null;
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        // The client's paid-redirect text tells the caller to retry with the
                        // returned token — this tool never returns the token, so replace it.
                        error = paidRedirect
                            ? "The account activation was paid, but the server answered with a redirect instead of " +
                              "returning the account. Do NOT pay again; contact support@lightningenable.com with the " +
                              "payment hash from your wallet's payment record."
                            : scrubbedError,
                        statusCode = fetch.StatusCode,
                        receipt_written = receiptScope.ReceiptWritten ?? false,
                        activation = amountKnown
                            ? (object)new { paid = true, amountSats = fetch.PaidAmountSats }
                            : new { paid = true },
                        warning = (amountKnown
                                      ? $"A payment of {fetch.PaidAmountSats:N0} sats was taken from your wallet"
                                      : "A payment may have been taken from your wallet") +
                                  " but account activation did not complete. Do NOT re-run this tool or you may pay the " +
                                  $"activation fee again — contact support@lightningenable.com with this email ({email}) to recover the account.",
                        hint = fetch.StatusCode switch
                        {
                            // Do NOT offer "service is unavailable" here — we know money moved and
                            // the server told us why. Suggesting an outage invites the retry loop.
                            409 => "This email already has a Lightning Enable account. The activation fee is charged before " +
                                   "the server checks for an existing account (it mints the challenge without a pre-check so " +
                                   "signup cannot be used to probe which emails are registered). Retrying with this email will " +
                                   "pay again and fail again. Sign in with the existing account, or use a different email.",
                            0 => "The payment went out, but the connection failed before the server's answer arrived, so the " +
                                 "account may or may not have been created. Do not re-pay; contact support with this email.",
                            _ when paidRedirect =>
                                "The activation fee was paid but the server redirected instead of returning the account. " +
                                "Do not re-pay; contact support with this email.",
                            // Do NOT say "check wallet balance" here — the wallet did its job and paid.
                            // That hint contradicts the client's own "do not retry it" guidance.
                            _ => "The activation fee was paid but the account did not activate — the payment may still be in " +
                                 "flight, or the wallet settled it without returning a preimage (L402 needs one to prove " +
                                 "payment). Check the payment status with your wallet provider; do not re-pay."
                        }
                    }, new JsonSerializerOptions { WriteIndented = true });
                }

                // Genuinely unpaid — the client bailed before spending anything (no wallet,
                // budget refusal, unparseable challenge). Here a retry after fixing the wallet
                // or config is the correct advice, and costs nothing.
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = scrubbedError,
                    statusCode = fetch.StatusCode,
                    activation = new
                    {
                        paid = false,
                        amountSats = 0
                    },
                    hint = fetch.StatusCode switch
                    {
                        402 => "The L402 activation challenge could not be completed and no payment was made. Check wallet " +
                               "balance and configuration.",
                        // No HTTP status: a transport or wallet error, not a server refusal. A
                        // wallet that timed out may still settle, so never say "no payment was made".
                        0 => FirstPartyPaymentGate.TransportErrorHint,
                        _ => "The signup endpoint returned an error before any payment was made. The email may already have " +
                             "an account, or the service is unavailable."
                    }
                });
            }

            // Parse the account payload the server returned after payment.
            JsonElement account;
            try
            {
                using var doc = JsonDocument.Parse(fetch.Content ?? "");
                account = doc.RootElement.Clone();
            }
            catch
            {
                // The fee was paid (fetch.Success implies the paid retry landed) — the
                // paid marker + receipt signal must survive even a garbled response body.
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Account activation paid but the server response was not valid JSON.",
                    receipt_written = fetch.PaidAmountSats > 0 ? (bool?)(receiptScope.ReceiptWritten ?? false) : null,
                    activation = new { paid = fetch.PaidAmountSats > 0, amountSats = fetch.PaidAmountSats }
                });
            }

            var apiKey = FirstPartyPaymentGate.GetString(account,"apiKey");
            if (string.IsNullOrEmpty(apiKey))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Account activation completed but no apiKey was returned by the server.",
                    receipt_written = fetch.PaidAmountSats > 0 ? (bool?)(receiptScope.ReceiptWritten ?? false) : null,
                    activation = new { paid = fetch.PaidAmountSats > 0, amountSats = fetch.PaidAmountSats }
                });
            }

            // Self-bootstrapping payoff: persist the key so the API-key-gated
            // producer/ASA tools pick it up (merge, don't clobber).
            var configPath = configService?.ConfigFilePath ?? DefaultConfigPath();
            var (configOk, configErr) = MergeApiKeyIntoConfig(configPath, apiKey);

            return JsonSerializer.Serialize(new
            {
                success = true,
                // Top level for consistency with every other payment tool's result shape.
                receipt_written = fetch.PaidAmountSats > 0 ? (bool?)(receiptScope.ReceiptWritten ?? false) : null,
                apiKey,
                merchantId = FirstPartyPaymentGate.GetString(account,"merchantId"),
                email = FirstPartyPaymentGate.GetString(account,"email") ?? email,
                planTier = FirstPartyPaymentGate.GetString(account,"planTier"),
                subscriptionStatus = FirstPartyPaymentGate.GetString(account,"subscriptionStatus"),
                trialEndsAt = FirstPartyPaymentGate.GetString(account,"trialEndsAt"),
                dashboardUrl = FirstPartyPaymentGate.GetString(account,"dashboardUrl"),
                activation = new
                {
                    paid = fetch.PaidAmountSats > 0,
                    amountSats = fetch.PaidAmountSats
                },
                config = new
                {
                    written = configOk,
                    path = configPath,
                    key = ConfigKey,
                    error = configOk ? null : configErr
                },
                message = configOk
                    ? $"Lightning Enable account activated. Your API key has been saved to {configPath} — restart the MCP " +
                      "server to unlock the producer and agent-marketplace tools (l402_producer and " +
                      "agent-to-agent commerce tools). After the restart, l402_producer action=configure_receive " +
                      "points payouts at your own wallet, and action=status shows what is still missing."
                    : "Lightning Enable account activated. Save the API key above (config write failed) — set it as " +
                      "LIGHTNING_ENABLE_API_KEY or lightningEnableApiKey in ~/.lightning-enable/config.json to unlock the producer/ASA tools."
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            // Scrub credential-shaped tokens from the exception message before it
            // reaches the model — never let a key/secret leak into an error string.
            // receipt_written: null = nothing was paid; true/false = the activation fee
            // moved (the client can throw after settlement) and the receipt did/didn't land —
            // and then the result also carries activation.paid + a do-not-re-run warning.
            return FirstPartyPaymentGate.ExceptionResult(
                $"Error creating Lightning Enable account: {Scrub(ex.Message)}",
                receiptScope,
                $"this email ({email}) and the time of this call");
        }
        finally
        {
            receiptScope?.Dispose();
        }
    }

    // Credential-shaped token patterns to redact from model-visible error strings
    // (mirrors the Python sanitize_error set; plus the le_live_/le_test_ API key shape).
    private static readonly Regex[] CredentialPatterns =
    {
        new(@"Bearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"shpat_\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"sk_live_\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"sk_test_\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"le_(?:live|test)_\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    /// <summary>
    /// Redacts credential-shaped tokens and caps length so a server error body /
    /// exception message can never leak a key into a model-visible error string.
    /// </summary>
    internal static string Scrub(string? message, int maxLen = 200)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;

        var scrubbed = message;
        foreach (var pattern in CredentialPatterns)
            scrubbed = pattern.Replace(scrubbed, "[REDACTED]");

        if (scrubbed.Length > maxLen)
            scrubbed = scrubbed.Substring(0, maxLen) + "...";

        return scrubbed;
    }

    // Same operator-configured origin the client's first-party POST path validates against.
    private static string BuildSignupUrl() => $"{FirstPartyOrigin.ResolveApiBaseUrl()}{SignupPath}";

    private static string DefaultConfigPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".lightning-enable",
        "config.json");

    /// <summary>
    /// Merges the API key into the config file without clobbering other keys
    /// (wallets, tiers, limits, ...). Best-effort: a write failure must NOT fail
    /// the signup — the apiKey is still returned in the tool result.
    /// </summary>
    internal static (bool ok, string? error) MergeApiKeyIntoConfig(string configPath, string apiKey)
    {
        try
        {
            var dir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            JsonObject root;
            if (File.Exists(configPath))
            {
                var existing = File.ReadAllText(configPath);
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    // Non-empty existing file: only merge if it parses to a JSON object.
                    // If it's malformed or not an object, DO NOT overwrite it — that would
                    // destroy the user's other secrets (wallet creds, budget limits). Return
                    // the key in the tool result instead so it can be saved by hand.
                    JsonNode? parsed;
                    try
                    {
                        parsed = JsonNode.Parse(existing);
                    }
                    catch
                    {
                        return (false, $"existing config is unparseable; not overwriting it — save the API key manually as '{ConfigKey}' in {configPath}.");
                    }

                    if (parsed is JsonObject obj)
                    {
                        root = obj;
                    }
                    else
                    {
                        return (false, $"existing config is not a JSON object; not overwriting it — save the API key manually as '{ConfigKey}' in {configPath}.");
                    }
                }
                else
                {
                    // Genuinely empty/whitespace file → safe to write fresh.
                    root = new JsonObject();
                }
            }
            else
            {
                root = new JsonObject();
            }

            root[ConfigKey] = apiKey;

            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);

            // Best-effort permission tightening (config can hold wallet creds).
            try { BudgetConfigurationService.RestrictFilePermissions(configPath); }
            catch { /* non-fatal */ }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
