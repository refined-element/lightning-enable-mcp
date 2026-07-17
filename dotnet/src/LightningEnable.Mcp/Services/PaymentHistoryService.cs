using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Service for tracking payment history during a session.
/// Thread-safe for concurrent access.
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

            _payments.Add(new PaymentRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Url = url,
                Method = method.ToUpperInvariant(),
                AmountSats = amountSats,
                Timestamp = DateTime.UtcNow,
                Status = status,
                Invoice = invoice,
                PreimageHex = preimageHex,
                L402Token = l402Token,
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
                Url = url,
                Method = method.ToUpperInvariant(),
                AmountSats = amountSats,
                Timestamp = DateTime.UtcNow,
                Status = PaymentStatus.Failed,
                Invoice = invoice,
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
}
