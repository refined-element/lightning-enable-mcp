using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for manually paying an L402 or MPP (Machine Payments Protocol) invoice.
/// Use this when you have received a 402 response and want to pay it manually
/// to get the authorization token for subsequent requests.
/// Supports both L402 (macaroon + preimage) and MPP (preimage only) protocols.
/// </summary>
[McpServerToolType]
public static class PayL402ChallengeTool
{
    /// <summary>
    /// Manually pays an L402 or MPP invoice and returns the authorization token.
    /// </summary>
    [McpServerTool(Name = "pay_l402_challenge"), Description("Manually pay an L402 or MPP Lightning invoice to get the authentication token. Omit macaroon for MPP mode.")]
    public static async Task<string> PayL402Challenge(
        [Description("BOLT11 Lightning invoice string from the L402 challenge")] string invoice,
        [Description("Base64-encoded macaroon from the L402 challenge. Optional for MPP (Machine Payments Protocol) where only invoice + preimage are needed.")] string? macaroon = null,
        [Description("Maximum satoshis allowed to pay. Defaults to 1000")] int maxSats = 1000,
        [Description("Confirmation code relayed by the human operator from the server console (stderr). Required when a previous call returned requiresConfirmation=true.")] string? confirmationNonce = null,
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

        if (l402Client == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "L402 HTTP client not available"
            });
        }

        // Ambient payment intent for the wallet-seam receipt writer. "manual_payment"
        // matches what this flow has always recorded in payment history — the challenge
        // was handed to the tool directly, so no endpoint URL exists to record.
        using var receiptScope = PaymentReceiptScope.Begin("l402", context: "manual_payment");

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
                receiptScope.Policy = AccessL402ResourceTool.PolicyString(approvalResult.Level);

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
                        var confirmation = budgetService.ValidateAndConsumeConfirmation(confirmationNonce.Trim().ToUpperInvariant(), approvalResult.AmountSats, "pay_l402_challenge", normalizedInvoice);
                        if (confirmation == null)
                        {
                            return JsonSerializer.Serialize(new
                            {
                                success = false,
                                error = "Confirmation code is invalid, expired, already used, or does not match THIS " +
                                        "payment's amount, tool, and invoice. Codes are bound to the exact amount, tool, and " +
                                        "destination approved — a code cannot be redirected to a different invoice.",
                                message = "The code may have expired (2-minute limit), been used already, or been issued for a " +
                                          "different amount/tool/invoice. Request a new confirmation by calling pay_l402_challenge without a confirmationNonce."
                            });
                        }

                        Console.Error.WriteLine($"[Lightning Enable] L402 challenge payment of {approvalResult.AmountUsd:C} confirmed via nonce {confirmation.Nonce}");
                    }
                    else
                    {
                        // Try MCP elicitation first (most clients don't support this yet)
                        var elicitationConfirmed = await RequestL402ChallengeConfirmationAsync(
                            server,
                            approvalResult,
                            normalizedInvoice,
                            cancellationToken);

                        if (!elicitationConfirmed)
                        {
                            // Always fall back to nonce-based confirmation.
                            // MCP elicitation is unreliable — many clients (including Claude Code)
                            // report Elicitation capability but don't handle it correctly.
                            var invoicePrefix = normalizedInvoice.Substring(0, Math.Min(30, normalizedInvoice.Length)) + "...";
                            var pending = budgetService.CreatePendingConfirmation(
                                budgetCheckAmount,
                                approvalResult.AmountUsd,
                                "pay_l402_challenge",
                                invoicePrefix,
                                normalizedInvoice);

                            // OUT-OF-BAND CONFIRMATION: code to STDERR only (human sees the server
                            // console/logs; the model only sees tool results). An injected agent
                            // can't read it to self-approve. The code MUST NOT appear in the result.
                            Console.Error.WriteLine(
                                "[Lightning Enable] *** L402 CHALLENGE PAYMENT CONFIRMATION REQUIRED ***\n" +
                                $"  pay_l402_challenge — {approvalResult.AmountUsd:C} ({budgetCheckAmount:N0} sats), invoice {invoicePrefix}\n" +
                                $"  Confirmation code: {pending.Nonce}\n" +
                                "  To approve, give this code to the agent. Expires in 120s.");

                            return JsonSerializer.Serialize(new
                            {
                                success = false,
                                requiresConfirmation = true,
                                error = "L402 challenge payment requires human confirmation",
                                message = $"This payment of {approvalResult.AmountUsd:C} ({budgetCheckAmount:N0} sats) exceeds the auto-approve threshold. " +
                                          "A confirmation code was printed to the server console/logs — visible to the human operator, NOT to you. " +
                                          "Ask the human to read that code and give it to you.",
                                howToConfirm = "Ask the human operator for the confirmation code shown in the server console, then call " +
                                               "pay_l402_challenge(invoice=\"...\", macaroon=\"...\", confirmationNonce=\"<code-from-human>\").",
                                expiresInSeconds = 120,
                                amount = new
                                {
                                    sats = budgetCheckAmount,
                                    usd = Math.Round(approvalResult.AmountUsd, 2)
                                },
                                thresholds = new
                                {
                                    autoApprove = budgetService.GetUserConfiguration().Tiers.AutoApprove,
                                    note = "Payments above this require a human-supplied confirmation code"
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

            var token = await l402Client.PayChallengeAsync(macaroon, normalizedInvoice, maxSats, cancellationToken);

            var isMpp = string.IsNullOrWhiteSpace(macaroon);
            var protocolName = isMpp ? "MPP" : "L402";

            // Budget recording and payment history are handled by L402HttpClient.PayChallengeAsync()

            var amountUsd = priceService != null
                ? await priceService.SatsToUsdAsync(budgetCheckAmount, cancellationToken)
                : 0m;

            var headerValue = isMpp
                ? $"Payment method=\"lightning\", preimage=\"{token}\""
                : $"L402 {token}";

            return JsonSerializer.Serialize(new
            {
                success = true,
                l402Token = token,
                protocol = protocolName,
                receipt_written = receiptScope.ReceiptWritten ?? false,
                payment = new
                {
                    amountSats = budgetCheckAmount,
                    amountUsd = Math.Round(amountUsd, 2)
                },
                usage = new
                {
                    headerName = "Authorization",
                    headerValue,
                    protocol = protocolName,
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

            // PayChallengeAsync throws AFTER money moved on the pending and
            // settled-without-preimage paths — the seam has already written the
            // receipt there, so surface its outcome: null = nothing was paid,
            // true/false = money moved and the receipt did/didn't land.
            return JsonSerializer.Serialize(new
            {
                success = false,
                receipt_written = receiptScope.ReceiptWritten,
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
