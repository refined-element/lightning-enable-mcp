namespace LightningEnable.Mcp.Models;

/// <summary>
/// Record of an L402 payment made during this session.
/// </summary>
public record PaymentRecord
{
    /// <summary>
    /// Unique identifier for this payment record.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The URL that was accessed.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Amount paid in satoshis.
    /// </summary>
    public required long AmountSats { get; init; }

    /// <summary>
    /// When the payment was made.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// HTTP method used (GET, POST, etc.).
    /// </summary>
    public required string Method { get; init; }

    /// <summary>
    /// Whether the payment was successful.
    /// </summary>
    public bool Success { get; init; } = true;

    /// <summary>
    /// The BOLT11 invoice that was paid.
    /// </summary>
    public string? Invoice { get; init; }

    /// <summary>
    /// The preimage proving payment (hex).
    /// </summary>
    public string? PreimageHex { get; init; }

    /// <summary>
    /// The L402 token received (macaroon:preimage).
    /// </summary>
    public string? L402Token { get; init; }

    /// <summary>
    /// HTTP status code of the final response.
    /// </summary>
    public int? ResponseStatusCode { get; init; }

    /// <summary>
    /// Error message if the request failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Summary of payment history for a session.
/// </summary>
public record PaymentHistorySummary
{
    /// <summary>
    /// Total number of payments made.
    /// </summary>
    public int TotalPayments { get; init; }

    /// <summary>
    /// Total satoshis spent.
    /// </summary>
    public long TotalSatsSpent { get; init; }

    /// <summary>
    /// Number of successful payments.
    /// </summary>
    public int SuccessfulPayments { get; init; }

    /// <summary>
    /// Number of failed payments.
    /// </summary>
    public int FailedPayments { get; init; }

    /// <summary>
    /// List of payment records.
    /// </summary>
    public required IReadOnlyList<PaymentRecord> Payments { get; init; }
}
