using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// Coverage for the receipt seam: EVERY payment that moves value through
/// <see cref="IWalletService"/> must leave exactly one durable receipt in
/// receipts.jsonl, regardless of which tool initiated it. Before this seam
/// existed only access_l402_resource wrote receipts — a pay_invoice purchase
/// left no durable record at all (the 2026-08-11 Musqet purchase).
/// </summary>
public class ReceiptRecordingWalletServiceTests
{
    // 210n = 210 nano-BTC = 21 sats
    private const string TestInvoice = "lnbc210n1p3abcdef";
    private const long TestInvoiceSats = 21;
    private const string TestPreimage = "5f78ca4b8e2c11d3a9b0f6e1d2c3b4a5968778695a4b3c2d1e0f9a8b7c6d5e4f";

    private static string ExpectedPaymentHash =>
        Convert.ToHexString(SHA256.HashData(Convert.FromHexString(TestPreimage))).ToLowerInvariant();

    private static string TempReceiptsPath() =>
        Path.Combine(Path.GetTempPath(), "le-receipts-tests", Guid.NewGuid().ToString("N"), "receipts.jsonl");

    private static Mock<IWalletService> WalletMock(string provider = "NWC")
    {
        var wallet = new Mock<IWalletService>();
        wallet.SetupGet(w => w.IsConfigured).Returns(true);
        wallet.SetupGet(w => w.ProviderName).Returns(provider);
        return wallet;
    }

