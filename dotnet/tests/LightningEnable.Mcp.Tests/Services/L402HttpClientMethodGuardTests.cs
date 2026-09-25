using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using LightningEnable.Mcp.Services;
using Moq;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// A3, client boundary: <see cref="L402HttpClient.FetchWithL402Async"/> is the shared
/// path every paid-fetch tool goes through, so the GET/HEAD restriction is enforced
/// HERE too — a tool cannot bypass it by skipping its own check. Refusal happens before
/// the initial request (zero HTTP calls) and before any payment (zero wallet/budget calls).
/// </summary>
public class L402HttpClientMethodGuardTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<HttpRequestMessage> Received { get; } = new();

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Received.Add(request);
            return Task.FromResult(_responder(request));
        }
    }

    private static HttpResponseMessage Challenge()
    {
        var r = new HttpResponseMessage(HttpStatusCode.PaymentRequired);
        r.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
            "L402", $"macaroon=\"YWJjZGVm\", invoice=\"{TestInvoices.Build("lnbc100n")}\""));
        r.Content = new StringContent("pay up");
        return r;
    }

    private static (L402HttpClient client, RecordingHandler handler, Mock<IWalletService> wallet, Mock<IBudgetService> budget)
        Build(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new RecordingHandler(responder);
        var wallet = new Mock<IWalletService>(MockBehavior.Strict);
        wallet.SetupGet(w => w.IsConfigured).Returns(true);
        wallet.SetupGet(w => w.ProviderName).Returns("NWC");
        var budget = new Mock<IBudgetService>(MockBehavior.Strict);
        var history = new Mock<IPaymentHistoryService>();
        var client = new L402HttpClient(new HttpClient(handler), wallet.Object, budget.Object, history.Object);
        return (client, handler, wallet, budget);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData(" post ")]
    [InlineData("Post")]
    [InlineData("OPTIONS")]
    [InlineData("")]
    public async Task UnsafeMethod_NeverHitsNetworkOrWallet(string method)
    {
        var (client, handler, wallet, budget) = Build(_ => Challenge());

        var result = await client.FetchWithL402Async(
            "https://api.example.com/things", method, null, "{\"x\":1}", maxSats: 5000);

        result.Success.Should().BeFalse();
        result.PaidAmountSats.Should().Be(0);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().Contain("GET").And.Contain("HEAD");

        handler.Received.Should().BeEmpty("the request must be refused before any HTTP call");
        wallet.Verify(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        budget.Verify(b => b.TryReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("GET", "GET")]
    [InlineData("HEAD", "HEAD")]
    [InlineData("get", "GET")]
    [InlineData(" head ", "HEAD")]
    public async Task GetAndHead_StillIssueTheRequest(string method, string expected)
    {
        var (client, handler, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("hello")
        });

        var result = await client.FetchWithL402Async("https://api.example.com/things", method);

        result.Success.Should().BeTrue();
        handler.Received.Should().ContainSingle().Which.Method.Method.Should().Be(expected);
    }

    // ----- Explicit first-party POST path (account bootstrap) -----

    [Fact]
    public async Task PostFirstParty_ToConfiguredOrigin_SendsPostWithJsonBody()
    {
        var (client, handler, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"apiKey\":\"x\"}")
        });

        var url = FirstPartyOrigin.ResolveApiBaseUrl() + "/api/signup/l402";
        var result = await client.PostFirstPartyAsync(url, "{\"email\":\"a@b.co\"}");

        result.Success.Should().BeTrue();
        var sent = handler.Received.Should().ContainSingle().Subject;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Theory]
    [InlineData("https://attacker.example.net/api/signup/l402")]
    [InlineData("https://api.lightningenable.com.evil.example/api/signup/l402")]
    [InlineData("http://api.lightningenable.com/api/signup/l402")]
    [InlineData("https://user:pw@api.lightningenable.com/api/signup/l402")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task PostFirstParty_ToAnyOtherOrigin_IsRefusedBeforeNetworkOrWallet(string url)
    {
        var (client, handler, wallet, budget) = Build(_ => Challenge());

        var result = await client.PostFirstPartyAsync(url, "{\"email\":\"a@b.co\"}");

        result.Success.Should().BeFalse();
        result.PaidAmountSats.Should().Be(0);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace().And.Contain("Lightning Enable API origin");
        handler.Received.Should().BeEmpty();
        wallet.Verify(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        budget.Verify(b => b.TryReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
