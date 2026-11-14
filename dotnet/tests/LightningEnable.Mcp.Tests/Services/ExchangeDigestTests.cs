using System.IO.Compression;
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
/// Salted request/response digests for paid L402 exchanges (digests only, never bodies).
/// Mirrors Python's <c>tests/test_receipt_exchange_digest.py</c>; the construction is pinned by
/// <c>shared/attestation-golden-vector.json</c>.
/// </summary>
public sealed class ExchangeDigestTests : IDisposable
{
    private const string Preimage = "abababababababababababababababababababababababababababababababab";
    private static readonly string PaymentHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Convert.FromHexString(Preimage))).ToLowerInvariant();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "le-digest-" + Guid.NewGuid().ToString("N"));
    private string ReceiptsPath => Path.Combine(_dir, "receipts.jsonl");

    public ExchangeDigestTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static JsonObject Digests() => AttestationTests.LoadVector()["exchangeDigests"]!.AsObject();
    private static byte[] VectorNonce => Convert.FromHexString(Digests()["nonceHex"]!.GetValue<string>());
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private JsonObject LastLine() => JsonNode.Parse(File.ReadAllLines(ReceiptsPath)[^1])!.AsObject();

    [Fact]
    public void RequestAndResponseDigests_MatchSharedVector()
    {
        foreach (var c in Digests()["requests"]!.AsArray())
        {
            var body = c!["bodyUtf8"]?.GetValue<string>();
            ExchangeDigest.RequestDigest(VectorNonce, c["method"]!.GetValue<string>(), c["url"]!.GetValue<string>(),
                    body == null ? null : B(body))
                .Should().Be(c["requestDigest"]!.GetValue<string>());
        }
        foreach (var c in Digests()["responses"]!.AsArray())
        {
            ExchangeDigest.ResponseDigest(VectorNonce, B(c!["bodyUtf8"]!.GetValue<string>()))
                .Should().Be(c["responseDigest"]!.GetValue<string>());
        }
    }

    [Fact]
    public void RequestDigest_IsHmacWithTheNonceAsKey()
    {
        var expected = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            VectorNonce, B("GET https://x.example/a?city=Paris\n"))).ToLowerInvariant();
        ExchangeDigest.RequestDigest(VectorNonce, "get", "https://x.example/a?city=Paris", null).Should().Be(expected);
    }

    [Fact]
    public void Digest_CannotBeMatchedByGuessingWithoutTheNonce()
    {
        var svc = new ReceiptService(ReceiptsPath);
        const string url = "https://api.example.com/v1/forecast?city=Paris";
        svc.LogExchangeDigest(PaymentHash, "GET", url, null, B("{}"));
        var recorded = svc.LookupPayment(PaymentHash)!.RequestDigest;
        foreach (var city in new[] { "London", "Paris", "Rome" })
        {
            var guess = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                B($"GET https://api.example.com/v1/forecast?city={city}\n"))).ToLowerInvariant();
            guess.Should().NotBe(recorded);
        }
        var nonce = Convert.FromHexString(LastLine()["digestNonce"]!.GetValue<string>());
        nonce.Should().HaveCount(32);
        ExchangeDigest.RequestDigest(nonce, "GET", url, null).Should().Be(recorded, "disclosing the nonce proves the digest");
    }

    [Fact]
    public void EachExchange_GetsAFreshNonce()
    {
        var svc = new ReceiptService(ReceiptsPath);
        svc.LogExchangeDigest(new string('1', 64), "GET", "https://x/", null, B("same"));
        svc.LogExchangeDigest(new string('2', 64), "GET", "https://x/", null, B("same"));
        var lines = File.ReadAllLines(ReceiptsPath).Select(l => JsonNode.Parse(l)!).ToList();
        lines[0]["digestNonce"]!.GetValue<string>().Should().NotBe(lines[1]["digestNonce"]!.GetValue<string>());
        lines[0]["requestDigest"]!.GetValue<string>().Should().NotBe(lines[1]["requestDigest"]!.GetValue<string>());
        lines.Should().OnlyContain(l => l["digestScheme"]!.GetValue<string>() == ExchangeDigest.Scheme);
    }

    [Fact]
    public void Digest_IsFoldedIntoItsReceipt_WithoutTheNonce()
    {
        var svc = new ReceiptService(ReceiptsPath);
        svc.LogPayment(new PaymentReceiptEntry { Kind = "l402", AmountSats = 10, Status = "settled", PaymentHash = PaymentHash, Wallet = "NWC" });
        svc.LogPayment(new PaymentReceiptEntry { Kind = "invoice", AmountSats = 5, Status = "settled", PaymentHash = new string('c', 64), Wallet = "NWC" });
        svc.LogExchangeDigest(PaymentHash, "GET", "https://x/", null, B("{}"), VectorNonce).Should().BeTrue();

        var rows = svc.ReadRecent(10);
        rows.Select(r => r["type"]!.GetValue<string>()).Should().Equal("payment_receipt", "payment_receipt");
        rows[0]["requestDigest"]!.GetValue<string>().Should().Be(ExchangeDigest.RequestDigest(VectorNonce, "GET", "https://x/", null));
        rows[0]["responseDigest"]!.GetValue<string>().Should().Be(ExchangeDigest.ResponseDigest(VectorNonce, B("{}")));
        string.Join("", rows.Select(r => r.ToJsonString())).Should().NotContain("digestNonce");
        rows[1].AsObject().ContainsKey("requestDigest").Should().BeFalse();
        LastLine()["type"]!.GetValue<string>().Should().Be("exchange_digest");
    }

    [Fact]
    public void ReadRecent_LimitStillCountsReceipts()
    {
        var svc = new ReceiptService(ReceiptsPath);
        for (var i = 0; i < 5; i++)
        {
            var h = string.Concat(Enumerable.Repeat(i.ToString("x2"), 32));
            svc.LogPayment(new PaymentReceiptEntry { Kind = "l402", AmountSats = i + 1, PaymentHash = h, Wallet = "NWC" });
            svc.LogExchangeDigest(h, "GET", "https://x/", null, Array.Empty<byte>());
        }
        var rows = svc.ReadRecent(3);
        rows.Select(r => r["amountSats"]!.GetValue<long>()).Should().Equal(3, 4, 5);
        rows.Should().OnlyContain(r => r["requestDigest"] != null);
    }

    [Fact]
    public void LookupPayment_ReportsKnownHashesAndSaltedDigests()
    {
        var svc = new ReceiptService(ReceiptsPath);
        svc.LogPayment(new PaymentReceiptEntry { Kind = "l402", AmountSats = 10, PaymentHash = PaymentHash, Wallet = "NWC" });
        svc.LogPayment(new PaymentReceiptEntry { Kind = "invoice", AmountSats = 5, PaymentHash = new string('c', 64), Wallet = "NWC" });
        svc.LogExchangeDigest(PaymentHash, "GET", "https://x/", null, B("{}"), VectorNonce);

        svc.LookupPayment(PaymentHash.ToUpperInvariant()).Should().Be(new LocalPaymentRecord(PaymentHash,
            ExchangeDigest.RequestDigest(VectorNonce, "GET", "https://x/", null),
            ExchangeDigest.ResponseDigest(VectorNonce, B("{}"))));
        svc.LookupPayment(new string('c', 64)).Should().Be(new LocalPaymentRecord(new string('c', 64), null, null));
        svc.LookupPayment(new string('e', 64)).Should().BeNull();
    }

    [Fact]
    public void UnsaltedLegacyDigestLine_IsNeverUsed()
    {
        var svc = new ReceiptService(ReceiptsPath);
        svc.LogPayment(new PaymentReceiptEntry { Kind = "l402", AmountSats = 10, PaymentHash = PaymentHash, Wallet = "NWC" });
        File.AppendAllText(ReceiptsPath, $"{{\"type\":\"exchange_digest\",\"paymentHash\":\"{PaymentHash}\",\"requestSha256\":\"{new string('1', 64)}\",\"responseSha256\":\"{new string('2', 64)}\"}}\n");
        svc.LookupPayment(PaymentHash).Should().Be(new LocalPaymentRecord(PaymentHash, null, null));
        var row = svc.ReadRecent(5)[0].AsObject();
        row.ContainsKey("requestSha256").Should().BeFalse();
        row.ContainsKey("requestDigest").Should().BeFalse();
    }

    [Fact]
    public void BodiesAreNeverWritten()
    {
        var svc = new ReceiptService(ReceiptsPath);
        svc.LogExchangeDigest(PaymentHash, "POST", "https://x/", B("REQUEST-BODY"), B("SECRET-BODY"));
        var text = File.ReadAllText(ReceiptsPath);
        text.Should().NotContain("SECRET-BODY").And.NotContain("REQUEST-BODY");
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    public void DecodeContent_RemovesContentEncoding_LikePython(string coding)
    {
        var plain = B("plain body");
        using var ms = new MemoryStream();
        using (Stream z = coding switch
        {
            "gzip" => new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true),
            "deflate" => new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true),
            _ => new BrotliStream(ms, CompressionLevel.Optimal, leaveOpen: true),
        })
        {
            z.Write(plain);
        }
        ExchangeDigest.DecodeContent(ms.ToArray(), new[] { coding }).Should().Equal(plain);
        ExchangeDigest.DecodeContent(plain, new[] { "identity" }).Should().Equal(plain);
        ExchangeDigest.DecodeContent(plain, Array.Empty<string>()).Should().Equal(plain);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private async Task<(L402FetchResult Result, ReceiptService Receipts)> PaidFetch(Func<HttpContent> body)
    {
        var handler = new Handler(request =>
        {
            if (request.Headers.Authorization != null)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = body() };
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
        return (await client.FetchWithL402Async("https://api.provider.com/premium", maxSats: 1000), receipts);
    }

    [Fact]
    public async Task PaidFetch_RecordsTheSaltedDigest_NeverTheBody()
    {
        const string url = "https://api.provider.com/premium";
        const string responseBody = "{\"forecast\":\"sun\"}";
        var (result, receipts) = await PaidFetch(() => new StringContent(responseBody));

        result.Success.Should().BeTrue();
        result.Content.Should().Be(responseBody);
        var nonce = Convert.FromHexString(LastLine()["digestNonce"]!.GetValue<string>());
        receipts.LookupPayment(PaymentHash).Should().Be(new LocalPaymentRecord(
            PaymentHash,
            ExchangeDigest.RequestDigest(nonce, "GET", url, null),
            ExchangeDigest.ResponseDigest(nonce, B(responseBody))));
        File.ReadAllText(ReceiptsPath).Should().NotContain("sun");
    }

    [Fact]
    public async Task CompressedResponse_IsDigestedAfterContentDecoding()
    {
        // Parity with Python, whose HTTP client hands back the decoded body.
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(B("plain body"));
        var gzipped = ms.ToArray();
        await PaidFetch(() =>
        {
            var c = new ByteArrayContent(gzipped);
            c.Headers.ContentEncoding.Add("gzip");
            return c;
        });
        var nonce = Convert.FromHexString(LastLine()["digestNonce"]!.GetValue<string>());
        LastLine()["responseDigest"]!.GetValue<string>().Should().Be(ExchangeDigest.ResponseDigest(nonce, B("plain body")));
    }
}
