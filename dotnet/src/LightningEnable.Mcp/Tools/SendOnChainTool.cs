using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for sending on-chain Bitcoin payments.
/// </summary>
[McpServerToolType]
public static class SendOnChainTool
{
    /// <summary>
    /// Sends an on-chain Bitcoin payment to a Bitcoin address.
    /// </summary>
    /// <param name="address">Bitcoin address to send to.</param>
    /// <param name="amountSats">Amount to send in satoshis.</param>
    /// <param name="walletService">Injected wallet service.</param>
    /// <param name="budgetService">Injected budget service for spending limits.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Payment result with transaction details.</returns>
    [McpServerTool(Name = "send_onchain"), Description("Send an on-chain Bitcoin payment to a Bitcoin address. Currently only available with Strike wallet.")]
    public static async Task<string> SendOnChain(
        [Description("Bitcoin address to send to (e.g., bc1q...)")] string address,
        [Description("Amount to send in satoshis")] long amountSats,
        IWalletService? walletService = null,
        IBudgetService? budgetService = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Bitcoin address is required"
            });
        }

        if (amountSats <= 0)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Amount must be greater than 0 sats"
            });
        }

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
                error = "Wallet not configured. Set STRIKE_API_KEY environment variable for on-chain payments."
            });
        }

        // Check budget if configured
        if (budgetService != null)
        {
            var budgetCheck = budgetService.CheckBudget(amountSats);
            if (!budgetCheck.Allowed)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"Budget check failed: {budgetCheck.DenialReason}"
                });
            }
        }

        try
        {
            var result = await walletService.SendOnChainAsync(address, amountSats, cancellationToken);

            if (!result.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage,
                    errorCode = result.ErrorCode,
                    hint = result.ErrorCode == "NOT_SUPPORTED"
                        ? $"{walletService.ProviderName} does not support on-chain payments. Use Strike wallet."
                        : null
                });
            }

            // Record spend if budget service available
            budgetService?.RecordSpend(amountSats + result.FeeSats);

            return JsonSerializer.Serialize(new
            {
                success = true,
                provider = walletService.ProviderName,
                payment = new
                {
                    id = result.PaymentId,
                    txId = result.TxId,
                    state = result.State,
                    amountSats = result.AmountSats,
                    feeSats = result.FeeSats
                },
                message = result.State == "COMPLETED"
                    ? $"On-chain payment of {amountSats} sats sent to {address}"
                    : $"On-chain payment initiated (status: {result.State})"
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
