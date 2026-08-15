using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// The idempotency guard sits at the wallet seam and consults the durable operation ledger
/// before paying, so the SAME Lightning invoice is never submitted twice — even across a
/// process restart or an agent retry. A duplicate is refused as a "no funds moved" failure,
/// which the calling tools already handle by releasing the budget reservation (no double
/// count) and telling the agent to check status rather than pay again.
/// </summary>
public class IdempotentWalletServiceTests
{
    private const string Invoice = "lnbc1000n1p3duplicatetest";
    private const string ValidPreimage = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

    private static string TempPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "op-idem-" + Guid.NewGuid().ToString("N") + ".jsonl");

    [Fact]
    public async Task SameInvoicePaidTwice_SecondIsRefused_WalletCalledOnce()
    {
        var inner = new CountingWallet(NwcPaymentResult.Succeeded(ValidPreimage));
        var svc = new IdempotentWalletService(inner, new OperationLedger(TempPath()));

        var first = await svc.PayInvoiceAsync(Invoice);
        var second = await svc.PayInvoiceAsync(Invoice);

        first.Success.Should().BeTrue();
        second.Success.Should().BeFalse();
        second.ErrorCode.Should().Be("DUPLICATE_SUBMISSION");
        inner.PayCount.Should().Be(1, "the invoice must only ever reach the wallet once");
    }

    [Fact]
    public async Task Duplicate_AcrossRestart_IsRefused()
    {
        var path = TempPath();
        var inner1 = new CountingWallet(NwcPaymentResult.Succeeded(ValidPreimage));
        await new IdempotentWalletService(inner1, new OperationLedger(path)).PayInvoiceAsync(Invoice);

        // New process: fresh wallet + fresh ledger over the SAME file.
        var inner2 = new CountingWallet(NwcPaymentResult.Succeeded(ValidPreimage));
        var afterRestart = await new IdempotentWalletService(inner2, new OperationLedger(path)).PayInvoiceAsync(Invoice);

        afterRestart.Success.Should().BeFalse();
        afterRestart.ErrorCode.Should().Be("DUPLICATE_SUBMISSION");
        inner2.PayCount.Should().Be(0, "a settled invoice from a prior session must not be paid again after restart");
    }

    [Fact]
    public async Task FailedFirstAttempt_AllowsRetry()
    {
        var path = TempPath();
        var failing = new CountingWallet(NwcPaymentResult.Failed("NO_ROUTE", "no route"));
        var firstResult = await new IdempotentWalletService(failing, new OperationLedger(path)).PayInvoiceAsync(Invoice);
        firstResult.Success.Should().BeFalse();

        // A proven hard failure moved no funds, so a genuine retry MUST be allowed.
        var succeeding = new CountingWallet(NwcPaymentResult.Succeeded(ValidPreimage));
        var retry = await new IdempotentWalletService(succeeding, new OperationLedger(path)).PayInvoiceAsync(Invoice);

        retry.Success.Should().BeTrue();
        succeeding.PayCount.Should().Be(1, "a previously-failed payment may be retried");
    }

    [Fact]
    public async Task DifferentInvoices_BothPay()
    {
        var inner = new CountingWallet(NwcPaymentResult.Succeeded(ValidPreimage));
        var svc = new IdempotentWalletService(inner, new OperationLedger(TempPath()));

        await svc.PayInvoiceAsync("lnbc1000n1p3aaaa");
        await svc.PayInvoiceAsync("lnbc1000n1p3bbbb");

        inner.PayCount.Should().Be(2, "distinct invoices are distinct operations");
    }

    [Fact]
    public async Task PendingFirstAttempt_BlocksReSubmission()
    {
        var path = TempPath();
        var pending = new CountingWallet(NwcPaymentResult.Pending("track-1", "still settling"));
        await new IdempotentWalletService(pending, new OperationLedger(path)).PayInvoiceAsync(Invoice);

        // A pending payment may still settle — re-submitting could pay twice, so it is refused.
        var again = new CountingWallet(NwcPaymentResult.Succeeded(ValidPreimage));
        var result = await new IdempotentWalletService(again, new OperationLedger(path)).PayInvoiceAsync(Invoice);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("DUPLICATE_SUBMISSION");
        again.PayCount.Should().Be(0);
    }

    [Fact]
    public async Task InnerThrows_RecordsFailedNoFunds_AllowsRetry()
    {
        // If the wallet call THROWS (e.g. OperationCanceledException on a timeout/cancel), the
        // operation must NOT stay Submitted — that would lock the invoice out of retry forever.
        // It must be recorded as FailedNoFunds so a genuine retry is allowed. The exception must
        // still propagate untouched.
        var path = TempPath();
        var ledger = new OperationLedger(path);
        var throwing = new ThrowingWallet(new OperationCanceledException());

        Func<Task> act = async () =>
            await new IdempotentWalletService(throwing, ledger).PayInvoiceAsync(Invoice);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // The ledger must record the operation as a no-funds failure, not leave it Submitted.
        var operationId = "ln:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Invoice.Trim().ToLowerInvariant()))).ToLowerInvariant();
        ledger.Lookup(operationId)!.State.Should().Be(OperationState.FailedNoFunds);

        // And a genuine retry of the same invoice must be allowed (not refused as a duplicate).
        var succeeding = new CountingWallet(NwcPaymentResult.Succeeded(ValidPreimage));
        var retry = await new IdempotentWalletService(succeeding, new OperationLedger(path)).PayInvoiceAsync(Invoice);

        retry.Success.Should().BeTrue();
        succeeding.PayCount.Should().Be(1, "a cancelled/thrown payment must be retryable, not permanently locked");
    }

    /// <summary>A minimal inner wallet that counts pay calls and returns a fixed result.</summary>
    private sealed class CountingWallet : IWalletService
    {
        private readonly NwcPaymentResult _result;
        public int PayCount { get; private set; }
        public CountingWallet(NwcPaymentResult result) => _result = result;

        public bool IsConfigured => true;
        public string ProviderName => "Counting";

        public Task<NwcPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken = default)
        {
            PayCount++;
            return Task.FromResult(_result);
        }

        public Task<NwcBalanceInfo> GetBalanceAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WalletInvoiceResult> CreateInvoiceAsync(long amountSats, string? memo = null, int expirySecs = 3600, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WalletInvoiceStatus> GetInvoiceStatusAsync(string invoiceId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WalletTickerResult> GetTickerAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public NwcConfig? GetConfig() => null;
        public Task<OnChainPaymentResult> SendOnChainAsync(string address, long amountSats, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<CurrencyExchangeResult> ExchangeCurrencyAsync(string sourceCurrency, string targetCurrency, decimal amount, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<MultiCurrencyBalance> GetAllBalancesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    /// <summary>A minimal inner wallet whose PayInvoiceAsync throws (e.g. a cancellation).</summary>
    private sealed class ThrowingWallet : IWalletService
    {
        private readonly Exception _toThrow;
        public ThrowingWallet(Exception toThrow) => _toThrow = toThrow;

        public bool IsConfigured => true;
        public string ProviderName => "Throwing";

        public Task<NwcPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken = default) => throw _toThrow;

        public Task<NwcBalanceInfo> GetBalanceAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WalletInvoiceResult> CreateInvoiceAsync(long amountSats, string? memo = null, int expirySecs = 3600, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WalletInvoiceStatus> GetInvoiceStatusAsync(string invoiceId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WalletTickerResult> GetTickerAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public NwcConfig? GetConfig() => null;
        public Task<OnChainPaymentResult> SendOnChainAsync(string address, long amountSats, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<CurrencyExchangeResult> ExchangeCurrencyAsync(string sourceCurrency, string targetCurrency, decimal amount, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<MultiCurrencyBalance> GetAllBalancesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
