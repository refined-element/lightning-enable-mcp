using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for paying any Lightning invoice directly.
/// Includes multi-tier approval based on payment amount.
/// </summary>
[McpServerToolType]
public static class PayInvoiceTool
{
    /// <summary>
    /// Pays a Lightning invoice directly using the configured wallet.
    /// Requires user confirmation for payments above configured thresholds.
    /// </summary>
    /// <param name="invoice">BOLT11 Lightning invoice string to pay.</param>
    /// <param name="confirmed">Set to true to confirm a payment that requires approval (for clients without elicitation support).</param>
    /// <param name="server">MCP server for elicitation.</param>
    /// <param name="walletService">Injected wallet service.</param>
    /// <param name="budgetService">Injected budget service.</param>
    /// <param name="priceService">Injected price service.</param>
    /// <param name="paymentHistory">Injected payment history service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Payment result with preimage proof.</returns>
    [McpServerTool(Name = "pay_invoice"), Description("Pay a Lightning invoice directly and get the preimage as proof of payment")]
    public static async Task<string> PayInvoice(
        [Description("BOLT11 Lightning invoice string to pay")] string invoice,
        [Description("Confirmation nonce from confirm_payment tool. Required when previous call returned requiresConfirmation=true.")] string? confirmationNonce = null,
        McpServer? server = null,
        IWalletService? walletService = null,
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
            // Normalize invoice to lowercase
            var normalizedInvoice = invoice.Trim().ToLowerInvariant();

            // Basic validation
            if (!normalizedInvoice.StartsWith("lnbc") && !normalizedInvoice.StartsWith("lntb") && !normalizedInvoice.StartsWith("lnbcrt"))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Invalid invoice format. Must be a BOLT11 invoice starting with 'lnbc' (mainnet), 'lntb' (testnet), or 'lnbcrt' (regtest)"
                });
            }

            // Extract amount from invoice
            var amountSats = Bolt11Parser.ExtractAmountSats(normalizedInvoice);
            if (amountSats == null || amountSats.Value <= 0)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Invoice has no amount specified. For security, only invoices with explicit amounts are supported."
                });
            }

            // Check approval level
            if (budgetService != null)
            {
                var approvalResult = await budgetService.CheckApprovalLevelAsync(amountSats.Value, cancellationToken);

                if (approvalResult.Level == ApprovalLevel.Deny)
                {
                    paymentHistory?.RecordFailedPayment(
                        "direct-invoice",
                        "PAY",
                        amountSats.Value,
                        approvalResult.DenialReason ?? "Budget limit exceeded",
                        normalizedInvoice);

                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = approvalResult.DenialReason,
                        budget = new
                        {
                            amountSats = amountSats.Value,
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
                                          "Request a new confirmation by calling pay_invoice without a nonce."
                            });
                        }

                        Console.Error.WriteLine($"[Lightning Enable] Payment of {approvalResult.AmountUsd:C} confirmed via nonce {confirmation.Nonce}");
                    }
                    else
                    {
                        // Try MCP elicitation first
                        var elicitationConfirmed = await RequestConfirmationAsync(
                            server,
                            approvalResult,
                            normalizedInvoice,
                            cancellationToken);

                        if (!elicitationConfirmed)
                        {
                            // Check if elicitation was even available
                            var elicitationAvailable = server?.ClientCapabilities?.Elicitation != null;

                            if (!elicitationAvailable)
                            {
                                // Create a pending confirmation with a nonce
                                var invoicePrefix = normalizedInvoice.Substring(0, Math.Min(30, normalizedInvoice.Length)) + "...";
                                var pending = budgetService.CreatePendingConfirmation(
                                    amountSats.Value,
                                    approvalResult.AmountUsd,
                                    "pay_invoice",
                                    invoicePrefix);

                                return JsonSerializer.Serialize(new
                                {
                                    success = false,
                                    requiresConfirmation = true,
                                    error = "Payment requires your confirmation",
                                    message = $"This payment of {approvalResult.AmountUsd:C} ({amountSats.Value:N0} sats) exceeds the auto-approve threshold.",
                                    nonce = pending.Nonce,
                                    howToConfirm = $"Step 1: Call confirm_payment(nonce: \"{pending.Nonce}\") to approve.\n" +
                                                   $"Step 2: Call pay_invoice(invoice=\"...\", confirmationNonce=\"{pending.Nonce}\") to proceed.",
                                    expiresInSeconds = 120,
                                    amount = new
                                    {
                                        sats = amountSats.Value,
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
                                error = "Payment cancelled by user",
                                requiresConfirmation = true,
                                amount = new
                                {
                                    sats = amountSats.Value,
                                    usd = approvalResult.AmountUsd
                                }
                            });
                        }
                    }
                }

                // Log if needed
                if (approvalResult.Level == ApprovalLevel.LogAndApprove)
                {
                    Console.Error.WriteLine($"[Lightning Enable] Auto-approved payment: {approvalResult.AmountUsd:C} ({amountSats.Value} sats)");
                }
            }

            // Pay the invoice
            var result = await walletService.PayInvoiceAsync(normalizedInvoice, cancellationToken);

            if (!result.Success)
            {
                paymentHistory?.RecordFailedPayment(
                    "direct-invoice",
                    "PAY",
                    amountSats.Value,
                    result.ErrorMessage ?? "Unknown error",
                    normalizedInvoice);

                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage,
                    errorCode = result.ErrorCode
                });
            }

            // Record the payment
            budgetService?.RecordSpend(amountSats.Value);
            budgetService?.RecordPaymentTime();
            paymentHistory?.RecordPayment(
                "direct-invoice",
                "PAY",
                amountSats.Value,
                normalizedInvoice,
                result.PreimageHex,
                null,
                200);

            var amountUsd = priceService != null
                ? await priceService.SatsToUsdAsync(amountSats.Value, cancellationToken)
                : 0m;

            // Check if preimage is available (some wallets like OpenNode don't return it)
            if (!result.HasPreimage)
            {
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    preimage = (string?)null,
                    trackingId = result.TrackingId,
                    message = "Payment successful",
                    warning = result.ErrorMessage ?? "Preimage not available from this wallet. L402 verification will not work.",
                    payment = new
                    {
                        amountSats = amountSats.Value,
                        amountUsd = Math.Round(amountUsd, 2),
                        invoice = normalizedInvoice.Substring(0, Math.Min(30, normalizedInvoice.Length)) + "..."
                    }
                });
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                preimage = result.PreimageHex,
                message = "Payment successful",
                payment = new
                {
                    amountSats = amountSats.Value,
                    amountUsd = Math.Round(amountUsd, 2),
                    invoice = normalizedInvoice.Substring(0, Math.Min(30, normalizedInvoice.Length)) + "..."
                }
            });
        }
        catch (Exception ex)
        {
            paymentHistory?.RecordFailedPayment(
                "direct-invoice",
                "PAY",
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
    /// Requests user confirmation based on the approval level.
    /// </summary>
    private static async Task<bool> RequestConfirmationAsync(
        McpServer? server,
        ApprovalCheckResult approvalResult,
        string invoice,
        CancellationToken cancellationToken)
    {
        // If no server or elicitation not supported, auto-deny for safety
        if (server?.ClientCapabilities?.Elicitation == null)
        {
            Console.Error.WriteLine($"[Lightning Enable] Payment of {approvalResult.AmountUsd:C} requires confirmation but elicitation not supported by client");
            Console.Error.WriteLine("[Lightning Enable] For payments requiring confirmation, use a client that supports MCP elicitation");
            return false;
        }

        try
        {
            if (approvalResult.Level == ApprovalLevel.FormConfirm)
            {
                // Form-based confirmation (in-band)
                var schema = new ElicitRequestParams.RequestSchema
                {
                    Properties =
                    {
                        ["approved"] = new ElicitRequestParams.BooleanSchema
                        {
                            Description = "Set to true to approve this payment"
                        }
                    }
                };

                var response = await server.ElicitAsync(new ElicitRequestParams
                {
                    Message = $"Payment Confirmation Required\n\n" +
                              $"Amount: {approvalResult.AmountUsd:C} ({approvalResult.AmountSats:N0} sats)\n" +
                              $"Invoice: {invoice.Substring(0, Math.Min(40, invoice.Length))}...\n\n" +
                              $"Do you approve this payment?",
                    RequestedSchema = schema
                }, cancellationToken);

                if (response.Action == "accept" &&
                    response.Content?.TryGetValue("approved", out var approvedElement) == true)
                {
                    return approvedElement.ValueKind == JsonValueKind.True;
                }

                return false;
            }
            else if (approvalResult.Level == ApprovalLevel.UrlConfirm)
            {
                // URL-based confirmation (out-of-band)
                // For now, we'll use form confirmation with a stronger message
                // In production, this would redirect to a secure confirmation page
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
                    Message = $"LARGE PAYMENT - Verification Required\n\n" +
                              $"Amount: {approvalResult.AmountUsd:C} ({approvalResult.AmountSats:N0} sats)\n" +
                              $"Invoice: {invoice.Substring(0, Math.Min(40, invoice.Length))}...\n\n" +
                              $"This is a significant payment. Please verify:\n" +
                              $"- You initiated this purchase\n" +
                              $"- The amount is correct\n" +
                              $"- You trust the recipient\n\n" +
                              $"Type the payment amount in USD to confirm (e.g., {approvalResult.AmountUsd:F2}):",
                    RequestedSchema = schema
                }, cancellationToken);

                if (response.Action == "accept" &&
                    response.Content?.TryGetValue("confirmAmount", out var amountElement) == true)
                {
                    var enteredAmount = amountElement.GetString();
                    // Accept if they entered the correct amount (with some tolerance for formatting)
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
            Console.Error.WriteLine($"[Lightning Enable] Elicitation failed: {ex.Message}");
            return false;
        }

        return false;
    }

}
