using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// What differs between the tools that pay a first-party Lightning Enable L402 challenge
/// (<c>create_lightning_enable_account</c>, <c>renew_lightning_enable_subscription</c>).
/// Everything else about the gate is shared in <see cref="FirstPartyPaymentGate"/>.
/// </summary>
internal sealed record FirstPartyPaymentGateOptions
{
    /// <summary>Tool name the confirmation code is bound to.</summary>
    public required string ToolName { get; init; }

    /// <summary>Exact destination URL the confirmation code is bound to.</summary>
    public required string Destination { get; init; }

    /// <summary>Ceiling this call may pay; the budget gate and the code are bound to it.</summary>
    public required long MaxSats { get; init; }

    /// <summary>Capitalized label, e.g. "Account activation" — used in the denial, error and log lines.</summary>
    public required string Label { get; init; }

    /// <summary>Lowercase noun, e.g. "activation" — used in "THIS activation's amount", "This activation may cost".</summary>
    public required string Noun { get; init; }

    /// <summary><see cref="ConfirmationRequest.Title"/> (stderr banner).</summary>
    public required string ConfirmationTitle { get; init; }

    /// <summary><see cref="ConfirmationRequest.Description"/>.</summary>
    public required string ConfirmationDescription { get; init; }

    /// <summary>Builds <see cref="ConfirmationRequest.Summary"/> from the approval.</summary>
    public required Func<ApprovalCheckResult, string> ConfirmationSummary { get; init; }

    /// <summary>The "howToConfirm" sentence in the requires-confirmation result.</summary>
    public required string HowToConfirm { get; init; }

    /// <summary>Builds the MCP elicitation prompt from the approval.</summary>
    public required Func<ApprovalCheckResult, string> ElicitationMessage { get; init; }

    /// <summary>Appended to the auto-approve stderr line (e.g. " for agent@example.com").</summary>
    public string AutoApproveLogSuffix { get; init; } = "";
}

