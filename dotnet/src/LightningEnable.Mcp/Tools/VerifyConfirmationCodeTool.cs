using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for verifying a payment confirmation code (nonce).
/// VERIFICATION ONLY — it never executes a payment. It appears as a distinct action in
/// Claude Code so the user sees and can approve/deny the check; to actually pay, the
/// agent re-calls the original payment tool with confirmation_nonce set to the code.
/// </summary>
[McpServerToolType]
public static class VerifyConfirmationCodeTool
{
    /// <summary>
    /// Verifies whether a confirmation code (relayed by the human from the server
    /// console/stderr, never returned in a tool result) is still valid and what it
    /// authorizes. VERIFICATION ONLY — this does not consume the code or move money.
    /// The payment-request tools (pay_invoice, send_onchain, ...) never return the code —
    /// the model must obtain it from the human — which is what stops a prompt-injected
    /// agent from self-approving. To execute, call the original payment tool again with
    /// confirmation_nonce set to the code. (This tool does echo the code back once you
    /// supply it.)
    /// </summary>
    [McpServerTool(Name = "verify_confirmation_code"), Description("Verify whether a payment confirmation code (relayed by the human from the server console) is still valid and what it authorizes. VERIFICATION ONLY — never executes a payment. To pay, call the original payment tool again with confirmation_nonce.")]
    public static string VerifyConfirmationCode(
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
            valid = true,
            // Retained for backward compatibility with the old confirm_payment shape.
            confirmed = true,
            amount_sats = confirmation.AmountSats,
            tool = confirmation.ToolName,
            message = $"Code verified — NOTHING HAS BEEN PAID. To execute, call " +
                      $"{confirmation.ToolName} again with confirmation_nonce={confirmation.Nonce}.",
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
