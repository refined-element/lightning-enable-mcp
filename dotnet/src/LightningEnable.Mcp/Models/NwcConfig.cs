namespace LightningEnable.Mcp.Models;

/// <summary>
/// Allowed values for <see cref="NwcConfig.Encryption"/>. Centralized so callers don't
/// duplicate magic strings.
/// </summary>
public static class NwcEncryption
{
    /// <summary>
    /// NIP-04 — original NIP-47 encryption (ECDH + sha256(shared_x) + AES-256-CBC).
    /// Compatible with Primal NWC, CoinOS, Mutiny, ZBD, and most other deployed
    /// wallets. Not accepted by Alby Hub.
    /// </summary>
    public const string Nip04 = "nip04";

    /// <summary>
    /// NIP-44 v2 — newer encryption (ECDH + HKDF + ChaCha20 + HMAC-SHA256). Required
    /// by Alby Hub. Silently dropped by older wallets, which surfaces as a 30-second
    /// no-response timeout — the original motivation for adding auto-detect.
    /// </summary>
    public const string Nip44V2 = "nip44_v2";

    /// <summary>
    /// Auto — fetch the wallet's NIP-47 INFO event (kind 13194) on first request,
    /// read the <c>encryption</c> tag, and pick <see cref="Nip44V2"/> if advertised
    /// (more secure) else <see cref="Nip04"/>. Result is cached for the lifetime
    /// of the service instance. Falls back to <see cref="Nip04"/> if the INFO event
    /// can't be fetched within ~3s — older wallets that don't publish 13194 still
    /// work because NIP-04 is the original NIP-47 default.
    /// </summary>
    public const string Auto = "auto";

    /// <summary>
    /// Default outbound encryption mode. <see cref="Auto"/> means zero-config for
    /// every spec-compliant wallet — Primal/CoinOS/Mutiny/ZBD/Alby Hub all just work.
    /// Operators can pin to a specific scheme via the <c>NWC_ENCRYPTION</c> env var.
    /// </summary>
    public const string Default = Auto;

    public static bool IsValid(string? value) =>
        value == Nip04 || value == Nip44V2 || value == Auto;

    /// <summary>
    /// Comma-separated list of all valid encryption values. Used in user-facing
    /// warning text so the message can never drift from <see cref="IsValid"/>.
    /// </summary>
    public static string AllowedValuesCsv => $"{Auto}, {Nip04}, {Nip44V2}";
}

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
    /// Outbound encryption scheme for NIP-47 requests sent to the wallet.
    /// Default is <c>"auto"</c> — see <see cref="NwcEncryption.Default"/>.
    /// <list type="bullet">
    ///   <item><c>"auto"</c> (default) — fetch the wallet's NIP-47 INFO event (kind 13194)
    ///   on first request, read the <c>encryption</c> tag, and pick the strongest
    ///   advertised scheme. Falls back to <c>"nip04"</c> when the INFO event isn't
    ///   available within ~3s. Result is cached on the service instance so subsequent
    ///   requests have no extra round-trip.</item>
    ///   <item><c>"nip04"</c> — original NIP-47 default. Compatible with Primal NWC,
    ///   CoinOS, Mutiny, ZBD, and most other deployed wallets. Not accepted by Alby Hub.</item>
    ///   <item><c>"nip44_v2"</c> — required by Alby Hub. Silently dropped by older wallets.</item>
    /// </list>
    /// Inbound responses are auto-detected (NIP-04 by <c>?iv=</c> marker, NIP-44 v2 otherwise)
    /// regardless of this setting — only outbound encoding is governed here.
    /// Override at runtime with the <c>NWC_ENCRYPTION</c> env var.
    /// </summary>
    public string Encryption { get; init; } = NwcEncryption.Default;

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

        // An NWC connection string can legitimately carry MULTIPLE relay= params — e.g.
        // getalby.com advertises two: ?relay=wss://relay.getalby.com&relay=wss://relay2.getalby.com.
        // HttpUtility.ParseQueryString's indexer (query["relay"]) COMMA-JOINS duplicate keys
        // into "wss://relay.getalby.com,wss://relay2.getalby.com", which then throws
        // UriFormatException at new Uri(RelayUrl) downstream and breaks EVERY payment via such
        // a wallet before a socket is opened. Read the separate values via GetValues and take
        // the FIRST non-empty relay. (Same defect confirmed + fixed in sibling lib L402Requests.)
        var relay = query.GetValues("relay")?.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
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
/// Validation for Lightning payment preimages.
///
/// Under L402 (and MPP) the preimage is not a receipt or a tracking number — it IS
/// the proof of payment. The client sends it back as
/// <c>Authorization: L402 &lt;macaroon&gt;:&lt;preimage&gt;</c>, and the server grants access
/// solely because possessing it proves the invoice was paid.
///
/// So anything that is not a preimage must never occupy that field. Wallets have
/// been observed handing back internal identifiers in its place — OpenNode
/// withdrawal IDs, Strike payment IDs, and Coinos UUIDs (its internal-transfer
/// bug). Publishing one of those to an agent produces an Authorization header the
/// server rejects: money spent, no access, and a payment record that falsely
/// claims a valid preimage.
///
/// Mirrors the Python port's <c>wallet_errors.is_valid_preimage</c>.
/// </summary>
public static class Preimage
{
    /// <summary>A preimage is a 32-byte value, hex-encoded => 64 hex characters.</summary>
    public const int HexLength = 64;

