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
    /// Human-readable description of the payment (URL, invoice prefix, etc.). May be
    /// redacted/truncated for display — do NOT use it for binding; use <see cref="Destination"/>.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// The exact payment target this confirmation authorizes — the BOLT11 invoice
    /// (pay_invoice / pay_l402_challenge), the resource URL (access_l402_resource), or the
    /// on-chain address (send_onchain). Bound and checked on consume so a code can never be
    /// redirected to a different destination (#21). Distinct from <see cref="Description"/>.
    /// </summary>
    public string Destination { get; init; } = string.Empty;

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
