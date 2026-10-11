using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool that pays for the merchant's own Lightning Enable subscription.
///
/// POSTs to <c>/api/merchant/subscription/renew</c> with the configured merchant API key
/// (<c>X-Api-Key</c>), pays the fixed-sats L402 challenge the server returns, and retries
/// with the credential — which buys or extends one period of the Agentic Commerce plan.
/// The price is set by the server; <c>maxSats</c> is only the ceiling this call may pay.
///
/// Mirrors <see cref="CreateAccountTool"/>: the same budget gate, the same out-of-band
/// confirmation (the code goes to the human on the configured approval channel, never into
/// this result), the same durable receipt scope, and the same paid-but-failed signalling so
/// an agent never pays twice by retrying. Differences: it needs the merchant API key (it
/// pays for an EXISTING account), and that key must never appear in any result — the server
/// error body and exception messages are redacted for the exact configured key as well as
/// for credential-shaped tokens.
/// </summary>
[McpServerToolType]
public static class RenewSubscriptionTool
{
    private const string RenewPath = "/api/merchant/subscription/renew";
    private const string ToolName = "renew_lightning_enable_subscription";
    private const string Redacted = "[REDACTED]";

    // Server error codes are snake_case identifiers; anything else is not surfaced as a code.
    private static readonly Regex ErrorCodeShape = new(@"^[a-z0-9_]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex PreimageShape = new(@"^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// Pays the subscription renewal challenge for the configured API key's account.
    /// </summary>
    [McpServerTool(
        Name = ToolName,
        Title = "Renew Lightning Enable subscription",
        ReadOnly = false,
        Destructive = true)]
    [Description(
        "Pay the L402 invoice that buys or extends 30 days of Lightning Enable Agentic "
        + "Commerce for the configured API key. Needs a preimage-capable wallet.")]
    public static async Task<string> RenewLightningEnableSubscription(
        [Description("Max sats to pay")] int maxSats = 50_000,
        [Description("Code the human reads off the server console. Omit to request one.")] string? confirmationNonce = null,
        McpServer? server = null,
        IL402HttpClient? l402Client = null,
        IBudgetService? budgetService = null,
        ILightningEnableApiService? apiService = null,
        CancellationToken cancellationToken = default)
    {
        // Captured once, before anything can fail, so every error path below — including the
        // catch — can redact it. Never logged, never serialized.
        var apiKey = apiService?.ApiKey;

        // Declared outside the try so the catch can report whether a durable receipt
        // landed when the client throws AFTER the renewal settled.
        PaymentReceiptScope? receiptScope = null;
        try
        {
            // The client does NOT refuse a blank key (it would send an empty X-Api-Key), so
            // the gate lives here, before any invoice is minted.
            if (apiService == null || !apiService.IsConfigured || string.IsNullOrWhiteSpace(apiKey))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = LightningEnableApiService.ProducerApiKeyRequired
                });
            }

