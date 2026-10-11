using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using LightningEnable.Mcp.Services;
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
/// Shares the budget / out-of-band confirmation gate and the paid-aware exception result
/// with <see cref="CreateAccountTool"/> through <see cref="FirstPartyPaymentGate"/>: the code
/// goes to the human on the configured approval channel, never into this result. Adds what a
/// renewal needs: the merchant API key (it pays for an EXISTING account), and that key must
/// never appear in any result — the server error body and exception messages are redacted for
/// the exact configured key as well as for credential-shaped tokens.
///
/// Every call starts a fresh challenge: <see cref="L402HttpClient"/> keeps no token cache, so
/// a second call buys a second period rather than replaying the first credential.
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

        // Declared outside the try so the catch can report whether a payment was observed
        // when the client throws AFTER the renewal settled.
        PaymentReceiptScope? receiptScope = null;
        try
        {
            // The client also refuses a blank key, but checking here first gives the agent the
            // actionable "configure your key" message before any invoice is minted.
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
            var (blocked, renewalPolicy) = await FirstPartyPaymentGate.CheckAsync(
                new FirstPartyPaymentGateOptions
                {
                    ToolName = ToolName,
                    Destination = renewUrl,
                    MaxSats = maxSats,
                    Label = "Subscription renewal",
                    Noun = "renewal",
                    ConfirmationTitle = "SUBSCRIPTION RENEWAL CONFIRMATION REQUIRED",
                    ConfirmationDescription = "Lightning Enable subscription renewal",
                    ConfirmationSummary = approval =>
                        $"{ToolName} — up to {approval.AmountUsd:C} ({maxSats:N0} sats)",
                    HowToConfirm = "Ask the human operator for the confirmation code, then call " +
                                   $"{ToolName}(confirmationNonce=\"<code-from-human>\") with the same maxSats.",
                    ElicitationMessage = approval =>
                        "Lightning Enable Subscription Renewal\n\n" +
                        $"Price: up to {approval.AmountUsd:C} ({approval.AmountSats:N0} sats)\n\n" +
                        "Authorize this renewal payment?"
                },
                confirmationNonce, server, budgetService, cancellationToken);
            if (blocked != null)
            {
                return blocked;
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
                //
                // Belt-and-braces: the wallet seam's receipt write is independent evidence of a
                // payment, so it counts even when the client reported no amount (a payment that
                // MAY have settled is never reported as retryable).
                if (fetch.PaidAmountSats > 0 || receiptScope.ReceiptWritten != null)
                {
                    var paymentHash = TryDerivePaymentHash(fetch.L402Token);
                    var paidRedirect = fetch.RedirectLocation != null;
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        // The client's paid-redirect text tells the caller to retry with the
                        // returned token — this tool never returns the token, so replace it.
                        error = paidRedirect
                            ? "The subscription renewal was paid, but the server answered with a redirect instead of " +
                              "confirming it. Do NOT pay again; contact support@lightningenable.com with the payment hash."
                            : scrubbedError,
                        errorCode,
                        statusCode = fetch.StatusCode,
                        receipt_written = receiptScope.ReceiptWritten ?? false,
                        paymentHash,
                        activation = PaidActivation(fetch.PaidAmountSats),
                        warning = PaidButFailedWarning(fetch.PaidAmountSats, paymentHash),
                        hint = fetch.StatusCode switch
                        {
                            // Do NOT say "check wallet balance" — the wallet paid; that contradicts
                            // the client's own "do not retry it" guidance.
                            402 => "The renewal invoice was paid but the server did not accept it yet — the payment may still be " +
                                   "in flight, or the wallet settled it without returning a preimage (L402 needs one to prove " +
                                   "payment). Check the payment status with your wallet provider; do not re-pay.",
                            0 => "The payment went out, but the connection failed before the server's answer arrived, so the " +
                                 "renewal may or may not have been credited. Do not re-pay; contact support with the payment hash.",
                            _ when paidRedirect =>
                                "The renewal was paid but the server redirected instead of confirming it. Do not re-pay; " +
                                "contact support with the payment hash.",
                            _ => $"The server answered HTTP {fetch.StatusCode} after the payment (see errorCode and error). " +
                                 "Retrying would pay a new invoice; it would not resubmit this one."
                        }
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
                        // No HTTP status: a transport or wallet error, not a server refusal. A
                        // wallet that timed out may still settle, so never say "nothing was charged".
                        0 => FirstPartyPaymentGate.TransportErrorHint,
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
                var paymentHash = TryDerivePaymentHash(fetch.L402Token);
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Subscription renewal paid but the server response was not valid JSON.",
                    receipt_written = fetch.PaidAmountSats > 0 ? (bool?)(receiptScope.ReceiptWritten ?? false) : null,
                    paymentHash,
                    activation = new { paid = fetch.PaidAmountSats > 0, amountSats = fetch.PaidAmountSats },
                    warning = fetch.PaidAmountSats > 0
                        ? PaidButFailedWarning(fetch.PaidAmountSats, paymentHash)
                        : null
                });
            }

            var currentPeriodEnd = FirstPartyPaymentGate.GetString(renewal, "currentPeriodEnd");
            if (string.IsNullOrEmpty(currentPeriodEnd))
            {
                var paymentHash = FirstPartyPaymentGate.GetString(renewal, "paymentHash")
                                  ?? TryDerivePaymentHash(fetch.L402Token);
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
                paymentHash = FirstPartyPaymentGate.GetString(renewal, "paymentHash")
                              ?? TryDerivePaymentHash(fetch.L402Token),
                alreadyCredited,
                planTier = FirstPartyPaymentGate.GetString(renewal, "planTier"),
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
            // the model. If the wallet seam observed a payment under this call, the shared result
            // carries activation.paid + a do-not-re-run warning; otherwise the plain error shape.
            return FirstPartyPaymentGate.ExceptionResult(
                $"Error renewing Lightning Enable subscription: {CreateAccountTool.Scrub(RedactKey(ex.Message, apiKey))}",
                receiptScope,
                "the time of this call");
        }
        finally
        {
            receiptScope?.Dispose();
        }
    }

    // Amount unknown (0) when only the receipt seam saw the payment — then no amount is claimed.
    private static object PaidActivation(long paidSats) =>
        paidSats > 0 ? (object)new { paid = true, amountSats = paidSats } : new { paid = true };

    private static string PaidButFailedWarning(long paidSats, string? paymentHash) =>
        (paidSats > 0
            ? $"A payment of {paidSats:N0} sats was taken from your wallet"
            : "A payment may have been taken from your wallet") +
        " but the renewal did not complete. Do NOT re-run " +
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
            var code = FirstPartyPaymentGate.GetString(doc.RootElement, "error");
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
}