/// <summary>
/// The budget / out-of-band confirmation gate shared by the tools that pay a first-party
/// Lightning Enable L402 challenge, plus the paid-aware exception result they both need.
///
/// The gate runs BEFORE any request: Deny → a refusal result; an above-threshold amount →
/// either the operator's code is validated and consumed (bound to amount, tool and
/// destination), MCP elicitation approves it, or a code is dispatched to the configured
/// approval channel and the tool returns requiresConfirmation (the code never enters the
/// result, so an injected agent cannot approve itself).
/// </summary>
internal static class FirstPartyPaymentGate
{
    /// <summary>
    /// Runs the gate. <c>Blocked</c> non-null = return it as the tool result and do not pay.
    /// <c>Policy</c> is the budget policy label for the receipt scope (null when no budget service).
    /// </summary>
    public static async Task<(string? Blocked, string? Policy)> CheckAsync(
        FirstPartyPaymentGateOptions options,
        string? confirmationNonce,
        McpServer? server,
        IBudgetService? budgetService,
        CancellationToken cancellationToken)
    {
        if (budgetService == null)
        {
            return (null, null);
        }

        var maxSats = options.MaxSats;
        var approval = await budgetService.CheckApprovalLevelAsync(maxSats, cancellationToken);
        var policy = PaymentPolicy.Label(approval.Level);

        if (approval.Level == ApprovalLevel.Deny)
        {
            return (JsonSerializer.Serialize(new
            {
                success = false,
                error = $"{options.Label} denied by budget policy: {approval.DenialReason}",
                budget = new
                {
                    amountSats = maxSats,
                    amountUsd = approval.AmountUsd,
                    remainingSessionUsd = approval.RemainingSessionBudgetUsd
                }
            }), policy);
        }

        if (approval.RequiresConfirmation)
        {
            if (!string.IsNullOrWhiteSpace(confirmationNonce))
            {
                var confirmation = budgetService.ValidateAndConsumeConfirmation(
                    confirmationNonce.Trim().ToUpperInvariant(), maxSats, options.ToolName, options.Destination);
                if (confirmation == null)
                {
                    return (JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "Confirmation code is invalid, expired, already used, or does not match THIS " +
                                $"{options.Noun}'s amount, tool, and destination. Codes are bound to the exact amount, " +
                                "tool, and destination approved — a code cannot be redirected.",
                        message = "The code may have expired (codes are short-lived), been used already, or been issued " +
                                  "for a different amount/tool. Request a new one by calling " +
                                  $"{options.ToolName} without a confirmationNonce."
                    }), policy);
                }

                Console.Error.WriteLine($"[Lightning Enable] {options.Label} of {approval.AmountUsd:C} confirmed by the operator's code");
            }
            else
            {
                var elicitationConfirmed = await RequestElicitationAsync(server, approval, options, cancellationToken);

                if (!elicitationConfirmed)
                {
                    // OUT-OF-BAND CONFIRMATION: the code goes to the human on the CONFIGURED
                    // approval channel (stderr locally; webhook/file/refuse when hosted) and
                    // never into this result, so an injected agent can't self-approve.
                    var dispatch = await budgetService.RequestConfirmationAsync(new ConfirmationRequest
                    {
                        AmountSats = maxSats,
                        AmountUsd = approval.AmountUsd,
                        ToolName = options.ToolName,
                        Description = options.ConfirmationDescription,
                        Destination = options.Destination,
                        Title = options.ConfirmationTitle,
                        Summary = options.ConfirmationSummary(approval)
                    }, cancellationToken);

                    if (!dispatch.Delivered)
                    {
                        return (JsonSerializer.Serialize(new
                        {
                            success = false,
                            requiresConfirmation = false,
                            confirmationChannel = dispatch.ChannelName,
                            error = dispatch.RefusalReason,
                            message = $"The {options.Noun} was REFUSED, not queued for approval — no human can be asked for a " +
                                      "code on this server. Retrying will not help until the operator changes the configuration.",
                            amount = new
                            {
                                maxSats,
                                usd = Math.Round(approval.AmountUsd, 2)
                            }
                        }), policy);
                    }

                    return (JsonSerializer.Serialize(new
                    {
                        success = false,
                        requiresConfirmation = true,
                        confirmationChannel = dispatch.ChannelName,
                        error = $"{options.Label} requires human confirmation",
                        message = $"This {options.Noun} may cost up to {approval.AmountUsd:C} ({maxSats:N0} sats), above the " +
                                  $"auto-approve threshold. A confirmation code was {dispatch.OperatorHint} — " +
                                  "visible to the human operator, NOT to you. Ask the human to read that code and give it to you.",
                        howToConfirm = options.HowToConfirm,
                        expiresInSeconds = dispatch.ExpiresInSeconds,
                        amount = new
                        {
                            maxSats,
                            usd = Math.Round(approval.AmountUsd, 2)
                        }
                    }), policy);
                }
            }
        }

        if (approval.Level == ApprovalLevel.LogAndApprove)
        {
            Console.Error.WriteLine(
                $"[Lightning Enable] Auto-approved {options.Label.ToLowerInvariant()}: {approval.AmountUsd:C} ({maxSats} sats){options.AutoApproveLogSuffix}");
        }

        return (null, policy);
    }

    /// <summary>
    /// The catch-block result for a tool that may have paid before it threw.
    /// <paramref name="safeError"/> must already be scrubbed/redacted by the caller.
    ///
    /// <para>The receipt scope is the only evidence available here: a non-null
    /// <see cref="PaymentReceiptScope.ReceiptWritten"/> means the wallet seam observed a
    /// payment under this call. Then the result carries <c>activation.paid</c> and an explicit
    /// do-not-retry warning — otherwise an agent reads a bare error as retryable and pays
    /// again. The amount is unknown on this path, so none is claimed. With no observed
    /// payment the plain error shape is kept.</para>
    /// </summary>
    public static string ExceptionResult(string safeError, PaymentReceiptScope? receiptScope, string supportContext)
    {
        var receiptWritten = receiptScope?.ReceiptWritten;
        if (receiptWritten == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                receipt_written = receiptWritten,
                error = safeError
            });
        }

        return JsonSerializer.Serialize(new
        {
            success = false,
            receipt_written = receiptWritten,
            error = safeError,
            activation = new { paid = true },
            warning = "A payment may have been taken from your wallet before this error. Do NOT re-run this tool or " +
                      $"you may pay again — contact support@lightningenable.com with {supportContext}."
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>A string property of a JSON object, or null.</summary>
    public static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Requests confirmation via MCP elicitation. Returns false when elicitation is
    /// unavailable (most clients), so the caller falls back to the out-of-band code flow.
    /// </summary>
    private static async Task<bool> RequestElicitationAsync(
        McpServer? server,
        ApprovalCheckResult approval,
        FirstPartyPaymentGateOptions options,
        CancellationToken cancellationToken)
    {
        if (server?.ClientCapabilities?.Elicitation == null)
        {
            Console.Error.WriteLine($"[Lightning Enable] {options.Label} of {approval.AmountUsd:C} requires confirmation but elicitation not supported by client");
            return false;
        }

        try
        {
            var schema = new ElicitRequestParams.RequestSchema
            {
                Properties =
                {
                    ["approved"] = new ElicitRequestParams.BooleanSchema
                    {
                        Description = $"Set to true to approve this Lightning Enable {options.Label.ToLowerInvariant()} payment"
                    }
                }
            };

            var response = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = options.ElicitationMessage(approval),
                RequestedSchema = schema
            }, cancellationToken);

            if (response.Action == "accept" &&
                response.Content?.TryGetValue("approved", out var approvedElement) == true)
            {
                return approvedElement.ValueKind == JsonValueKind.True;
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Lightning Enable] {options.Label} elicitation failed: {ex.Message}");
            return false;
        }
    }
}
