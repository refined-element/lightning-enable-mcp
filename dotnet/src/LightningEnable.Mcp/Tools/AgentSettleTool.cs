using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for settling agent service agreements via L402 payment (consumer/requester side).
/// Pays the L402 endpoint specified in the agreement, completing the service transaction.
/// For the PROVIDER side, use create_l402_challenge to generate invoices and
/// verify_l402_payment to confirm payment before delivering the service.
/// </summary>
[McpServerToolType]
public static class AgentSettleTool
{
    /// <summary>
    /// Settles an agent service agreement by paying the L402 endpoint (consumer/requester side).
    /// </summary>
    [McpServerTool(Name = "settle_agent_service"), Description(
        "Settle an agent service agreement via L402 payment (CONSUMER/REQUESTER side). " +
        "Pays the L402 endpoint specified in the agreement, completing the service transaction. " +
        "Uses the same L402 auto-pay flow as access_l402_resource. " +
        "The L402 endpoint URL comes from discover_agent_services or request_agent_service results. " +
        "NOTE: If you are the PROVIDER (selling a service), use create_l402_challenge to generate " +
        "a Lightning invoice at the agreed price, share it with the requester, then use " +
        "verify_l402_payment to confirm payment before delivering the service.")]
    public static async Task<string> SettleAgentService(
        [Description("L402 endpoint URL from the service agreement")] string l402Endpoint,
        [Description("HTTP method (GET, POST). Defaults to GET")] string method = "GET",
        [Description("Optional request body for POST requests (e.g., service parameters as JSON)")] string? body = null,
        [Description("Agreement event ID for tracking")] string? agreementId = null,
        [Description("Maximum satoshis to pay (default: 1000)")] int maxSats = 1000,
        IL402HttpClient? l402Client = null,
        IBudgetService? budgetService = null,
        IPaymentHistoryService? paymentHistoryService = null,
        CancellationToken cancellationToken = default)
    {
        // Declared outside the try so the catch can report whether a durable receipt
        // landed when the client throws AFTER the invoice settled.
        PaymentReceiptScope? receiptScope = null;
        try
        {
            // Input validation
            if (string.IsNullOrWhiteSpace(l402Endpoint))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "L402 endpoint URL is required. Get it from discover_agent_services or request_agent_service results."
                });
            }

            if (!Uri.TryCreate(l402Endpoint, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Invalid L402 endpoint URL. Must be an HTTP or HTTPS URL."
                });
            }

            // Security: reject plain HTTP except for localhost (dev use)
            if (uri.Scheme == "http" &&
                uri.Host != "localhost" && uri.Host != "127.0.0.1" && uri.Host != "::1")
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "L402 settlement requires HTTPS. Plain HTTP is only allowed for localhost during development."
                });
            }

            // Validate HTTP method against whitelist
            var allowedMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };
            if (!allowedMethods.Contains(method))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"Invalid HTTP method '{method}'. Allowed methods: {string.Join(", ", allowedMethods)}."
                });
            }

            if (l402Client == null)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "L402 HTTP client not available. Ensure a wallet is configured."
                });
            }

            // Budget gate before settlement (early deny; no network I/O if the ceiling
            // can't be covered). This tool is PASSIVE on recording — it does NOT record a
            // failed payment here: no payment was attempted, and the client (L402HttpClient)
            // is the single source of truth for all payment/failed-payment recording.
            if (budgetService != null)
            {
                var budgetCheck = budgetService.CheckBudget(maxSats);
                if (!budgetCheck.Allowed)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "Budget limit exceeded",
                        details = new
                        {
                            requestedSats = maxSats,
                            remainingSats = budgetCheck.RemainingSessionBudget,
                            reason = budgetCheck.DenialReason
                        },
                        hint = "Increase maxSats or check get_budget_status for current limits."
                    });
                }
            }

            // Ambient payment intent: the durable receipt is written at the wallet seam
            // (ReceiptRecordingWalletService) when the L402 invoice is paid; this scope
            // enriches it with the redacted settlement endpoint and returns the honest
            // receipt_written signal.
            receiptScope = PaymentReceiptScope.Begin(
                "l402", context: AccessL402ResourceTool.RedactUrl(l402Endpoint));

            // Execute the L402 payment flow
            var result = await l402Client.FetchWithL402Async(
                l402Endpoint,
                method,
                null, // headers
                body,
                maxSats,
                cancellationToken);

            // Paid-retry redirect: the L402 invoice was paid (the client already recorded
            // the spend + a successful payment in history) and THEN the resource returned an
            // unfollowed 3xx. This is NOT a settlement failure — do NOT RecordFailedPayment
            // (that would contradict the client's successful RecordPayment) and do NOT
            // re-record the spend here (the client already did — no double-record). Surface
            // the paid amount + token + redirect target with an explicit "already paid, do
            // NOT pay again" message so the agent retries the redirect target WITH the token
            // instead of re-paying. See L402HttpClient's paid-retry redirect branch.
            if (!string.IsNullOrEmpty(result.RedirectLocation) && result.PaidAmountSats > 0)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    alreadyPaid = true,
                    l402Endpoint,
                    agreementId,
                    statusCode = result.StatusCode,
                    redirect_location = result.RedirectLocation,
                    receipt_written = receiptScope.ReceiptWritten ?? false,
                    payment = new
                    {
                        paid = true,
                        amountSats = result.PaidAmountSats,
                        l402Token = result.L402Token,
                        protocol = result.Protocol ?? "L402"
                    },
                    error = result.ErrorMessage,
                    message = $"Payment succeeded ({result.PaidAmountSats} sats). The resource redirected to " +
                              $"{result.RedirectLocation}. You have ALREADY PAID — do NOT pay again. Retry the " +
                              "redirect target with the l402Token above."
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            if (result.Success)
            {
                if (result.PaidAmountSats > 0)
                {
                    // PASSIVE: the client (L402HttpClient) already recorded the spend, the
                    // payment history, and the cooldown EXACTLY ONCE. The tool only formats.
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        receipt_written = receiptScope.ReceiptWritten ?? false,
                        settlement = new
                        {
                            paid = true,
                            amountSats = result.PaidAmountSats,
                            l402Token = result.L402Token,
                            l402Endpoint,
                            agreementId
                        },
                        response = new
                        {
                            statusCode = result.StatusCode,
                            contentType = result.ContentType,
                            content = result.Content
                        },
                        message = $"Service settled successfully. Paid {result.PaidAmountSats} sats via L402."
                    }, new JsonSerializerOptions { WriteIndented = true });
                }
                else
                {
                    // No payment was required (free tier or already paid)
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        settlement = new
                        {
                            paid = false,
                            l402Endpoint,
                            agreementId
                        },
                        response = new
                        {
                            statusCode = result.StatusCode,
                            contentType = result.ContentType,
                            content = result.Content
                        },
                        message = "Service accessed successfully. No payment was required."
                    }, new JsonSerializerOptions { WriteIndented = true });
                }
            }
            else if (result.PaidAmountSats > 0 && !string.IsNullOrEmpty(result.L402Token))
            {
                // PAID, then the authorized retry returned a non-2xx, non-redirect status
                // (e.g. HTTP 500). The L402 invoice was ALREADY paid — the client recorded
                // the spend + payment + cooldown once. This is NOT a settlement failure: the
                // tool must NEVER record a failed payment here (that would contradict the
                // client's settled RecordPayment and invite a double-pay) and must NEVER
                // return a "verify the URL" result. Surface the paid amount + token so the
                // agent reuses the credential instead of paying again.
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    alreadyPaid = true,
                    l402Endpoint,
                    agreementId,
                    statusCode = result.StatusCode,
                    receipt_written = receiptScope.ReceiptWritten ?? false,
                    payment = new
                    {
                        paid = true,
                        amountSats = result.PaidAmountSats,
                        l402Token = result.L402Token,
                        protocol = result.Protocol ?? "L402"
                    },
                    error = result.ErrorMessage,
                    message = $"Payment succeeded ({result.PaidAmountSats} sats), but the endpoint returned " +
                              $"HTTP {result.StatusCode} on the authorized retry. You have ALREADY PAID — do NOT " +
                              "pay again. Retry the endpoint with the l402Token above."
                }, new JsonSerializerOptions { WriteIndented = true });
            }
            else if (result.PaidAmountSats > 0)
            {
                // Money moved but there is NO usable token: the payment is pending (may
                // still fail) or settled without a preimage. Claiming "Payment succeeded —
                // retry with the l402Token above" here would hand the agent a null token
                // and a false success, so report committed-not-proven instead. The seam
                // has already written the matching (pending) receipt.
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    l402Endpoint,
                    agreementId,
                    statusCode = result.StatusCode,
                    receipt_written = receiptScope.ReceiptWritten ?? false,
                    payment = new
                    {
                        paid = true,
                        amountSats = result.PaidAmountSats,
                        l402Token = (string?)null,
                        protocol = result.Protocol ?? "L402"
                    },
                    error = result.ErrorMessage,
                    message = $"Sats were committed for this settlement ({result.PaidAmountSats} sats) but no usable " +
                              "L402 token is available — the payment may still be settling, or it settled without a " +
                              "preimage. You have ALREADY PAID — do NOT pay again; check the payment status with your " +
                              "wallet provider first."
                }, new JsonSerializerOptions { WriteIndented = true });
            }
            else
            {
                // Genuine pre-payment failure: no invoice was paid (PaidAmountSats == 0).
                // This is the ONLY not-paid failure the tool surfaces, and it still records
                // nothing — the client already recorded a failed payment if an actual
                // payment attempt failed; a budget/challenge denial simply returns clean.
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage,
                    l402Endpoint,
                    agreementId,
                    statusCode = result.StatusCode,
                    hint = result.StatusCode == 402
                        ? "The L402 payment challenge could not be completed. No payment was made. Check wallet balance and configuration."
                        : "The endpoint returned an error and no payment was made. Verify the L402 endpoint URL is correct."
                });
            }
        }
        catch (Exception ex)
        {
            // The client can throw AFTER the invoice settled. null = nothing was paid;
            // true/false = money moved and the durable receipt did/didn't land.
            return JsonSerializer.Serialize(new
            {
                success = false,
                receipt_written = receiptScope?.ReceiptWritten,
                error = $"Error settling service: {ex.Message}",
                l402Endpoint,
                agreementId
            });
        }
        finally
        {
            receiptScope?.Dispose();
        }
    }
}
