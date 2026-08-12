using System.Security.Cryptography;
using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// The single receipt seam: decorates the resolved <see cref="IWalletService"/>
/// so EVERY payment that moves value — from any tool, current or future — leaves
/// exactly one durable line in <c>~/.lightning-enable/receipts.jsonl</c>.
///
/// Before this seam existed only access_l402_resource wrote receipts; a
/// pay_invoice / pay_l402_challenge / settle_agent_service / on-chain payment
/// left no durable record. Recording at the wallet seam (every provider and every
/// payment tool funnels through <see cref="PayInvoiceAsync"/> /
/// <see cref="SendOnChainAsync"/>) closes that coverage gap without per-tool code.
///
/// Receipt writing is best-effort and must never break a payment; a failed write
/// is surfaced via <see cref="PaymentReceiptScope.ReceiptWritten"/> so tools can
/// report <c>receipt_written: false</c> instead of hiding it.
/// </summary>
public sealed class ReceiptRecordingWalletService : IWalletService
{
    private readonly IWalletService _inner;
    private readonly IReceiptService _receipts;
    private readonly IBudgetService? _budget;

    public ReceiptRecordingWalletService(IWalletService inner, IReceiptService receipts, IBudgetService? budget = null)
    {
        _inner = inner;
        _receipts = receipts;
        _budget = budget;
    }

    public bool IsConfigured => _inner.IsConfigured;

    public string ProviderName => _inner.ProviderName;

    public async Task<NwcPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken = default)
    {
        var result = await _inner.PayInvoiceAsync(bolt11, cancellationToken);

        // Receipt on settled AND pending: pending funds are committed (the budget
        // records them), so the durable log must not under-report the budget.
        // Hard failures move no money and get no receipt.
        if (result.Success || result.IsPending)
        {
            var amountSats = Bolt11Parser.ExtractAmountSats(bolt11) ?? 0;
            var scope = PaymentReceiptScope.Current;
            WriteReceipt(scope, new PaymentReceiptEntry
            {
                Kind = scope?.Kind ?? "invoice",
                Wallet = SafeProviderName(),
                AmountSats = amountSats,
                Status = result.Success ? "settled" : "pending",
                PaymentHash = TryDerivePaymentHash(result),
                Context = scope?.Context,
                Policy = scope?.Policy,
                SessionSpentSats = ProjectSessionSpent(amountSats),
            });
        }

        return result;
    }

    public async Task<OnChainPaymentResult> SendOnChainAsync(
        string address,
        long amountSats,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.SendOnChainAsync(address, amountSats, cancellationToken);

        if (result.Success)
        {
            var sentSats = result.AmountSats > 0 ? result.AmountSats : amountSats;
            var scope = PaymentReceiptScope.Current;
            WriteReceipt(scope, new PaymentReceiptEntry
            {
                Kind = "onchain",
                Wallet = SafeProviderName(),
                AmountSats = sentSats,
                // A broadcast-but-unconfirmed send has still left the wallet; only a
                // provider-confirmed COMPLETED state reads as settled.
                Status = string.Equals(result.State, "COMPLETED", StringComparison.OrdinalIgnoreCase)
                    ? "settled"
                    : "pending",
                Context = scope?.Context,
                Policy = scope?.Policy,
                SessionSpentSats = ProjectSessionSpent(sentSats + result.FeeSats),
                FeeSats = result.FeeSats,
                TxId = result.TxId,
            });
        }

        return result;
    }

    // ----- pass-throughs (no value movement, no receipt) -----

    public Task<NwcBalanceInfo> GetBalanceAsync(CancellationToken cancellationToken = default)
        => _inner.GetBalanceAsync(cancellationToken);

    public Task<WalletInvoiceResult> CreateInvoiceAsync(
        long amountSats, string? memo = null, int expirySecs = 3600, CancellationToken cancellationToken = default)
        => _inner.CreateInvoiceAsync(amountSats, memo, expirySecs, cancellationToken);

    public Task<WalletInvoiceStatus> GetInvoiceStatusAsync(string invoiceId, CancellationToken cancellationToken = default)
        => _inner.GetInvoiceStatusAsync(invoiceId, cancellationToken);

    public Task<WalletTickerResult> GetTickerAsync(CancellationToken cancellationToken = default)
        => _inner.GetTickerAsync(cancellationToken);

    public NwcConfig? GetConfig() => _inner.GetConfig();

    public Task<CurrencyExchangeResult> ExchangeCurrencyAsync(
        string sourceCurrency, string targetCurrency, decimal amount, CancellationToken cancellationToken = default)
        => _inner.ExchangeCurrencyAsync(sourceCurrency, targetCurrency, amount, cancellationToken);

    public Task<MultiCurrencyBalance> GetAllBalancesAsync(CancellationToken cancellationToken = default)
        => _inner.GetAllBalancesAsync(cancellationToken);

    // ----- receipt plumbing -----

    private void WriteReceipt(PaymentReceiptScope? scope, PaymentReceiptEntry entry)
    {
        bool written;
        try
        {
            written = _receipts.LogPayment(entry);
        }
        catch (Exception ex)
        {
            // LogPayment already never throws; this guards the seam itself so a
            // receipt can never break a payment that already settled.
            Console.Error.WriteLine($"[Lightning Enable] Failed to write payment receipt: {ex.Message}");
            written = false;
        }
        scope?.RecordWrite(written);
    }

    private string SafeProviderName()
    {
        try { return _inner.ProviderName; }
        catch { return "unknown"; }
    }

    /// <summary>
    /// SHA256(preimage) — the Lightning payment hash. Safe to persist (it is public
    /// routing data), useless to spend, and proves which payment the receipt is for.
    /// The preimage itself must never reach the file.
    /// </summary>
    private static string? TryDerivePaymentHash(NwcPaymentResult result)
    {
        if (!result.HasPreimage) return null;
        try
        {
            var hash = SHA256.HashData(Convert.FromHexString(result.PreimageHex!));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Projected post-payment session total. The seam writes BEFORE the calling
    /// tool/client records the spend, so "current + this payment" is what the
    /// budget will read immediately after the tool returns.
    /// </summary>
    private long? ProjectSessionSpent(long amountSats)
    {
        try
        {
            var spent = _budget?.GetConfig()?.SessionSpent;
            return spent.HasValue ? spent.Value + amountSats : null;
        }
        catch
        {
            return null;
        }
    }
}
