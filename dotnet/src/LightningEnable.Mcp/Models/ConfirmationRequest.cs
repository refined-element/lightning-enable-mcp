namespace LightningEnable.Mcp.Models;

/// <summary>
/// What a payment tool asks the approval channel to deliver. Everything here is
/// operator-facing; none of it (and above all no confirmation code) is returned to the model.
/// </summary>
public record ConfirmationRequest
{
    /// <summary>Amount the code authorizes, in sats. Bound to the code on consume.</summary>
    public required long AmountSats { get; init; }

    /// <summary>Amount in USD, for the human reading the notification.</summary>
    public required decimal AmountUsd { get; init; }

    /// <summary>Tool the code authorizes (<c>pay_invoice</c>, ...). Bound on consume.</summary>
    public required string ToolName { get; init; }

    /// <summary>Display string for the target (may be truncated/redacted).</summary>
    public required string Description { get; init; }

    /// <summary>The exact payment target the code authorizes. Bound on consume.</summary>
    public required string Destination { get; init; }

    /// <summary>Banner for the stderr channel, e.g. "PAYMENT CONFIRMATION REQUIRED".</summary>
    public required string Title { get; init; }

    /// <summary>One-line human summary, e.g. "pay_invoice — $50.00 (50,000 sats), invoice lnbc...".</summary>
    public required string Summary { get; init; }
}

/// <summary>Outcome of handing a pending confirmation to the configured channel.</summary>
public readonly record struct ConfirmationDeliveryResult(bool Success, string? Error)
{
    public static ConfirmationDeliveryResult Ok() => new(true, null);

    public static ConfirmationDeliveryResult Fail(string error) => new(false, error);
}

/// <summary>
/// What a payment tool gets back from <c>IBudgetService.RequestConfirmationAsync</c>.
/// Either a pending confirmation whose code went out on the configured channel, or a
/// refusal — there is no third state, and a refusal never leaves a usable code behind.
/// </summary>
public record ConfirmationDispatchResult
{
    /// <summary>True when a code was generated AND the channel accepted it.</summary>
    public bool Delivered { get; init; }

    /// <summary>The pending confirmation. Null on a refusal.</summary>
    public PendingConfirmation? Pending { get; init; }

    /// <summary>Which channel handled (or refused) the request.</summary>
    public ConfirmationChannelKind Channel { get; init; }

    /// <summary>Lower-case channel name for tool results and logs.</summary>
    public string ChannelName => Channel.ToString().ToLowerInvariant();

    /// <summary>
    /// Agent-safe description of WHERE the code went ("printed to the server console/logs",
    /// "sent to the operator approval webhook", ...). Never contains the code.
    /// </summary>
    public string? OperatorHint { get; init; }

    /// <summary>Descriptive, operator-actionable reason when <see cref="Delivered"/> is false.</summary>
    public string? RefusalReason { get; init; }

    public static ConfirmationDispatchResult Refused(ConfirmationChannelKind channel, string reason) =>
        new() { Delivered = false, Channel = channel, RefusalReason = reason };

    public static ConfirmationDispatchResult DeliveredTo(
        ConfirmationChannelKind channel, PendingConfirmation pending, string operatorHint) =>
        new() { Delivered = true, Channel = channel, Pending = pending, OperatorHint = operatorHint };
}
