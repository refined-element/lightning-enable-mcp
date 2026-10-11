using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using LightningEnable.Mcp.Models;
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

    // ----- Authenticated first-party POST (merchant-authed L402 endpoints) -----

    // Letters-only fixture: never a real credential, and no gitleaks generic-api-key hit.
    private const string FixtureMerchantKey = "fixture-string-merchant-credential";

    private const string FixturePreimage = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string RenewUrl() => FirstPartyOrigin.ResolveApiBaseUrl() + "/api/merchant/subscription/renew";

    private static string? HeaderValue(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static void ArrangeSuccessfulPayment(Mock<IWalletService> wallet, Mock<IBudgetService> budget)
    {
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NwcPaymentResult { Success = true, PreimageHex = FixturePreimage });
        budget.Setup(b => b.TryReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long amt, CancellationToken _) => SpendReservationResult.Reserved("res", amt));
        budget.Setup(b => b.CommitReservation(It.IsAny<string>(), It.IsAny<long>()));
        budget.Setup(b => b.RecordPaymentTime());
    }

    [Fact]
    public async Task PostFirstPartyAuthenticated_ToConfiguredOrigin_SendsApiKeyOnFirstRequest()
    {
        var (client, handler, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"active\"}")
        });

        var result = await client.PostFirstPartyAuthenticatedAsync(RenewUrl(), "{}", FixtureMerchantKey);

        result.Success.Should().BeTrue();
        var sent = handler.Received.Should().ContainSingle().Subject;
        sent.Method.Should().Be(HttpMethod.Post);
        HeaderValue(sent, "X-Api-Key").Should().Be(FixtureMerchantKey);
        sent.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task PostFirstPartyAuthenticated_PaidRetry_CarriesApiKeyAndL402Authorization()
    {
        var calls = 0;
        var (client, handler, wallet, budget) = Build(_ => ++calls == 1
            ? Challenge()
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"active\"}") });
        ArrangeSuccessfulPayment(wallet, budget);

        var result = await client.PostFirstPartyAuthenticatedAsync(RenewUrl(), "{}", FixtureMerchantKey, maxSats: 1000);

        result.Success.Should().BeTrue();
        result.PaidAmountSats.Should().Be(10);
        handler.Received.Should().HaveCount(2);

        var first = handler.Received[0];
        first.Method.Should().Be(HttpMethod.Post);
        HeaderValue(first, "X-Api-Key").Should().Be(FixtureMerchantKey);
        first.Headers.Authorization.Should().BeNull("the initial request carries no payment proof");

        var retry = handler.Received[1];
        retry.Method.Should().Be(HttpMethod.Post);
        HeaderValue(retry, "X-Api-Key").Should().Be(FixtureMerchantKey,
            "the merchant-authed endpoint must see the key on the paid retry too");
        retry.Headers.Authorization.Should().NotBeNull();
        retry.Headers.Authorization!.Scheme.Should().Be("L402");
        retry.Headers.Authorization.Parameter.Should().Be($"YWJjZGVm:{FixturePreimage}");
        wallet.Verify(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Renewal correctness depends on this: a second renew call on the same client must
    /// mint and pay a NEW challenge. If the client replayed the first credential, the server
    /// would answer alreadyCredited and no new period would be bought.
    /// </summary>
    [Fact]
    public async Task PostFirstPartyAuthenticated_SecondCall_DoesNotReplayTheFirstCredential()
    {
        // 402 for any request without proof; 200 for any request carrying it.
        var (client, handler, wallet, budget) = Build(req => req.Headers.Authorization == null
            ? Challenge()
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"active\"}") });
        ArrangeSuccessfulPayment(wallet, budget);

        var first = await client.PostFirstPartyAuthenticatedAsync(RenewUrl(), "{}", FixtureMerchantKey, maxSats: 1000);
        var second = await client.PostFirstPartyAuthenticatedAsync(RenewUrl(), "{}", FixtureMerchantKey, maxSats: 1000);

        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();
        second.PaidAmountSats.Should().Be(10, "the second call pays its own invoice");
        handler.Received.Should().HaveCount(4, "each call is unpaid request + paid retry");
        handler.Received[2].Headers.Authorization.Should().BeNull(
            "the second call's first request must not carry the first call's credential");
        wallet.Verify(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData("https://attacker.example.net/api/merchant/subscription/renew")]
    [InlineData("https://api.lightningenable.com.evil.example/api/merchant/subscription/renew")]
    [InlineData("http://api.lightningenable.com/api/merchant/subscription/renew")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task PostFirstPartyAuthenticated_ToAnyOtherOrigin_IsRefusedBeforeNetworkOrWallet(string url)
    {
        var (client, handler, wallet, budget) = Build(_ => Challenge());

        var result = await client.PostFirstPartyAuthenticatedAsync(url, "{}", FixtureMerchantKey);

        result.Success.Should().BeFalse();
        result.PaidAmountSats.Should().Be(0);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace().And.Contain("Lightning Enable API origin");
        result.ErrorMessage.Should().NotContain(FixtureMerchantKey);
        handler.Received.Should().BeEmpty("the key must never be sent to a non-first-party origin");
        wallet.Verify(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        budget.Verify(b => b.TryReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PostFirstPartyAuthenticated_ServerError_DoesNotEchoApiKeyInErrorMessage()
    {
        var (client, handler, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("internal error")
        });

        var result = await client.PostFirstPartyAuthenticatedAsync(RenewUrl(), "{}", FixtureMerchantKey);

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(500);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace().And.Contain("HTTP 500");
        result.ErrorMessage.Should().NotContain(FixtureMerchantKey);
        handler.Received.Should().ContainSingle();
    }

    [Fact]
    public async Task PostFirstPartyAuthenticated_ServerErrorAfterPayment_DoesNotEchoApiKeyInErrorMessage()
    {
        var calls = 0;
        var (client, _, wallet, budget) = Build(_ => ++calls == 1
            ? Challenge()
            : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("internal error") });
        ArrangeSuccessfulPayment(wallet, budget);

        var result = await client.PostFirstPartyAuthenticatedAsync(RenewUrl(), "{}", FixtureMerchantKey);

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(500);
        result.PaidAmountSats.Should().Be(10);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace().And.NotContain(FixtureMerchantKey);
    }

    // ----- Blank merchant key (refused before anything is sent) -----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PostFirstPartyAuthenticated_BlankApiKey_IsRefusedBeforeNetworkOrWallet(string? apiKey)
    {
        var (client, handler, wallet, budget) = Build(_ => Challenge());

        var result = await client.PostFirstPartyAuthenticatedAsync(RenewUrl(), "{}", apiKey!);

        result.Success.Should().BeFalse();
        result.PaidAmountSats.Should().Be(0);
        result.ErrorMessage.Should().Be("No merchant API key configured; nothing was sent.");
        handler.Received.Should().BeEmpty("a blank key must never go out as an empty X-Api-Key");
        wallet.Verify(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        budget.Verify(b => b.TryReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ----- Paid, then the retry throws (funds-safety: never report a settled payment as unpaid) -----

    public static IEnumerable<object[]> RetryFailures() => new[]
    {
        new object[] { new HttpRequestException("connection reset by peer"), "connection reset by peer" },
        new object[] { new TaskCanceledException("timeout"), "timed out" },
        new object[] { new IOException("stream closed"), "stream closed" },
    };

    /// <summary>
    /// The wallet settled the invoice, then the paid retry threw. The generic catch in
    /// FetchCoreAsync used to turn that into PaidAmountSats = 0 with no token, so every caller
    /// told the agent nothing was charged. The paid amount and credential must survive.
    /// </summary>
    [Theory]
    [MemberData(nameof(RetryFailures))]
    public async Task PostFirstPartyAuthenticated_RetryThrowsAfterPayment_KeepsPaidAmountAndToken(Exception failure, string reason)
    {
        var calls = 0;
        var (client, handler, wallet, budget) = Build(_ => ++calls == 1 ? Challenge() : throw failure);
        ArrangeSuccessfulPayment(wallet, budget);

        var result = await client.PostFirstPartyAuthenticatedAsync(RenewUrl(), "{}", FixtureMerchantKey, maxSats: 1000);

        result.Success.Should().BeFalse();
        result.PaidAmountSats.Should().Be(10);
        result.L402Token.Should().Be($"YWJjZGVm:{FixturePreimage}");
        result.Protocol.Should().Be("L402");
        result.StatusCode.Should().Be(0);
        result.ErrorMessage.Should().Contain("Payment succeeded (10 sats)")
            .And.Contain(reason)
            .And.Contain("Do NOT pay again")
            .And.NotContain(FixtureMerchantKey);
        handler.Received.Should().HaveCount(2);
        wallet.Verify(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        budget.Verify(b => b.CommitReservation(It.IsAny<string>(), 10), Times.Once);
    }

    [Fact]
    public async Task PostFirstPartyAuthenticated_CallerCancelsDuringPaidRetry_StillReportsThePayment()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var (client, _, wallet, budget) = Build(_ =>
        {
            if (++calls == 1) return Challenge();
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        ArrangeSuccessfulPayment(wallet, budget);

        var result = await client.PostFirstPartyAuthenticatedAsync(
            RenewUrl(), "{}", FixtureMerchantKey, maxSats: 1000, cancellationToken: cts.Token);

        result.Success.Should().BeFalse();
        result.PaidAmountSats.Should().Be(10, "cancellation after settlement must not erase the payment");
        result.L402Token.Should().Be($"YWJjZGVm:{FixturePreimage}");
        result.ErrorMessage.Should().Contain("cancelled").And.Contain("Do NOT pay again");
    }

    /// <summary>
    /// Same shared path for the generic GET fetch (access_l402_resource / settle_agent_service).
    /// </summary>
    [Fact]
    public async Task FetchWithL402_RetryThrowsAfterPayment_KeepsPaidAmountAndToken()
    {
        var calls = 0;
        var (client, _, wallet, budget) = Build(_ => ++calls == 1
            ? Challenge()
            : throw new HttpRequestException("connection reset by peer"));
        ArrangeSuccessfulPayment(wallet, budget);

        var result = await client.FetchWithL402Async("https://api.example.com/things", "GET", maxSats: 1000);

        result.Success.Should().BeFalse();
        result.PaidAmountSats.Should().Be(10);
        result.L402Token.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().Contain("Do NOT pay again");
    }
}
