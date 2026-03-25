using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for initiating a previously created payout.
/// This triggers the actual bank transfer and cannot be reversed.
/// Requires confirmation since it moves real money.
/// </summary>
[McpServerToolType]
public static class InitiatePayoutTool
{
    /// <summary>
    /// Initiates a previously created payout, triggering the bank transfer.
    /// This action is irreversible.
    /// </summary>
    [McpServerTool(Name = "initiate_payout"), Description("Initiate a previously created payout. This triggers the actual bank transfer and cannot be reversed.")]
    public static async Task<string> InitiatePayout(
        [Description("Payout ID from create_payout")] string payoutId,
        [Description("Confirmation nonce from confirm_payment tool. Required for fund movement operations.")] string? confirmationNonce = null,
        McpServer? server = null,
        IStrikeBankingService? bankingService = null,
        IBudgetService? budgetService = null,
        CancellationToken cancellationToken = default)
    {
        // Validate required parameters
        if (string.IsNullOrWhiteSpace(payoutId))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "payoutId is required. Use create_payout first to create a payout."
            });
        }

        if (bankingService == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Strike banking service not available"
            });
        }

        if (!bankingService.IsConfigured)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Strike not configured. Set STRIKE_API_KEY environment variable."
            });
        }

        var trimmedPayoutId = payoutId.Trim();

        // Require confirmation for fund movement
        if (string.IsNullOrWhiteSpace(confirmationNonce))
        {
            // Try MCP elicitation first
            if (server?.ClientCapabilities?.Elicitation != null)
            {
                try
                {
                    var schema = new ElicitRequestParams.RequestSchema
                    {
                        Properties =
                        {
                            ["approved"] = new ElicitRequestParams.BooleanSchema
                            {
                                Description = "Set to true to approve initiating this payout"
                            }
                        }
                    };

                    var response = await server.ElicitAsync(new ElicitRequestParams
                    {
                        Message = $"Initiate Payout Confirmation\n\n" +
                                  $"Payout ID: {trimmedPayoutId}\n\n" +
                                  $"WARNING: This will trigger an irreversible bank transfer.\n\n" +
                                  $"Do you approve initiating this payout?",
                        RequestedSchema = schema
                    }, cancellationToken);

                    if (response.Action != "accept" ||
                        response.Content?.TryGetValue("approved", out var approved) != true ||
                        approved.ValueKind != JsonValueKind.True)
                    {
                        return JsonSerializer.Serialize(new { success = false, error = "Payout initiation cancelled by user" });
                    }
                    // Elicitation approved, proceed
                }
                catch
                {
                    // Fall through to nonce-based confirmation
                    return ReturnNonceConfirmation(budgetService, trimmedPayoutId);
                }
            }
            else
            {
                // No elicitation, use nonce-based confirmation
                return ReturnNonceConfirmation(budgetService, trimmedPayoutId);
            }
        }
        else
        {
            // Validate the nonce
            if (budgetService != null)
            {
                var confirmation = budgetService.ValidateAndConsumeConfirmation(confirmationNonce.Trim().ToUpperInvariant());
                if (confirmation == null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "Invalid, expired, or already-used confirmation nonce. Request a new one by calling without a nonce."
                    });
                }

                Console.Error.WriteLine($"[Lightning Enable] Payout initiation confirmed via nonce {confirmation.Nonce} for payout {trimmedPayoutId}");
            }
        }

        // Proceed with initiating the payout
        try
        {
            var result = await bankingService.InitiatePayoutAsync(trimmedPayoutId, cancellationToken);

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
                payout = new
                {
                    id = result.Id,
                    state = result.State
                },
                message = "Payout initiated. ACH transfers typically settle in 1-5 business days."
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

    private static string ReturnNonceConfirmation(IBudgetService? budgetService, string payoutId)
    {
        if (budgetService != null)
        {
            var pending = budgetService.CreatePendingConfirmation(
                0,
                0,
                "initiate_payout",
                $"Initiate payout {payoutId}");

            return JsonSerializer.Serialize(new
            {
                success = false,
                requiresConfirmation = true,
                error = "This operation requires your confirmation",
                message = $"Initiate payout {payoutId}? This will trigger an irreversible bank transfer.",
                nonce = pending.Nonce,
                howToConfirm = $"Step 1: Call confirm_payment(nonce: \"{pending.Nonce}\") to approve.\n" +
                               $"Step 2: Call initiate_payout with confirmationNonce=\"{pending.Nonce}\" to proceed.",
                expiresInSeconds = 120
            });
        }

        return JsonSerializer.Serialize(new { success = false, error = "Confirmation required but budget service not available" });
    }
}
