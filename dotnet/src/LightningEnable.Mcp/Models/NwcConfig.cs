namespace LightningEnable.Mcp.Models;

/// <summary>
/// Configuration for Nostr Wallet Connect (NWC).
/// Parsed from a nostr+walletconnect:// URI.
/// </summary>
public record NwcConfig
{
    /// <summary>
    /// The public key (hex) of the wallet service.
    /// </summary>
    public required string WalletPubkey { get; init; }

    /// <summary>
    /// The relay URL to connect to for NWC communication.
    /// </summary>
    public required string RelayUrl { get; init; }

    /// <summary>
    /// The secret key (hex) for signing NWC requests.
    /// </summary>
    public required string Secret { get; init; }

    /// <summary>
    /// Optional LUD16 lightning address associated with this wallet.
    /// </summary>
    public string? Lud16 { get; init; }

    /// <summary>
    /// Parses an NWC connection string into configuration.
    /// Format: nostr+walletconnect://{pubkey}?relay={relay}&secret={secret}&lud16={optional}
    /// </summary>
    public static NwcConfig Parse(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("NWC connection string is required", nameof(connectionString));

        // Handle both URI schemes
        var normalized = connectionString
            .Replace("nostr+walletconnect://", "nwc://")
            .Replace("nostr+walletconnect:", "nwc:");

        if (!normalized.StartsWith("nwc://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid NWC URI scheme. Expected nostr+walletconnect:// or nwc://", nameof(connectionString));

        var uri = new Uri(normalized);

        // The host is the wallet pubkey
        var walletPubkey = uri.Host;
        if (string.IsNullOrEmpty(walletPubkey) || walletPubkey.Length != 64)
            throw new ArgumentException("Invalid wallet pubkey in NWC URI", nameof(connectionString));

        // Parse query parameters
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        var relay = query["relay"];
        if (string.IsNullOrEmpty(relay))
            throw new ArgumentException("Missing 'relay' parameter in NWC URI", nameof(connectionString));

        var secret = query["secret"];
        if (string.IsNullOrEmpty(secret) || secret.Length != 64)
            throw new ArgumentException("Invalid or missing 'secret' parameter in NWC URI", nameof(connectionString));

        return new NwcConfig
        {
            WalletPubkey = walletPubkey,
            RelayUrl = relay,
            Secret = secret,
            Lud16 = query["lud16"]
        };
    }

    /// <summary>
    /// Attempts to parse an NWC connection string, returning null on failure.
    /// </summary>
    public static NwcConfig? TryParse(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        try
        {
            return Parse(connectionString);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Result of an NWC pay_invoice request.
/// </summary>
public record NwcPaymentResult
{
    /// <summary>
    /// Whether the payment was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// The payment preimage (hex) proving payment was made.
    /// May be null if the wallet provider doesn't return preimages (e.g., OpenNode).
    /// </summary>
    public string? PreimageHex { get; init; }

    /// <summary>
    /// Tracking ID from the wallet provider (e.g., withdrawal ID).
    /// Used when preimage is not available.
    /// </summary>
    public string? TrackingId { get; init; }

    /// <summary>
    /// Error code if payment failed.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Error message if payment failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Whether a valid preimage is available for L402 verification.
    /// </summary>
    public bool HasPreimage => !string.IsNullOrEmpty(PreimageHex) &&
                                !PreimageHex.StartsWith("NO_PREIMAGE") &&
                                !PreimageHex.StartsWith("PENDING");

    /// <summary>
    /// Creates a successful payment result with preimage.
    /// </summary>
    public static NwcPaymentResult Succeeded(string preimageHex) =>
        new() { Success = true, PreimageHex = preimageHex };

    /// <summary>
    /// Creates a successful payment result without preimage.
    /// Use when payment succeeded but wallet doesn't provide preimage (e.g., OpenNode).
    /// </summary>
    public static NwcPaymentResult SucceededWithoutPreimage(string trackingId, string warning) =>
        new()
        {
            Success = true,
            PreimageHex = null,
            TrackingId = trackingId,
            ErrorCode = "NO_PREIMAGE",
            ErrorMessage = warning
        };

    /// <summary>
    /// Creates a failed payment result.
    /// </summary>
    public static NwcPaymentResult Failed(string errorCode, string errorMessage) =>
        new() { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}

/// <summary>
/// Wallet balance information from NWC get_balance request.
/// </summary>
public record NwcBalanceInfo
{
    /// <summary>
    /// Available balance in millisatoshis.
    /// </summary>
    public long BalanceMsat { get; init; }

    /// <summary>
    /// Available balance in satoshis.
    /// </summary>
    public long BalanceSats => BalanceMsat / 1000;
}

/// <summary>
/// Result of creating a Lightning invoice.
/// </summary>
public record WalletInvoiceResult
{
    /// <summary>
    /// Whether the invoice was created successfully.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// The invoice ID for status checks.
    /// </summary>
    public string? InvoiceId { get; init; }

    /// <summary>
    /// The BOLT11-encoded invoice string.
    /// </summary>
    public string? Bolt11 { get; init; }

    /// <summary>
    /// Amount in satoshis.
    /// </summary>
    public long AmountSats { get; init; }

    /// <summary>
    /// Invoice expiry time (UTC).
    /// </summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>
    /// Error code if creation failed.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Error message if creation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    public static WalletInvoiceResult Succeeded(string invoiceId, string bolt11, long amountSats, DateTime? expiresAt = null) =>
        new() { Success = true, InvoiceId = invoiceId, Bolt11 = bolt11, AmountSats = amountSats, ExpiresAt = expiresAt };

    public static WalletInvoiceResult Failed(string errorCode, string errorMessage) =>
        new() { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}

/// <summary>
/// Status of a Lightning invoice.
/// </summary>
public record WalletInvoiceStatus
{
    /// <summary>
    /// Whether the status check succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// The invoice ID.
    /// </summary>
    public string? InvoiceId { get; init; }

    /// <summary>
    /// Invoice state: PENDING, PAID, EXPIRED, CANCELLED.
    /// </summary>
    public string? State { get; init; }

    /// <summary>
    /// Whether the invoice has been paid.
    /// </summary>
    public bool IsPaid => State?.ToUpperInvariant() is "PAID" or "COMPLETED" or "SETTLED";

    /// <summary>
    /// Whether the invoice is still pending payment.
    /// </summary>
    public bool IsPending => State?.ToUpperInvariant() is "PENDING" or "UNPAID" or "OPEN";

    /// <summary>
    /// Amount in satoshis.
    /// </summary>
    public long AmountSats { get; init; }

    /// <summary>
    /// When the invoice was paid (if paid).
    /// </summary>
    public DateTime? PaidAt { get; init; }

    /// <summary>
    /// Error code if status check failed.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Error message if status check failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    public static WalletInvoiceStatus Succeeded(string invoiceId, string state, long amountSats, DateTime? paidAt = null) =>
        new() { Success = true, InvoiceId = invoiceId, State = state, AmountSats = amountSats, PaidAt = paidAt };

    public static WalletInvoiceStatus Failed(string errorCode, string errorMessage) =>
        new() { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}

/// <summary>
/// BTC price ticker information.
/// </summary>
public record WalletTickerResult
{
    /// <summary>
    /// Whether the ticker request succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// BTC price in USD.
    /// </summary>
    public decimal? BtcUsd { get; init; }

    /// <summary>
    /// Timestamp of the rate.
    /// </summary>
    public DateTime? Timestamp { get; init; }

    /// <summary>
    /// Error code if request failed.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Error message if request failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    public static WalletTickerResult Succeeded(decimal btcUsd, DateTime? timestamp = null) =>
        new() { Success = true, BtcUsd = btcUsd, Timestamp = timestamp ?? DateTime.UtcNow };

    public static WalletTickerResult Failed(string errorCode, string errorMessage) =>
        new() { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage };

    public static WalletTickerResult NotSupported() =>
        new() { Success = false, ErrorCode = "NOT_SUPPORTED", ErrorMessage = "This wallet provider does not support price ticker" };
}

/// <summary>
/// Result of an on-chain Bitcoin payment.
/// </summary>
public record OnChainPaymentResult
{
    /// <summary>
    /// Whether the payment was initiated successfully.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Payment ID for tracking.
    /// </summary>
    public string? PaymentId { get; init; }

    /// <summary>
    /// Bitcoin transaction ID once broadcast.
    /// </summary>
    public string? TxId { get; init; }

    /// <summary>
    /// Payment state: PENDING, COMPLETED, FAILED.
    /// </summary>
    public string? State { get; init; }

    /// <summary>
    /// Amount sent in satoshis.
    /// </summary>
    public long AmountSats { get; init; }

    /// <summary>
    /// Network fee in satoshis.
    /// </summary>
    public long FeeSats { get; init; }

    /// <summary>
    /// Error code if payment failed.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Error message if payment failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    public static OnChainPaymentResult Succeeded(string paymentId, string? txId, string state, long amountSats, long feeSats = 0) =>
        new() { Success = true, PaymentId = paymentId, TxId = txId, State = state, AmountSats = amountSats, FeeSats = feeSats };

    public static OnChainPaymentResult Failed(string errorCode, string errorMessage) =>
        new() { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage };

    public static OnChainPaymentResult NotSupported() =>
        new() { Success = false, ErrorCode = "NOT_SUPPORTED", ErrorMessage = "This wallet provider does not support on-chain payments" };
}

/// <summary>
/// Result of a currency exchange operation.
/// </summary>
public record CurrencyExchangeResult
{
    /// <summary>
    /// Whether the exchange was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Exchange quote/transaction ID.
    /// </summary>
    public string? ExchangeId { get; init; }

    /// <summary>
    /// Source currency (e.g., "USD", "BTC").
    /// </summary>
    public string? SourceCurrency { get; init; }

    /// <summary>
    /// Target currency (e.g., "BTC", "USD").
    /// </summary>
    public string? TargetCurrency { get; init; }

    /// <summary>
    /// Amount in source currency.
    /// </summary>
    public decimal SourceAmount { get; init; }

    /// <summary>
    /// Amount in target currency.
    /// </summary>
    public decimal TargetAmount { get; init; }

    /// <summary>
    /// Exchange rate used.
    /// </summary>
    public decimal? Rate { get; init; }

    /// <summary>
    /// Fee amount (in source currency).
    /// </summary>
    public decimal? Fee { get; init; }

    /// <summary>
    /// Exchange state: PENDING, COMPLETED, FAILED.
    /// </summary>
    public string? State { get; init; }

    /// <summary>
    /// Error code if exchange failed.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Error message if exchange failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    public static CurrencyExchangeResult Succeeded(string exchangeId, string sourceCurrency, string targetCurrency,
        decimal sourceAmount, decimal targetAmount, decimal? rate = null, decimal? fee = null, string? state = "COMPLETED") =>
        new()
        {
            Success = true,
            ExchangeId = exchangeId,
            SourceCurrency = sourceCurrency,
            TargetCurrency = targetCurrency,
            SourceAmount = sourceAmount,
            TargetAmount = targetAmount,
            Rate = rate,
            Fee = fee,
            State = state
        };

    public static CurrencyExchangeResult Failed(string errorCode, string errorMessage) =>
        new() { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage };

    public static CurrencyExchangeResult NotSupported() =>
        new() { Success = false, ErrorCode = "NOT_SUPPORTED", ErrorMessage = "This wallet provider does not support currency exchange" };
}

/// <summary>
/// Multi-currency balance information.
/// </summary>
public record MultiCurrencyBalance
{
    /// <summary>
    /// Whether the balance check succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// List of balances by currency.
    /// </summary>
    public List<CurrencyBalance> Balances { get; init; } = new();

    /// <summary>
    /// Error code if check failed.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Error message if check failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    public static MultiCurrencyBalance Succeeded(List<CurrencyBalance> balances) =>
        new() { Success = true, Balances = balances };

    public static MultiCurrencyBalance Failed(string errorCode, string errorMessage) =>
        new() { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}

/// <summary>
/// Balance in a specific currency.
/// </summary>
public record CurrencyBalance
{
    /// <summary>
    /// Currency code (e.g., "USD", "BTC").
    /// </summary>
    public string Currency { get; init; } = "";

    /// <summary>
    /// Available balance.
    /// </summary>
    public decimal Available { get; init; }

    /// <summary>
    /// Total balance (including pending).
    /// </summary>
    public decimal Total { get; init; }

    /// <summary>
    /// Pending balance.
    /// </summary>
    public decimal Pending { get; init; }
}
