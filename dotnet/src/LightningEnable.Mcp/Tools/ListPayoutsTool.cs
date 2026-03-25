using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for listing payouts from a Strike account.
/// </summary>
[McpServerToolType]
public static class ListPayoutsTool
{
    /// <summary>
    /// Lists all payouts (ACH/wire transfers to bank accounts) on the Strike account.
    /// </summary>
    /// <param name="bankingService">Injected Strike banking service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON with payouts array.</returns>
    [McpServerTool(Name = "list_payouts"), Description("List payouts from your Strike account. Shows ACH/wire transfers to bank accounts with status.")]
    public static async Task<string> ListPayouts(
        IStrikeBankingService? bankingService = null,
        CancellationToken cancellationToken = default)
    {
        if (bankingService == null || !bankingService.IsConfigured)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Strike banking service not configured. Set STRIKE_API_KEY environment variable."
            });
        }

        try
        {
            var result = await bankingService.ListPayoutsAsync(cancellationToken);

            if (!result.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage,
                    errorCode = result.ErrorCode
                });
            }

            var items = result.Items ?? [];

            var payouts = items.Select(p => new
            {
                id = p.Id,
                state = p.State,
                amount = p.Amount,
                currency = p.Currency,
                fee = p.Fee,
                created = p.Created,
                completed = p.Completed
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                success = true,
                provider = "Strike",
                payouts,
                count = payouts.Count,
                message = payouts.Count == 0
                    ? "No payouts found on your Strike account."
                    : $"Found {payouts.Count} payout(s) on your Strike account."
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
