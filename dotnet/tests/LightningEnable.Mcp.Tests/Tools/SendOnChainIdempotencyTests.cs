using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// send_onchain is IRREVERSIBLE and settles over ~10 minutes, so "pending" is its normal state
/// and a lost response after the provider's execute call is AMBIGUOUS, not a failure. These
/// tests drive the tool through the real decorator chain (IdempotentWalletService over
/// ReceiptRecordingWalletService over a scripted fake wallet) and a real on-disk
/// OperationLedger, and prove:
///  - ambiguous / pending outcomes retain budget, write a receipt, and are recorded in the ledger;
///  - a retry with the same address + amount never re-sends while funds may have moved — it
///    reports the recorded payment's status instead;
///  - only a proven pre-submit failure releases budget and allows a fresh send.
/// No real payments: the wallet is a fake and the budget is a mock.
/// </summary>
public class SendOnChainIdempotencyTests
{
    private const string Address = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
    private const long Amount = 5000;
    private const long Headroom = 1000; // max(1000, 10% of 5000)
    private const string Code = "ONCH42";

    private static string TempPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "onchain-idem-" + Guid.NewGuid().ToString("N") + ".jsonl");

    /// <summary>The shared contract: "onchain:" + hex(SHA256("onchain:" + address + ":" + amountSats)).</summary>
    private static string OperationId(string address, long amountSats, string? intentId = null) =>
        "onchain:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("onchain:" + address + ":" + amountSats
                + (string.IsNullOrWhiteSpace(intentId) ? "" : ":" + intentId.Trim())))).ToLowerInvariant();

    private const string AmbiguousWarningText =
        "The send may have executed at the provider even though this call could not confirm it. The budget for it has been retained (not released). Calling send_onchain again with the same address and amount will report this payment's status rather than re-send. Check the provider dashboard / get_balance BEFORE retrying with any other parameters — on-chain payments are irreversible.";

    private const string PendingNoteText =
        "On-chain payments normally stay PENDING for ~10 minutes until confirmed. Do NOT send again: calling send_onchain again with the same address and amount will report this payment's status rather than re-send.";

    private static string BlockedMessage(string? id, string provider) =>
        $"Nothing was sent by this call: an on-chain send for the same address and amount was already submitted (payment id {id ?? "not recorded"}). On-chain payments are irreversible, so it will not be sent again while that payment may have moved funds. Verify its status at the provider ({provider}) before doing anything else. To intentionally pay this address this amount again, supply a new intentId.";

    private sealed class Harness
    {
        public Mock<IBudgetService> Budget { get; } = new();
        public Mock<IReceiptService> Receipts { get; } = new();
        public ConcurrentBag<PaymentReceiptEntry> ReceiptEntries { get; } = new();
        public OperationLedger Ledger { get; }
        public ScriptedWallet Wallet { get; }
        public IWalletService Chain { get; }
        private int _reservations;

        public Harness(ScriptedWallet wallet, string? ledgerPath = null)
        {
            Wallet = wallet;
            Ledger = new OperationLedger(ledgerPath ?? TempPath());

            Budget.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ApprovalCheckResult { Level = ApprovalLevel.AutoApprove, AmountSats = Amount, AmountUsd = 5m });
            Budget.Setup(b => b.ValidateAndConsumeConfirmation(It.IsAny<string>(), It.IsAny<long>(), "send_onchain", It.IsAny<string>()))
                .Returns(() => new PendingConfirmation
                {
                    Nonce = Code,
                    AmountSats = Amount,
                    AmountUsd = 5m,
                    ToolName = "send_onchain",
                    Description = Address,
                    Destination = Address,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(2)
                });
            Budget.Setup(b => b.TryReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((long sats, CancellationToken _) =>
                    SpendReservationResult.Reserved("res-" + Interlocked.Increment(ref _reservations), sats));

            Receipts.Setup(r => r.LogPayment(It.IsAny<PaymentReceiptEntry>()))
                .Returns((PaymentReceiptEntry e) => { ReceiptEntries.Add(e); return true; });

            Chain = new IdempotentWalletService(
                new ReceiptRecordingWalletService(wallet, Receipts.Object, Budget.Object),
                Ledger);
        }

        public Task<string> Send(string? nonce = Code, string? intentId = null) => SendOnChainTool.SendOnChain(
            address: Address,
            amountSats: Amount,
            confirmationNonce: nonce,
            intentId: intentId,
            walletService: Chain,
            budgetService: Budget.Object,
            operationLedger: Ledger);

        public OperationRecord? Record(string? intentId = null) => Ledger.Lookup(OperationId(Address, Amount, intentId));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteSucceeded_PollTimedOut_IsPending_CommitsBudget_WritesPendingReceipt_LedgerPending()
    {
        var wallet = new ScriptedWallet(_ => Task.FromResult(new OnChainPaymentResult
        {
            Success = true, Submitted = true, State = "PENDING",
            PaymentId = "pay-1", QuoteId = "q-1", AmountSats = Amount, FeeSats = 300
        }));
        var h = new Harness(wallet);

        var json = Parse(await h.Send());

        json.GetProperty("payment").GetProperty("state").GetString().Should().Be("PENDING");
        json.GetProperty("payment").GetProperty("id").GetString().Should().Be("pay-1");
        json.GetProperty("receipt_written").GetBoolean().Should().BeTrue();
        h.Budget.Verify(b => b.CommitReservation("res-1", Amount + 300), Times.Once);
        h.Budget.Verify(b => b.ReleaseReservation(It.IsAny<string>()), Times.Never);
        h.ReceiptEntries.Should().ContainSingle().Which.Status.Should().Be("pending");

        var rec = h.Record();
        rec.Should().NotBeNull();
        rec!.State.Should().Be(OperationState.Pending);
        rec.PaymentId.Should().Be("pay-1");
        rec.QuoteId.Should().Be("q-1");
    }

    [Fact]
    public async Task CancellationOnExecute_IsUnknown_CommitsPrincipalPlusHeadroom_LedgerUnknown_WarnsAgent()
    {
        var wallet = new ScriptedWallet(_ => Task.FromResult(new OnChainPaymentResult
        {
            Success = false, Submitted = true, State = "UNKNOWN", QuoteId = "q-2",
            ErrorCode = "TIMEOUT", ErrorMessage = "Execute request timed out"
        }));
        var h = new Harness(wallet);

        var raw = await h.Send();
        var json = Parse(raw);

        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("state").GetString().Should().Be("UNKNOWN");
        json.GetProperty("receipt_written").GetBoolean().Should().BeTrue();
        var warning = json.GetProperty("warning").GetString();
        warning.Should().Contain("may have executed");
        warning.Should().Contain("retained");
        warning.Should().Contain("will report");
        h.Budget.Verify(b => b.CommitReservation("res-1", Amount + Headroom), Times.Once);
        h.Budget.Verify(b => b.ReleaseReservation(It.IsAny<string>()), Times.Never);
        h.ReceiptEntries.Should().ContainSingle();
        h.Record()!.State.Should().Be(OperationState.Unknown);
        h.Record()!.QuoteId.Should().Be("q-2");
        raw.Should().NotContain(Code);
    }

    [Fact]
    public async Task TimeoutDuringQuote_IsProvenPreSubmit_ReleasesBudget_LedgerFailed_RetrySendsExactlyOnce()
    {
        var calls = 0;
        var wallet = new ScriptedWallet(_ => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? new OnChainPaymentResult { Success = false, Submitted = false, ErrorCode = "TIMEOUT", ErrorMessage = "Quote request timed out" }
            : new OnChainPaymentResult { Success = true, Submitted = true, State = "PENDING", PaymentId = "pay-3", QuoteId = "q-3", AmountSats = Amount, FeeSats = 200 }));
        var h = new Harness(wallet);

        var first = Parse(await h.Send());
        first.GetProperty("success").GetBoolean().Should().BeFalse();
        h.Budget.Verify(b => b.ReleaseReservation("res-1"), Times.Once);
        h.Budget.Verify(b => b.CommitReservation("res-1", It.IsAny<long>()), Times.Never);
        h.Record()!.State.Should().Be(OperationState.FailedNoFunds);
        h.ReceiptEntries.Should().BeEmpty();

        var retry = Parse(await h.Send());
        retry.GetProperty("success").GetBoolean().Should().BeTrue();
        wallet.SendCount.Should().Be(2, "a proven pre-submit failure must allow exactly one fresh send");
        h.Record()!.State.Should().Be(OperationState.Pending);
    }

    [Theory]
    [InlineData("PENDING", true)]
    [InlineData("UNKNOWN", false)]
    public async Task Retry_AfterPendingOrUnknown_DoesNotResend_LooksUpStatusOnce_ReportsPaymentId(string firstState, bool firstSuccess)
    {
        var wallet = new ScriptedWallet(_ => Task.FromResult(new OnChainPaymentResult
        {
            Success = firstSuccess, Submitted = true, State = firstState,
            PaymentId = "pay-9", QuoteId = "q-9", AmountSats = Amount, FeeSats = firstSuccess ? 300 : 0,
            ErrorCode = firstSuccess ? null : "HTTP_ERROR", ErrorMessage = firstSuccess ? null : "connection reset"
        }))
        {
            Status = _ => Task.FromResult(new OnChainPaymentResult
            {
                Success = true, Submitted = true, PaymentId = "pay-9", State = "COMPLETED", TxId = "txabc"
            })
        };
        var h = new Harness(wallet);

        await h.Send();
        var raw = await h.Send();
        var retry = Parse(raw);

        wallet.SendCount.Should().Be(1, "a retry while funds may have moved must never reach the wallet's send");
        wallet.StatusCount.Should().Be(1, "the retry refreshes the provider status instead");
        wallet.StatusPaymentIds.Should().ContainSingle().Which.Should().Be("pay-9");
        retry.GetProperty("success").GetBoolean().Should().BeFalse();
        retry.GetProperty("paymentId").GetString().Should().Be("pay-9");
        retry.GetProperty("state").GetString().Should().Be("COMPLETED");
        retry.GetProperty("txId").GetString().Should().Be("txabc");
        // The blocked retry moved nothing and reserved nothing (status is reported before the budget).
        h.Budget.Verify(b => b.TryReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Once);
        retry.GetProperty("errorCode").GetString().Should().Be("ALREADY_SUBMITTED");
        // The refreshed status updates the ledger.
        h.Record()!.State.Should().Be(OperationState.Settled);
        h.Record()!.TxId.Should().Be("txabc");
        h.Record()!.PaymentId.Should().Be("pay-9");
    }

    [Fact]
    public async Task Ledger_SurvivesRestart_RetryStillRefuses_AndNamesRecordedPaymentId()
    {
        var path = TempPath();
        var first = new Harness(new ScriptedWallet(_ => Task.FromResult(new OnChainPaymentResult
        {
            Success = true, Submitted = true, State = "PENDING", PaymentId = "pay-r", QuoteId = "q-r", AmountSats = Amount
        })), path);
        await first.Send();

        // New process: new wallet (no status lookup support), new ledger over the SAME file.
        var wallet2 = new ScriptedWallet(_ => Task.FromResult(new OnChainPaymentResult { Success = true, Submitted = true, State = "PENDING", PaymentId = "pay-dup" }));
        var second = new Harness(wallet2, path);
        var raw = await second.Send();
        var json = Parse(raw);

        wallet2.SendCount.Should().Be(0, "a pending send from a prior process must not be re-sent after restart");
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("paymentId").GetString().Should().Be("pay-r");
        json.GetProperty("message").GetString().Should().Be(BlockedMessage("pay-r", "FakeChain"));
        json.GetProperty("statusLookup").GetProperty("attempted").GetBoolean().Should().BeTrue();
        json.GetProperty("statusLookup").GetProperty("succeeded").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task OperationCanceled_AfterSubmission_CommitsReservation_NotRelease()
    {
        var wallet = new ScriptedWallet(_ => throw new OperationCanceledException("cancelled mid-send"));
        var h = new Harness(wallet);

        string? raw = null;
        try { raw = await h.Send(); }
        catch (OperationCanceledException) { /* propagating is acceptable; the budget must be committed first */ }

        h.Budget.Verify(b => b.CommitReservation("res-1", Amount + Headroom), Times.Once);
        h.Budget.Verify(b => b.ReleaseReservation(It.IsAny<string>()), Times.Never);
        h.Record()!.State.Should().Be(OperationState.Unknown);
        if (raw != null)
        {
            var json = Parse(raw);
            json.GetProperty("success").GetBoolean().Should().BeFalse();
            json.GetProperty("state").GetString().Should().Be("UNKNOWN");
            json.GetProperty("warning").GetString().Should().Contain("may have executed");
        }
    }

    [Fact]
    public async Task TwoConcurrentSends_SameAddressAndAmount_AtMostOneWalletSend()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wallet = new ScriptedWallet(async _ =>
        {
            await gate.Task;
            return new OnChainPaymentResult { Success = true, Submitted = true, State = "PENDING", PaymentId = "pay-c", AmountSats = Amount };
        });
        var h = new Harness(wallet);
        using var barrier = new Barrier(2);

        Task<string> Run() => Task.Run(() => { barrier.SignalAndWait(); return h.Send(); });
        var a = Run();
        var b = Run();

        (await wallet.WaitForEntryAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        var secondEntered = await wallet.WaitForEntryAsync(TimeSpan.FromMilliseconds(750));
        gate.SetResult();
        await Task.WhenAll(a, b);

        secondEntered.Should().BeFalse("the second concurrent send for the same operation must never reach the wallet");
        wallet.SendCount.Should().Be(1);
    }

    [Fact]
    public async Task PlainFailure_ResponseCarriesCheckBeforeRetryingWarning()
    {
        var wallet = new ScriptedWallet(_ => Task.FromResult(new OnChainPaymentResult
        {
            Success = false, Submitted = false, ErrorCode = "HTTP_400", ErrorMessage = "Failed to create on-chain quote"
        }));
        var h = new Harness(wallet);

        var json = Parse(await h.Send());

        json.GetProperty("success").GetBoolean().Should().BeFalse();
        var warning = json.GetProperty("warning").GetString();
        warning.Should().Contain("BEFORE retrying");
        warning.Should().Contain("irreversible");
        h.Budget.Verify(b => b.ReleaseReservation("res-1"), Times.Once);
    }

    // ---------------------------------------------------------------------------------
    // Reconciled cross-port contract: status before code, intentId, error codes, texts.

    private static ScriptedWallet PendingWallet(string paymentId = "pay-p") => new(_ => Task.FromResult(new OnChainPaymentResult
    {
        Success = true, Submitted = true, State = "PENDING", PaymentId = paymentId, QuoteId = "q-p", AmountSats = Amount, FeeSats = 100
    }));

    [Fact]
    public async Task BlockedRetry_WithoutNonce_ReportsStatus_BeforeAnyCodeBudgetOrSend()
    {
        var wallet = PendingWallet();
        wallet.Status = _ => Task.FromResult(new OnChainPaymentResult { Success = true, Submitted = true, PaymentId = "pay-p", State = "PENDING" });
        var h = new Harness(wallet);
        await h.Send();
        h.Budget.Invocations.Clear();

        var json = Parse(await h.Send(nonce: null));

        h.Budget.Verify(b => b.RequestConfirmationAsync(It.IsAny<ConfirmationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Budget.Verify(b => b.ValidateAndConsumeConfirmation(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        h.Budget.Verify(b => b.TryReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Budget.Verify(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        wallet.SendCount.Should().Be(1);
        wallet.StatusCount.Should().Be(1);

        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("errorCode").GetString().Should().Be("ALREADY_SUBMITTED");
        json.GetProperty("duplicate").GetBoolean().Should().BeTrue();
        json.GetProperty("state").GetString().Should().Be("PENDING");
        json.GetProperty("paymentId").GetString().Should().Be("pay-p");
        json.GetProperty("quoteId").GetString().Should().Be("q-p");
        json.TryGetProperty("txId", out _).Should().BeTrue();
        json.GetProperty("provider").GetString().Should().Be("FakeChain");
        json.GetProperty("statusLookup").GetProperty("attempted").GetBoolean().Should().BeTrue();
        json.GetProperty("statusLookup").GetProperty("succeeded").GetBoolean().Should().BeTrue();
        json.GetProperty("receipt_written").GetBoolean().Should().BeFalse();
        json.GetProperty("message").GetString().Should().Be(BlockedMessage("pay-p", "FakeChain"));
        json.TryGetProperty("requiresConfirmation", out _).Should().BeFalse();
    }

    [Fact]
    public async Task BlockedRetry_WithNonce_DoesNotConsumeTheCode()
    {
        var h = new Harness(PendingWallet());
        await h.Send();
        h.Budget.Invocations.Clear();

        var json = Parse(await h.Send());

        json.GetProperty("errorCode").GetString().Should().Be("ALREADY_SUBMITTED");
        h.Budget.Verify(b => b.ValidateAndConsumeConfirmation(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        h.Budget.Verify(b => b.TryReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BlockedRetry_NoPaymentIdRecorded_DoesNotLookUpStatus_SaysNotRecorded()
    {
        var wallet = new ScriptedWallet(_ => Task.FromResult(new OnChainPaymentResult
        {
            Success = false, Submitted = true, State = "UNKNOWN", ErrorCode = "TIMEOUT", ErrorMessage = "timed out"
        }));
        var h = new Harness(wallet);
        await h.Send();

        var json = Parse(await h.Send(nonce: null));

        wallet.StatusCount.Should().Be(0);
        json.GetProperty("statusLookup").GetProperty("attempted").GetBoolean().Should().BeFalse();
        json.GetProperty("statusLookup").GetProperty("succeeded").GetBoolean().Should().BeFalse();
        json.GetProperty("state").GetString().Should().Be("UNKNOWN");
        json.GetProperty("message").GetString().Should().Be(BlockedMessage(null, "FakeChain"));
    }

    [Fact]
    public async Task NewIntentId_SameAddressAndAmount_SendsOnceMore()
    {
        var wallet = PendingWallet();
        var h = new Harness(wallet);

        Parse(await h.Send()).GetProperty("success").GetBoolean().Should().BeTrue();
        var second = Parse(await h.Send(intentId: "rent-october"));

        second.GetProperty("success").GetBoolean().Should().BeTrue();
        wallet.SendCount.Should().Be(2);
        h.Record("rent-october")!.State.Should().Be(OperationState.Pending);
        h.Record()!.State.Should().Be(OperationState.Pending);
    }

    [Fact]
    public async Task SameIntentIdTwice_IsBlocked()
    {
        var wallet = PendingWallet();
        var h = new Harness(wallet);

        await h.Send(intentId: "inv-7");
        var second = Parse(await h.Send(intentId: " inv-7 "));

        second.GetProperty("errorCode").GetString().Should().Be("ALREADY_SUBMITTED");
        wallet.SendCount.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankIntentId_EqualsOmitted(string blank)
    {
        var wallet = PendingWallet();
        var h = new Harness(wallet);

        await h.Send();
        var second = Parse(await h.Send(intentId: blank));

        second.GetProperty("errorCode").GetString().Should().Be("ALREADY_SUBMITTED");
        wallet.SendCount.Should().Be(1);
    }

    [Fact]
    public void OperationId_IncludesTrimmedIntentId()
    {
        IdempotentWalletService.DeriveOnChainOperationId(Address, Amount, " x ").Should().Be(OperationId(Address, Amount, "x"));
        IdempotentWalletService.DeriveOnChainOperationId(Address, Amount, " ").Should().Be(OperationId(Address, Amount));
        IdempotentWalletService.DeriveOnChainOperationId(Address, Amount, null).Should().Be(OperationId(Address, Amount));
    }

    [Fact]
    public async Task Ambiguous_UsesOutcomeUnknown_AndCanonicalWarning()
    {
        var h = new Harness(new ScriptedWallet(_ => Task.FromResult(new OnChainPaymentResult
        {
            Success = false, Submitted = true, State = "UNKNOWN", PaymentId = "pay-u", QuoteId = "q-u",
            ErrorCode = "TIMEOUT", ErrorMessage = "Execute request timed out"
        })));

        var json = Parse(await h.Send());

        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("errorCode").GetString().Should().Be("OUTCOME_UNKNOWN");
        json.GetProperty("state").GetString().Should().Be("UNKNOWN");
        json.GetProperty("paymentId").GetString().Should().Be("pay-u");
        json.GetProperty("quoteId").GetString().Should().Be("q-u");
        json.TryGetProperty("txId", out _).Should().BeTrue();
        json.GetProperty("provider").GetString().Should().Be("FakeChain");
        json.GetProperty("amountSats").GetInt64().Should().Be(Amount);
        json.TryGetProperty("receipt_written", out _).Should().BeTrue();
        json.GetProperty("warning").GetString().Should().Be(AmbiguousWarningText);
    }

    [Fact]
    public async Task Exception_IsOutcomeUnknown()
    {
        var h = new Harness(new ScriptedWallet(_ => throw new HttpRequestException("reset")));
        var json = Parse(await h.Send());
        json.GetProperty("errorCode").GetString().Should().Be("OUTCOME_UNKNOWN");
        json.GetProperty("state").GetString().Should().Be("UNKNOWN");
    }

    [Fact]
    public async Task PendingSuccess_HasTopLevelStateAndPaymentId_AndCanonicalNote()
    {
        var h = new Harness(PendingWallet("pay-top"));

        var json = Parse(await h.Send());

        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("state").GetString().Should().Be("PENDING");
        json.GetProperty("paymentId").GetString().Should().Be("pay-top");
        json.GetProperty("note").GetString().Should().Be(PendingNoteText);
    }

    [Fact]
    public async Task WalletLayerGuard_StillBlocks_WithAlreadySubmitted_WhenToolHasNoLedger()
    {
        var wallet = PendingWallet();
        var h = new Harness(wallet);
        await h.Send();

        // No ledger injected into the tool: the wallet-layer (race-closing) guard must still hold.
        var json = Parse(await SendOnChainTool.SendOnChain(address: Address, amountSats: Amount, confirmationNonce: Code,
            walletService: h.Chain, budgetService: h.Budget.Object));

        wallet.SendCount.Should().Be(1);
        json.GetProperty("errorCode").GetString().Should().Be("ALREADY_SUBMITTED");
        json.GetProperty("message").GetString().Should().Be(BlockedMessage("pay-p", "FakeChain"));
        h.Budget.Verify(b => b.ReleaseReservation("res-2"), Times.Once);
    }


    // ---------------------------------------------------------------------------------

    /// <summary>Fake on-chain wallet: scripted send, optional status lookup, entry signalling.</summary>
    private sealed class ScriptedWallet : IWalletService
    {
        private readonly Func<CancellationToken, Task<OnChainPaymentResult>> _send;
        private readonly SemaphoreSlim _entered = new(0);
        private int _sendCount;
        private int _statusCount;

        public ScriptedWallet(Func<CancellationToken, Task<OnChainPaymentResult>> send) => _send = send;

        /// <summary>Status lookup; null means "not supported" (the interface default).</summary>
        public Func<string, Task<OnChainPaymentResult>>? Status { get; set; }

        public int SendCount => Volatile.Read(ref _sendCount);
        public int StatusCount => Volatile.Read(ref _statusCount);
        public ConcurrentQueue<string> StatusPaymentIds { get; } = new();

        public Task<bool> WaitForEntryAsync(TimeSpan timeout) => _entered.WaitAsync(timeout);

        public bool IsConfigured => true;
        public string ProviderName => "FakeChain";

        public Task<OnChainPaymentResult> SendOnChainAsync(string address, long amountSats, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _sendCount);
            _entered.Release();
            return _send(cancellationToken);
        }

        public Task<OnChainPaymentResult> GetOnChainPaymentStatusAsync(string paymentId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _statusCount);
            StatusPaymentIds.Enqueue(paymentId);
            return Status != null ? Status(paymentId) : Task.FromResult(OnChainPaymentResult.NotSupported());
        }

        public Task<NwcPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<NwcBalanceInfo> GetBalanceAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WalletInvoiceResult> CreateInvoiceAsync(long amountSats, string? memo = null, int expirySecs = 3600, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WalletInvoiceStatus> GetInvoiceStatusAsync(string invoiceId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WalletTickerResult> GetTickerAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public NwcConfig? GetConfig() => null;
        public Task<CurrencyExchangeResult> ExchangeCurrencyAsync(string sourceCurrency, string targetCurrency, decimal amount, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<MultiCurrencyBalance> GetAllBalancesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