    /// <summary>
    /// True only if <paramref name="value"/> is a syntactically valid preimage:
    /// a 64-character hex string.
    ///
    /// This does NOT verify the preimage against a payment hash — it only rejects
    /// values that cannot possibly be a preimage (withdrawal IDs, payment IDs,
    /// UUIDs, BOLT11 invoices, empty/null). Anything failing this check must never
    /// be handed to a caller as proof of payment.
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.AsSpan().Trim();
        if (candidate.Length != HexLength)
            return false;

        foreach (var c in candidate)
        {
            var isHexDigit = (c >= '0' && c <= '9')
                          || (c >= 'a' && c <= 'f')
                          || (c >= 'A' && c <= 'F');
            if (!isHexDigit)
                return false;
        }

        return true;
    }
}

/// <summary>
/// Result of an NWC pay_invoice request.
/// </summary>
public record NwcPaymentResult
{
    /// <summary>
    /// Whether the payment reached a SETTLED state. False for both hard failures
    /// and payments still in flight (see <see cref="IsPending"/>) — an in-flight
    /// payment may still fail, so it must never read as terminal success.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Whether the payment was accepted but has NOT settled yet. It may still
    /// succeed, and it may still FAIL.
    ///
    /// Distinct from a hard failure: the funds are committed, so the caller must
    /// neither claim the payment worked nor retry it (retrying risks paying twice).
    /// Poll the provider with <see cref="TrackingId"/> to resolve it.
    /// </summary>
    public bool IsPending { get; init; }

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
    /// Whether a REAL preimage is available for L402/MPP verification.
    ///
    /// This is the gate that stops an internal identifier from reaching an L402
    /// Authorization header, so it asks the honest question — "is this actually a
    /// preimage?" — rather than checking for a couple of known-bad sentinel
    /// strings, which a UUID or a withdrawal ID would sail straight past.
    /// </summary>
    public bool HasPreimage => Preimage.IsValid(PreimageHex);

    /// <summary>
    /// Creates a successful payment result with preimage.
    /// If <paramref name="preimageHex"/> is not a real preimage, the result still
    /// reports success (the payment settled) but <see cref="HasPreimage"/> is
    /// false — the value is never passed off as proof of payment.
    /// </summary>
    public static NwcPaymentResult Succeeded(string preimageHex) =>
        new() { Success = true, PreimageHex = preimageHex };

    /// <summary>
    /// Creates a successful payment result without preimage.
    /// Use when the payment SETTLED — the funds are gone — but the wallet does not
    /// provide a usable preimage (e.g. OpenNode, which never does). This is NOT a
    /// payment failure: reporting it as one invites the caller to retry and pay twice.
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
    /// Creates an IN-FLIGHT payment result: accepted by the provider but not yet
    /// settled, and it may still fail.
    ///
    /// Deliberately NOT <see cref="Success"/> — a payment that can still fail must
    /// not collapse into terminal success, or the caller proceeds believing it paid.
    /// Equally it is not a hard failure, so callers must not retry it.
    /// </summary>
    public static NwcPaymentResult Pending(string trackingId, string message) =>
        new()
        {
            Success = false,
            IsPending = true,
            PreimageHex = null,
            TrackingId = trackingId,
            ErrorCode = "PAYMENT_PENDING",
            ErrorMessage = message
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
