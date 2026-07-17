namespace LightningEnable.Mcp.Models;

/// <summary>
/// Outcome of a payment in the session audit trail. Mirrors the Python port's
/// <c>PaymentRecord.status</c> ("success" / "failed" / "pending").
///
/// <para><b>Pending is not a success.</b> An in-flight payment may still FAIL. Recording
/// it as successful leaves the agent's audit trail permanently claiming a settled
/// payment for money that never arrived — so this is a distinct third outcome, not a
/// boolean.</para>
/// </summary>
public enum PaymentStatus
{
    /// <summary>The payment settled. The funds moved and the outcome is final.</summary>
    Success,

    /// <summary>The payment failed. No funds moved.</summary>
    Failed,

    /// <summary>
    /// Accepted but not settled — it may still succeed or fail. The funds are
    /// committed (so they count toward <see cref="PaymentHistorySummary.TotalSatsSpent"/>),
    /// but nothing has settled (so it never counts toward
    /// <see cref="PaymentHistorySummary.SuccessfulPayments"/>).
    /// </summary>
    Pending
}

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
    /// The outcome of this payment: settled, failed, or still in flight.
    /// </summary>
    public PaymentStatus Status { get; init; } = PaymentStatus.Success;

    /// <summary>
    /// Whether the payment settled successfully.
    ///
    /// <para>DERIVED from <see cref="Status"/> on purpose — it is deliberately NOT
    /// settable. This field previously had an <c>init</c> setter hardcoded to <c>true</c>
    /// by the recording path, which let a <see cref="PaymentStatus.Pending"/> payment be
    /// stamped as a success. Deriving it makes that class of bug unrepresentable: the
    /// only way to be successful is to actually have settled.</para>
    /// </summary>
    public bool Success => Status == PaymentStatus.Success;

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
    /// Total satoshis committed — settled payments PLUS in-flight ones.
    ///
    /// <para>Pending payments are counted here even though they are not successes: the
    /// funds are already committed, and under-counting them would let an agent retry its
    /// way past its own budget.</para>
    /// </summary>
    public long TotalSatsSpent { get; init; }

    /// <summary>
    /// Number of payments that actually SETTLED. Never includes pending payments —
    /// nothing has settled yet, and one that later fails must not have been counted.
    /// </summary>
    public int SuccessfulPayments { get; init; }

    /// <summary>
    /// Number of payments that failed outright. Never includes pending payments —
    /// they have not failed, they have not finished.
    /// </summary>
    public int FailedPayments { get; init; }

    /// <summary>
    /// Number of payments still in flight — neither settled nor failed. These are the
    /// records whose sats count toward <see cref="TotalSatsSpent"/> but toward neither
    /// <see cref="SuccessfulPayments"/> nor <see cref="FailedPayments"/>.
    /// </summary>
    public int PendingPayments { get; init; }

    /// <summary>
    /// List of payment records.
    /// </summary>
    public required IReadOnlyList<PaymentRecord> Payments { get; init; }
}
