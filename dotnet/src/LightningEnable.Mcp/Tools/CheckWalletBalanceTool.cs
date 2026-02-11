using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for checking the connected wallet balance.
/// </summary>
[McpServerToolType]
public static class CheckWalletBalanceTool
{
    /// <summary>
    /// Checks the connected Lightning wallet balance.
    /// </summary>
    /// <param name="walletService">Injected wallet service.</param>
    /// <param name="budgetService">Injected budget service for session stats.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Wallet balance in satoshis and session spending info.</returns>
    [McpServerTool(Name = "check_wallet_balance"), Description("Check connected Lightning wallet balance")]
    public static async Task<string> CheckWalletBalance(
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
            var balance = await walletService.GetBalanceAsync(cancellationToken);
            var config = budgetService?.GetConfig();

            return JsonSerializer.Serialize(new
            {
                success = true,
                provider = walletService.ProviderName,
                wallet = new
                {
                    balanceSats = balance.BalanceSats,
                    balanceMsat = balance.BalanceMsat
                },
                session = config != null ? new
                {
                    spentSats = config.SessionSpent,
                    remainingBudgetSats = config.RemainingSessionBudget,
                    maxPerRequestSats = config.MaxSatsPerRequest,
                    maxPerSessionSats = config.MaxSatsPerSession,
                    requestCount = config.RequestCount,
                    sessionStarted = config.SessionStarted
                } : null
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
