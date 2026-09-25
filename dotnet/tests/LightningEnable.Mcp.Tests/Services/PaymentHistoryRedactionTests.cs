using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Resources;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// B1 — the session payment history and the durable receipts hold only SAFE correlation
/// material. The immediate tool result of a payment may return the preimage / L402 token
/// (the protocol needs them), but none of it may be copied into history, receipts, the
/// receipt resources, or anything reachable through JSON / <c>ToString()</c> of those
/// objects. Mirrors the Python port, whose <c>PaymentRecord</c> has no preimage field at all.
///
/// Every secret is seeded as a conspicuous sentinel and the whole rendered surface is
/// searched for each one.
/// </summary>
public class PaymentHistoryRedactionTests
{
    // --- sentinels ---------------------------------------------------------------------
    // 210n = 21 sats. The invoice string itself is the sentinel (it is unique enough).
    private static readonly string SentinelInvoice = TestInvoices.Build("lnbc210n");
    private const long SentinelInvoiceSats = 21;

    // A valid 64-hex preimage that is unmistakable in any dump.
    private const string SentinelPreimage =
        "feedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedface";

    private const string SentinelMacaroonPlain = "LEAKED_MACAROON_SENTINEL";
    private static readonly string SentinelMacaroonB64 =
        Convert.ToBase64String(Encoding.UTF8.GetBytes(SentinelMacaroonPlain));

    private const string SentinelUser = "LEAKED_USERINFO_USER";
    private const string SentinelPassword = "LEAKED_USERINFO_PASSWORD";
    private const string SentinelQueryKey = "LEAKED_QUERY_API_KEY";
    private const string SentinelFragment = "LEAKED_FRAGMENT";

    private static readonly string SentinelUrl =
        $"https://{SentinelUser}:{SentinelPassword}@api.example.com/paid/data?api_key={SentinelQueryKey}#{SentinelFragment}";

    private static string ExpectedPaymentHash =>
        Convert.ToHexString(SHA256.HashData(Convert.FromHexString(SentinelPreimage))).ToLowerInvariant();

    /// <summary>Everything that must never leave the payment store.</summary>
    private static readonly string[] Sentinels =
    [
        SentinelInvoice,
        SentinelPreimage,
        SentinelMacaroonB64,
        SentinelMacaroonPlain,
        SentinelUser,
        SentinelPassword,
        SentinelQueryKey,
        SentinelFragment,
        SentinelMacaroonB64 + ":" + SentinelPreimage,
    ];

    // --- scaffolding -------------------------------------------------------------------

    private static string TempReceiptsPath() =>
        Path.Combine(Path.GetTempPath(), "le-history-redaction-tests", Guid.NewGuid().ToString("N"), "receipts.jsonl");

    private static Mock<IWalletService> WalletMock(NwcPaymentResult payResult)
    {
        var wallet = new Mock<IWalletService>();
        wallet.SetupGet(w => w.IsConfigured).Returns(true);
        wallet.SetupGet(w => w.ProviderName).Returns("NWC");
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(payResult);
        return wallet;
    }