            if (l402Client == null)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "No wallet configured. Renewing pays a Lightning invoice, so a " +
                            "preimage-capable wallet (LND, NWC, or Strike) is required. Set LND_REST_HOST+LND_MACAROON_HEX, " +
                            "NWC_CONNECTION_STRING, or STRIKE_API_KEY."
                });
            }

            var renewUrl = BuildRenewUrl();

            // Budget gating BEFORE payment. Gated on maxSats (the ceiling) because the exact
            // price isn't known until the 402 is minted inside the client; the confirmation is
            // bound to maxSats, this tool, and the renew URL, so a code can't be redirected.
            string? renewalPolicy = null;
            if (budgetService != null)
            {
                var approval = await budgetService.CheckApprovalLevelAsync(maxSats, cancellationToken);
                renewalPolicy = PaymentPolicy.Label(approval.Level);

                if (approval.Level == ApprovalLevel.Deny)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Subscription renewal denied by budget policy: {approval.DenialReason}",
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
                            confirmationNonce.Trim().ToUpperInvariant(), maxSats, ToolName, renewUrl);
                        if (confirmation == null)
                        {
                            return JsonSerializer.Serialize(new
                            {
                                success = false,
                                error = "Confirmation code is invalid, expired, already used, or does not match THIS " +
                                        "renewal's amount, tool, and destination. Codes are bound to the exact amount, " +
                                        "tool, and destination approved — a code cannot be redirected.",
                                message = "The code may have expired (codes are short-lived), been used already, or been issued " +
                                          "for a different amount/tool. Request a new one by calling " +
                                          $"{ToolName} without a confirmationNonce."
                            });
                        }

                        Console.Error.WriteLine($"[Lightning Enable] Subscription renewal of {approval.AmountUsd:C} confirmed by the operator's code");
                    }
                    else
                    {
                        var elicitationConfirmed = await RequestRenewalConfirmationAsync(
                            server, approval, cancellationToken);

                        if (!elicitationConfirmed)
                        {
                            // OUT-OF-BAND CONFIRMATION: the code goes to the human on the CONFIGURED
                            // approval channel (stderr locally; webhook/file/refuse when hosted) and
                            // never into this result, so an injected agent can't self-approve.
                            var dispatch = await budgetService.RequestConfirmationAsync(new ConfirmationRequest
                            {
                                AmountSats = maxSats,
                                AmountUsd = approval.AmountUsd,
                                ToolName = ToolName,
                                Description = "Lightning Enable subscription renewal",
                                Destination = renewUrl,
                                Title = "SUBSCRIPTION RENEWAL CONFIRMATION REQUIRED",
                                Summary = $"{ToolName} — up to {approval.AmountUsd:C} ({maxSats:N0} sats)"
                            }, cancellationToken);

                            if (!dispatch.Delivered)
                            {
                                return JsonSerializer.Serialize(new
                                {
                                    success = false,
                                    requiresConfirmation = false,
                                    confirmationChannel = dispatch.ChannelName,
                                    error = dispatch.RefusalReason,
                                    message = "The renewal was REFUSED, not queued for approval — no human can be asked for a " +
                                              "code on this server. Retrying will not help until the operator changes the configuration.",
                                    amount = new
                                    {
                                        maxSats,
                                        usd = Math.Round(approval.AmountUsd, 2)
                                    }
                                });
                            }

                            return JsonSerializer.Serialize(new
                            {
                                success = false,
                                requiresConfirmation = true,
                                confirmationChannel = dispatch.ChannelName,
                                error = "Subscription renewal requires human confirmation",
                                message = $"This renewal may cost up to {approval.AmountUsd:C} ({maxSats:N0} sats), above the " +
                                          $"auto-approve threshold. A confirmation code was {dispatch.OperatorHint} — " +
                                          "visible to the human operator, NOT to you. Ask the human to read that code and give it to you.",
                                howToConfirm = "Ask the human operator for the confirmation code, then call " +
                                               $"{ToolName}(confirmationNonce=\"<code-from-human>\") with the same maxSats.",
                                expiresInSeconds = dispatch.ExpiresInSeconds,
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
                    Console.Error.WriteLine($"[Lightning Enable] Auto-approved subscription renewal: up to {approval.AmountUsd:C} ({maxSats} sats)");
                }
            }

            // Ambient payment intent: the durable receipt is written at the wallet seam
            // (ReceiptRecordingWalletService) when the renewal invoice is paid.
            receiptScope = PaymentReceiptScope.Begin(
                "l402", context: "l402_subscription_renewal", policy: renewalPolicy);

            // POST {} -> 402 -> pay -> retry POST with the L402 credential, X-Api-Key on both.
            // Spend + payment history are recorded inside the client, so the tool does NOT
            // record again (same delegation pattern as create_lightning_enable_account).
            var fetch = await l402Client.PostFirstPartyAuthenticatedAsync(
                renewUrl, "{}", apiKey, maxSats, cancellationToken);

            if (!fetch.Success)
            {
                var rawError = RedactKey(fetch.ErrorMessage ?? "Subscription renewal failed.", apiKey);
                var scrubbedError = CreateAccountTool.Scrub(rawError);
                var errorCode = TryExtractErrorCode(rawError);

                // PAID BUT RENEWAL FAILED — the invoice settled (or is in flight) yet no renewal
                // came back. The spend is ALREADY recorded inside the client; do NOT record again.
                // What matters is the agent-facing signal: without `paid` and an explicit
                // do-not-retry, the agent reads a retryable error and pays again.
                if (fetch.PaidAmountSats > 0)
                {
                    var paymentHash = TryDerivePaymentHash(fetch.L402Token);
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = scrubbedError,
                        errorCode,
                        statusCode = fetch.StatusCode,
                        receipt_written = receiptScope.ReceiptWritten ?? false,
                        paymentHash,
                        activation = new
                        {
                            paid = true,
                            amountSats = fetch.PaidAmountSats
                        },
                        warning = PaidButFailedWarning(fetch.PaidAmountSats, paymentHash),
                        hint = fetch.StatusCode == 402
                            // Do NOT say "check wallet balance" — the wallet paid; that contradicts
                            // the client's own "do not retry it" guidance.
                            ? "The renewal invoice was paid but the server did not accept it yet — the payment may still be " +
                              "in flight, or the wallet settled it without returning a preimage (L402 needs one to prove " +
                              "payment). Check the payment status with your wallet provider; do not re-pay."
                            : $"The server answered HTTP {fetch.StatusCode} after the payment (see errorCode and error). " +
                              "Retrying would pay a new invoice; it would not resubmit this one."
                    }, new JsonSerializerOptions { WriteIndented = true });
                }

                // Genuinely unpaid — the server refused before any challenge was paid (Stripe
                // subscriber, Business plan, prepaid cap, disabled account, bad key) or the client
                // bailed before spending (no wallet, ceiling too low, budget refusal).
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = scrubbedError,
                    errorCode,
                    statusCode = fetch.StatusCode,
                    activation = new
                    {
                        paid = false,
                        amountSats = 0
                    },
                    hint = fetch.StatusCode switch
                    {
                        402 => "The renewal challenge could not be completed and no payment was made. Check wallet " +
                               "balance and configuration, and that maxSats covers the quoted price.",
                        401 => "The Lightning Enable API key was rejected and no payment was made. Check " +
                               "LIGHTNING_ENABLE_API_KEY (or lightningEnableApiKey in ~/.lightning-enable/config.json).",
                        _ => "The renewal endpoint refused the request before any payment was made (see errorCode and " +
                             "error). Nothing was charged; retrying will get the same answer until that changes."
                    }
                });
            }

            // Parse the renewal payload the server returned after payment.
            JsonElement renewal;
            try
            {
                using var doc = JsonDocument.Parse(fetch.Content ?? "");
                renewal = doc.RootElement.Clone();
            }
            catch
            {
                // The invoice was paid (fetch.Success implies the paid retry landed) — the paid
                // marker + receipt signal must survive even a garbled response body.
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Subscription renewal paid but the server response was not valid JSON.",
                    receipt_written = fetch.PaidAmountSats > 0 ? (bool?)(receiptScope.ReceiptWritten ?? false) : null,
                    paymentHash = TryDerivePaymentHash(fetch.L402Token),
                    activation = new { paid = fetch.PaidAmountSats > 0, amountSats = fetch.PaidAmountSats },
                    warning = fetch.PaidAmountSats > 0
                        ? PaidButFailedWarning(fetch.PaidAmountSats, TryDerivePaymentHash(fetch.L402Token))
                        : null
                });
            }

            var currentPeriodEnd = GetString(renewal, "currentPeriodEnd");
            if (string.IsNullOrEmpty(currentPeriodEnd))
            {
                var paymentHash = GetString(renewal, "paymentHash") ?? TryDerivePaymentHash(fetch.L402Token);
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Subscription renewal completed but the server returned no currentPeriodEnd.",
                    receipt_written = fetch.PaidAmountSats > 0 ? (bool?)(receiptScope.ReceiptWritten ?? false) : null,
                    paymentHash,
                    activation = new { paid = fetch.PaidAmountSats > 0, amountSats = fetch.PaidAmountSats },
                    warning = fetch.PaidAmountSats > 0
                        ? PaidButFailedWarning(fetch.PaidAmountSats, paymentHash)
                        : null
                });
            }

            var alreadyCredited = GetBool(renewal, "alreadyCredited") ?? false;
            return JsonSerializer.Serialize(new
            {
                success = true,
                receipt_written = fetch.PaidAmountSats > 0 ? (bool?)(receiptScope.ReceiptWritten ?? false) : null,
                currentPeriodEnd,
                paidSats = GetLong(renewal, "paidSats") ?? fetch.PaidAmountSats,
                paymentHash = GetString(renewal, "paymentHash") ?? TryDerivePaymentHash(fetch.L402Token),
                alreadyCredited,
                planTier = GetString(renewal, "planTier"),
                activation = new
                {
                    paid = fetch.PaidAmountSats > 0,
                    amountSats = fetch.PaidAmountSats
                },
                message = alreadyCredited
                    ? $"This payment was already credited; the Lightning Enable subscription is paid through {currentPeriodEnd}."
                    : $"Lightning Enable subscription renewed. It is now paid through {currentPeriodEnd}."
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            // Redact the configured key AND credential-shaped tokens before the message reaches
            // the model. receipt_written: null = nothing was paid; true/false = the renewal
            // moved money (the client can throw after settlement) and the receipt did/didn't land.
            return JsonSerializer.Serialize(new
            {
                success = false,
                receipt_written = receiptScope?.ReceiptWritten,
                error = $"Error renewing Lightning Enable subscription: {CreateAccountTool.Scrub(RedactKey(ex.Message, apiKey))}"
            });
        }
        finally
        {
            receiptScope?.Dispose();
        }
    }

    private static string PaidButFailedWarning(long paidSats, string? paymentHash) =>
        $"A payment of {paidSats:N0} sats was taken from your wallet but the renewal did not complete. Do NOT re-run " +
        "this tool or you may pay again — the server credits each payment once, and this tool cannot resend the same " +
        "credential. Contact support@lightningenable.com with the payment hash" +
        (paymentHash != null ? $" ({paymentHash})." : " from your wallet's payment record.");

    // Same operator-configured origin the client's first-party POST path validates against.
    private static string BuildRenewUrl() => $"{FirstPartyOrigin.ResolveApiBaseUrl()}{RenewPath}";

    /// <summary>Removes every occurrence of the configured API key from a model-visible string.</summary>
    private static string RedactKey(string message, string? apiKey) =>
        string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(message)
            ? message
            : message.Replace(apiKey, Redacted, StringComparison.Ordinal);

    /// <summary>
    /// Pulls the server's <c>error</c> code out of the client's failure text
    /// ("HTTP 409: {json}" / "Request failed after payment: HTTP 409: {json}").
    /// Only a snake_case identifier is returned; anything else is null.
    /// </summary>
    private static string? TryExtractErrorCode(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage)) return null;
        var start = errorMessage.IndexOf('{');
        if (start < 0) return null;

        try
        {
            using var doc = JsonDocument.Parse(errorMessage[start..]);
            var code = GetString(doc.RootElement, "error");
            return code != null && ErrorCodeShape.IsMatch(code) ? code : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The payment hash is SHA256(preimage) — public, and what support needs to find the
    /// payment. Derived from the credential so the preimage itself never leaves this method.
    /// </summary>
    private static string? TryDerivePaymentHash(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var preimage = token[(token.LastIndexOf(':') + 1)..];
        if (!PreimageShape.IsMatch(preimage)) return null;
        return Convert.ToHexString(SHA256.HashData(Convert.FromHexString(preimage))).ToLowerInvariant();
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetLong(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : null;

    private static bool? GetBool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    /// <summary>
    /// Requests confirmation for an above-threshold renewal via MCP elicitation. Returns
    /// false when elicitation is unavailable (most clients), so the caller falls back to the
    /// out-of-band code flow.
    /// </summary>
    private static async Task<bool> RequestRenewalConfirmationAsync(
        McpServer? server,
        ApprovalCheckResult approval,
        CancellationToken cancellationToken)
    {
        if (server?.ClientCapabilities?.Elicitation == null)
        {
            Console.Error.WriteLine($"[Lightning Enable] Subscription renewal of {approval.AmountUsd:C} requires confirmation but elicitation not supported by client");
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
                        Description = "Set to true to approve this Lightning Enable subscription renewal payment"
                    }
                }
            };

            var response = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = "Lightning Enable Subscription Renewal\n\n" +
                          $"Price: up to {approval.AmountUsd:C} ({approval.AmountSats:N0} sats)\n\n" +
                          "Authorize this renewal payment?",
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
            Console.Error.WriteLine($"[Lightning Enable] Subscription renewal elicitation failed: {ex.Message}");
            return false;
        }
    }
}
