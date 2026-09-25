using System.Security.Cryptography;
using System.Text;
using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Service for tracking payment history during a session.
/// Thread-safe for concurrent access.
///
/// <para><b>This is the minimizing boundary.</b> Callers hand over what they have (the
/// invoice, the preimage, the token, the raw URL) and this service keeps only what is
/// safe: a redacted URL and a truncated hash-derived <see cref="PaymentRecord.PaymentReference"/>.
/// The secrets are consumed to derive the reference and are never stored, so no reader —
/// the history tool, JSON, <c>ToString()</c>, a log line — can leak them, whatever a
/// caller passed in. Mirrors the Python port's preimage-free <c>PaymentRecord</c>.</para>
/// </summary>
public class PaymentHistoryService : IPaymentHistoryService
{
    private const int MaxPaymentRecords = 1000;
    private readonly object _lock = new();
    private readonly List<PaymentRecord> _payments = new();

    public void RecordPayment(
        string url,
        string method,
        long amountSats,
        string? invoice = null,
        string? preimageHex = null,
        string? l402Token = null,
        int? statusCode = null,
        PaymentStatus status = PaymentStatus.Success,
        string? errorMessage = null)
    {
        lock (_lock)
        {
            if (_payments.Count >= MaxPaymentRecords)
            {
                // Remove oldest entries to make room
                _payments.RemoveRange(0, _payments.Count - MaxPaymentRecords + 1);
            }

            // invoice / preimageHex / l402Token are consumed here and only here: they
            // feed the reference and are dropped. The token is never even hashed — it
            // embeds the preimage, which the payment hash already commits to.
            _ = l402Token;
            _payments.Add(new PaymentRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Url = UrlRedaction.RedactUrl(url),
                Method = method.ToUpperInvariant(),
                AmountSats = amountSats,
                Timestamp = DateTime.UtcNow,
                Status = status,
                PaymentReference = DerivePaymentReference(preimageHex, invoice),
                ResponseStatusCode = statusCode,
                ErrorMessage = errorMessage
            });
        }
    }

    public void RecordFailedPayment(
        string url,
        string method,
        long amountSats,
        string errorMessage,
        string? invoice = null)
    {
        lock (_lock)
        {
            if (_payments.Count >= MaxPaymentRecords)
            {
                _payments.RemoveRange(0, _payments.Count - MaxPaymentRecords + 1);
            }

            _payments.Add(new PaymentRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Url = UrlRedaction.RedactUrl(url),
                Method = method.ToUpperInvariant(),
                AmountSats = amountSats,
                Timestamp = DateTime.UtcNow,
                Status = PaymentStatus.Failed,
                PaymentReference = DerivePaymentReference(null, invoice),
                ErrorMessage = errorMessage
            });
        }
    }

    public IReadOnlyList<PaymentRecord> GetRecentPayments(int limit = 10)
    {
        lock (_lock)
        {
            return _payments
                .OrderByDescending(p => p.Timestamp)
                .Take(limit)
                .ToList();
        }
    }

    public PaymentHistorySummary GetSummary()
    {
        lock (_lock)
        {
            var payments = _payments.ToList();
            return new PaymentHistorySummary
            {
                TotalPayments = payments.Count,
                // Committed funds, not settled funds: a pending payment's sats are already
                // gone from the agent's budget, so they count here. Only an outright
                // FAILURE moved no money.
                TotalSatsSpent = payments
                    .Where(p => p.Status is PaymentStatus.Success or PaymentStatus.Pending)
                    .Sum(p => p.AmountSats),
                // Settled only. `!p.Success` used to sweep pending into the failed bucket
                // as well, so pending was miscounted at BOTH ends once it stopped being
                // stamped as a success — each status now counts exactly itself.
                SuccessfulPayments = payments.Count(p => p.Status == PaymentStatus.Success),
                FailedPayments = payments.Count(p => p.Status == PaymentStatus.Failed),
                PendingPayments = payments.Count(p => p.Status == PaymentStatus.Pending),
                Payments = payments.OrderByDescending(p => p.Timestamp).ToList()
            };
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _payments.Clear();
        }
    }

    /// <summary>
    /// Derives the short non-secret reference stored on a record. Prefers the payment
    /// hash (SHA-256 of a valid preimage — the value the wallet and the durable receipt
    /// already carry, so the two logs correlate); falls back to a SHA-256 commitment to
    /// the invoice string; <c>null</c> when neither is available. Never throws — a
    /// malformed preimage simply falls through to the invoice commitment.
    /// </summary>
    internal static string? DerivePaymentReference(string? preimageHex, string? invoice)
    {
        if (Preimage.IsValid(preimageHex))
        {
            try
            {
                var hash = SHA256.HashData(Convert.FromHexString(preimageHex!));
                return Truncate(hash);
            }
            catch (FormatException)
            {
                // fall through to the invoice commitment
            }
        }

        if (!string.IsNullOrWhiteSpace(invoice))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(invoice.Trim().ToLowerInvariant()));
            return Truncate(hash);
        }

        return null;
    }

    private static string Truncate(byte[] hash) =>
        Convert.ToHexString(hash).ToLowerInvariant()[..PaymentRecord.PaymentReferenceLength];
}
