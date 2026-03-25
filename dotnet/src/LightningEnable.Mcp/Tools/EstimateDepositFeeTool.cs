using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for estimating deposit fees on a Strike account.
/// </summary>
[McpServerToolType]
public static class EstimateDepositFeeTool
{
    /// <summary>
    /// Estimates the fee for depositing funds from a linked bank account into the Strike account.
    /// </summary>
    /// <param name="paymentMethodId">ID of the bank payment method to deposit from.</param>
    /// <param name="amount">Amount to deposit.</param>
    /// <param name="currency">Currency code (USD, EUR, etc.).</param>
    /// <param name="bankingService">Injected Strike banking service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON with fee estimate details.</returns>
    [McpServerTool(Name = "estimate_deposit_fee"), Description("Estimate the fee for depositing funds from a linked bank account into your Strike account.")]
    public static async Task<string> EstimateDepositFee(
        [Description("ID of the bank payment method to deposit from")] string paymentMethodId,
        [Description("Amount to deposit")] string amount,
        [Description("Currency code (USD, EUR, etc.)")] string currency,
        IStrikeBankingService? bankingService = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(paymentMethodId))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Payment method ID is required."
            });
        }

        if (string.IsNullOrWhiteSpace(amount))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Amount is required."
            });
        }

        if (string.IsNullOrWhiteSpace(currency))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Currency is required (e.g., USD, EUR)."
            });
        }

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
            var result = await bankingService.EstimateDepositFeeAsync(new EstimateDepositFeeRequest
            {
                PaymentMethodId = paymentMethodId,
                Amount = amount,
                Currency = currency
            }, cancellationToken);

            if (!result.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage,
                    errorCode = result.ErrorCode
                });
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                provider = "Strike",
                fee = new
                {
                    amount = result.FeeAmount,
                    currency = result.FeeCurrency
                },
                totalAmount = new
                {
                    amount = result.TotalAmount,
                    currency
                },
                message = $"Estimated fee for depositing {amount} {currency.ToUpperInvariant()}: {result.FeeAmount} {result.FeeCurrency}"
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
