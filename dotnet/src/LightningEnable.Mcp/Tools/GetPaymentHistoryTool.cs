using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for retrieving L402 payment history for the current session.
/// </summary>
[McpServerToolType]
public static class GetPaymentHistoryTool
{
    /// <summary>
    /// Lists recent L402 payments made in this session.
    /// </summary>
    /// <param name="limit">Maximum number of payments to return. Defaults to 10.</param>
    /// <param name="historyService">Injected payment history service.</param>
    /// <returns>List of recent payments with details.</returns>
    [McpServerTool(Name = "get_payment_history"), Description("List recent L402 payments made in this session")]
    public static string GetPaymentHistory(
        [Description("Maximum number of payments to return. Defaults to 10")] int limit = 10,
        IPaymentHistoryService? historyService = null)
    {
        if (historyService == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Payment history service not available"
            });
        }

        try
        {
            var summary = historyService.GetSummary();
            var recentPayments = historyService.GetRecentPayments(limit);

            return JsonSerializer.Serialize(new
            {
                success = true,
                summary = new
                {
                    totalPayments = summary.TotalPayments,
                    // Includes pending: the funds are committed even though not settled.
                    totalSatsSpent = summary.TotalSatsSpent,
                    successfulPayments = summary.SuccessfulPayments,
                    failedPayments = summary.FailedPayments,
                    pendingPayments = summary.PendingPayments
                },
                payments = recentPayments.Select(p => new
                {
                    id = p.Id,
                    url = p.Url,
                    method = p.Method,
                    amountSats = p.AmountSats,
                    timestamp = p.Timestamp,
                    // status is the authoritative outcome ("success"/"failed"/"pending");
                    // success stays for compatibility but is false for a pending payment,
                    // which has NOT settled and may still fail.
                    status = p.Status.ToString().ToLowerInvariant(),
                    success = p.Success,
                    statusCode = p.ResponseStatusCode,
                    error = p.ErrorMessage
                }).ToList()
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message
            });
        }
    }
}
