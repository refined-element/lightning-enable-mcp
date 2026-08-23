using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using Moq;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// MPP draft-00 flow through the REAL L402HttpClient: 402 with a modern Payment
/// challenge → pay → retry with the modern <c>Authorization: Payment &lt;b64url&gt;</c>
/// credential → surface the Payment-Receipt response header. Legacy Payment and L402
/// flows are unchanged. Fixtures only — no real invoices or payments.
/// </summary>
public class L402HttpClientMppDraft00Tests
{
    private const string FixtureInvoice = "lnbc100n1pjtest"; // 10 sats
    private const string FixturePreimage = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FutureExpiry = "2099-01-01T00:00:00Z";

    private static string B64UrlNoPad(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string RequestJson(string amount = "10") =>
        $"{{\"amount\":\"{amount}\",\"currency\":\"sat\",\"methodDetails\":{{\"invoice\":\"{FixtureInvoice}\",\"network\":\"mainnet\"}}}}";

    private static string ModernHeaderValue(string requestEncoded, string expires = FutureExpiry) =>
        $"id=\"chal-1\", realm=\"api.example.com\", method=\"lightning\", intent=\"charge\", request=\"{requestEncoded}\", expires=\"{expires}\"";

    /// <summary>Records every request the client actually issues, and returns a canned response per request.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<(Uri Uri, Dictionary<string, string> Headers)> Received { get; } = new();

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(
                h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            Received.Add((request.RequestUri!, headers));
            return Task.FromResult(_responder(request));
        }
    }

