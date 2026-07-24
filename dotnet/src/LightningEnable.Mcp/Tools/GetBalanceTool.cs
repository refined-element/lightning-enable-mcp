using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for reading the connected wallet's balance.
///
/// Supersedes check_wallet_balance and get_all_balances, returning a single superset
/// shape that drops nothing either old tool returned: the scalar sats/msat balance
/// (from GetBalanceAsync — the old check_wallet_balance), the multi-currency balances[]
/// array (from GetAllBalancesAsync — the old get_all_balances; single BTC entry for
/// non-Strike wallets), and the session spend summary.
/// </summary>
[McpServerToolType]
public static class GetBalanceTool
{
    /// <summary>
    /// Gets the wallet balance: sats + msat, all currency balances (Strike), and session spend.
    /// </summary>
    /// <param name="walletService">Injected wallet service.</param>
    /// <param name="budgetService">Injected budget service for session stats.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [McpServerTool(Name = "get_balance"), Description("Get the connected wallet's balance: the sats balance plus, where available, all currency balances (USD, BTC, ... — most useful with Strike) and wallet info. Supersedes check_wallet_balance and get_all_balances.")]
    public static async Task<string> GetBalance(
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
                error = WalletMessages.NotConfiguredForReceiving,
                configured = false
            });
        }

        try
        {
            // Scalar balance (sats + msat) — the old check_wallet_balance contribution.
            var balance = await walletService.GetBalanceAsync(cancellationToken);

            // Multi-currency balances[] — the old get_all_balances contribution. If it
            // fails (e.g. a provider that can't enumerate currencies), fall back to a
            // single BTC entry derived from the scalar so balances[] is never empty and
            // nothing check_wallet_balance returned is lost.
            var multi = await walletService.GetAllBalancesAsync(cancellationToken);

            var formattedBalances = (multi.Success && multi.Balances.Count > 0)
                ? multi.Balances.Select(b => new
                {
                    currency = b.Currency,
                    available = b.Available,
                    total = b.Total,
                    pending = b.Pending,
                    formatted = b.Currency == "BTC"
                        ? $"{b.Available:F8} BTC ({(long)(b.Available * 100_000_000m):N0} sats)"
                        : $"{b.Available:N2} {b.Currency}"
                }).ToList<object>()
                : new List<object>
                {
                    new
                    {
                        currency = "BTC",
                        available = balance.BalanceSats / 100_000_000m,
                        total = balance.BalanceSats / 100_000_000m,
                        pending = 0m,
                        formatted = $"{balance.BalanceSats / 100_000_000m:F8} BTC ({balance.BalanceSats:N0} sats)"
                    }
                };

            var config = budgetService?.GetConfig();

            return JsonSerializer.Serialize(new
            {
                success = true,
                walletType = walletService.ProviderName.ToLowerInvariant(),
                provider = walletService.ProviderName,
                wallet = new
                {
                    balanceSats = balance.BalanceSats,
                    balanceMsat = balance.BalanceMsat
                },
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
                message = $"Retrieved {formattedBalances.Count} currency balance(s) from {walletService.ProviderName}"
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
