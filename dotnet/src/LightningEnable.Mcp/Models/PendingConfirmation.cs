namespace LightningEnable.Mcp.Models;

/// <summary>
/// Represents a pending payment confirmation that requires a separate tool call to approve.
/// The nonce acts as a one-time code that binds the confirmation to a specific payment amount.
/// </summary>
public record PendingConfirmation
{
    /// <summary>
    /// 6-character alphanumeric nonce code for confirmation.
    /// </summary>
    public string Nonce { get; init; } = string.Empty;

    /// <summary>
    /// The payment amount in satoshis this confirmation is bound to.
    /// </summary>
    public long AmountSats { get; init; }

    /// <summary>
    /// The payment amount in USD (for display purposes).
    /// </summary>
    public decimal AmountUsd { get; init; }

    /// <summary>
    /// Which tool requested this confirmation (e.g., "access_l402_resource", "pay_invoice").
    /// </summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable description of the payment (URL, invoice prefix, etc.).
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// When this confirmation was created.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// When this confirmation expires (2 minutes from creation).
    /// </summary>
    public DateTime ExpiresAt { get; init; }

    /// <summary>
    /// Whether this confirmation has expired.
    /// </summary>
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
}
