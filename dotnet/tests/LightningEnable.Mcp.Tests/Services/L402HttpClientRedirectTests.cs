using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// FIX A / FIX D — redirect posture of the L402 fetch path.
///
/// The clients run with AllowAutoRedirect = false (Program.cs). This reverts the prior
/// AllowAutoRedirect = true decision: following a redirect would (1) re-send the agent's
/// custom headers (X-Api-Key, Cookie, ...) to a cross-origin redirect target — .NET only
/// auto-strips Authorization, not arbitrary headers — and (2) let the L402 flow pay a
/// provider that host-redirects before its 402, then lose the L402 header on the paid
/// retry's host change (pay, receive nothing).
///
/// These exercise the REAL L402HttpClient with a recording fake handler (a custom
/// HttpMessageHandler is terminal — it never auto-follows — so the assertions pin the
/// client's OWN behavior: it surfaces a 3xx as actionable and never issues the follow-up
/// request that would leak headers or pay-then-lose the resource).
/// </summary>
public class L402HttpClientRedirectTests
{
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

    private static HttpResponseMessage Redirect(HttpStatusCode code, string location)
    {
        var response = new HttpResponseMessage(code);
        response.Headers.Location = new Uri(location);
        response.Content = new StringContent("<html>Moved</html>");
        return response;
    }