    private static Mock<IBudgetService> BudgetMock()
    {
        var budget = new Mock<IBudgetService>();
        budget.Setup(b => b.GetConfig()).Returns(new BudgetConfig());
        budget.Setup(b => b.CheckBudget(It.IsAny<long>()))
            .Returns(new BudgetCheckResult { Allowed = true });
        budget.Setup(b => b.TryReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long amt, CancellationToken _) => SpendReservationResult.Reserved("res", amt));
        budget.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalCheckResult
            {
                Level = ApprovalLevel.AutoApprove,
                AmountSats = SentinelInvoiceSats,
                AmountUsd = 0.02m
            });
        return budget;
    }

    /// <summary>402 with an L402 challenge (sentinel macaroon + invoice) until authorized, then 200.</summary>
    private sealed class L402ThenOkHandler : HttpMessageHandler
    {
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
            challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                "L402", $"macaroon=\"{SentinelMacaroonB64}\", invoice=\"{SentinelInvoice}\""));
            return Task.FromResult(challenge);
        }
    }

    private sealed record Surface(
        PaymentHistoryService History,
        ReceiptService Receipts,
        string ToolResult);

    private static async Task<Surface> RunL402PaymentAsync(NwcPaymentResult payResult)
    {
        var history = new PaymentHistoryService();
        var receipts = new ReceiptService(TempReceiptsPath());
        var budget = BudgetMock();
        var wallet = new ReceiptRecordingWalletService(WalletMock(payResult).Object, receipts, budget.Object);
        var client = new L402HttpClient(new HttpClient(new L402ThenOkHandler()), wallet, budget.Object, history);

        var toolResult = await AccessL402ResourceTool.AccessL402Resource(
            url: SentinelUrl,
            l402Client: client,
            budgetService: budget.Object,
            paymentHistory: history);

        return new Surface(history, receipts, toolResult);
    }

    /// <summary>
    /// Renders every reader of the payment store into one big string: raw records and
    /// summary (JSON + ToString), the history tool, the receipts tool, and both receipt
    /// resources.
    /// </summary>
    private static string RenderEverything(Surface s)
    {
        var sb = new StringBuilder();
        var opts = new JsonSerializerOptions { WriteIndented = false };

        var recent = s.History.GetRecentPayments(50);
        var summary = s.History.GetSummary();

        sb.AppendLine(JsonSerializer.Serialize(recent, opts));
        sb.AppendLine(JsonSerializer.Serialize(summary, opts));
        sb.AppendLine(summary.ToString());
        foreach (var record in recent)
        {
            sb.AppendLine(record.ToString());
            sb.AppendLine(JsonSerializer.Serialize(record, opts));
        }

        sb.AppendLine(GetPaymentHistoryTool.GetPaymentHistory(historyService: s.History));
        sb.AppendLine(ReceiptsTool.Receipts(ReceiptsSource.session, historyService: s.History));

        sb.AppendLine(GetReceiptsTool.GetReceipts(receiptService: s.Receipts));
        sb.AppendLine(ReceiptsTool.Receipts(ReceiptsSource.durable, receiptService: s.Receipts));
        sb.AppendLine(ReceiptResources.Receipts(s.Receipts));
        foreach (var node in s.Receipts.ReadRecent(50))
        {
            var hash = node?["paymentHash"]?.ToString();
            if (!string.IsNullOrEmpty(hash))
            {
                sb.AppendLine(ReceiptResources.Receipt(hash, s.Receipts));
            }
        }

        return sb.ToString();
    }

    private static void AssertNoSentinel(string rendered)
    {
        foreach (var sentinel in Sentinels)
        {
            rendered.Should().NotContain(sentinel,
                $"the sentinel '{sentinel[..Math.Min(24, sentinel.Length)]}…' must never reach history, receipts, or resources");
        }
    }

    // --- the immediate result is NOT weakened -------------------------------------------

    [Fact]
    public async Task L402_SuccessfulPayment_ImmediateToolResult_StillCarriesTheToken()
    {
        var s = await RunL402PaymentAsync(NwcPaymentResult.Succeeded(SentinelPreimage));

        var json = JsonDocument.Parse(s.ToolResult).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        // The protocol needs the token in the immediate result — that is allowed. It
        // just must not be COPIED anywhere durable (asserted by the other tests).
        s.ToolResult.Should().Contain(SentinelPreimage);
    }

    // --- success ------------------------------------------------------------------------

    [Fact]
    public async Task L402_SuccessfulPayment_NoSentinelReachesHistoryReceiptsOrResources()
    {
        var s = await RunL402PaymentAsync(NwcPaymentResult.Succeeded(SentinelPreimage));

        s.History.GetSummary().TotalPayments.Should().Be(1);
        AssertNoSentinel(RenderEverything(s));
    }

    [Fact]
    public async Task L402_SuccessfulPayment_SafeCorrelationMaterialRemains()
    {
        var s = await RunL402PaymentAsync(NwcPaymentResult.Succeeded(SentinelPreimage));

        var record = s.History.GetRecentPayments(1).Single();
        record.Id.Should().NotBeNullOrWhiteSpace();
        record.Status.Should().Be(PaymentStatus.Success);
        record.Success.Should().BeTrue();
        record.AmountSats.Should().Be(SentinelInvoiceSats);
        record.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        record.Method.Should().Be("GET");
        record.ResponseStatusCode.Should().Be(200);

        // Redacted URL: host + path stay, userinfo / query / fragment are gone.
        record.Url.Should().Contain("api.example.com");
        record.Url.Should().Contain("/paid/data");
        record.Url.Should().NotContain("@");
        record.Url.Should().NotContain("?");
        record.Url.Should().NotContain("#");

        // Non-secret payment reference: a short prefix of the payment hash, which is
        // SHA-256(preimage) and safe to publish (the wallet already saw the hash).
        record.PaymentReference.Should().NotBeNullOrWhiteSpace();
        record.PaymentReference.Should().Be(ExpectedPaymentHash[..8]);
        record.PaymentReference!.Length.Should().BeLessThan(SentinelPreimage.Length / 2,
            "the reference is a truncated commitment, not the secret");

        // The history tool surfaces the same safe fields.
        var hist = JsonDocument.Parse(GetPaymentHistoryTool.GetPaymentHistory(historyService: s.History)).RootElement;
        var p = hist.GetProperty("payments")[0];
        p.GetProperty("id").GetString().Should().Be(record.Id);
        p.GetProperty("status").GetString().Should().Be("success");
        p.GetProperty("amountSats").GetInt64().Should().Be(SentinelInvoiceSats);
        p.GetProperty("method").GetString().Should().Be("GET");
        p.GetProperty("url").GetString().Should().Contain("api.example.com/paid/data");
        p.GetProperty("paymentReference").GetString().Should().Be(ExpectedPaymentHash[..8]);
        p.TryGetProperty("timestamp", out _).Should().BeTrue();

        // The durable receipt keeps the full payment hash (safe) and the redacted context.
        var receipt = s.Receipts.ReadRecent(1).Single()!;
        receipt["paymentHash"]!.ToString().Should().Be(ExpectedPaymentHash);
        receipt["context"]!.ToString().Should().Contain("api.example.com");
        receipt["context"]!.ToString().Should().NotContain("@");
    }

    // --- failed -------------------------------------------------------------------------

    [Fact]
    public async Task L402_FailedPayment_NoSentinelReachesHistoryReceiptsOrResources()
    {
        var s = await RunL402PaymentAsync(NwcPaymentResult.Failed("PAYMENT_FAILED", "route not found"));

        var record = s.History.GetRecentPayments(1).Single();
        record.Status.Should().Be(PaymentStatus.Failed);
        record.Success.Should().BeFalse();
        record.AmountSats.Should().Be(SentinelInvoiceSats);
        record.Method.Should().Be("GET");
        record.Url.Should().Contain("api.example.com/paid/data");
        record.ErrorMessage.Should().Contain("route not found");

        AssertNoSentinel(RenderEverything(s));
    }

    // --- pending ------------------------------------------------------------------------

    [Fact]
    public async Task L402_PendingPayment_NoSentinelReachesHistoryReceiptsOrResources()
    {
        var s = await RunL402PaymentAsync(NwcPaymentResult.Pending("trk_1", "in flight"));

        var record = s.History.GetRecentPayments(1).Single();
        record.Status.Should().Be(PaymentStatus.Pending);
        record.Success.Should().BeFalse();
        record.AmountSats.Should().Be(SentinelInvoiceSats);
        record.Method.Should().Be("GET");
        record.Url.Should().Contain("api.example.com/paid/data");
        s.History.GetSummary().PendingPayments.Should().Be(1);

        AssertNoSentinel(RenderEverything(s));
    }

    // --- the service boundary itself ---------------------------------------------------

    [Fact]
    public void RecordPayment_DirectCall_NeverRetainsInvoicePreimageTokenOrUrlSecrets()
    {
        // Every writer goes through RecordPayment / RecordFailedPayment, so the service
        // itself must be the minimizing boundary — not each caller's good manners.
        var history = new PaymentHistoryService();
        history.RecordPayment(
            url: SentinelUrl,
            method: "post",
            amountSats: 5,
            invoice: SentinelInvoice,
            preimageHex: SentinelPreimage,
            l402Token: SentinelMacaroonB64 + ":" + SentinelPreimage, // gitleaks:allow — fake test sentinel
            statusCode: 200);
        history.RecordFailedPayment(SentinelUrl, "get", 7, "boom", SentinelInvoice);
        history.RecordPayment(SentinelUrl, "get", 9, SentinelInvoice, null, null, null,
            PaymentStatus.Pending, "still settling");

        var sb = new StringBuilder();
        var summary = history.GetSummary();
        sb.AppendLine(JsonSerializer.Serialize(summary));
        sb.AppendLine(summary.ToString());
        foreach (var r in history.GetRecentPayments(10))
        {
            sb.AppendLine(r.ToString());
            sb.AppendLine(JsonSerializer.Serialize(r));
        }
        sb.AppendLine(GetPaymentHistoryTool.GetPaymentHistory(historyService: history));

        AssertNoSentinel(sb.ToString());

        summary.TotalPayments.Should().Be(3);
        summary.SuccessfulPayments.Should().Be(1);
        summary.FailedPayments.Should().Be(1);
        summary.PendingPayments.Should().Be(1);
        summary.Payments.Should().OnlyContain(p => p.Url.Contains("api.example.com/paid/data"));
        summary.Payments.Should().OnlyContain(p => !p.Url.Contains("@") && !p.Url.Contains("?"));

        var settled = summary.Payments.Single(p => p.Status == PaymentStatus.Success);
        settled.PaymentReference.Should().Be(ExpectedPaymentHash[..8]);
        settled.Method.Should().Be("POST");
    }

    [Fact]
    public void PaymentRecord_HasNoSecretBearingProperties()
    {
        // Structural guard: the record type must not even have a slot for the secret.
        // Mirrors the Python port's PaymentRecord (no preimage field).
        var names = typeof(PaymentRecord).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToArray();
        names.Should().NotContain(n => n.Contains("preimage"));
        names.Should().NotContain(n => n.Contains("token"));
        names.Should().NotContain(n => n == "invoice");
    }
}
