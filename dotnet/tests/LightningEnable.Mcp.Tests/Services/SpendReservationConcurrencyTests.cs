using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using FluentAssertions;
using Moq;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// P0 concurrency regression for the check-then-pay-then-record race in the budget
/// service (atomic spend-reservations remediation).
///
/// The session cap is only a real cap if a payment cannot be authorized against a
/// balance that another in-flight payment is about to consume. The current code
/// checks the budget, releases the lock, calls the wallet, and only records the
/// spend afterward — so two concurrent payments can both pass their check against
/// the same pre-payment balance and collectively exceed the cap.
///
/// This test forces that exact interleaving with a gate-controlled fake wallet that
/// parks every payment at the point AFTER the budget check but BEFORE any spend is
/// recorded. It drives the REAL <see cref="BudgetService"/> through the REAL
/// <see cref="PayInvoiceTool"/> so it exercises the actual production path, and it
/// asserts the invariant — not the current (buggy) behavior — so it stays valid
/// through the reservation-lifecycle refactor.
/// </summary>
public class SpendReservationConcurrencyTests
{
    private readonly Mock<IBudgetConfigurationService> _configServiceMock = new();
    private readonly Mock<IPriceService> _priceServiceMock = new();

    // 60,000 sats each at the mock rate (100,000 sats = $1.00) => $0.60 per payment.
    // One fits under a 100,000-sat ($1.00) session cap; two together (120,000) must not.
    private const string InvoiceA = "lnbc600000n1p3aaaaaa";
    private const string InvoiceB = "lnbc600000n1p3bbbbbb";
    private const long PaymentSats = 60_000;
    private const long SessionCapSats = 100_000;
    private const string ValidPreimage = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

    public SpendReservationConcurrencyTests()
    {
        // Deterministic mock rate: 100,000 sats = $1.00 (NOT a real BTC price).
        _priceServiceMock.Setup(p => p.SatsToUsdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long sats, CancellationToken _) => sats / 100_000m);
        _priceServiceMock.Setup(p => p.UsdToSatsAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((decimal usd, CancellationToken _) => (long)(usd * 100_000));
        _priceServiceMock.Setup(p => p.GetBtcPriceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100_000m);

        // Session cap $1.00 (= 100,000 sats). Per-payment $1.00 so a single 60,000-sat
        // payment is allowed. Tiers leave $0.60 in the log-and-approve band (no
        // interactive confirmation), first-payment approval off, cooldown off — so the
        // ONLY thing that can stop the second payment is the session cap.
        _configServiceMock.Setup(c => c.Configuration).Returns(new UserBudgetConfiguration
        {
            Currency = "USD",
            Tiers = new TierThresholds
            {
                AutoApprove = 0.10m,
                LogAndApprove = 10.00m,
                FormConfirm = 100.00m,
                UrlConfirm = 1000.00m
            },
            Limits = new PaymentLimits
            {
                MaxPerPayment = 1.00m,
                MaxPerSession = 1.00m
            },
            Session = new SessionSettings
            {
                RequireApprovalForFirstPayment = false,
                CooldownSeconds = 0
            }
        });
    }

    [Fact]
    public async Task TwoConcurrentPayments_CannotCollectivelyExceedSessionCap()
    {
        // Arrange
        var budget = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);
        var wallet = new GatedWallet();

        // Act — fire both payments concurrently. Each does check -> (park in wallet) ->
        // record. The gate holds them at the post-check/pre-record window.
        var callA = Task.Run(() => PayInvoiceTool.PayInvoice(
            InvoiceA, walletService: wallet, budgetService: budget, priceService: _priceServiceMock.Object));
        var callB = Task.Run(() => PayInvoiceTool.PayInvoice(
            InvoiceB, walletService: wallet, budgetService: budget, priceService: _priceServiceMock.Object));

        // At least one payment always reaches the wallet — wait for the first entry.
        (await wallet.WaitForEntryAsync(TimeSpan.FromSeconds(5)))
            .Should().BeTrue("at least one payment must reach the wallet");

        // Give a would-be second entry a chance to arrive. Under the buggy code both
        // pass the check and both reach the wallet; under the fixed code the second is
        // denied at reservation time and never reaches the wallet (its task returns).
        var secondEntered = await wallet.WaitForEntryAsync(TimeSpan.FromMilliseconds(750));

        // Release whoever is parked and let both tool calls finish.
        wallet.ReleaseAll();
        var resultA = await callA;
        var resultB = await callB;

        // Assert — the cap is a cap.
        wallet.PayCallCount.Should().Be(1,
            "exactly one payment may reach the wallet; the other must be denied before it can spend");
        secondEntered.Should().BeFalse(
            "the second concurrent payment must be denied at the budget check, not reach the wallet");

        var successCount = new[] { resultA, resultB }.Count(ToolResult.IsSuccess);
        successCount.Should().Be(1, "exactly one of two over-cap concurrent payments may succeed");

        budget.GetConfig().SessionSpent.Should().BeLessThanOrEqualTo(SessionCapSats,
            "settled spend must never exceed the configured session cap");
        budget.GetConfig().SessionSpent.Should().Be(PaymentSats, "only the single winning payment settled");
    }

    private static class ToolResult
    {
        public static bool IsSuccess(string json)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("success", out var s)
                   && s.ValueKind == System.Text.Json.JsonValueKind.True;
        }
    }

    /// <summary>
    /// A fake wallet that parks every <see cref="PayInvoiceAsync"/> call after signaling
    /// it has been entered, holding it until the test releases the gate. This keeps both
    /// concurrent tool calls at the exact window between the budget check and the spend
    /// record, where the race lives.
    /// </summary>
    private sealed class GatedWallet : IWalletService
    {
        private readonly SemaphoreSlim _entered = new(0);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _payCallCount;

        public int PayCallCount => Volatile.Read(ref _payCallCount);

        public Task<bool> WaitForEntryAsync(TimeSpan timeout) => _entered.WaitAsync(timeout);

        public void ReleaseAll() => _release.TrySetResult();

        public bool IsConfigured => true;
        public string ProviderName => "Gated";

        public async Task<NwcPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _payCallCount);
            _entered.Release();       // announce "a payment reached the wallet"
            await _release.Task;      // park until the test opens the gate
            return NwcPaymentResult.Succeeded(ValidPreimage);
        }

        public Task<NwcBalanceInfo> GetBalanceAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<WalletInvoiceResult> CreateInvoiceAsync(long amountSats, string? memo = null, int expirySecs = 3600, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<WalletInvoiceStatus> GetInvoiceStatusAsync(string invoiceId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<WalletTickerResult> GetTickerAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public NwcConfig? GetConfig() => null;
        public Task<OnChainPaymentResult> SendOnChainAsync(string address, long amountSats, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<CurrencyExchangeResult> ExchangeCurrencyAsync(string sourceCurrency, string targetCurrency, decimal amount, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<MultiCurrencyBalance> GetAllBalancesAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
