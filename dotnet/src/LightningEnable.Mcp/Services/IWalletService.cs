using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Service for interacting with a Lightning wallet.
/// Supports NWC, Strike, OpenNode, and other backends.
/// </summary>
public interface IWalletService
{
    /// <summary>
    /// Whether the wallet is configured and ready to use.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// The name of the wallet provider (e.g., "NWC", "Strike", "OpenNode").
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Pays a BOLT11 Lightning invoice.
    /// </summary>
    /// <param name="bolt11">BOLT11-encoded invoice.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Payment result with preimage if successful.</returns>
    Task<NwcPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the wallet balance.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Balance information.</returns>
    Task<NwcBalanceInfo> GetBalanceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a Lightning invoice to receive payment.
    /// </summary>
    /// <param name="amountSats">Amount in satoshis.</param>
    /// <param name="memo">Optional description for the invoice.</param>
    /// <param name="expirySecs">Invoice expiry time in seconds (default 3600).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Invoice details including BOLT11 string.</returns>
    Task<WalletInvoiceResult> CreateInvoiceAsync(
        long amountSats,
        string? memo = null,
        int expirySecs = 3600,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the status of a previously created invoice.
    /// </summary>
    /// <param name="invoiceId">The invoice ID returned from CreateInvoiceAsync.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Invoice status information.</returns>
    Task<WalletInvoiceStatus> GetInvoiceStatusAsync(string invoiceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current BTC price ticker (if supported by provider).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ticker information with BTC/USD rate.</returns>
    Task<WalletTickerResult> GetTickerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current NWC configuration (NWC-specific, returns null for other providers).
    /// </summary>
    NwcConfig? GetConfig();

    /// <summary>
    /// Sends an on-chain Bitcoin payment (if supported by provider).
    /// </summary>
    /// <param name="address">Bitcoin address to send to.</param>
    /// <param name="amountSats">Amount in satoshis.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>On-chain payment result.</returns>
    Task<OnChainPaymentResult> SendOnChainAsync(
        string address,
        long amountSats,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges currency (e.g., USD to BTC or BTC to USD).
    /// </summary>
    /// <param name="sourceCurrency">Currency to convert from (e.g., "USD", "BTC").</param>
    /// <param name="targetCurrency">Currency to convert to (e.g., "BTC", "USD").</param>
    /// <param name="amount">Amount in source currency.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Exchange result with converted amount.</returns>
    Task<CurrencyExchangeResult> ExchangeCurrencyAsync(
        string sourceCurrency,
        string targetCurrency,
        decimal amount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets balances for all currencies (multi-currency wallets like Strike).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Multi-currency balance information.</returns>
    Task<MultiCurrencyBalance> GetAllBalancesAsync(CancellationToken cancellationToken = default);
}
