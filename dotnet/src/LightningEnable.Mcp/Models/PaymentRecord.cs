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
/// Record of a payment made during this session.
///
/// <para><b>Holds only safe correlation material.</b> There is deliberately no slot for the
/// BOLT11 invoice, the preimage, or the L402 token: the preimage IS the proof of payment
/// and the token is a bearer credential, so anything that renders this record (the
/// history tool, JSON, <c>ToString()</c>, logs) would otherwise leak them. Mirrors the
/// Python port's <c>PaymentRecord</c>, which has no preimage field at all. The immediate
/// tool result of a payment may still carry the token — that is the protocol's need —
/// but it is never copied here.</para>
/// </summary>
public record PaymentRecord
{
    /// <summary>
    /// Unique identifier for this payment record.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The URL that was accessed, REDACTED: userinfo, query string, and fragment are
    /// stripped (see <c>UrlRedaction.RedactUrl</c>), so a <c>?api_key=</c> or
    /// <c>user:pass@</c> never reaches the audit trail. Non-URL identifiers such as
    /// <c>direct-invoice</c> pass through unchanged.
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
    /// Short, non-secret payment reference for correlating this record with the wallet
    /// and the durable receipt log: the first <see cref="PaymentReferenceLength"/> hex
    /// characters of the payment hash (SHA-256 of the preimage) when the payment settled
    /// with a preimage, otherwise of a SHA-256 commitment to the invoice. Never the
    /// preimage, the invoice, or a token — a truncated hash cannot be inverted into any of
    /// them. <c>null</c> when nothing was available to derive it from.
    /// </summary>
    public string? PaymentReference { get; init; }

    /// <summary>How many hex characters <see cref="PaymentReference"/> keeps.</summary>
    public const int PaymentReferenceLength = 8;

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
