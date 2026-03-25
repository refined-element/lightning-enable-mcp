using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for listing payout originators on a Strike account.
/// </summary>
[McpServerToolType]
public static class ListPayoutOriginatorsTool
{
    /// <summary>
    /// Lists all payout originators on the Strike account.
    /// </summary>
    /// <param name="bankingService">Injected Strike banking service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON with originators array.</returns>
    [McpServerTool(Name = "list_payout_originators"), Description("List payout originators on your Strike account. Originators must be approved before payouts can be initiated.")]
    public static async Task<string> ListPayoutOriginators(
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
            var result = await bankingService.ListPayoutOriginatorsAsync(cancellationToken);

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

            var originators = items.Select(o => new
            {
                id = o.Id,
                state = o.State,
                type = o.Type,
                name = o.Name
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                success = true,
                provider = "Strike",
                originators,
                count = originators.Count,
                message = originators.Count == 0
                    ? "No payout originators found on your Strike account."
                    : $"Found {originators.Count} payout originator(s) on your Strike account."
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
