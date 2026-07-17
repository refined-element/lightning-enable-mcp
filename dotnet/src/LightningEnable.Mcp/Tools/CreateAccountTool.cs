using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Protocol;
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
    private const string DefaultBaseUrl = "https://api.lightningenable.com";
    private const string ConfigKey = "lightningEnableApiKey";
    private const string ToolName = "create_lightning_enable_account";

    // Reasonable email shape check (not RFC-perfect) — fail fast before minting an invoice.
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    /// <summary>
    /// Activates a Lightning Enable account with a Lightning micropayment and returns the merchant API key.
    /// </summary>
    [McpServerTool(Name = ToolName), Description(
        "Self-bootstrapping signup: activate a Lightning Enable account with a tiny Lightning payment " +
        "(~100 sats) and get back a merchant API key. Requires NO Lightning Enable API key (it CREATES one) " +
        "— only a connected wallet. On success the API key is saved to ~/.lightning-enable/config.json so the " +
        "producer/ASA tools unlock. Above-threshold activation fees require a human-supplied confirmation code " +
        "(same out-of-band flow as pay_l402_challenge).")]
    public static async Task<string> CreateLightningEnableAccount(
        [Description("Email address to register the Lightning Enable account under")] string email,
        [Description("Maximum satoshis to pay for activation. Defaults to 1000; the fee is ~100 sats")] int maxSats = 1000,
        [Description("Confirmation code the human read from the server console, for an above-threshold activation fee. The code is NEVER in a tool result — ask the human for it. Omit on the first call to request one.")] string? confirmationNonce = null,
        McpServer? server = null,
        IL402HttpClient? l402Client = null,
        IBudgetService? budgetService = null,
        IPaymentHistoryService? paymentHistory = null,
        IBudgetConfigurationService? configService = null,
        CancellationToken cancellationToken = default)
    {
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
            if (budgetService != null)
            {
                var approval = await budgetService.CheckApprovalLevelAsync(maxSats, cancellationToken);

                if (approval.Level == ApprovalLevel.Deny)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Account activation denied by budget policy: {approval.DenialReason}",
                        budget = new
                        {
                            amountSats = maxSats,
                            amountUsd = approval.AmountUsd,
                            remainingSessionUsd = approval.RemainingSessionBudgetUsd
                        }
                    });
                }

                if (approval.RequiresConfirmation)
                {
                    if (!string.IsNullOrWhiteSpace(confirmationNonce))
                    {
                        var confirmation = budgetService.ValidateAndConsumeConfirmation(
                            confirmationNonce.Trim().ToUpperInvariant(), maxSats, ToolName, signupUrl);
                        if (confirmation == null)
                        {
                            return JsonSerializer.Serialize(new
                            {
                                success = false,
                                error = "Confirmation code is invalid, expired, already used, or does not match THIS " +
                                        "activation's amount, tool, and destination. Codes are bound to the exact amount, " +
                                        "tool, and destination approved — a code cannot be redirected.",
                                message = "The code may have expired (2-minute limit), been used already, or been issued " +
                                          "for a different amount/tool. Request a new one by calling " +
                                          "create_lightning_enable_account without a confirmationNonce."
                            });
                        }

                        Console.Error.WriteLine($"[Lightning Enable] Account activation of {approval.AmountUsd:C} confirmed via nonce {confirmation.Nonce}");
                    }
                    else
                    {
                        var elicitationConfirmed = await RequestActivationConfirmationAsync(
                            server, approval, email, cancellationToken);

                        if (!elicitationConfirmed)
                        {
                            var pending = budgetService.CreatePendingConfirmation(
                                maxSats, approval.AmountUsd, ToolName, $"activation for {email}", signupUrl);

                            // OUT-OF-BAND CONFIRMATION: code to STDERR only (human sees the server
                            // console/logs; the model only sees tool results). The code MUST NOT
                            // appear in the result — that is what stops a prompt-injected agent
                            // from reading it and self-approving.
                            Console.Error.WriteLine(
                                "[Lightning Enable] *** ACCOUNT ACTIVATION CONFIRMATION REQUIRED ***\n" +
                                $"  create_lightning_enable_account — {approval.AmountUsd:C} ({maxSats:N0} sats), email {email}\n" +
                                $"  Confirmation code: {pending.Nonce}\n" +
                                "  To approve, give this code to the agent. Expires in 120s.");

                            return JsonSerializer.Serialize(new
                            {
                                success = false,
                                requiresConfirmation = true,
                                error = "Account activation requires human confirmation",
                                message = $"This activation may cost up to {approval.AmountUsd:C} ({maxSats:N0} sats), above the " +
                                          "auto-approve threshold. A confirmation code was printed to the server console/logs — " +
                                          "visible to the human operator, NOT to you. Ask the human to read that code and give it to you.",
                                howToConfirm = "Ask the human operator for the confirmation code shown in the server console, then call " +
                                               "create_lightning_enable_account(email=\"...\", confirmationNonce=\"<code-from-human>\").",
                                expiresInSeconds = 120,
                                amount = new
                                {
                                    maxSats,
                                    usd = Math.Round(approval.AmountUsd, 2)
                                }
                            });
                        }
                    }
                }

                if (approval.Level == ApprovalLevel.LogAndApprove)
                {
                    Console.Error.WriteLine($"[Lightning Enable] Auto-approved account activation: {approval.AmountUsd:C} ({maxSats} sats) for {email}");
                }
            }

            // Execute the L402 signup flow: POST {email} -> 402 -> pay -> retry POST.
            // Spend + payment history are recorded inside FetchWithL402Async (the client),
            // so the tool does NOT record again (avoids double-counting — same delegation
            // pattern as pay_l402_challenge).
            var body = JsonSerializer.Serialize(new { email });
            var fetch = await l402Client.FetchWithL402Async(
                signupUrl,
                "POST",
                null, // headers — L402HttpClient defaults body content-type to application/json
                body,
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
                if (fetch.PaidAmountSats > 0)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = scrubbedError,
                        statusCode = fetch.StatusCode,
                        activation = new
                        {
                            paid = true,
                            amountSats = fetch.PaidAmountSats
                        },
                        warning = $"A payment of {fetch.PaidAmountSats:N0} sats was taken from your wallet but account " +
                                  "activation did not complete. Do NOT re-run this tool or you may pay the activation " +
                                  $"fee again — contact support@lightningenable.com with this email ({email}) to recover the account.",
                        hint = fetch.StatusCode == 409
                            // Do NOT offer "service is unavailable" here — we know money moved and
                            // the server told us why. Suggesting an outage invites the retry loop.
                            ? "This email already has a Lightning Enable account. The activation fee is charged before " +
                              "the server checks for an existing account (it mints the challenge without a pre-check so " +
                              "signup cannot be used to probe which emails are registered). Retrying with this email will " +
                              "pay again and fail again. Sign in with the existing account, or use a different email."
                            // Do NOT say "check wallet balance" here — the wallet did its job and paid.
                            // That hint contradicts the client's own "do not retry it" guidance.
                            : "The activation fee was paid but the account did not activate — the payment may still be in " +
                              "flight, or the wallet settled it without returning a preimage (L402 needs one to prove " +
                              "payment). Check the payment status with your wallet provider; do not re-pay."
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
                    hint = fetch.StatusCode == 402
                        ? "The L402 activation challenge could not be completed and no payment was made. Check wallet " +
                          "balance and configuration."
                        : "The signup endpoint returned an error before any payment was made. The email may already have " +
                          "an account, or the service is unavailable."
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
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Account activation paid but the server response was not valid JSON.",
                    amountSats = fetch.PaidAmountSats
                });
            }

            var apiKey = GetString(account, "apiKey");
            if (string.IsNullOrEmpty(apiKey))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Account activation completed but no apiKey was returned by the server.",
                    amountSats = fetch.PaidAmountSats
                });
            }

            // Self-bootstrapping payoff: persist the key so the API-key-gated
            // producer/ASA tools pick it up (merge, don't clobber).
            var configPath = configService?.ConfigFilePath ?? DefaultConfigPath();
            var (configOk, configErr) = MergeApiKeyIntoConfig(configPath, apiKey);

            return JsonSerializer.Serialize(new
            {
                success = true,
                apiKey,
                merchantId = GetString(account, "merchantId"),
                email = GetString(account, "email") ?? email,
                planTier = GetString(account, "planTier"),
                subscriptionStatus = GetString(account, "subscriptionStatus"),
                trialEndsAt = GetString(account, "trialEndsAt"),
                dashboardUrl = GetString(account, "dashboardUrl"),
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
                      "server to unlock the producer/ASA tools (create_l402_challenge, verify_l402_payment, and the " +
                      "agent-to-agent commerce tools)."
                    : "Lightning Enable account activated. Save the API key above (config write failed) — set it as " +
                      "LIGHTNING_ENABLE_API_KEY or lightningEnableApiKey in ~/.lightning-enable/config.json to unlock the producer/ASA tools."
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            // Scrub credential-shaped tokens from the exception message before it
            // reaches the model — never let a key/secret leak into an error string.
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Error creating Lightning Enable account: {Scrub(ex.Message)}"
            });
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

    private static string BuildSignupUrl()
    {
        var baseUrl = Environment.GetEnvironmentVariable("LIGHTNING_ENABLE_API_URL")?.TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl) || baseUrl.StartsWith("${"))
            baseUrl = DefaultBaseUrl;
        return $"{baseUrl}{SignupPath}";
    }

    private static string DefaultConfigPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".lightning-enable",
        "config.json");

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

    /// <summary>
    /// Requests user confirmation for an above-threshold activation fee via MCP
    /// elicitation. Returns false when elicitation is unavailable (most clients),
    /// so the caller falls back to the out-of-band nonce flow.
    /// </summary>
    private static async Task<bool> RequestActivationConfirmationAsync(
        McpServer? server,
        ApprovalCheckResult approval,
        string email,
        CancellationToken cancellationToken)
    {
        if (server?.ClientCapabilities?.Elicitation == null)
        {
            Console.Error.WriteLine($"[Lightning Enable] Account activation of {approval.AmountUsd:C} requires confirmation but elicitation not supported by client");
            return false;
        }

        try
        {
            var schema = new ElicitRequestParams.RequestSchema
            {
                Properties =
                {
                    ["approved"] = new ElicitRequestParams.BooleanSchema
                    {
                        Description = "Set to true to approve this Lightning Enable account activation payment"
                    }
                }
            };

            var response = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = $"Lightning Enable Account Activation\n\n" +
                          $"Email: {email}\n" +
                          $"Fee: up to {approval.AmountUsd:C} ({approval.AmountSats:N0} sats)\n\n" +
                          $"Authorize this activation payment?",
                RequestedSchema = schema
            }, cancellationToken);

            if (response.Action == "accept" &&
                response.Content?.TryGetValue("approved", out var approvedElement) == true)
            {
                return approvedElement.ValueKind == JsonValueKind.True;
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Lightning Enable] Account activation elicitation failed: {ex.Message}");
            return false;
        }
    }
}
