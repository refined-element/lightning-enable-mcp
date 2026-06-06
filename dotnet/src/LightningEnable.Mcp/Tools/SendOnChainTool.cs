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
    [McpServerTool(Name = "send_onchain"), Description("Send an on-chain Bitcoin payment to a Bitcoin address. Currently only available with Strike wallet.")]
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
                    confirmationNonce.Trim().ToUpperInvariant(), amountSats, "send_onchain");
                if (confirmation == null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "Confirmation code is invalid, expired, already used, or does not match THIS " +
                                "send's amount and tool. Codes are bound to the exact amount + tool they were approved for.",
                        message = "Request a fresh confirmation by calling send_onchain again without a confirmationNonce, then supply the new code."
                    });
                }
                Console.Error.WriteLine($"[Lightning Enable] On-chain send of {amountSats:N0} sats to {address} confirmed.");
            }
            else
            {
                var pending = budgetService.CreatePendingConfirmation(
                    amountSats, approval.AmountUsd, "send_onchain", address);

                // Code to STDERR only — the human sees it; the model never does.
                Console.Error.WriteLine(
                    "[Lightning Enable] *** ON-CHAIN SEND CONFIRMATION REQUIRED (irreversible) ***\n" +
                    $"  send_onchain — {amountSats:N0} sats to {address}\n" +
                    $"  Confirmation code: {pending.Nonce}\n" +
                    "  To approve, give this code to the agent. Expires in 120s.");

                return JsonSerializer.Serialize(new
                {
                    success = false,
                    requiresConfirmation = true,
                    error = "On-chain send requires human confirmation",
                    message = $"On-chain sends are irreversible, so this {amountSats:N0}-sat send to {address} requires confirmation. " +
                              "A confirmation code was printed to the server console/logs — visible to the human operator, NOT to you. " +
                              "Ask the human to read that code and give it to you.",
                    howToConfirm = "Ask the human operator for the confirmation code shown in the server console, then call " +
                                   "send_onchain(address=\"...\", amountSats=..., confirmationNonce=\"<code-from-human>\").",
                    expiresInSeconds = 120,
                    amount = new { sats = amountSats, usd = Math.Round(approval.AmountUsd, 2) }
                });
            }
        }

        try
        {
            var result = await walletService.SendOnChainAsync(address, amountSats, cancellationToken);

            if (!result.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage,
                    errorCode = result.ErrorCode,
                    hint = result.ErrorCode == "NOT_SUPPORTED"
                        ? $"{walletService.ProviderName} does not support on-chain payments. Use Strike wallet."
                        : null
                });
            }

            // Record spend if budget service available
            budgetService?.RecordSpend(amountSats + result.FeeSats);

            return JsonSerializer.Serialize(new
            {
                success = true,
                provider = walletService.ProviderName,
                payment = new
                {
                    id = result.PaymentId,
                    txId = result.TxId,
                    state = result.State,
                    amountSats = result.AmountSats,
                    feeSats = result.FeeSats
                },
                message = result.State == "COMPLETED"
                    ? $"On-chain payment of {amountSats} sats sent to {address}"
                    : $"On-chain payment initiated (status: {result.State})"
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
