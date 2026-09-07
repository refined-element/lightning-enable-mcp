using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using Moq;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// Shared setup for the payment tools' approval-channel tests. The channel changes WHERE the
/// code goes; it must never change WHETHER the code reaches the model — so every tool's
/// "does not leak the code" test runs across all three delivering channels, and every tool has
/// a refusing-channel test proving no payment happens when no human can be asked.
/// </summary>
internal static class ConfirmationTestSetup
{
    /// <summary>A refusal an operator would actually see, used by the refuse-path tests.</summary>
    public const string RefusalReason =
        "This payment needs human approval, but this server has no approval channel "
        + "(confirmation.channel = \"refuse\"), so it was refused rather than approved.";

    /// <summary>Make the budget mock hand back a delivered confirmation on the given channel.</summary>
    public static PendingConfirmation SetupDelivered(
        Mock<IBudgetService> budget,
        ConfirmationChannelKind channel,
        string nonce,
        long amountSats,
        decimal amountUsd,
        string toolName)
    {
        var pending = new PendingConfirmation
        {
            Nonce = nonce,
            AmountSats = amountSats,
            AmountUsd = amountUsd,
            ToolName = toolName,
            Description = "test-destination",
            Destination = "test-destination",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(2)
        };

        budget.Setup(b => b.RequestConfirmationAsync(It.IsAny<ConfirmationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfirmationDispatchResult.DeliveredTo(channel, pending, HintFor(channel)));

        return pending;
    }

    /// <summary>Make the budget mock refuse: no code exists, so nothing can be approved.</summary>
    public static void SetupRefused(Mock<IBudgetService> budget)
    {
        budget.Setup(b => b.RequestConfirmationAsync(It.IsAny<ConfirmationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfirmationDispatchResult.Refused(ConfirmationChannelKind.Refuse, RefusalReason));
    }

    private static string HintFor(ConfirmationChannelKind channel) => channel switch
    {
        ConfirmationChannelKind.Stderr => "printed to the server console/logs",
        ConfirmationChannelKind.Webhook => "sent to the operator's approval webhook",
        ConfirmationChannelKind.File => "appended to the approval file",
        _ => "not delivered"
    };
}
