using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for creating Lightning invoices to receive payments.
/// </summary>
[McpServerToolType]
public static class CreateInvoiceTool
{
    /// <summary>
    /// Creates a Lightning invoice to receive a payment.
    /// </summary>
    /// <param name="amountSats">Amount to receive in satoshis.</param>
    /// <param name="memo">Optional description for the invoice.</param>
    /// <param name="expirySecs">Invoice expiry time in seconds (default 3600 = 1 hour).</param>
    /// <param name="walletService">Injected wallet service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Invoice details including BOLT11 string to share with payer.</returns>
    [McpServerTool(Name = "create_invoice"), Description("Create a Lightning invoice to receive a payment. Returns a BOLT11 invoice string to share with the payer.")]
    public static async Task<string> CreateInvoice(
        [Description("Amount to receive in satoshis")] long amountSats,
        [Description("Optional description/memo for the invoice")] string? memo = null,
        [Description("Invoice expiry time in seconds. Defaults to 3600 (1 hour)")] int expirySecs = 3600,
        IWalletService? walletService = null,
        CancellationToken cancellationToken = default)
    {
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
                error = "Wallet not configured. Set STRIKE_API_KEY, OPENNODE_API_KEY, or NWC_CONNECTION_STRING environment variable."
            });
        }

        try
        {
            var result = await walletService.CreateInvoiceAsync(amountSats, memo, expirySecs, cancellationToken);

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
                provider = walletService.ProviderName,
                invoice = new
                {
                    id = result.InvoiceId,
                    bolt11 = result.Bolt11,
                    amountSats = result.AmountSats,
                    expiresAt = result.ExpiresAt?.ToString("o")
                },
                message = $"Invoice created for {amountSats} sats. Share the bolt11 string with the payer."
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
