using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Shared receipt vocabulary: maps a budget approval level to the snake_case
/// policy label written into receipts.jsonl. Matches the Python runtime's
/// ApprovalLevel.value so the file carries one consistent policy string
/// regardless of which server (Python or .NET) wrote the line.
/// </summary>
public static class PaymentPolicy
{
    /// <summary>
    /// Label for a payment that only proceeds after the human-supplied
    /// confirmation code was validated (e.g. every on-chain send).
    /// </summary>
    public const string HumanConfirmed = "confirm";

    public static string Label(ApprovalLevel level) => level switch
    {
        ApprovalLevel.AutoApprove => "auto_approve",
        ApprovalLevel.LogAndApprove => "log_and_approve",
        ApprovalLevel.FormConfirm => "form_confirm",
        ApprovalLevel.UrlConfirm => "url_confirm",
        ApprovalLevel.Deny => "deny",
        _ => level.ToString().ToLowerInvariant(),
    };
}
