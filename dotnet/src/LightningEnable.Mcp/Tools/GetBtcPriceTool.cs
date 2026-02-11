using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for getting the current Bitcoin price.
/// </summary>
[McpServerToolType]
public static class GetBtcPriceTool
{
    /// <summary>
    /// Gets the current BTC/USD exchange rate from the wallet provider.
    /// </summary>
    /// <param name="walletService">Injected wallet service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Current BTC price in USD.</returns>
    [McpServerTool(Name = "get_btc_price"), Description("Get the current Bitcoin price in USD. Only available with Strike wallet.")]
    public static async Task<string> GetBtcPrice(
        IWalletService? walletService = null,
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
                error = "Wallet not configured. Set STRIKE_API_KEY environment variable for price data."
            });
        }

        try
        {
            var result = await walletService.GetTickerAsync(cancellationToken);

            if (!result.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage,
                    errorCode = result.ErrorCode,
                    hint = walletService.ProviderName != "Strike"
                        ? "Price ticker is only available with Strike wallet. Set STRIKE_API_KEY."
                        : null
                });
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                provider = walletService.ProviderName,
                ticker = new
                {
                    btcUsd = result.BtcUsd,
                    timestamp = result.Timestamp?.ToString("o")
                },
                message = $"Current BTC price: ${result.BtcUsd:N2} USD"
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
