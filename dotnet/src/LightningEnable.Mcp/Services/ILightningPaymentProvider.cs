using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Unified interface for Lightning payment providers.
/// Abstracts differences between OpenNode, Lightspark, Strike, and NWC.
/// </summary>
public interface ILightningPaymentProvider
{
    /// <summary>
    /// Provider name for logging and selection.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Whether this provider returns preimage on successful outgoing payments.
    /// Critical for L402 protocol support.
    /// </summary>
    bool SupportsPreimage { get; }

    /// <summary>
    /// Whether the provider is configured and ready to use.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Pay a BOLT11 Lightning invoice.
    /// </summary>
    /// <param name="bolt11Invoice">The BOLT11 encoded invoice to pay</param>
    /// <param name="maxFeeSats">Maximum fee willing to pay (optional)</param>
    /// <param name="timeoutSecs">Timeout in seconds (default 60)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Payment result including preimage if supported</returns>
    Task<ProviderPaymentResult> PayInvoiceAsync(
        string bolt11Invoice,
        long? maxFeeSats = null,
        int timeoutSecs = 60,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create an invoice to receive a Lightning payment.
    /// </summary>
    /// <param name="amountSats">Amount in satoshis</param>
    /// <param name="memo">Optional payment description</param>
    /// <param name="expirySeconds">Invoice expiry time (default 3600)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Invoice details including BOLT11 string</returns>
    Task<ProviderInvoiceResult> CreateInvoiceAsync(
        long amountSats,
        string? memo = null,
        int expirySeconds = 3600,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the wallet balance.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Balance information</returns>
    Task<ProviderBalanceResult> GetBalanceAsync(CancellationToken cancellationToken = default);
}
