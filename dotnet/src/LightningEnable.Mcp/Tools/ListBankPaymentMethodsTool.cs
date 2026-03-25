using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for listing linked bank payment methods on a Strike account.
/// </summary>
[McpServerToolType]
public static class ListBankPaymentMethodsTool
{
    /// <summary>
    /// Lists all bank payment methods (ACH, wire, SEPA, FPS, BSB) on the Strike account.
    /// </summary>
    /// <param name="bankingService">Injected Strike banking service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON with payment methods array.</returns>
    [McpServerTool(Name = "list_bank_payment_methods"), Description("List all linked bank payment methods on your Strike account. Shows ACH, wire, SEPA, FPS, and BSB accounts.")]
    public static async Task<string> ListBankPaymentMethods(
        IStrikeBankingService? bankingService = null,
        CancellationToken cancellationToken = default)
    {
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
            var result = await bankingService.ListBankPaymentMethodsAsync(cancellationToken);

            if (!result.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage,
                    errorCode = result.ErrorCode
                });
            }

            var items = result.Items ?? [];

            var paymentMethods = items.Select(pm => new
            {
                id = pm.Id,
                transferType = pm.TransferType,
                accountNumber = pm.AccountNumber,
                state = pm.State,
                currency = pm.Currency,
                supportedActions = pm.SupportedActions
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                success = true,
                provider = "Strike",
                paymentMethods,
                count = paymentMethods.Count,
                message = paymentMethods.Count == 0
                    ? "No bank payment methods found on your Strike account."
                    : $"Found {paymentMethods.Count} bank payment method(s) on your Strike account."
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
