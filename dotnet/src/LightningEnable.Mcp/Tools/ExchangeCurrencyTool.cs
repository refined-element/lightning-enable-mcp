using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for exchanging currencies (e.g., USD to BTC or BTC to USD).
/// </summary>
[McpServerToolType]
public static class ExchangeCurrencyTool
{
    /// <summary>
    /// Exchanges currency within the wallet (e.g., USD to BTC or BTC to USD).
    /// </summary>
    /// <param name="sourceCurrency">Currency to convert from (USD or BTC).</param>
    /// <param name="targetCurrency">Currency to convert to (BTC or USD).</param>
    /// <param name="amount">Amount in source currency.</param>
    /// <param name="walletService">Injected wallet service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Exchange result with converted amount.</returns>
    [McpServerTool(Name = "exchange_currency"), Description("Exchange currency within your wallet (USD to BTC or BTC to USD). Currently only available with Strike wallet.")]
    public static async Task<string> ExchangeCurrency(
        [Description("Currency to convert from: USD or BTC")] string sourceCurrency,
        [Description("Currency to convert to: BTC or USD")] string targetCurrency,
        [Description("Amount in source currency (e.g., 100 for $100 or 0.001 for 0.001 BTC)")] decimal amount,
        IWalletService? walletService = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceCurrency))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Source currency is required (USD or BTC)"
            });
        }

        if (string.IsNullOrWhiteSpace(targetCurrency))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Target currency is required (BTC or USD)"
            });
        }

        if (amount <= 0)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Amount must be greater than 0"
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
                error = "Wallet not configured. Set STRIKE_API_KEY environment variable for currency exchange."
            });
        }

        try
        {
            var result = await walletService.ExchangeCurrencyAsync(
                sourceCurrency, targetCurrency, amount, cancellationToken);

            if (!result.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage,
                    errorCode = result.ErrorCode,
                    hint = result.ErrorCode == "NOT_SUPPORTED"
                        ? $"{walletService.ProviderName} does not support currency exchange. Use Strike wallet."
                        : null
                });
            }

            // Format amounts for display
            var sourceFormatted = result.SourceCurrency == "BTC"
                ? $"{result.SourceAmount:F8} BTC"
                : $"${result.SourceAmount:N2} USD";

            var targetFormatted = result.TargetCurrency == "BTC"
                ? $"{result.TargetAmount:F8} BTC"
                : $"${result.TargetAmount:N2} USD";

            return JsonSerializer.Serialize(new
            {
                success = true,
                provider = walletService.ProviderName,
                exchange = new
                {
                    id = result.ExchangeId,
                    sourceCurrency = result.SourceCurrency,
                    targetCurrency = result.TargetCurrency,
                    sourceAmount = result.SourceAmount,
                    targetAmount = result.TargetAmount,
                    rate = result.Rate,
                    fee = result.Fee,
                    state = result.State
                },
                message = $"Exchanged {sourceFormatted} for {targetFormatted}"
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
