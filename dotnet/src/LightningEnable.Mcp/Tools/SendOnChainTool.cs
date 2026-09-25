using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for sending on-chain Bitcoin payments.
/// </summary>
[McpServerToolType]
public static class SendOnChainTool
{
    /// <summary>
    /// Sends an on-chain Bitcoin payment to a Bitcoin address.
    /// </summary>
    /// <param name="address">Bitcoin address to send to.</param>
    /// <param name="amountSats">Amount to send in satoshis.</param>
    /// <param name="walletService">Injected wallet service.</param>
    /// <param name="budgetService">Injected budget service for spending limits.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Payment result with transaction details.</returns>
    [McpServerTool(Name = "send_onchain", Title = "Send on-chain (deprecated)", ReadOnly = false, Destructive = true), Description("Send an on-chain Bitcoin payment to a Bitcoin address. Currently only available with Strike wallet.")]
    public static async Task<string> SendOnChain(
        [Description("Bitcoin address to send to (e.g., bc1q...)")] string address,
        [Description("Amount to send in satoshis")] long amountSats,
        [Description("Confirmation code the human operator read from the server console. Required to actually send — on-chain payments are irreversible and ALWAYS require confirmation. Omit on the first call to request one.")] string? confirmationNonce = null,
        IWalletService? walletService = null,
        IBudgetService? budgetService = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Bitcoin address is required"
            });
        }

        // Normalize once so validation and the actual send use the SAME value
        // (the validator trims internally; without this an input like " bc1…"
        // would pass validation but a different string would reach the wallet).
        address = address.Trim();

        // C-2: validate the address before doing anything else. On-chain sends are
        // irreversible, so a typo'd, garbage, or wrong-network address must be
        // rejected here rather than risk broadcasting funds to an unrecoverable
        // destination. Only valid mainnet addresses pass.
        if (!BitcoinAddressValidator.IsValidMainnet(address))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Invalid Bitcoin address. Provide a valid mainnet Bitcoin address " +
                        "(starts with bc1, 1, or 3). The address failed validation and was NOT sent — " +
                        "on-chain payments are irreversible, so a malformed or wrong-network address is rejected."
            });
        }

        if (amountSats <= 0)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Amount must be greater than 0 sats"
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
                error = "Wallet not configured. Set STRIKE_API_KEY environment variable for on-chain payments."
            });
        }

        // C-2b: on-chain sends are IRREVERSIBLE, so they ALWAYS require explicit human
        // confirmation — regardless of amount/tier — via the out-of-band code flow (the
        // code is printed to stderr, never to the model). Budget limits are still
        // enforced: an over-limit amount (or a price outage) is refused outright, since
        // confirmation must not authorize a payment beyond the configured ceiling.
        // FAIL CLOSED: an irreversible on-chain send must never bypass the confirmation
        // gate. If the budget/confirmation service isn't available, refuse rather than send.
        if (budgetService == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Budget/confirmation service is unavailable, so this on-chain send was refused " +
                        "(fail-closed). On-chain payments are irreversible and must go through the confirmation gate."
            });
        }

        {
            var approval = await budgetService.CheckApprovalLevelAsync(amountSats, cancellationToken);
            if (approval.Level == ApprovalLevel.Deny)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"Budget check failed: {approval.DenialReason}"
                });
            }

            if (!string.IsNullOrWhiteSpace(confirmationNonce))
            {
                var confirmation = budgetService.ValidateAndConsumeConfirmation(
                    confirmationNonce.Trim().ToUpperInvariant(), amountSats, "send_onchain", address);
                if (confirmation == null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "Confirmation code is invalid, expired, already used, or does not match THIS " +
                                "send's amount, tool, and address. Codes are bound to the exact amount, tool, and " +
                                "destination they were approved for — a code cannot be redirected to a different address.",
                        message = "Request a fresh confirmation by calling send_onchain again without a confirmationNonce, then supply the new code."
                    });
                }
                Console.Error.WriteLine($"[Lightning Enable] On-chain send of {amountSats:N0} sats to {address} confirmed.");
            }
            else
            {
                // Code to the human on the CONFIGURED approval channel — the model never sees it,
                // on any channel. On a "refuse" server there is no code at all and the
                // (irreversible) send is turned down rather than left half-approved.
                var dispatch = await budgetService.RequestConfirmationAsync(new ConfirmationRequest
                {
                    AmountSats = amountSats,
                    AmountUsd = approval.AmountUsd,
                    ToolName = "send_onchain",
                    Description = address,
                    Destination = address,
                    Title = "ON-CHAIN SEND CONFIRMATION REQUIRED (irreversible)",
                    Summary = $"send_onchain — {amountSats:N0} sats to {address}"
                }, cancellationToken);

                if (!dispatch.Delivered)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        requiresConfirmation = false,
                        confirmationChannel = dispatch.ChannelName,
                        error = dispatch.RefusalReason,
                        message = "The send was REFUSED, not queued for approval — no human can be asked for a code on " +
                                  "this server. Retrying will not help until the operator changes the configuration.",
                        amount = new { sats = amountSats, usd = Math.Round(approval.AmountUsd, 2) }
                    });
                }

                return JsonSerializer.Serialize(new
                {
                    success = false,
                    requiresConfirmation = true,
                    confirmationChannel = dispatch.ChannelName,
                    error = "On-chain send requires human confirmation",
                    message = $"On-chain sends are irreversible, so this {amountSats:N0}-sat send to {address} requires confirmation. " +
                              $"A confirmation code was {dispatch.OperatorHint} — visible to the human operator, NOT to you. " +
                              "Ask the human to read that code and give it to you.",
                    howToConfirm = "Ask the human operator for the confirmation code shown in the server console, then call " +
                                   "send_onchain(address=\"...\", amountSats=..., confirmationNonce=\"<code-from-human>\").",
                    expiresInSeconds = 120,
                    amount = new { sats = amountSats, usd = Math.Round(approval.AmountUsd, 2) }
                });
            }
        }

        // Ambient payment intent: the durable receipt is written at the wallet seam
        // (ReceiptRecordingWalletService) when the send succeeds. The destination
        // address is public chain data, so it is safe as receipt context. Reaching
        // this point requires the human confirmation code to have been consumed
        // above, so the policy is always human-confirmed.
        using var receiptScope = PaymentReceiptScope.Begin(
            "onchain", context: address, policy: PaymentPolicy.HumanConfirmed);

        // Reserve principal + a fee headroom BEFORE broadcasting. On-chain fees are added by
        // the provider ON TOP of the principal, so reserving (and checking) only the principal
        // — as the approval check above does — would let the final debit (principal + fee)
        // exceed the session cap. Reserve the maximum, commit the actual debit, and the unused
        // headroom is released automatically. Headroom = max(1000 sats, 10% of principal).
        long feeHeadroomSats = Math.Max(1000, amountSats / 10);
        var reservation = await budgetService.TryReserveAsync(amountSats + feeHeadroomSats, cancellationToken);
        if (!reservation.Success)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Budget check failed: {reservation.DenialReason}"
            });
        }
        var reservationId = reservation.ReservationId!;

        OnChainPaymentResult? result;
        try
        {
            result = await walletService.SendOnChainAsync(address, amountSats, cancellationToken);
        }
        catch (Exception ex)
        {
            // A throw (including OperationCanceledException) carries no proof of WHERE the
            // send stopped: it may have been after the provider executed it. On-chain funds
            // are irreversible, so treat it as AMBIGUOUS — retain the budget (principal +
            // headroom), never release it — and tell the agent not to re-send. The idempotency
            // ledger has recorded the operation as Unknown, so a retry reports status.
            budgetService.CommitReservation(reservationId, amountSats + feeHeadroomSats);
            Console.Error.WriteLine($"[Lightning Enable] On-chain send ended with {ex.GetType().Name}; outcome unknown, budget retained.");
            return AmbiguousResponse(walletService, receiptScope, amountSats, address,
                state: "UNKNOWN", paymentId: null, quoteId: null, txId: null,
                errorCode: ex is OperationCanceledException ? "CANCELLED" : "EXCEPTION",
                error: ex is OperationCanceledException
                    ? "The on-chain send was cancelled or timed out before its outcome was known."
                    : $"The on-chain send failed with an unexpected {ex.GetType().Name} before its outcome was known.");
        }

        if (result is null)
        {
            budgetService.CommitReservation(reservationId, amountSats + feeHeadroomSats);
            return AmbiguousResponse(walletService, receiptScope, amountSats, address,
                "UNKNOWN", null, null, null, "INVALID_RESPONSE", "The wallet returned no on-chain result.");
        }

        if (result.Duplicate)
        {
            // Refused as a duplicate: THIS call sent nothing. The original attempt already
            // retained its budget, so this call's fresh reservation is released.
            budgetService.ReleaseReservation(reservationId);
            return JsonSerializer.Serialize(new
            {
                success = false,
                duplicate = true,
                errorCode = result.ErrorCode,
                error = result.ErrorMessage,
                state = result.State,
                paymentId = result.PaymentId,
                quoteId = result.QuoteId,
                txId = result.TxId,
                provider = walletService.ProviderName,
                receipt_written = false,
                warning = DuplicateWarning(result, walletService.ProviderName)
            });
        }

        if (result.Success)
        {
            // Commit the ACTUAL debit (principal + network fee). Committing less than the
            // reserved maximum automatically releases the unused fee headroom.
            budgetService.CommitReservation(reservationId, amountSats + result.FeeSats);

            var completed = string.Equals(result.State, "COMPLETED", StringComparison.OrdinalIgnoreCase);
            return JsonSerializer.Serialize(new
            {
                success = true,
                provider = walletService.ProviderName,
                receipt_written = receiptScope.ReceiptWritten ?? false,
                payment = new
                {
                    id = result.PaymentId,
                    txId = result.TxId,
                    state = result.State,
                    amountSats = result.AmountSats,
                    feeSats = result.FeeSats
                },
                message = completed
                    ? $"On-chain payment of {amountSats} sats sent to {address}"
                    : $"On-chain payment initiated (status: {result.State})",
                note = completed
                    ? null
                    : PendingNote
            });
        }

        if (result.IsAmbiguous)
        {
            // Submitted but the outcome is unknown (timeout / cancellation / transport error
            // after the execute call was issued). Funds may have moved: RETAIN the budget —
            // principal + known fee, else principal + headroom — never release it.
            var debit = amountSats + (result.FeeSats > 0 ? result.FeeSats : feeHeadroomSats);
            budgetService.CommitReservation(reservationId, debit);
            return AmbiguousResponse(walletService, receiptScope, amountSats, address,
                result.State ?? "UNKNOWN", result.PaymentId, result.QuoteId, result.TxId,
                result.ErrorCode, result.ErrorMessage);
        }

        // Proven failure: either the send never reached the provider's money-moving call
        // (Submitted = false) or the provider reported the payment terminally FAILED. No
        // funds moved — release the reservation and its headroom.
        budgetService.ReleaseReservation(reservationId);
        return JsonSerializer.Serialize(new
        {
            success = false,
            error = result.ErrorMessage,
            errorCode = result.ErrorCode,
            state = result.State,
            paymentId = result.PaymentId,
            receipt_written = receiptScope.ReceiptWritten,
            hint = result.ErrorCode == "NOT_SUPPORTED"
                ? $"{walletService.ProviderName} does not support on-chain payments. Use Strike wallet."
                : null,
            warning = FailureWarning
        });
    }

    /// <summary>Shared with the Python port — keep the text aligned.</summary>
    internal const string FailureWarning =
        "If this failure was a network/timeout error, the send may still have executed at the provider. " +
        "Check the provider dashboard / get_balance BEFORE retrying — on-chain payments are irreversible.";

    /// <summary>Shared with the Python port — keep the text aligned.</summary>
    internal const string PendingNote =
        "On-chain payments normally stay PENDING for ~10 minutes until confirmed. Do NOT send again: calling " +
        "send_onchain again with the same address and amount will report this payment's status rather than re-send.";

    private static string AmbiguousWarning(string? paymentId, string? quoteId, string provider) =>
        "The on-chain send may have executed: it was submitted to " + provider + " but its outcome is not confirmed. " +
        "On-chain payments are irreversible. The budget for this send has been retained (not released). " +
        "Calling send_onchain again with the same address and amount will report status rather than re-send. " +
        "Verify the payment at the provider (" +
        (!string.IsNullOrEmpty(paymentId) ? $"payment id {paymentId}"
            : !string.IsNullOrEmpty(quoteId) ? $"quote id {quoteId}"
            : "check recent payments") +
        ") before taking any other action.";

    private static string DuplicateWarning(OnChainPaymentResult result, string provider) =>
        "Nothing was sent by this call: an on-chain send for the same address and amount was already submitted" +
        (!string.IsNullOrEmpty(result.PaymentId) ? $" (payment id {result.PaymentId})" : "") +
        ". On-chain payments are irreversible, so it will not be sent again while that payment may have moved funds. " +
        "Verify its status at the provider (" + provider + ") before doing anything else.";

    private static string AmbiguousResponse(
        IWalletService walletService, PaymentReceiptScope receiptScope, long amountSats, string address,
        string state, string? paymentId, string? quoteId, string? txId, string? errorCode, string? error)
    {
        var provider = walletService.ProviderName;
        return JsonSerializer.Serialize(new
        {
            success = false,
            state,
            paymentId,
            quoteId,
            txId,
            provider,
            errorCode,
            error = error ?? "The on-chain send's outcome is unknown.",
            receipt_written = receiptScope.ReceiptWritten ?? false,
            amountSats,
            warning = AmbiguousWarning(paymentId, quoteId, provider)
        });
    }
}