    private static Mock<IBudgetService> BudgetMock(long sessionSpent = 0)
    {
        var budget = new Mock<IBudgetService>();
        budget.Setup(b => b.GetConfig()).Returns(new BudgetConfig { SessionSpent = sessionSpent });
        budget.Setup(b => b.CheckBudget(It.IsAny<long>()))
            .Returns(new BudgetCheckResult { Allowed = true });
        budget.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalCheckResult
            {
                Level = ApprovalLevel.AutoApprove,
                AmountSats = TestInvoiceSats,
                AmountUsd = 0.02m
            });
        return budget;
    }

    private static List<JsonObject> ReadAll(string path) =>
        new ReceiptService(path).ReadRecent(200).Select(r => r.AsObject()).ToList();

    // ---------------------------------------------------------------
    // The decorator seam: one receipt per settled payment, any wallet
    // ---------------------------------------------------------------

    [Fact]
    public async Task PayInvoice_Settled_WritesExactlyOneGenericReceipt()
    {
        var path = TempReceiptsPath();
        var wallet = WalletMock();
        wallet.Setup(w => w.PayInvoiceAsync(TestInvoice, It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded(TestPreimage));
        var svc = new ReceiptRecordingWalletService(wallet.Object, new ReceiptService(path), BudgetMock().Object);

        var result = await svc.PayInvoiceAsync(TestInvoice);

        result.Success.Should().BeTrue();
        var receipts = ReadAll(path);
        receipts.Should().HaveCount(1);
        var r = receipts[0];
        r["type"]!.GetValue<string>().Should().Be("payment_receipt");
        r["kind"]!.GetValue<string>().Should().Be("invoice");
        r["wallet"]!.GetValue<string>().Should().Be("NWC");
        r["amountSats"]!.GetValue<long>().Should().Be(TestInvoiceSats);
        r["status"]!.GetValue<string>().Should().Be("settled");
        r["paymentHash"]!.GetValue<string>().Should().Be(ExpectedPaymentHash);
        r["revokePath"]!.GetValue<string>().ToLowerInvariant().Should().Contain("connection");
        r["timestamp"]!.GetValue<string>().Should().EndWith("Z");
    }

    [Fact]
    public async Task Receipt_IsReadableByAFreshReader_AfterRestart()
    {
        var path = TempReceiptsPath();
        var wallet = WalletMock();
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded(TestPreimage));
        var svc = new ReceiptRecordingWalletService(wallet.Object, new ReceiptService(path), null);

        await svc.PayInvoiceAsync(TestInvoice);

        // A brand-new reader on the same path (fresh process) must see the receipt.
        var fresh = new ReceiptService(path).ReadRecent(10);
        fresh.Should().HaveCount(1);
        fresh[0].AsObject()["amountSats"]!.GetValue<long>().Should().Be(TestInvoiceSats);
    }

    [Theory]
    [InlineData("NWC")]
    [InlineData("Strike")]
    [InlineData("LND")]
    public async Task EveryPreimageProvider_WritesViaDecorator(string provider)
    {
        var path = TempReceiptsPath();
        var wallet = WalletMock(provider);
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded(TestPreimage));
        var svc = new ReceiptRecordingWalletService(wallet.Object, new ReceiptService(path), null);

        await svc.PayInvoiceAsync(TestInvoice);

        var r = ReadAll(path).Single();
        r["wallet"]!.GetValue<string>().Should().Be(provider);
    }

    [Fact]
    public async Task OpenNode_SettledWithoutPreimage_StillWritesReceipt_WithoutPaymentHash()
    {
        var path = TempReceiptsPath();
        var wallet = WalletMock("OpenNode");
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.SucceededWithoutPreimage("wd_123", "no preimage"));
        var svc = new ReceiptRecordingWalletService(wallet.Object, new ReceiptService(path), null);

        await svc.PayInvoiceAsync(TestInvoice);

        var r = ReadAll(path).Single();
        r["wallet"]!.GetValue<string>().Should().Be("OpenNode");
        r["status"]!.GetValue<string>().Should().Be("settled");
        r.ContainsKey("paymentHash").Should().BeFalse("no preimage means no derivable payment hash");
    }

    [Fact]
    public async Task FailedPayment_WritesNoReceipt()
    {
        var path = TempReceiptsPath();
        var wallet = WalletMock();
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Failed("ROUTE_NOT_FOUND", "no route"));
        var svc = new ReceiptRecordingWalletService(wallet.Object, new ReceiptService(path), null);

        var result = await svc.PayInvoiceAsync(TestInvoice);

        result.Success.Should().BeFalse();
        ReadAll(path).Should().BeEmpty("no money moved, so nothing to receipt");
    }

    [Fact]
    public async Task PendingPayment_WritesPendingReceipt()
    {
        // Funds are committed on a pending payment (budget records them); the durable
        // log must not under-report relative to the budget.
        var path = TempReceiptsPath();
        var wallet = WalletMock();
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Pending("track_1", "in flight"));
        var svc = new ReceiptRecordingWalletService(wallet.Object, new ReceiptService(path), null);

        await svc.PayInvoiceAsync(TestInvoice);

        var r = ReadAll(path).Single();
        r["status"]!.GetValue<string>().Should().Be("pending");
        r.ContainsKey("paymentHash").Should().BeFalse();
    }

    [Fact]
    public async Task SendOnChain_WritesOnchainReceipt()
    {
        var path = TempReceiptsPath();
        var wallet = WalletMock("Strike");
        wallet.Setup(w => w.SendOnChainAsync("bc1qtestaddr", 5000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnChainPaymentResult.Succeeded("pay_1", "txid_abc", "COMPLETED", 5000, feeSats: 250));
        var svc = new ReceiptRecordingWalletService(wallet.Object, new ReceiptService(path), null);

        var result = await svc.SendOnChainAsync("bc1qtestaddr", 5000);

        result.Success.Should().BeTrue();
        var r = ReadAll(path).Single();
        r["kind"]!.GetValue<string>().Should().Be("onchain");
        r["amountSats"]!.GetValue<long>().Should().Be(5000);
        r["feeSats"]!.GetValue<long>().Should().Be(250);
        r["txId"]!.GetValue<string>().Should().Be("txid_abc");
        r["wallet"]!.GetValue<string>().Should().Be("Strike");
    }

    [Fact]
    public async Task SendOnChain_ProviderAdjustedAmount_ProjectsSessionFromRequestedAmount()
    {
        // SendOnChainTool records budget spend as REQUESTED amount + fee, so the
        // receipt's projected session total must use the same figures even when the
        // provider reports an adjusted sent amount — otherwise receipts.jsonl and
        // get_budget_status permanently disagree.
        var path = TempReceiptsPath();
        var wallet = WalletMock("Strike");
        wallet.Setup(w => w.SendOnChainAsync("bc1qtestaddr", 5000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnChainPaymentResult.Succeeded("pay_1", "txid_abc", "COMPLETED", 4990, feeSats: 250));
        var svc = new ReceiptRecordingWalletService(wallet.Object, new ReceiptService(path), BudgetMock(sessionSpent: 100).Object);

        await svc.SendOnChainAsync("bc1qtestaddr", 5000);

        var r = ReadAll(path).Single();
        r["amountSats"]!.GetValue<long>().Should().Be(4990, "the receipt records what the provider says was sent");
        r["sessionSpentSats"]!.GetValue<long>().Should().Be(100 + 5000 + 250, "the projection must match what the budget records");
    }

    [Fact]
    public async Task SendOnChain_Failed_WritesNoReceipt()
    {
        var path = TempReceiptsPath();
        var wallet = WalletMock();
        wallet.Setup(w => w.SendOnChainAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnChainPaymentResult.NotSupported());
        var svc = new ReceiptRecordingWalletService(wallet.Object, new ReceiptService(path), null);

        await svc.SendOnChainAsync("bc1qtestaddr", 5000);

        ReadAll(path).Should().BeEmpty();
    }

    [Fact]
    public async Task ReceiptFile_CarriesNoSecrets()
    {
        var path = TempReceiptsPath();
        var wallet = WalletMock();
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded(TestPreimage));
        var svc = new ReceiptRecordingWalletService(wallet.Object, new ReceiptService(path), BudgetMock().Object);

        using (var scope = PaymentReceiptScope.Begin("l402", context: "https://api.example.com/paid", policy: "auto_approve"))
        {
            await svc.PayInvoiceAsync(TestInvoice);
        }

        var raw = File.ReadAllText(path);
        raw.Should().NotContain(TestPreimage, "the preimage is proof-of-payment and must never persist");
        raw.Should().NotContain(TestInvoice, "the BOLT11 invoice must never persist");
        foreach (var forbidden in new[] { "preimage", "macaroon", "nostr+walletconnect", "connectionString" })
            raw.Should().NotContain(forbidden);
    }

    // ---------------------------------------------------------------
    // Ambient intent scope: context enrichment + honest write signal
    // ---------------------------------------------------------------

    [Fact]
    public async Task Scope_EnrichesReceipt_AndSignalsWritten()
    {
        var path = TempReceiptsPath();
        var wallet = WalletMock();
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded(TestPreimage));
        var svc = new ReceiptRecordingWalletService(wallet.Object, new ReceiptService(path), null);

        using var scope = PaymentReceiptScope.Begin("l402", context: "https://api.example.com/data", policy: "auto_approve");
        await svc.PayInvoiceAsync(TestInvoice);

        scope.ReceiptWritten.Should().BeTrue();
        var r = ReadAll(path).Single();
        r["kind"]!.GetValue<string>().Should().Be("l402");
        r["context"]!.GetValue<string>().Should().Be("https://api.example.com/data");
        r["policy"]!.GetValue<string>().Should().Be("auto_approve");
    }

    [Fact]
    public async Task Scope_NoPaymentObserved_ReceiptWrittenIsNull()
    {
        using var scope = PaymentReceiptScope.Begin("invoice");
        scope.ReceiptWritten.Should().BeNull("no payment has been attempted under this scope");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Scope_IsRestoredOnDispose()
    {
        using (PaymentReceiptScope.Begin("invoice"))
        {
            PaymentReceiptScope.Current.Should().NotBeNull();
        }
        PaymentReceiptScope.Current.Should().BeNull();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WriteFailure_SignalsReceiptWrittenFalse_PaymentStillSucceeds()
    {
        // Parent path is a FILE so the write must fail — the payment result must be
        // untouched, and the failure must be VISIBLE via the scope, never silent.
        var dir = Path.Combine(Path.GetTempPath(), "le-receipts-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var aFile = Path.Combine(dir, "afile");
        File.WriteAllText(aFile, "x");
        var badReceipts = new ReceiptService(Path.Combine(aFile, "sub", "receipts.jsonl"));

        var wallet = WalletMock();
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded(TestPreimage));
        var svc = new ReceiptRecordingWalletService(wallet.Object, badReceipts, null);

        using var scope = PaymentReceiptScope.Begin("invoice");
        var result = await svc.PayInvoiceAsync(TestInvoice);

        result.Success.Should().BeTrue("a receipt failure must NEVER break a payment");
        scope.ReceiptWritten.Should().BeFalse("a failed write must be visible, not hidden");
    }

    [Fact]
    public async Task SessionSpentSats_ProjectsPostPaymentTotal()
    {
        // The decorator writes BEFORE the caller records the spend, so the receipt
        // carries the projected post-payment session total (current + this payment),
        // matching what get_budget_status reports right after the tool returns.
        var path = TempReceiptsPath();
        var wallet = WalletMock();
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded(TestPreimage));
        var svc = new ReceiptRecordingWalletService(wallet.Object, new ReceiptService(path), BudgetMock(sessionSpent: 100).Object);

        await svc.PayInvoiceAsync(TestInvoice);

        ReadAll(path).Single()["sessionSpentSats"]!.GetValue<long>().Should().Be(100 + TestInvoiceSats);
    }

    [Fact]
    public void OldL402ReceiptLines_RemainReadable_AlongsideNewOnes()
    {
        var path = TempReceiptsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A pre-generalization line, exactly as the old writer produced it.
        File.WriteAllText(path,
            "{\"type\":\"l402_payment_receipt\",\"timestamp\":\"2026-08-01T00:00:00.000Z\",\"endpoint\":\"https://e/x\",\"amountSats\":7,\"wallet\":\"NWC\",\"policy\":\"auto_approve\",\"sessionSpentSats\":7,\"revokePath\":\"r\"}\n");

        var svc = new ReceiptService(path);
        svc.LogPayment(new PaymentReceiptEntry
        {
            Kind = "invoice",
            Wallet = "NWC",
            AmountSats = 21,
            Status = "settled"
        }).Should().BeTrue();

        var recs = svc.ReadRecent(10);
        recs.Should().HaveCount(2);
        recs[0].AsObject()["type"]!.GetValue<string>().Should().Be("l402_payment_receipt");
        recs[1].AsObject()["type"]!.GetValue<string>().Should().Be("payment_receipt");
    }

    // ---------------------------------------------------------------
    // Tool level: the actual reported bug (pay_invoice left no record)
    // ---------------------------------------------------------------

    [Fact]
    public async Task PayInvoiceTool_PersistsReceipt_AndReportsReceiptWritten()
    {
        var path = TempReceiptsPath();
        var wallet = WalletMock();
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded(TestPreimage));
        var decorated = new ReceiptRecordingWalletService(wallet.Object, new ReceiptService(path), null);

        var result = await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: decorated);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("receipt_written").GetBoolean().Should().BeTrue();

        var r = ReadAll(path).Single();
        r["kind"]!.GetValue<string>().Should().Be("invoice");
        r["amountSats"]!.GetValue<long>().Should().Be(TestInvoiceSats);
    }

    [Fact]
    public async Task AccessL402Resource_WritesExactlyOneReceipt_NotTwo()
    {
        // The old per-tool LogPayment call is gone; the decorator is now the only
        // writer. An L402 payment must produce exactly ONE receipt line.
        var path = TempReceiptsPath();
        var receipts = new ReceiptService(path);

        var wallet = WalletMock();
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded(TestPreimage));
        var budget = BudgetMock();
        var decorated = new ReceiptRecordingWalletService(wallet.Object, receipts, budget.Object);

        var handler = new L402ThenOkHandler(TestInvoice);
        var client = new L402HttpClient(
            new HttpClient(handler),
            decorated,
            budget.Object,
            new Mock<IPaymentHistoryService>().Object);

        var result = await AccessL402ResourceTool.AccessL402Resource(
            url: "https://api.example.com/paid/data",
            l402Client: client,
            budgetService: budget.Object);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("receipt_written").GetBoolean().Should().BeTrue();

        var all = ReadAll(path);
        all.Should().HaveCount(1, "the decorator is the single receipt writer — no per-tool double write");
        all[0]["kind"]!.GetValue<string>().Should().Be("l402");
        all[0]["context"]!.GetValue<string>().Should().Contain("api.example.com");
        all[0]["amountSats"]!.GetValue<long>().Should().Be(TestInvoiceSats);
    }

    [Fact]
    public async Task AccessL402Resource_PendingPayment_WritesPendingReceipt_AndSurfacesPaid()
    {
        // Money moved (committed, not settled): the seam writes a pending receipt and
        // the tool result must carry receipt_written + a paid marker instead of a bare
        // error — otherwise the committed sats are invisible in the tool result.
        var path = TempReceiptsPath();
        var wallet = WalletMock();
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Pending("trk_1", "in flight"));
        var budget = BudgetMock();
        var decorated = new ReceiptRecordingWalletService(wallet.Object, new ReceiptService(path), budget.Object);

        var client = new L402HttpClient(
            new HttpClient(new L402ThenOkHandler(TestInvoice)),
            decorated,
            budget.Object,
            new Mock<IPaymentHistoryService>().Object);

        var result = await AccessL402ResourceTool.AccessL402Resource(
            url: "https://api.example.com/paid/data",
            l402Client: client,
            budgetService: budget.Object);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("receipt_written").GetBoolean().Should().BeTrue();
        json.GetProperty("payment").GetProperty("paid").GetBoolean().Should().BeTrue();
        json.GetProperty("payment").GetProperty("amountSats").GetInt64().Should().Be(TestInvoiceSats);

        var r = ReadAll(path).Single();
        r["status"]!.GetValue<string>().Should().Be("pending");
    }

    /// <summary>402 with an L402 challenge until an Authorization header arrives, then 200.</summary>
    private sealed class L402ThenOkHandler : HttpMessageHandler
    {
        private readonly string _invoice;
        public L402ThenOkHandler(string invoice) => _invoice = invoice;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization != null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":\"paid content\"}")
                });
            }

            var challenge = new HttpResponseMessage(HttpStatusCode.PaymentRequired)
            {
                Content = new StringContent("payment required")
            };
            challenge.Headers.TryAddWithoutValidation(
                "WWW-Authenticate", $"L402 macaroon=\"dGVzdC1tYWNhcm9vbg==\", invoice=\"{_invoice}\"");
            return Task.FromResult(challenge);
        }
    }
}
