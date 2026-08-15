using System.Security.Cryptography;
using System.Text;
using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Wallet-seam decorator that makes Lightning invoice payment IDEMPOTENT via the durable
/// <see cref="IOperationLedger"/>. Before paying, it derives a stable operation id from the
/// invoice and refuses to submit one that is already <c>Submitted</c>/<c>Pending</c>/
/// <c>Settled</c> — even across a process restart or an agent retry — so a crash or a retry
/// can never cause a blind duplicate payment. A refused duplicate returns a hard-failure
/// result (<c>DUPLICATE_SUBMISSION</c>); the calling tools treat a failure as "no funds
/// moved" and release the budget reservation, so a duplicate neither pays twice nor
/// double-counts the budget.
///
/// This sits OUTSIDE the receipt seam in the decorator chain, so a refused duplicate never
/// reaches the wallet and never writes a receipt. On-chain sends are passed through
/// unguarded: they have no invoice/payment-hash and each one already requires a fresh
/// out-of-band human confirmation, so a blind restart re-send cannot happen silently.
///
/// The operation id is <c>SHA256(normalized bolt11)</c> — a stable, non-secret key. The raw
/// invoice, preimage, macaroon, and connection string are never persisted.
/// </summary>
public sealed class IdempotentWalletService : IWalletService
{
    private readonly IWalletService _inner;
    private readonly IOperationLedger _ledger;

    public IdempotentWalletService(IWalletService inner, IOperationLedger ledger)
    {
        _inner = inner;
        _ledger = ledger;
    }

    public bool IsConfigured => _inner.IsConfigured;
    public string ProviderName => _inner.ProviderName;

    public async Task<NwcPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken = default)
    {
        var operationId = DeriveOperationId(bolt11);

        // Idempotency gate: refuse to re-submit an invoice already in a money-moving state.
        var existing = _ledger.Lookup(operationId);
        if (existing is not null && IsMoneyMoving(existing.State))
        {
            return NwcPaymentResult.Failed(
                "DUPLICATE_SUBMISSION",
                "This invoice was already submitted in a prior attempt (state: " +
                $"{existing.State.ToString().ToLowerInvariant()}). Refusing to pay it again to avoid a " +
                "double-payment. If you need to know whether it settled, check its status with your " +
                "wallet — do NOT retry.");
        }

        // Durably record submission BEFORE the wallet call so a crash immediately after
        // submission still leaves a record that blocks a blind re-pay on restart.
        _ledger.RecordSubmitted(operationId, Bolt11Parser.ExtractAmountSats(bolt11) ?? 0, SafeProviderName());

        var result = await _inner.PayInvoiceAsync(bolt11, cancellationToken);

        _ledger.RecordOutcome(operationId, MapState(result), TryDerivePaymentHash(result));
        return result;
    }

    // On-chain sends carry no payment hash and each requires a fresh human confirmation
    // code, so they cannot be blindly re-sent on restart — pass through unguarded.
    public Task<OnChainPaymentResult> SendOnChainAsync(string address, long amountSats, CancellationToken cancellationToken = default)
        => _inner.SendOnChainAsync(address, amountSats, cancellationToken);

    // ----- pass-throughs (no value movement) -----
    public Task<NwcBalanceInfo> GetBalanceAsync(CancellationToken cancellationToken = default)
        => _inner.GetBalanceAsync(cancellationToken);
    public Task<WalletInvoiceResult> CreateInvoiceAsync(long amountSats, string? memo = null, int expirySecs = 3600, CancellationToken cancellationToken = default)
        => _inner.CreateInvoiceAsync(amountSats, memo, expirySecs, cancellationToken);
    public Task<WalletInvoiceStatus> GetInvoiceStatusAsync(string invoiceId, CancellationToken cancellationToken = default)
        => _inner.GetInvoiceStatusAsync(invoiceId, cancellationToken);
    public Task<WalletTickerResult> GetTickerAsync(CancellationToken cancellationToken = default)
        => _inner.GetTickerAsync(cancellationToken);
    public NwcConfig? GetConfig() => _inner.GetConfig();
    public Task<CurrencyExchangeResult> ExchangeCurrencyAsync(string sourceCurrency, string targetCurrency, decimal amount, CancellationToken cancellationToken = default)
        => _inner.ExchangeCurrencyAsync(sourceCurrency, targetCurrency, amount, cancellationToken);
    public Task<MultiCurrencyBalance> GetAllBalancesAsync(CancellationToken cancellationToken = default)
        => _inner.GetAllBalancesAsync(cancellationToken);

    // ----- helpers -----

    private static bool IsMoneyMoving(OperationState state) =>
        state is OperationState.Submitted or OperationState.Pending or OperationState.Settled;

    private static OperationState MapState(NwcPaymentResult result) =>
        result.Success ? OperationState.Settled
        : result.IsPending ? OperationState.Pending
        : OperationState.FailedNoFunds;

    /// <summary>SHA256 of the normalized invoice — a stable, non-secret idempotency key.</summary>
    private static string DeriveOperationId(string bolt11)
    {
        var normalized = (bolt11 ?? string.Empty).Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "ln:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string SafeProviderName()
    {
        try { return _inner.ProviderName; }
        catch { return "unknown"; }
    }

    private static string? TryDerivePaymentHash(NwcPaymentResult result)
    {
        if (!result.HasPreimage) return null;
        try
        {
            var hash = SHA256.HashData(Convert.FromHexString(result.PreimageHex!));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch { return null; }
    }
}
