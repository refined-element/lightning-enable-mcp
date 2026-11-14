using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using Moq;
using Xunit;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// Request/response SHA-256 capture for paid L402 exchanges (hashes only, never bodies).
/// Mirrors Python's <c>tests/test_receipt_exchange_digest.py</c>; the digest definitions are
/// pinned by <c>shared/attestation-golden-vector.json</c>.
/// </summary>
public sealed class ExchangeDigestTests : IDisposable
{
    private const string Preimage = "abababababababababababababababababababababababababababababababab";
    private static readonly string PaymentHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Convert.FromHexString(Preimage))).ToLowerInvariant();
    private static readonly string H11 = new('1', 64);
    private static readonly string H22 = new('2', 64);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "le-digest-" + Guid.NewGuid().ToString("N"));
    private string ReceiptsPath => Path.Combine(_dir, "receipts.jsonl");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static JsonObject Digests() => AttestationTests.LoadVector()["exchangeDigests"]!.AsObject();

    [Fact]
    public void RequestAndResponseDigests_MatchSharedVector()
    {
        foreach (var c in Digests()["requests"]!.AsArray())
        {
            var body = c!["bodyUtf8"]?.GetValue<string>();
            ExchangeDigest.RequestSha256(c["method"]!.GetValue<string>(), c["url"]!.GetValue<string>(),
                    body == null ? null : Encoding.UTF8.GetBytes(body))
                .Should().Be(c["requestSha256"]!.GetValue<string>());
        }
        foreach (var c in Digests()["responses"]!.AsArray())
        {
            ExchangeDigest.ResponseSha256(Encoding.UTF8.GetBytes(c!["bodyUtf8"]!.GetValue<string>()))
                .Should().Be(c["responseSha256"]!.GetValue<string>());
        }
    }

    [Fact]
    public void Digest_IsFoldedIntoItsReceipt_AndNotListedAlone()
    {
        var svc = new ReceiptService(ReceiptsPath);
        svc.LogPayment(new PaymentReceiptEntry { Kind = "l402", AmountSats = 10, Status = "settled", PaymentHash = PaymentHash, Wallet = "NWC" });
        svc.LogPayment(new PaymentReceiptEntry { Kind = "invoice", AmountSats = 5, Status = "settled", PaymentHash = new string('c', 64), Wallet = "NWC" });
        svc.LogExchangeDigest(PaymentHash, H11, H22).Should().BeTrue();

        var rows = svc.ReadRecent(10);
        rows.Select(r => r["type"]!.GetValue<string>()).Should().Equal("payment_receipt", "payment_receipt");
        rows[0]["requestSha256"]!.GetValue<string>().Should().Be(H11);
        rows[0]["responseSha256"]!.GetValue<string>().Should().Be(H22);
        rows[1].AsObject().ContainsKey("requestSha256").Should().BeFalse();

        var last = File.ReadAllLines(ReceiptsPath)[^1];
        JsonNode.Parse(last)!["type"]!.GetValue<string>().Should().Be("exchange_digest");
    }

    [Fact]
    public void ReadRecent_LimitStillCountsReceipts()
    {
        var svc = new ReceiptService(ReceiptsPath);
        for (var i = 0; i < 5; i++)
        {
            var h = string.Concat(Enumerable.Repeat(i.ToString("x2"), 32));
            svc.LogPayment(new PaymentReceiptEntry { Kind = "l402", AmountSats = i + 1, PaymentHash = h, Wallet = "NWC" });
            svc.LogExchangeDigest(h, H11, H22);
        }
        var rows = svc.ReadRecent(3);
        rows.Select(r => r["amountSats"]!.GetValue<long>()).Should().Equal(3, 4, 5);
        rows.Should().OnlyContain(r => r["requestSha256"]!.GetValue<string>() == H11);
    }

    [Fact]
    public void LookupPayment_ReportsKnownHashesAndDigests()
    {
        var svc = new ReceiptService(ReceiptsPath);
        svc.LogPayment(new PaymentReceiptEntry { Kind = "l402", AmountSats = 10, PaymentHash = PaymentHash, Wallet = "NWC" });
        svc.LogPayment(new PaymentReceiptEntry { Kind = "invoice", AmountSats = 5, PaymentHash = new string('c', 64), Wallet = "NWC" });
        svc.LogExchangeDigest(PaymentHash, H11, H22);

        svc.LookupPayment(PaymentHash.ToUpperInvariant()).Should().Be(new LocalPaymentRecord(PaymentHash, H11, H22));
        svc.LookupPayment(new string('c', 64)).Should().Be(new LocalPaymentRecord(new string('c', 64), null, null));
        svc.LookupPayment(new string('e', 64)).Should().BeNull();
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    [Fact]
    public async Task PaidFetch_RecordsTheExchangeDigest_NeverTheBody()
    {
        const string url = "https://api.provider.com/premium";
        const string responseBody = "{\"forecast\":\"sun\"}";
        var handler = new Handler(request =>
        {
            if (request.Headers.Authorization != null)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody) };
            var challenge = new HttpResponseMessage(HttpStatusCode.PaymentRequired) { Content = new StringContent("pay") };
            challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                "L402", $"macaroon=\"YWJjZGVm\", invoice=\"{TestInvoices.Build("lnbc100n")}\""));
            return challenge;
        });
        var wallet = new Mock<IWalletService>();
        wallet.SetupGet(w => w.IsConfigured).Returns(true);
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NwcPaymentResult { Success = true, PreimageHex = Preimage });
        var budget = new Mock<IBudgetService>();
        budget.Setup(b => b.TryReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long amt, CancellationToken _) => SpendReservationResult.Reserved("res", amt));
        var receipts = new ReceiptService(ReceiptsPath);
        var client = new L402HttpClient(new HttpClient(handler), wallet.Object, budget.Object,
            new Mock<IPaymentHistoryService>().Object, receipts);

        var result = await client.FetchWithL402Async(url, maxSats: 1000);

        result.Success.Should().BeTrue();
        result.Content.Should().Be(responseBody);
        receipts.LookupPayment(PaymentHash).Should().Be(new LocalPaymentRecord(
            PaymentHash,
            ExchangeDigest.RequestSha256("GET", url, null),
            ExchangeDigest.ResponseSha256(Encoding.UTF8.GetBytes(responseBody))));
        File.ReadAllText(ReceiptsPath).Should().NotContain("sun");
    }
}
