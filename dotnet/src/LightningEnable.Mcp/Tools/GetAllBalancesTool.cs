using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for getting all currency balances (multi-currency wallets).
/// </summary>
[McpServerToolType]
public static class GetAllBalancesTool
{
    /// <summary>
    /// Gets all currency balances from the wallet (USD, BTC, etc.).
    /// </summary>
    /// <param name="walletService">Injected wallet service.</param>
    /// <param name="budgetService">Injected budget service for session stats.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All currency balances and session spending info.</returns>
    [McpServerTool(Name = "get_all_balances"), Description("Get all currency balances from your wallet (USD, BTC, etc.). Most useful with Strike wallet which supports multiple currencies.")]
    public static async Task<string> GetAllBalances(
        IWalletService? walletService = null,
        IBudgetService? budgetService = null,
        CancellationToken cancellationToken = default)
    {
        if (walletService == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Wallet service not available"
            });
        }

        if (!walletService.IsConfigured)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Wallet not configured. Set STRIKE_API_KEY, OPENNODE_API_KEY, or NWC_CONNECTION_STRING environment variable.",
                configured = false
            });
        }

        try
        {
            var result = await walletService.GetAllBalancesAsync(cancellationToken);

            if (!result.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage,
                    errorCode = result.ErrorCode
                });
            }

            var config = budgetService?.GetConfig();

            // Format balances for display
            var formattedBalances = result.Balances.Select(b => new
            {
                currency = b.Currency,
                available = b.Available,
                total = b.Total,
                pending = b.Pending,
                formatted = b.Currency == "BTC"
                    ? $"{b.Available:F8} BTC ({(long)(b.Available * 100_000_000m):N0} sats)"
                    : $"{b.Available:N2} {b.Currency}"
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                success = true,
                provider = walletService.ProviderName,
                balances = formattedBalances,
                session = config != null ? new
                {
                    spentSats = config.SessionSpent,
                    remainingBudgetSats = config.RemainingSessionBudget,
                    maxPerRequestSats = config.MaxSatsPerRequest,
                    maxPerSessionSats = config.MaxSatsPerSession,
                    requestCount = config.RequestCount,
                    sessionStarted = config.SessionStarted
                } : null,
                message = $"Retrieved {result.Balances.Count} currency balance(s) from {walletService.ProviderName}"
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
