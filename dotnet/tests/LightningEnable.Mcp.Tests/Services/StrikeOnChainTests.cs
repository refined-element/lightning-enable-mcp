using System.Net;
using System.Text;
using FluentAssertions;
using LightningEnable.Mcp.Services;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// Strike on-chain sends are quote → execute → poll. Once the execute call has been ISSUED,
/// a timeout, cancellation, or transport error is AMBIGUOUS (funds may have moved) and must be
/// reported as <c>Submitted = true, State = UNKNOWN</c> with the ids that are known — never as a
/// plain failure. A poll that outlives its window after a successful execute is PENDING (the
/// normal on-chain state), not a failure. Only errors before execute prove no funds moved.
/// All HTTP is served by a fake handler: no real API key, no real payment.
/// </summary>
public class StrikeOnChainTests
{
    private const string Address = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _route;
        public List<string> Calls { get; } = new();
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> route) => _route = route;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(_route(request));
        }
    }

    private static (StrikeWalletService svc, FakeHandler handler) Strike(Func<HttpRequestMessage, HttpResponseMessage> route)
    {
        var handler = new FakeHandler(route);
        var svc = new StrikeWalletService(new HttpClient(handler), "fake-test-key",
            onChainPollTimeout: TimeSpan.FromMilliseconds(200), onChainPollInterval: TimeSpan.FromMilliseconds(20));
        return (svc, handler);
    }

    private static bool IsQuote(HttpRequestMessage r) => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/payment-quotes/onchain");
    private static bool IsExecute(HttpRequestMessage r) => r.Method == HttpMethod.Patch && r.RequestUri!.AbsolutePath.EndsWith("/execute");

    private const string QuoteBody = """{"paymentQuoteId":"q-1","onchainFee":{"amount":"0.00000300","currency":"BTC"}}""";

    [Fact]
    public async Task ExecuteSucceeds_PollTimesOut_IsSubmittedPending_WithIds_NotAFailure()
    {
        var (svc, _) = Strike(r =>
            IsQuote(r) ? Json(QuoteBody)
            : IsExecute(r) ? Json("""{"paymentId":"pay-1","state":"PENDING"}""")
            : Json("""{"paymentId":"pay-1","state":"PENDING"}"""));

        var result = await svc.SendOnChainAsync(Address, 5000);

        result.Submitted.Should().BeTrue();
        result.Success.Should().BeTrue("a poll timeout after a successful execute is pending, not failed");
        result.State.Should().Be("PENDING");
        result.PaymentId.Should().Be("pay-1");
        result.QuoteId.Should().Be("q-1");
        result.FeeSats.Should().Be(300);
    }

    [Fact]
    public async Task ExecuteTimesOut_IsSubmittedUnknown_WithQuoteId()
    {
        var (svc, _) = Strike(r =>
            IsQuote(r) ? Json(QuoteBody)
            : IsExecute(r) ? throw new TaskCanceledException("timeout")
            : Json("{}"));

        var result = await svc.SendOnChainAsync(Address, 5000);

        result.Submitted.Should().BeTrue("the execute call was issued, so funds may have moved");
        result.Success.Should().BeFalse();
        result.State.Should().Be("UNKNOWN");
        result.QuoteId.Should().Be("q-1");
    }

    [Fact]
    public async Task ExecuteTransportError_IsSubmittedUnknown()
    {
        var (svc, _) = Strike(r =>
            IsQuote(r) ? Json(QuoteBody)
            : IsExecute(r) ? throw new HttpRequestException("connection reset")
            : Json("{}"));

        var result = await svc.SendOnChainAsync(Address, 5000);

        result.Submitted.Should().BeTrue();
        result.Success.Should().BeFalse();
        result.State.Should().Be("UNKNOWN");
        result.QuoteId.Should().Be("q-1");
    }

    [Fact]
    public async Task ExecuteServerError_IsSubmittedUnknown()
    {
        var (svc, _) = Strike(r =>
            IsQuote(r) ? Json(QuoteBody)
            : IsExecute(r) ? Json("""{"error":"upstream"}""", HttpStatusCode.BadGateway)
            : Json("{}"));

        var result = await svc.SendOnChainAsync(Address, 5000);

        result.Submitted.Should().BeTrue("a 5xx on execute does not prove the execute did not happen");
        result.State.Should().Be("UNKNOWN");
    }

    [Fact]
    public async Task QuoteTimesOut_IsNotSubmitted_AndNeverExecutes()
    {
        var (svc, handler) = Strike(r =>
            IsQuote(r) ? throw new TaskCanceledException("timeout") : Json("{}"));

        var result = await svc.SendOnChainAsync(Address, 5000);

        result.Submitted.Should().BeFalse("no execute call was issued, so no funds moved");
        result.Success.Should().BeFalse();
        handler.Calls.Should().NotContain(c => c.EndsWith("/execute"));
    }

    [Fact]
    public async Task ExecuteThenCompleted_IsSubmittedCompleted()
    {
        var (svc, _) = Strike(r =>
            IsQuote(r) ? Json(QuoteBody)
            : IsExecute(r) ? Json("""{"paymentId":"pay-2","state":"PENDING"}""")
            : Json("""{"paymentId":"pay-2","state":"COMPLETED"}"""));

        var result = await svc.SendOnChainAsync(Address, 5000);

        result.Success.Should().BeTrue();
        result.Submitted.Should().BeTrue();
        result.State.Should().Be("COMPLETED");
        result.PaymentId.Should().Be("pay-2");
    }

    [Fact]
    public async Task GetOnChainPaymentStatus_QueriesPaymentsEndpoint_ReturnsStateAndTxId()
    {
        var (svc, handler) = Strike(r =>
            r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/v1/payments/pay-7"
                ? Json("""{"paymentId":"pay-7","state":"COMPLETED","onchain":{"txnId":"abc123"}}""")
                : Json("{}", HttpStatusCode.NotFound));

        var status = await ((IWalletService)svc).GetOnChainPaymentStatusAsync("pay-7");

        handler.Calls.Should().ContainSingle().Which.Should().Be("GET /v1/payments/pay-7");
        status.Success.Should().BeTrue();
        status.State.Should().Be("COMPLETED");
        status.TxId.Should().Be("abc123");
        status.PaymentId.Should().Be("pay-7");
    }
}
