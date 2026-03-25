using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for creating a payout from Strike to a linked bank account.
/// Requires confirmation since it moves real money.
/// </summary>
[McpServerToolType]
public static class CreatePayoutTool
{
    /// <summary>
    /// Creates a payout from the Strike account to a linked bank account.
    /// The payout must be initiated separately via initiate_payout.
    /// </summary>
    [McpServerTool(Name = "create_payout"), Description("Create a payout from your Strike account to a linked bank account. Requires confirmation. After creating, call initiate_payout to send.")]
    public static async Task<string> CreatePayout(
        [Description("ID of the bank payment method to pay to (from list_bank_payment_methods)")] string paymentMethodId,
        [Description("Amount to send (e.g., '247.00')")] string amount,
        [Description("Currency code (e.g., USD, EUR, GBP, AUD)")] string currency,
        [Description("ID of the approved payout originator (from list_payout_originators)")] string payoutOriginatorId,
        [Description("Fee policy: INCLUSIVE (fee included in amount) or EXCLUSIVE (fee added on top). Default: EXCLUSIVE")] string? feePolicy = null,
        [Description("Confirmation nonce from confirm_payment tool. Required for fund movement operations.")] string? confirmationNonce = null,
        McpServer? server = null,
        IStrikeBankingService? bankingService = null,
        IBudgetService? budgetService = null,
        CancellationToken cancellationToken = default)
    {
        // Validate required parameters
        if (string.IsNullOrWhiteSpace(paymentMethodId))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "paymentMethodId is required. Use list_bank_payment_methods to find available payment methods."
            });
        }

        if (string.IsNullOrWhiteSpace(amount))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "amount is required (e.g., '247.00')"
            });
        }

        if (string.IsNullOrWhiteSpace(currency))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "currency is required (e.g., USD, EUR, GBP, AUD)"
            });
        }

        if (string.IsNullOrWhiteSpace(payoutOriginatorId))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "payoutOriginatorId is required. Use list_payout_originators to find available originators."
            });
        }

        // Parse and validate amount
        if (!decimal.TryParse(amount.Trim(), out var parsedAmount) || parsedAmount <= 0)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "amount must be a positive number (e.g., '247.00')"
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

        // Normalize fee policy
        var normalizedFeePolicy = feePolicy?.Trim().ToUpperInvariant();
        if (normalizedFeePolicy != null && normalizedFeePolicy != "INCLUSIVE" && normalizedFeePolicy != "EXCLUSIVE")
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "feePolicy must be INCLUSIVE or EXCLUSIVE"
            });
        }
        normalizedFeePolicy ??= "EXCLUSIVE";

        var normalizedCurrency = currency.Trim().ToUpperInvariant();

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
                                Description = "Set to true to approve this payout"
                            }
                        }
                    };

                    var response = await server.ElicitAsync(new ElicitRequestParams
                    {
                        Message = $"Payout Confirmation Required\n\n" +
                                  $"Amount: {parsedAmount:F2} {normalizedCurrency}\n" +
                                  $"Payment Method: {paymentMethodId}\n" +
                                  $"Fee Policy: {normalizedFeePolicy}\n\n" +
                                  $"Do you approve creating this payout?",
                        RequestedSchema = schema
                    }, cancellationToken);

                    if (response.Action != "accept" ||
                        response.Content?.TryGetValue("approved", out var approved) != true ||
                        approved.ValueKind != JsonValueKind.True)
                    {
                        return JsonSerializer.Serialize(new { success = false, error = "Payout cancelled by user" });
                    }
                    // Elicitation approved, proceed
                }
                catch
                {
                    // Fall through to nonce-based confirmation
                    return ReturnNonceConfirmation(budgetService, parsedAmount, normalizedCurrency, paymentMethodId, normalizedFeePolicy);
                }
            }
            else
            {
                // No elicitation, use nonce-based confirmation
                return ReturnNonceConfirmation(budgetService, parsedAmount, normalizedCurrency, paymentMethodId, normalizedFeePolicy);
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

                Console.Error.WriteLine($"[Lightning Enable] Payout of {parsedAmount:F2} {normalizedCurrency} confirmed via nonce {confirmation.Nonce}");
            }
        }

        // Proceed with creating the payout
        try
        {
            var request = new CreatePayoutRequest
            {
                PayoutOriginatorId = payoutOriginatorId.Trim(),
                PaymentMethodId = paymentMethodId.Trim(),
                Amount = parsedAmount.ToString("F2"),
                Currency = normalizedCurrency,
                FeePolicy = normalizedFeePolicy
            };

            var result = await bankingService.CreatePayoutAsync(request, cancellationToken);

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
                    state = result.State,
                    amount = result.Amount,
                    currency = result.Currency,
                    fee = result.Fee
                },
                message = $"Payout created (state: {result.State}). Call initiate_payout with ID '{result.Id}' to send.",
                nextStep = "initiate_payout"
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

    private static string ReturnNonceConfirmation(
        IBudgetService? budgetService,
        decimal parsedAmount,
        string currency,
        string paymentMethodId,
        string feePolicy)
    {
        if (budgetService != null)
        {
            var pending = budgetService.CreatePendingConfirmation(
                0,
                parsedAmount,
                "create_payout",
                $"{parsedAmount:F2} {currency} to payment method {paymentMethodId}");

            return JsonSerializer.Serialize(new
            {
                success = false,
                requiresConfirmation = true,
                error = "This payout requires your confirmation",
                message = $"Payout {parsedAmount:F2} {currency} to bank account (payment method: {paymentMethodId}, fee policy: {feePolicy})",
                nonce = pending.Nonce,
                howToConfirm = $"Step 1: Call confirm_payment(nonce: \"{pending.Nonce}\") to approve.\n" +
                               $"Step 2: Call create_payout with confirmationNonce=\"{pending.Nonce}\" to proceed.",
                expiresInSeconds = 120
            });
        }

        return JsonSerializer.Serialize(new { success = false, error = "Confirmation required but budget service not available" });
    }
}