    private static HttpResponseMessage Challenge402(string paymentHeaderValue)
    {
        var challenge = new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent("payment required")
        };
        // Raw parameterized header value: added via TryAddWithoutValidation because
        // AuthenticationHeaderValue would treat the whole param list as a single token.
        challenge.Headers.TryAddWithoutValidation("WWW-Authenticate", $"Payment {paymentHeaderValue}");
        return challenge;
    }

    private static (L402HttpClient client, RecordingHandler handler, Mock<IWalletService> wallet)
        BuildClient(Func<HttpRequestMessage, HttpResponseMessage> responder, string preimage = FixturePreimage)
    {
        var handler = new RecordingHandler(responder);
        var http = new HttpClient(handler);
        var wallet = new Mock<IWalletService>();
        wallet.SetupGet(w => w.IsConfigured).Returns(true);
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NwcPaymentResult { Success = true, PreimageHex = preimage });
        var budget = new Mock<IBudgetService>();
        budget.Setup(b => b.TryReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long amt, CancellationToken _) => SpendReservationResult.Reserved("res", amt));
        var history = new Mock<IPaymentHistoryService>();
        var client = new L402HttpClient(http, wallet.Object, budget.Object, history.Object);
        return (client, handler, wallet);
    }

    private static JsonElement DecodeCredential(string credential)
    {
        var padded = credential.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
    }

    [Fact]
    public async Task Fetch_ModernChallenge_RetriesWithModernCredential()
    {
        var encoded = B64UrlNoPad(RequestJson());
        var (client, handler, wallet) = BuildClient(req =>
        {
            if (!req.Headers.Contains("Authorization"))
                return Challenge402(ModernHeaderValue(encoded));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        });

        var result = await client.FetchWithL402Async("https://api.provider.com/premium", maxSats: 1000);

        result.Success.Should().BeTrue();
        result.PaidAmountSats.Should().Be(10);
        wallet.Verify(w => w.PayInvoiceAsync(FixtureInvoice, It.IsAny<CancellationToken>()), Times.Once);

        handler.Received.Should().HaveCount(2);
        var auth = handler.Received[1].Headers["Authorization"];
        auth.Should().StartWith("Payment ");
        auth.Should().NotContain("preimage=", "the modern credential is a base64url blob, not the legacy param format");

        var credential = auth["Payment ".Length..];
        var decoded = DecodeCredential(credential);
        decoded.GetProperty("challenge").GetProperty("request").GetString().Should().Be(encoded);
        decoded.GetProperty("payload").GetProperty("preimage").GetString().Should().Be(FixturePreimage);
    }

    [Fact]
    public async Task Fetch_SupersetHeader_UsesModernCredentialNotLegacy()
    {
        var encoded = B64UrlNoPad(RequestJson());
        var superset = ModernHeaderValue(encoded) +
                       $", invoice=\"{FixtureInvoice}\", amount=\"10\", currency=\"sat\"";
        var (client, handler, _) = BuildClient(req =>
        {
            if (!req.Headers.Contains("Authorization"))
                return Challenge402(superset);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });

        var result = await client.FetchWithL402Async("https://api.provider.com/premium", maxSats: 1000);

        result.Success.Should().BeTrue();
        var auth = handler.Received[1].Headers["Authorization"];
        auth.Should().StartWith("Payment ");
        auth.Should().NotContain("preimage=");
    }

    [Fact]
    public async Task Fetch_LegacyPaymentChallenge_StillUsesLegacyHeader()
    {
        var legacy = $"realm=\"api.example.com\", method=\"lightning\", invoice=\"{FixtureInvoice}\", amount=\"10\", currency=\"sat\"";
        var (client, handler, _) = BuildClient(req =>
        {
            if (!req.Headers.Contains("Authorization"))
                return Challenge402(legacy);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });

        var result = await client.FetchWithL402Async("https://api.provider.com/premium", maxSats: 1000);

        result.Success.Should().BeTrue();
        handler.Received[1].Headers["Authorization"]
            .Should().Be($"Payment method=\"lightning\", preimage=\"{FixturePreimage}\"",
                "the legacy profile keeps the legacy Authorization format unchanged");
    }

    [Fact]
    public async Task Fetch_ExpiredModernChallenge_RefusesToPayWithError()
    {
        var encoded = B64UrlNoPad(RequestJson());
        var (client, _, wallet) = BuildClient(_ =>
            Challenge402(ModernHeaderValue(encoded, expires: "2001-01-01T00:00:00Z")));

        var result = await client.FetchWithL402Async("https://api.provider.com/premium", maxSats: 1000);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("expired");
        result.PaidAmountSats.Should().Be(0);
        wallet.Verify(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Fetch_ModernAmountMismatchesInvoice_RefusesToPay()
    {
        // Challenge declares 9999 sats but the invoice is 10 sats — inconsistent, refuse.
        var encoded = B64UrlNoPad(RequestJson(amount: "9999"));
        var (client, _, wallet) = BuildClient(_ => Challenge402(ModernHeaderValue(encoded)));

        var result = await client.FetchWithL402Async("https://api.provider.com/premium", maxSats: 100000);

        result.Success.Should().BeFalse();
        result.PaidAmountSats.Should().Be(0);
        wallet.Verify(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Fetch_PaymentReceiptHeader_SurfacedInResult()
    {
        var encoded = B64UrlNoPad(RequestJson());
        var receiptJson = "{\"challengeId\":\"chal-1\",\"method\":\"lightning\",\"reference\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"status\":\"settled\",\"timestamp\":\"2026-08-23T12:00:00Z\"}";
        var (client, _, _) = BuildClient(req =>
        {
            if (!req.Headers.Contains("Authorization"))
                return Challenge402(ModernHeaderValue(encoded));
            var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            ok.Headers.TryAddWithoutValidation("Payment-Receipt", B64UrlNoPad(receiptJson));
            return ok;
        });

        var result = await client.FetchWithL402Async("https://api.provider.com/premium", maxSats: 1000);

        result.Success.Should().BeTrue();
        result.PaymentReceipt.Should().NotBeNull();
        result.PaymentReceipt!.ChallengeId.Should().Be("chal-1");
        result.PaymentReceipt.Reference.Should().Be("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        result.PaymentReceipt.Status.Should().Be("settled");
    }

    [Fact]
    public async Task Fetch_MalformedPaymentReceipt_DoesNotFailTheSuccess()
    {
        var encoded = B64UrlNoPad(RequestJson());
        var (client, _, _) = BuildClient(req =>
        {
            if (!req.Headers.Contains("Authorization"))
                return Challenge402(ModernHeaderValue(encoded));
            var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            ok.Headers.TryAddWithoutValidation("Payment-Receipt", "!!!garbage!!!");
            return ok;
        });

        var result = await client.FetchWithL402Async("https://api.provider.com/premium", maxSats: 1000);

        result.Success.Should().BeTrue("a malformed receipt must never fail a successful payment");
        result.PaymentReceipt.Should().BeNull();
    }

    [Fact]
    public async Task Fetch_MissingPaymentReceipt_IsFine()
    {
        var encoded = B64UrlNoPad(RequestJson());
        var (client, _, _) = BuildClient(req =>
        {
            if (!req.Headers.Contains("Authorization"))
                return Challenge402(ModernHeaderValue(encoded));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });

        var result = await client.FetchWithL402Async("https://api.provider.com/premium", maxSats: 1000);

        result.Success.Should().BeTrue();
        result.PaymentReceipt.Should().BeNull();
    }

    [Fact]
    public async Task Fetch_ModernCredential_IsSingleUse_NeverReplayed()
    {
        // Two sequential fetches: each starts unauthenticated (fresh 402) and pays anew.
        // The modern credential from the first fetch must NOT be replayed on the second.
        var encoded = B64UrlNoPad(RequestJson());
        var (client, handler, wallet) = BuildClient(req =>
        {
            if (!req.Headers.Contains("Authorization"))
                return Challenge402(ModernHeaderValue(encoded));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });

        var first = await client.FetchWithL402Async("https://api.provider.com/premium", maxSats: 1000);
        var second = await client.FetchWithL402Async("https://api.provider.com/premium", maxSats: 1000);

        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();
        wallet.Verify(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        handler.Received.Should().HaveCount(4);
        // The first request of the SECOND fetch carries no Authorization — no cached credential.
        handler.Received[2].Headers.Should().NotContainKey("Authorization",
            "modern credentials are single-use and must never be served from a cache");
    }
}
