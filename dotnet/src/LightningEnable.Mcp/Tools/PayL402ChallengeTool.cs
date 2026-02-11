using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for manually paying an L402 invoice.
/// Use this when you have received a 402 response and want to pay it manually
/// to get the L402 token for subsequent requests.
/// </summary>
[McpServerToolType]
public static class PayL402ChallengeTool
{
    /// <summary>
    /// Manually pays an L402 invoice and returns the token.
    /// </summary>
    [McpServerTool(Name = "pay_l402_challenge"), Description("Manually pay an L402 Lightning invoice to get the authentication token")]
    public static async Task<string> PayL402Challenge(
        [Description("BOLT11 Lightning invoice string from the L402 challenge")] string invoice,
        [Description("Base64-encoded macaroon from the L402 challenge")] string macaroon,
        [Description("Maximum satoshis allowed to pay. Defaults to 1000")] int maxSats = 1000,
        [Description("Confirmation nonce from confirm_payment tool. Required when previous call returned requiresConfirmation=true.")] string? confirmationNonce = null,
        McpServer? server = null,
        IL402HttpClient? l402Client = null,
        IBudgetService? budgetService = null,
        IPriceService? priceService = null,
        IPaymentHistoryService? paymentHistory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invoice))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Invoice is required"
            });
        }

        if (string.IsNullOrWhiteSpace(macaroon))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Macaroon is required"
            });
        }

        if (l402Client == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "L402 HTTP client not available"
            });
        }

        try
        {
            // Extract amount from invoice for budget checking
            var normalizedInvoice = invoice.Trim().ToLowerInvariant();
            var amountSats = Bolt11Parser.ExtractAmountSats(normalizedInvoice);

            // Use extracted amount or fall back to maxSats for budget check
            var budgetCheckAmount = amountSats ?? (long)maxSats;

            // Check budget approval
            if (budgetService != null)
            {
                var approvalResult = await budgetService.CheckApprovalLevelAsync(budgetCheckAmount, cancellationToken);

                if (approvalResult.Level == ApprovalLevel.Deny)
                {
                    paymentHistory?.RecordFailedPayment(
                        "l402-challenge",
                        "L402",
                        budgetCheckAmount,
                        approvalResult.DenialReason ?? "Budget limit exceeded",
                        normalizedInvoice);

                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = approvalResult.DenialReason,
                        budget = new
                        {
                            amountSats = budgetCheckAmount,
                            amountUsd = approvalResult.AmountUsd,
                            remainingSessionUsd = approvalResult.RemainingSessionBudgetUsd
                        }
                    });
                }

                // Handle confirmation requirements
                if (approvalResult.RequiresConfirmation)
                {
                    // Check if a confirmed nonce was provided
                    if (!string.IsNullOrWhiteSpace(confirmationNonce))
                    {
                        var confirmation = budgetService.ValidateAndConsumeConfirmation(confirmationNonce.Trim().ToUpperInvariant());
                        if (confirmation == null)
                        {
                            return JsonSerializer.Serialize(new
                            {
                                success = false,
                                error = "Invalid, expired, or already-used confirmation nonce",
                                message = "The nonce may have expired (2 minute limit) or was already used. " +
                                          "Request a new confirmation by calling pay_l402_challenge without a nonce."
                            });
                        }

                        Console.Error.WriteLine($"[Lightning Enable] L402 challenge payment of {approvalResult.AmountUsd:C} confirmed via nonce {confirmation.Nonce}");
                    }
                    else
                    {
                        // Try MCP elicitation first
                        var elicitationConfirmed = await RequestL402ChallengeConfirmationAsync(
                            server,
                            approvalResult,
                            normalizedInvoice,
                            cancellationToken);

                        if (!elicitationConfirmed)
                        {
                            var elicitationAvailable = server?.ClientCapabilities?.Elicitation != null;

                            if (!elicitationAvailable)
                            {
                                // Create a pending confirmation with a nonce
                                var invoicePrefix = normalizedInvoice.Substring(0, Math.Min(30, normalizedInvoice.Length)) + "...";
                                var pending = budgetService.CreatePendingConfirmation(
                                    budgetCheckAmount,
                                    approvalResult.AmountUsd,
                                    "pay_l402_challenge",
                                    invoicePrefix);

                                return JsonSerializer.Serialize(new
                                {
                                    success = false,
                                    requiresConfirmation = true,
                                    error = "L402 challenge payment requires your confirmation",
                                    message = $"This payment of {approvalResult.AmountUsd:C} ({budgetCheckAmount:N0} sats) exceeds the auto-approve threshold.",
                                    nonce = pending.Nonce,
                                    howToConfirm = $"Step 1: Call confirm_payment(nonce: \"{pending.Nonce}\") to approve.\n" +
                                                   $"Step 2: Call pay_l402_challenge(invoice=\"...\", macaroon=\"...\", confirmationNonce=\"{pending.Nonce}\") to proceed.",
                                    expiresInSeconds = 120,
                                    amount = new
                                    {
                                        sats = budgetCheckAmount,
                                        usd = Math.Round(approvalResult.AmountUsd, 2)
                                    },
                                    thresholds = new
                                    {
                                        autoApprove = budgetService.GetUserConfiguration().Tiers.AutoApprove,
                                        note = "Payments above this require confirmation via confirm_payment tool"
                                    }
                                });
                            }

                            // Elicitation was available but user declined
                            return JsonSerializer.Serialize(new
                            {
                                success = false,
                                error = "L402 challenge payment cancelled by user",
                                requiresConfirmation = true,
                                amount = new
                                {
                                    sats = budgetCheckAmount,
                                    usd = approvalResult.AmountUsd
                                }
                            });
                        }
                    }
                }

                // Log if needed
                if (approvalResult.Level == ApprovalLevel.LogAndApprove)
                {
                    Console.Error.WriteLine($"[Lightning Enable] Auto-approved L402 challenge payment: {approvalResult.AmountUsd:C} ({budgetCheckAmount} sats)");
                }
            }

            var token = await l402Client.PayChallengeAsync(macaroon, invoice, maxSats, cancellationToken);

            // Record the payment
            budgetService?.RecordSpend(budgetCheckAmount);
            budgetService?.RecordPaymentTime();
            paymentHistory?.RecordPayment(
                "l402-challenge",
                "L402",
                budgetCheckAmount,
                normalizedInvoice,
                null,
                token,
                200);

            var amountUsd = priceService != null
                ? await priceService.SatsToUsdAsync(budgetCheckAmount, cancellationToken)
                : 0m;

            return JsonSerializer.Serialize(new
            {
                success = true,
                l402Token = token,
                payment = new
                {
                    amountSats = budgetCheckAmount,
                    amountUsd = Math.Round(amountUsd, 2)
                },
                usage = new
                {
                    headerName = "Authorization",
                    headerValue = $"L402 {token}",
                    description = "Include this header in subsequent requests to the same endpoint"
                }
            });
        }
        catch (Exception ex)
        {
            paymentHistory?.RecordFailedPayment(
                "l402-challenge",
                "L402",
                0,
                ex.Message,
                invoice);

            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Requests user confirmation for L402 challenge payments via MCP elicitation.
    /// </summary>
    private static async Task<bool> RequestL402ChallengeConfirmationAsync(
        McpServer? server,
        ApprovalCheckResult approvalResult,
        string invoice,
        CancellationToken cancellationToken)
    {
        if (server?.ClientCapabilities?.Elicitation == null)
        {
            Console.Error.WriteLine($"[Lightning Enable] L402 challenge payment of {approvalResult.AmountUsd:C} requires confirmation but elicitation not supported by client");
            return false;
        }

        try
        {
            var invoiceDisplay = invoice.Length > 40 ? invoice.Substring(0, 40) + "..." : invoice;

            if (approvalResult.Level == ApprovalLevel.FormConfirm)
            {
                var schema = new ElicitRequestParams.RequestSchema
                {
                    Properties =
                    {
                        ["approved"] = new ElicitRequestParams.BooleanSchema
                        {
                            Description = "Set to true to approve this L402 challenge payment"
                        }
                    }
                };

                var response = await server.ElicitAsync(new ElicitRequestParams
                {
                    Message = $"L402 Challenge Payment Confirmation\n\n" +
                              $"Amount: {approvalResult.AmountUsd:C} ({approvalResult.AmountSats:N0} sats)\n" +
                              $"Invoice: {invoiceDisplay}\n\n" +
                              $"Authorize this L402 payment?",
                    RequestedSchema = schema
                }, cancellationToken);

                if (response.Action == "accept" &&
                    response.Content?.TryGetValue("approved", out var approvedElement) == true)
                {
                    return approvedElement.ValueKind == System.Text.Json.JsonValueKind.True;
                }

                return false;
            }
            else if (approvalResult.Level == ApprovalLevel.UrlConfirm)
            {
                var schema = new ElicitRequestParams.RequestSchema
                {
                    Properties =
                    {
                        ["confirmAmount"] = new ElicitRequestParams.StringSchema
                        {
                            Description = $"Enter '{approvalResult.AmountUsd:F2}' to confirm this payment"
                        }
                    }
                };

                var response = await server.ElicitAsync(new ElicitRequestParams
                {
                    Message = $"LARGE L402 PAYMENT - Verification Required\n\n" +
                              $"Amount: {approvalResult.AmountUsd:C} ({approvalResult.AmountSats:N0} sats)\n" +
                              $"Invoice: {invoiceDisplay}\n\n" +
                              $"Type the payment amount in USD to confirm (e.g., {approvalResult.AmountUsd:F2}):",
                    RequestedSchema = schema
                }, cancellationToken);

                if (response.Action == "accept" &&
                    response.Content?.TryGetValue("confirmAmount", out var amountElement) == true)
                {
                    var enteredAmount = amountElement.GetString();
                    if (decimal.TryParse(enteredAmount?.Replace("$", "").Trim(), out var amount))
                    {
                        return Math.Abs(amount - approvalResult.AmountUsd) < 0.01m;
                    }
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Lightning Enable] L402 challenge elicitation failed: {ex.Message}");
            return false;
        }

        return false;
    }
}
