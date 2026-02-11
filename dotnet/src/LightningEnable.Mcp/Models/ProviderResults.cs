namespace LightningEnable.Mcp.Models;

/// <summary>
/// Result of a payment operation from any provider.
/// </summary>
public class ProviderPaymentResult
{
    public bool Success { get; init; }
    public string? Preimage { get; init; }
    public string? PaymentId { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public long? FeeSats { get; init; }

    public static ProviderPaymentResult Succeeded(string? preimage, string? paymentId = null, long? feeSats = null)
        => new() { Success = true, Preimage = preimage, PaymentId = paymentId, FeeSats = feeSats };

    public static ProviderPaymentResult Failed(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };
}

/// <summary>
/// Result of creating an invoice.
/// </summary>
public class ProviderInvoiceResult
{
    public bool Success { get; init; }
    public string? InvoiceId { get; init; }
    public string? Bolt11 { get; init; }
    public string? PaymentHash { get; init; }
    public long AmountSats { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static ProviderInvoiceResult Succeeded(string invoiceId, string bolt11, string paymentHash, long amountSats, DateTime? expiresAt = null)
        => new() { Success = true, InvoiceId = invoiceId, Bolt11 = bolt11, PaymentHash = paymentHash, AmountSats = amountSats, ExpiresAt = expiresAt };

    public static ProviderInvoiceResult Failed(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };
}

/// <summary>
/// Result of a balance query.
/// </summary>
public class ProviderBalanceResult
{
    public bool Success { get; init; }
    public long BalanceSats { get; init; }
    public long? AvailableToSendSats { get; init; }
    public long? AvailableToReceiveSats { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static ProviderBalanceResult Succeeded(long balanceSats, long? availableToSend = null, long? availableToReceive = null)
        => new() { Success = true, BalanceSats = balanceSats, AvailableToSendSats = availableToSend, AvailableToReceiveSats = availableToReceive };

    public static ProviderBalanceResult Failed(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };
}
