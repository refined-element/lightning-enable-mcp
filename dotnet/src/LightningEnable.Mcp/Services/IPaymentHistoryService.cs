using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Service for tracking payment history during a session.
/// </summary>
public interface IPaymentHistoryService
{
    /// <summary>
    /// Records a payment. Defaults to a settled (successful) payment.
    /// </summary>
    /// <param name="status">
    /// The outcome. Pass <see cref="PaymentStatus.Pending"/> for an in-flight payment —
    /// it may still fail, and the audit trail must never claim it settled.
    /// </param>
    /// <param name="errorMessage">Optional context (e.g. why a payment is still pending).</param>
    void RecordPayment(
        string url,
        string method,
        long amountSats,
        string? invoice = null,
        string? preimageHex = null,
        string? l402Token = null,
        int? statusCode = null,
        PaymentStatus status = PaymentStatus.Success,
        string? errorMessage = null);

    /// <summary>
    /// Records a failed payment attempt.
    /// </summary>
    void RecordFailedPayment(
        string url,
        string method,
        long amountSats,
        string errorMessage,
        string? invoice = null);

    /// <summary>
    /// Gets recent payments.
    /// </summary>
    /// <param name="limit">Maximum number of payments to return.</param>
    IReadOnlyList<PaymentRecord> GetRecentPayments(int limit = 10);

    /// <summary>
    /// Gets a summary of all payments in the session.
    /// </summary>
    PaymentHistorySummary GetSummary();

    /// <summary>
    /// Clears the payment history.
    /// </summary>
    void Clear();
}
