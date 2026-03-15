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
        "a Lightning invoice at the negotiated price, share it with the requester, then use " +
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

            // Budget check before settlement
            if (budgetService != null)
            {
                var budgetCheck = budgetService.CheckBudget(maxSats);
                if (!budgetCheck.Allowed)
                {
                    paymentHistoryService?.RecordFailedPayment(
                        l402Endpoint,
                        "ASA-Settlement",
                        maxSats,
                        budgetCheck.DenialReason ?? "Budget limit exceeded");

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

            // Execute the L402 payment flow
            var result = await l402Client.FetchWithL402Async(
                l402Endpoint,
                method,
                null, // headers
                body,
                maxSats,
                cancellationToken);

            if (result.Success)
            {
                if (result.PaidAmountSats > 0)
                {
                    // Record the payment
                    budgetService?.RecordSpend(result.PaidAmountSats);
                    budgetService?.RecordPaymentTime();
                    paymentHistoryService?.RecordPayment(
                        l402Endpoint,
                        "ASA-Settlement",
                        result.PaidAmountSats,
                        null,
                        null,
                        result.L402Token,
                        result.StatusCode);

                    return JsonSerializer.Serialize(new
                    {
                        success = true,
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
            else
            {
                paymentHistoryService?.RecordFailedPayment(
                    l402Endpoint,
                    "ASA-Settlement",
                    maxSats,
                    result.ErrorMessage ?? "Settlement failed");

                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage,
                    l402Endpoint,
                    agreementId,
                    statusCode = result.StatusCode,
                    hint = result.StatusCode == 402
                        ? "The L402 payment challenge could not be completed. Check wallet balance and configuration."
                        : "The endpoint returned an error. Verify the L402 endpoint URL is correct."
                });
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Error settling service: {ex.Message}",
                l402Endpoint,
                agreementId
            });
        }
    }
}
