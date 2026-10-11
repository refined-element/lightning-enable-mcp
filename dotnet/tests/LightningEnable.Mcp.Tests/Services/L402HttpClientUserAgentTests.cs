using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using Moq;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// Every paid call (access_l402_resource, pay_l402_challenge, settle_agent_service,
/// create_lightning_enable_account, renew_lightning_enable_subscription) goes through
/// <see cref="L402HttpClient"/>. Server-side analytics attribute traffic by User-Agent, so
/// both the initial 402 request and the paid retry must carry the versioned value.
/// </summary>
public class L402HttpClientUserAgentTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<(bool HasAuthorization, string UserAgent)> Received { get; } = new();

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var userAgent = request.Headers.TryGetValues("User-Agent", out var values)
                ? string.Join(" ", values)
                : string.Empty;
            Received.Add((request.Headers.Authorization != null, userAgent));
            return Task.FromResult(_responder(request));
        }
    }

    private static RecordingHandler ChallengeThenOk() => new(request =>
    {
        if (request.Headers.Authorization != null)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("paid content") };
        }

        var challenge = new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent("payment required")
        };
        challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
            "L402", $"macaroon=\"YWJjZGVm\", invoice=\"{TestInvoices.Build("lnbc100n")}\""));
        return challenge;
    });

    private static L402HttpClient BuildClient(RecordingHandler handler)
    {
        var wallet = new Mock<IWalletService>();
        wallet.SetupGet(w => w.IsConfigured).Returns(true);
        wallet.SetupGet(w => w.ProviderName).Returns("NWC");
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NwcPaymentResult
            {
                Success = true,
                PreimageHex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            });
        var budget = new Mock<IBudgetService>();
        budget.Setup(b => b.CheckBudget(It.IsAny<long>()))
            .Returns(BudgetCheckResult.Allow(100000, 1000));
        budget.Setup(b => b.TryReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long amt, CancellationToken _) => SpendReservationResult.Reserved("res", amt));
        var history = new Mock<IPaymentHistoryService>();
        return new L402HttpClient(new HttpClient(handler), wallet.Object, budget.Object, history.Object);
    }

    [Fact]
    public async Task PaidFetch_SendsVersionedUserAgent_OnChallengeRequestAndPaidRetry()
    {
        var handler = ChallengeThenOk();
        var client = BuildClient(handler);

        var result = await client.FetchWithL402Async("https://api.provider.com/premium", maxSats: 1000);

        result.Success.Should().BeTrue();
        handler.Received.Should().HaveCount(2);
        handler.Received[0].HasAuthorization.Should().BeFalse("the first request is the unpaid one that gets the 402");
        handler.Received[1].HasAuthorization.Should().BeTrue("the second request is the paid retry");
        handler.Received.Should().OnlyContain(r => r.UserAgent == UserAgent.Value);
        handler.Received[0].UserAgent.Should().MatchRegex(@"^LightningEnable-MCP/\d+\.\d+\.\d+$");
    }

    [Fact]
    public async Task PaidFetch_CallerSuppliedUserAgent_StillWins()
    {
        var handler = ChallengeThenOk();
        var client = BuildClient(handler);

        var result = await client.FetchWithL402Async(
            "https://api.provider.com/premium",
            headers: "{\"User-Agent\":\"fixture-agent/9.9\"}",
            maxSats: 1000);

        result.Success.Should().BeTrue();
        handler.Received.Should().HaveCount(2);
        handler.Received.Should().OnlyContain(r => r.UserAgent == "fixture-agent/9.9");
    }

    [Fact]
    public void ConstructingTwiceOnOneHttpClient_DoesNotDuplicateTheHeader()
    {
        var http = new HttpClient();
        _ = new L402HttpClient(http, Mock.Of<IWalletService>(), Mock.Of<IBudgetService>(), Mock.Of<IPaymentHistoryService>());
        _ = new L402HttpClient(http, Mock.Of<IWalletService>(), Mock.Of<IBudgetService>(), Mock.Of<IPaymentHistoryService>());

        http.DefaultRequestHeaders.GetValues("User-Agent").Should().ContainSingle()
            .Which.Should().Be(UserAgent.Value);
    }
}
