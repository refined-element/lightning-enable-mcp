using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for checking the status of a Lightning invoice.
/// </summary>
[McpServerToolType]
public static class CheckInvoiceStatusTool
{
    /// <summary>
    /// Checks the payment status of a previously created invoice.
    /// </summary>
    /// <param name="invoiceId">The invoice ID returned when the invoice was created.</param>
    /// <param name="walletService">Injected wallet service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Invoice status including whether it has been paid.</returns>
    [McpServerTool(Name = "check_invoice_status"), Description("Check if a Lightning invoice has been paid. Use the invoice ID from create_invoice.")]
    public static async Task<string> CheckInvoiceStatus(
        [Description("The invoice ID returned from create_invoice")] string invoiceId,
        IWalletService? walletService = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invoiceId))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Invoice ID is required"
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
            var result = await walletService.GetInvoiceStatusAsync(invoiceId, cancellationToken);

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
                    state = result.State,
                    isPaid = result.IsPaid,
                    isPending = result.IsPending,
                    amountSats = result.AmountSats,
                    paidAt = result.PaidAt?.ToString("o")
                },
                message = result.IsPaid
                    ? $"Invoice {invoiceId} has been PAID!"
                    : result.IsPending
                        ? $"Invoice {invoiceId} is still pending payment."
                        : $"Invoice {invoiceId} status: {result.State}"
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
