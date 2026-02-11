using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Service for tracking payment history during a session.
/// Thread-safe for concurrent access.
/// </summary>
public class PaymentHistoryService : IPaymentHistoryService
{
    private readonly object _lock = new();
    private readonly List<PaymentRecord> _payments = new();

    public void RecordPayment(
        string url,
        string method,
        long amountSats,
        string? invoice = null,
        string? preimageHex = null,
        string? l402Token = null,
        int? statusCode = null)
    {
        lock (_lock)
        {
            _payments.Add(new PaymentRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Url = url,
                Method = method.ToUpperInvariant(),
                AmountSats = amountSats,
                Timestamp = DateTime.UtcNow,
                Success = true,
                Invoice = invoice,
                PreimageHex = preimageHex,
                L402Token = l402Token,
                ResponseStatusCode = statusCode
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
            _payments.Add(new PaymentRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Url = url,
                Method = method.ToUpperInvariant(),
                AmountSats = amountSats,
                Timestamp = DateTime.UtcNow,
                Success = false,
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
                TotalSatsSpent = payments.Where(p => p.Success).Sum(p => p.AmountSats),
                SuccessfulPayments = payments.Count(p => p.Success),
                FailedPayments = payments.Count(p => !p.Success),
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
