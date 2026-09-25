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
/// result (<c>ALREADY_SUBMITTED</c>); the calling tools treat a failure as "no funds
/// moved" and release the budget reservation, so a duplicate neither pays twice nor
/// double-counts the budget.
///
/// This sits OUTSIDE the receipt seam in the decorator chain, so a refused duplicate never
/// reaches the wallet and never writes a receipt. On-chain sends are guarded the same way,
/// keyed by <see cref="DeriveOnChainOperationId"/> (address + amount [+ intentId]): a fresh human
/// confirmation code does NOT authorize re-sending an on-chain payment that may already have
/// executed — see <see cref="SendOnChainAsync"/>.
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
                "ALREADY_SUBMITTED",
                "This invoice was already submitted in a prior attempt (state: " +
                $"{existing.State.ToString().ToLowerInvariant()}). Refusing to pay it again to avoid a " +
                "double-payment. If you need to know whether it settled, check its status with your " +
                "wallet — do NOT retry.");
        }

        // Durably record submission BEFORE the wallet call so a crash immediately after
        // submission still leaves a record that blocks a blind re-pay on restart.
        _ledger.RecordSubmitted(operationId, Bolt11Parser.ExtractAmountSats(bolt11) ?? 0, SafeProviderName());

        NwcPaymentResult result;
        try
        {
            result = await _inner.PayInvoiceAsync(bolt11, cancellationToken);
        }
        catch
        {
            // If the wallet call THROWS (e.g. OperationCanceledException on a timeout/cancel),
            // the operation would otherwise stay Submitted and lock the invoice out of retry
            // forever. Record it as a no-funds failure so a genuine retry is allowed — the
            // network's invoice single-use is the double-spend backstop if funds moved
            // post-submit — then rethrow so the cancellation/exception propagates untouched.
            _ledger.RecordOutcome(operationId, OperationState.FailedNoFunds, null);
            throw;
        }

        _ledger.RecordOutcome(operationId, MapState(result), TryDerivePaymentHash(result));
        return result;
    }

    /// <summary>
    /// On-chain sends are irreversible and settle over ~10 minutes, so they are keyed by a
    /// stable operation id (address + amount) and guarded like invoices: while a prior send
    /// for the same operation is Submitted / Pending / Unknown / Settled, the wallet's send is
    /// NEVER called again. Instead the recorded provider payment is refreshed via
    /// <see cref="IWalletService.GetOnChainPaymentStatusAsync"/> (when supported) and returned
    /// as a <c>ALREADY_SUBMITTED</c> result carrying the recorded ids. Only a recorded,
    /// proven pre-submit failure (<see cref="OperationState.FailedNoFunds"/>) allows a fresh send.
    /// </summary>
    public async Task<OnChainPaymentResult> SendOnChainAsync(string address, long amountSats, CancellationToken cancellationToken = default)
    {
        var operationId = DeriveOnChainOperationId(address, amountSats, _onChainIntent.Value);

        // Second, race-closing check (the tool performs the first one before the confirmation
        // gate). Atomic check-and-record: two concurrent sends for the same operation can never both
        // reach the wallet. Submitted is durably recorded BEFORE the wallet call, so a crash
        // mid-send still blocks a blind re-send after restart.
        if (!_ledger.TryBeginSubmission(operationId, amountSats, SafeProviderName(), out var existing))
        {
            return await DescribeExistingOnChainAsync(_ledger, _inner, SafeProviderName(), operationId, existing, amountSats, cancellationToken);
        }

        OnChainPaymentResult? result;
        try
        {
            result = await _inner.SendOnChainAsync(address, amountSats, cancellationToken);
        }
        catch
        {
            // A throw gives no proof of where the send stopped — it may have been after the
            // provider executed it. Record Unknown (blocks a re-send) and let it propagate.
            _ledger.RecordOutcome(operationId, OperationState.Unknown, null);
            throw;
        }

        if (result is null)
        {
            _ledger.RecordOutcome(operationId, OperationState.Unknown, null);
            return new OnChainPaymentResult
            {
                Success = false, Submitted = true, State = "UNKNOWN", AmountSats = amountSats,
                ErrorCode = "INVALID_RESPONSE", ErrorMessage = "The wallet returned no on-chain result."
            };
        }

        _ledger.RecordOutcome(operationId, MapOnChainState(result), null, result.PaymentId, result.QuoteId, result.TxId);
        return result;
    }

    public async Task<OnChainPaymentResult> GetOnChainPaymentStatusAsync(string paymentId, CancellationToken cancellationToken = default)
        => await _inner.GetOnChainPaymentStatusAsync(paymentId, cancellationToken) ?? OnChainPaymentResult.NotSupported();

    private static readonly AsyncLocal<string?> _onChainIntent = new();

    /// <summary>
    /// Scopes the caller's optional on-chain <c>intentId</c> so <see cref="SendOnChainAsync"/>
    /// derives the same operation id as the tool's pre-confirmation check (the wallet interface
    /// carries no intent parameter). Dispose to restore the previous value.
    /// </summary>
    public static IDisposable BeginOnChainIntent(string? intentId)
    {
        var previous = _onChainIntent.Value;
        _onChainIntent.Value = intentId;
        return new IntentRestore(previous);
    }

    private sealed class IntentRestore(string? previous) : IDisposable
    {
        public void Dispose() => _onChainIntent.Value = previous;
    }

    /// <summary>
    /// Describes an on-chain operation already in a blocking state (Submitted / Pending /
    /// Settled / Unknown) as an <c>ALREADY_SUBMITTED</c> duplicate result. When a provider
    /// payment id is recorded, the status is refreshed ONCE via <paramref name="statusSource"/>
    /// and the ledger updated. Never sends. Shared by the tool's pre-confirmation check and the
    /// wallet-layer race-closing check.
    /// </summary>
    public static async Task<OnChainPaymentResult> DescribeExistingOnChainAsync(
        IOperationLedger ledger, IWalletService statusSource, string provider,
        string operationId, OperationRecord? existing, long amountSats, CancellationToken cancellationToken)
    {
        var state = existing?.State ?? OperationState.Submitted;
        var paymentId = existing?.PaymentId;
        var quoteId = existing?.QuoteId;
        var txId = existing?.TxId;
        var providerState = ToProviderState(state);
        var statusRefreshed = false;
        var statusAttempted = false;

        if (!string.IsNullOrEmpty(paymentId))
        {
            statusAttempted = true;
            OnChainPaymentResult? status = null;
            try
            {
                status = await statusSource.GetOnChainPaymentStatusAsync(paymentId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[Lightning Enable] On-chain status lookup failed: {ex.GetType().Name}");
            }

            if (status is { Success: true } && !string.IsNullOrEmpty(status.State))
            {
                statusRefreshed = true;
                providerState = status.State!.ToUpperInvariant();
                txId = status.TxId ?? txId;
                var refreshed = providerState switch
                {
                    "COMPLETED" => OperationState.Settled,
                    // Provider-confirmed terminal failure of the recorded payment: no funds moved.
                    "FAILED" => OperationState.FailedNoFunds,
                    _ => state == OperationState.Settled ? OperationState.Settled : OperationState.Pending,
                };
                ledger.RecordOutcome(operationId, refreshed, null, paymentId, quoteId, txId);
            }
        }

        var reference = !string.IsNullOrEmpty(paymentId) ? $"payment id {paymentId}"
            : !string.IsNullOrEmpty(quoteId) ? $"quote id {quoteId} (no payment id was returned)"
            : "no provider id (the send was interrupted before the provider answered)";

        return new OnChainPaymentResult
        {
            Success = false,
            Submitted = true,
            Duplicate = true,
            StatusLookupAttempted = statusAttempted,
            StatusLookupSucceeded = statusRefreshed,
            State = providerState,
            PaymentId = paymentId,
            QuoteId = quoteId,
            TxId = txId,
            AmountSats = amountSats,
            ErrorCode = "ALREADY_SUBMITTED",
            ErrorMessage = statusRefreshed
                ? $"An on-chain send of {amountSats} sats to this address was already submitted ({reference}); " +
                  $"current provider status: {providerState}. It was NOT sent again."
                : $"An on-chain send of {amountSats} sats to this address was already submitted ({reference}, " +
                  $"recorded state: {providerState}). It was NOT sent again. Verify the payment at the provider " +
                  $"({provider}) before doing anything else."
        };
    }

    private static string ToProviderState(OperationState state) => state switch
    {
        OperationState.Settled => "COMPLETED",
        OperationState.Pending => "PENDING",
        OperationState.FailedNoFunds => "FAILED",
        _ => "UNKNOWN", // Submitted (interrupted) or Unknown
    };

    private static OperationState MapOnChainState(OnChainPaymentResult result)
    {
        if (result.Success)
            return string.Equals(result.State, "COMPLETED", StringComparison.OrdinalIgnoreCase)
                ? OperationState.Settled
                : OperationState.Pending;
        if (!result.Submitted)
            return OperationState.FailedNoFunds; // proven pre-submit: no funds moved
        // Submitted, then the provider itself reported the payment terminally FAILED.
        if (string.Equals(result.State, "FAILED", StringComparison.OrdinalIgnoreCase))
            return OperationState.FailedNoFunds;
        return OperationState.Unknown;
    }

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

    /// <summary>
    /// Stable, non-secret on-chain operation id:
    /// <c>"onchain:" + hex(SHA256("onchain:" + normalizedAddress + ":" + amountSats [+ ":" + intentId.Trim()]))</c>.
    /// A null/blank <paramref name="intentId"/> is identical to omitting it.
    /// </summary>
    public static string DeriveOnChainOperationId(string address, long amountSats, string? intentId = null)
    {
        var normalized = NormalizeAddress(address);
        var scope = string.IsNullOrWhiteSpace(intentId) ? "" : ":" + intentId.Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("onchain:" + normalized + ":" + amountSats + scope));
        return "onchain:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Trim; bech32/bech32m (bc1...) is case-insensitive, so lower-case it. Base58
    /// (1.../3...) is case-sensitive and is kept as-is.</summary>
    private static string NormalizeAddress(string? address)
    {
        var a = (address ?? string.Empty).Trim();
        return a.StartsWith("bc1", StringComparison.OrdinalIgnoreCase) ? a.ToLowerInvariant() : a;
    }

    private static bool IsMoneyMoving(OperationState state) => OperationLedger.BlocksResubmission(state);

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
