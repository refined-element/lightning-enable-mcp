using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Service for tracking payment history during a session.
/// </summary>
public interface IPaymentHistoryService
{
    /// <summary>
    /// Records a successful payment.
    /// </summary>
    void RecordPayment(
        string url,
        string method,
        long amountSats,
        string? invoice = null,
        string? preimageHex = null,
        string? l402Token = null,
        int? statusCode = null);

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
