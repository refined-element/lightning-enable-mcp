using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for depositing funds from a linked bank account into Strike via ACH.
/// Requires confirmation since it moves real money.
/// </summary>
[McpServerToolType]
public static class InitiateDepositTool
{
    /// <summary>
    /// Initiates a deposit from a linked bank account into the Strike account.
    /// </summary>
    [McpServerTool(Name = "initiate_deposit"), Description("Deposit funds from a linked bank account into your Strike account via ACH. Requires confirmation.")]
    public static async Task<string> InitiateDeposit(
        [Description("ID of the bank payment method to deposit from (from list_bank_payment_methods)")] string paymentMethodId,
        [Description("Amount to deposit (e.g., '500.00')")] string amount,
        [Description("Currency code (e.g., USD)")] string currency,
        [Description("Fee policy: INCLUSIVE or EXCLUSIVE. Default: EXCLUSIVE")] string? feePolicy = null,
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
                error = "amount is required (e.g., '500.00')"
            });
        }

        if (string.IsNullOrWhiteSpace(currency))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "currency is required (e.g., USD)"
            });
        }

        // Parse and validate amount
        if (!decimal.TryParse(amount.Trim(), out var parsedAmount) || parsedAmount <= 0)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "amount must be a positive number (e.g., '500.00')"
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
                                Description = "Set to true to approve this deposit"
                            }
                        }
                    };

                    var response = await server.ElicitAsync(new ElicitRequestParams
                    {
                        Message = $"Deposit Confirmation Required\n\n" +
                                  $"Amount: {parsedAmount:F2} {normalizedCurrency}\n" +
                                  $"From Bank Account: {paymentMethodId}\n" +
                                  $"Fee Policy: {normalizedFeePolicy}\n\n" +
                                  $"Do you approve this deposit into your Strike account?",
                        RequestedSchema = schema
                    }, cancellationToken);

                    if (response.Action != "accept" ||
                        response.Content?.TryGetValue("approved", out var approved) != true ||
                        approved.ValueKind != JsonValueKind.True)
                    {
                        return JsonSerializer.Serialize(new { success = false, error = "Deposit cancelled by user" });
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

                Console.Error.WriteLine($"[Lightning Enable] Deposit of {parsedAmount:F2} {normalizedCurrency} confirmed via nonce {confirmation.Nonce}");
            }
        }

        // Proceed with initiating the deposit
        try
        {
            var request = new InitiateDepositRequest
            {
                PaymentMethodId = paymentMethodId.Trim(),
                Amount = parsedAmount.ToString("F2"),
                Currency = normalizedCurrency,
                FeePolicy = normalizedFeePolicy
            };

            var result = await bankingService.InitiateDepositAsync(request, cancellationToken);

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
                deposit = new
                {
                    id = result.Id,
                    state = result.State,
                    amount = result.Amount,
                    currency = result.Currency,
                    fee = result.Fee
                },
                message = "Deposit initiated. ACH deposits typically settle in 1-5 business days."
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
                "initiate_deposit",
                $"Deposit {parsedAmount:F2} {currency} from payment method {paymentMethodId}");

            return JsonSerializer.Serialize(new
            {
                success = false,
                requiresConfirmation = true,
                error = "This deposit requires your confirmation",
                message = $"Deposit {parsedAmount:F2} {currency} from bank account into Strike (payment method: {paymentMethodId}, fee policy: {feePolicy})",
                nonce = pending.Nonce,
                howToConfirm = $"Step 1: Call confirm_payment(nonce: \"{pending.Nonce}\") to approve.\n" +
                               $"Step 2: Call initiate_deposit with confirmationNonce=\"{pending.Nonce}\" to proceed.",
                expiresInSeconds = 120
            });
        }

        return JsonSerializer.Serialize(new { success = false, error = "Confirmation required but budget service not available" });
    }
}