    private static (L402HttpClient client, RecordingHandler handler, Mock<IWalletService> wallet, Mock<IBudgetService> budget)
        BuildClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new RecordingHandler(responder);
        var http = new HttpClient(handler);
        var wallet = new Mock<IWalletService>();
        wallet.SetupGet(w => w.IsConfigured).Returns(true);
        wallet.SetupGet(w => w.ProviderName).Returns("NWC");
        var budget = new Mock<IBudgetService>();
        var history = new Mock<IPaymentHistoryService>();
        var client = new L402HttpClient(http, wallet.Object, budget.Object, history.Object);
        return (client, handler, wallet, budget);
    }

    // (i) A 302 to a DIFFERENT host must NOT leak the agent's custom headers: the client
    //     never contacts the second host (no auto-follow), so the X-Api-Key added to the
    //     original request is never re-sent cross-origin.
    [Fact]
    public async Task Fetch_302ToDifferentHost_DoesNotFollow_NorLeakCustomHeaders()
    {
        var (client, handler, _, _) = BuildClient(_ =>
            Redirect(HttpStatusCode.Found, "https://attacker.example.net/collect"));

        var result = await client.FetchWithL402Async(
            "https://original.example.com/data",
            headers: "{\"X-Api-Key\":\"super-secret\"}");

        // The redirect is surfaced, not followed.
        result.Success.Should().BeFalse();
        result.RedirectLocation.Should().Be("https://attacker.example.net/collect");
        result.PaidAmountSats.Should().Be(0);

        // Exactly ONE request was issued — to the original host — carrying the secret.
        handler.Received.Should().ContainSingle();
        handler.Received[0].Uri.Host.Should().Be("original.example.com");
        handler.Received[0].Headers.Should().ContainKey("X-Api-Key");

        // The attacker host was NEVER contacted, so the secret header never left the origin.
        handler.Received.Should().NotContain(r => r.Uri.Host == "attacker.example.net");
    }

    // (ii) An L402 provider that host-redirects BEFORE its 402 must NOT debit sats: the
    //      3xx is seen first, so payment code never runs (no pay, no RecordSpend).
    [Fact]
    public async Task Fetch_L402ProviderHostRedirects_DoesNotPay_SurfacesRedirect()
    {
        var (client, handler, wallet, budget) = BuildClient(_ =>
            Redirect(HttpStatusCode.Found, "https://paywall.other-host.com/402"));

        var result = await client.FetchWithL402Async("https://api.provider.com/premium", maxSats: 5000);

        result.Success.Should().BeFalse();
        result.RedirectLocation.Should().Be("https://paywall.other-host.com/402");
        result.PaidAmountSats.Should().Be(0, "no payment may happen on a redirect before any 402");

        // The money path was never touched.
        wallet.Verify(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        budget.Verify(b => b.RecordSpend(It.IsAny<long>()), Times.Never);
        budget.Verify(b => b.CheckBudget(It.IsAny<long>()), Times.Never);

        // And only the initial request was made — the redirect target was never fetched.
        handler.Received.Should().ContainSingle();
        handler.Received[0].Uri.Host.Should().Be("api.provider.com");
    }

    // (iii) A plain 301 returns the actionable redirect_location result through the tool.
    [Fact]
    public async Task AccessL402Resource_Plain301_ReturnsActionableRedirectResult()
    {
        var (client, _, _, _) = BuildClient(_ =>
            Redirect(HttpStatusCode.MovedPermanently, "https://www.example.com/new-home"));

        var json = await AccessL402ResourceTool.AccessL402Resource(
            url: "https://example.com/old-home",
            l402Client: client);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("statusCode").GetInt32().Should().Be(301);
        root.GetProperty("redirect_location").GetString().Should().Be("https://www.example.com/new-home");
        root.GetProperty("error").GetString().Should().Contain("redirected to https://www.example.com/new-home");
    }

    // A relative Location is resolved to an absolute URL so the agent can re-call with it.
    [Fact]
    public async Task Fetch_RelativeRedirect_ResolvesToAbsoluteTarget()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            response.Headers.Location = new Uri("/v2/data", UriKind.Relative);
            response.Content = new StringContent("moved");
            return response;
        });
        var client = new L402HttpClient(
            new HttpClient(handler),
            Mock.Of<IWalletService>(w => w.IsConfigured == true),
            Mock.Of<IBudgetService>(),
            Mock.Of<IPaymentHistoryService>());

        var result = await client.FetchWithL402Async("https://api.example.com/v1/data");

        result.RedirectLocation.Should().Be("https://api.example.com/v2/data");
    }

    // FIX 1 — a 3xx AFTER a paid retry (402 → pay → 302) is an honest "already paid, then
    // redirected" outcome: the client records the settled payment EXACTLY ONCE (RecordSpend +
    // RecordPayment), NEVER RecordFailedPayment, and surfaces the paid amount + token + target
    // with an explicit "ALREADY PAID — do NOT pay again" message so a consumer can warn the
    // agent off a double-pay and reuse the token against the redirect target.
    [Fact]
    public async Task Fetch_402ThenPaidRetryRedirects_RecordsPaymentOnce_SurfacesTokenAndAlreadyPaid()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Headers.Authorization != null)
            {
                // Paid retry (carries the L402 token) → the resource redirects.
                return Redirect(HttpStatusCode.Found, "https://cdn.example.com/delivered-asset");
            }
            // Initial request → 402 with an L402 challenge (100n = 10 sats).
            var challenge = new HttpResponseMessage(HttpStatusCode.PaymentRequired)
            {
                Content = new StringContent("payment required")
            };
            challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                "L402", "macaroon=\"YWJjZGVm\", invoice=\"lnbc100n1pjtest\""));
            return challenge;
        });

        var wallet = new Mock<IWalletService>();
        wallet.SetupGet(w => w.IsConfigured).Returns(true);
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NwcPaymentResult
            {
                Success = true,
                PreimageHex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            });
        var budget = new Mock<IBudgetService>();
        budget.Setup(b => b.CheckBudget(It.IsAny<long>()))
            .Returns(BudgetCheckResult.Allow(100000, 1000));
        var history = new Mock<IPaymentHistoryService>();

        var client = new L402HttpClient(new HttpClient(handler), wallet.Object, budget.Object, history.Object);

        var result = await client.FetchWithL402Async("https://api.provider.com/premium", maxSats: 1000);

        // Honest paid-redirect result: not a success, but the payment info is surfaced.
        result.Success.Should().BeFalse();
        result.RedirectLocation.Should().Be("https://cdn.example.com/delivered-asset");
        result.PaidAmountSats.Should().Be(10);
        result.L402Token.Should().Be("YWJjZGVm:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        result.ErrorMessage.Should().Contain("ALREADY PAID");
        result.ErrorMessage.Should().Contain("do NOT pay again");
        result.ErrorMessage.Should().Contain("https://cdn.example.com/delivered-asset");

        // Recorded EXACTLY ONCE — a settled payment, never a failed one.
        budget.Verify(b => b.RecordSpend(10), Times.Once);
        history.Verify(h => h.RecordPayment(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(),
            It.IsAny<PaymentStatus>(), It.IsAny<string?>()), Times.Once);
        history.Verify(h => h.RecordFailedPayment(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<string>(), It.IsAny<string?>()), Times.Never);

        // The paid retry was made (2 requests total), but the redirect target was NOT fetched.
        handler.Received.Should().HaveCount(2);
        handler.Received.Should().NotContain(r => r.Uri.Host == "cdn.example.com");
    }

    // A 304 Not Modified is NOT a redirect (no Location) — it must flow through normal
    // handling, not be reported as a broken redirect.
    [Fact]
    public async Task Fetch_304NotModified_IsNotTreatedAsRedirect()
    {
        var (client, _, _, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.NotModified)
        {
            Content = new StringContent("")
        });

        var result = await client.FetchWithL402Async("https://api.example.com/data");

        result.RedirectLocation.Should().BeNull();
    }
}
