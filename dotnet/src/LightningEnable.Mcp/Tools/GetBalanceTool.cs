using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for reading the connected wallet's balance.
///
/// Supersedes check_wallet_balance and get_all_balances, returning a single superset
/// shape that drops nothing either old tool returned: the primary wallet's scalar
/// sats/msat balance is always the headline (from GetBalanceAsync — the old
/// check_wallet_balance), and a multi-currency balances[] array is added (for Strike,
/// via GetAllBalancesAsync — the old get_all_balances; a single derived BTC entry for
/// single-currency backends), plus the session spend summary.
///
/// A single-currency backend (NWC/LND/OpenNode) does exactly ONE balance round-trip:
/// GetAllBalancesAsync on those providers just re-calls GetBalanceAsync, so calling both
/// would double the relay/API load for no new data. Only Strike — genuinely
/// multi-currency — uses the multi-currency path, and the scalar headline is taken from
/// that single call's BTC entry.
///
/// When the balance is genuinely unavailable (GetBalanceAsync's -1 sentinel, or a failed
/// Strike GetAllBalancesAsync) the tool returns an honest success:false + errorCode and
/// NEVER a fabricated or negative balance. A real zero balance stays success:true.
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
            // Read-only tool: use the receiving-oriented guidance (any backend can report
            // a balance) rather than the payment-oriented NotConfigured, which wrongly
            // tells the caller OpenNode cannot pay L402 — irrelevant to a balance read.
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = WalletMessages.NotConfiguredForReceiving,
                configured = false
            });
        }

        try
        {
            long balanceSats;
            long balanceMsat;
            List<object> formattedBalances;

            // Strike is the only genuinely multi-currency backend. Every other provider's
            // GetAllBalancesAsync just re-calls GetBalanceAsync, so we call GetBalanceAsync
            // directly there — exactly one balance round-trip.
            var isStrike = string.Equals(walletService.ProviderName, "Strike", StringComparison.OrdinalIgnoreCase);

            if (isStrike)
            {
                // Multi-currency path (Strike). One call; the scalar headline is derived
                // from this call's BTC entry (no second GetBalanceAsync round-trip).
                var multi = await walletService.GetAllBalancesAsync(cancellationToken);

                if (!multi.Success)
                {
                    // Genuinely unavailable — surface it honestly, never a phantom balance.
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = multi.ErrorMessage ?? "Multi-currency balance not available",
                        errorCode = multi.ErrorCode ?? "BALANCE_UNAVAILABLE"
                    });
                }

                formattedBalances = multi.Balances.Select(b => new
                {
                    currency = b.Currency,
                    available = b.Available,
                    total = b.Total,
                    pending = b.Pending,
                    formatted = b.Currency == "BTC"
                        ? $"{b.Available:F8} BTC ({(long)(b.Available * 100_000_000m):N0} sats)"
                        : $"{b.Available:N2} {b.Currency}"
                }).ToList<object>();

                // Headline scalar = the BTC entry (0 sats if the account holds no BTC —
                // an honest zero, distinct from "unavailable").
                var btc = multi.Balances.FirstOrDefault(b =>
                    string.Equals(b.Currency, "BTC", StringComparison.OrdinalIgnoreCase));
                balanceSats = btc != null ? (long)(btc.Available * 100_000_000m) : 0L;
                balanceMsat = balanceSats * 1000L;
            }
            else
            {
                // Single-currency path (NWC/LND/OpenNode): exactly one balance round-trip.
                var balance = await walletService.GetBalanceAsync(cancellationToken);

                // Negative sats is the "balance unavailable" sentinel (e.g. OpenNode has no
                // balance endpoint). Distinguish it from a real zero balance and report it
                // honestly instead of fabricating a negative BTC entry.
                if (balance.BalanceSats < 0)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Balance not available from {walletService.ProviderName}. " +
                                "The provider did not report a balance (this is not a zero balance).",
                        errorCode = "BALANCE_UNAVAILABLE"
                    });
                }

                balanceSats = balance.BalanceSats;
                balanceMsat = balance.BalanceMsat;
                formattedBalances = new List<object>
                {
                    new
                    {
                        currency = "BTC",
                        available = balanceSats / 100_000_000m,
                        total = balanceSats / 100_000_000m,
                        pending = 0m,
                        formatted = $"{balanceSats / 100_000_000m:F8} BTC ({balanceSats:N0} sats)"
                    }
                };
            }

            var config = budgetService?.GetConfig();

            return JsonSerializer.Serialize(new
            {
                success = true,
                walletType = walletService.ProviderName.ToLowerInvariant(),
                provider = walletService.ProviderName,
                wallet = new
                {
                    balanceSats,
                    balanceMsat
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
