using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for confirming a pending payment via nonce.
/// This is a separate tool call that appears as a distinct action in Claude Code,
/// ensuring the user sees and can approve/deny the confirmation.
/// </summary>
[McpServerToolType]
public static class ConfirmPaymentTool
{
    /// <summary>
    /// Confirms a pending payment using the confirmation code that the SERVER printed to
    /// its console/stderr (visible to the human operator, not to the model). The
    /// payment-request tools (pay_invoice, send_onchain, ...) never return the code — the
    /// model must obtain it from the human — which is what stops a prompt-injected agent
    /// from self-approving. (This tool does echo the code back once you supply it.)
    /// </summary>
    [McpServerTool(Name = "confirm_payment"), Description("Confirm a pending payment using the code the human operator read from the server console. The payment-request tools never return this code — you must ask the human for it.")]
    public static string ConfirmPayment(
        [Description("The confirmation code the human read from the server console/logs")] string nonce,
        IBudgetService? budgetService = null)
    {
        if (string.IsNullOrWhiteSpace(nonce))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Nonce is required"
            });
        }

        if (budgetService == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Budget service not available"
            });
        }

        var confirmation = budgetService.ValidateConfirmation(nonce.Trim().ToUpperInvariant());

        if (confirmation == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Invalid, expired, or already-used confirmation nonce",
                message = "The nonce may have expired (2 minute limit) or was already used. " +
                          "Request a new confirmation by calling the original payment tool again."
            });
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            confirmed = true,
            message = $"Payment of {confirmation.AmountUsd:C} ({confirmation.AmountSats:N0} sats) confirmed",
            confirmation = new
            {
                nonce = confirmation.Nonce,
                amountSats = confirmation.AmountSats,
                amountUsd = Math.Round(confirmation.AmountUsd, 2),
                toolName = confirmation.ToolName,
                description = confirmation.Description
            }
        });
    }
}
