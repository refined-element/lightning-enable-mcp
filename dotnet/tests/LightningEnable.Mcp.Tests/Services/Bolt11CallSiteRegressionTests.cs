using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// End-to-end guards for the BOLT11 amount regression: a whole-BTC invoice must be seen as
/// whole BTC (and refused by the caps), and an amountless invoice must be refused — at every
/// payment entry point, before the wallet is ever called.
/// </summary>
public class Bolt11CallSiteRegressionTests
{
    private static readonly string OneBtcInvoice = TestInvoices.Build("lnbc1");   // lnbc11p…
    private static readonly string AmountlessInvoice = TestInvoices.Build("lnbc"); // lnbc1p…

    private static (L402HttpClient client, Mock<IWalletService> wallet, Mock<IBudgetService> budget)
        BuildL402Client(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var handler = new StubHandler(responder ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var wallet = new Mock<IWalletService>();
        wallet.SetupGet(w => w.IsConfigured).Returns(true);
        wallet.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NwcPaymentResult { Success = true, PreimageHex = new string('a', 64) });
        var budget = new Mock<IBudgetService>();
        budget.Setup(b => b.TryReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long amt, CancellationToken _) => SpendReservationResult.Reserved("res", amt));
        var client = new L402HttpClient(new HttpClient(handler), wallet.Object, budget.Object,
            new Mock<IPaymentHistoryService>().Object);
        return (client, wallet, budget);
    }

    [Fact]
    public async Task PayChallenge_WholeBtcInvoice_ExceedsMaxSats_NeverPays()
    {
        var (client, wallet, budget) = BuildL402Client();

        var act = () => client.PayChallengeAsync("YWJjZGVm", OneBtcInvoice, maxSats: 1000);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*100000000 sats exceeds maximum 1000*");
        wallet.Verify(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        budget.Verify(b => b.TryReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PayChallenge_AmountlessInvoice_IsRefused_NeverPays()
    {
        var (client, wallet, _) = BuildL402Client();

        var act = () => client.PayChallengeAsync("YWJjZGVm", AmountlessInvoice, maxSats: 1000);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*no amount*");
        wallet.Verify(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("lnbc1", "exceeds maximum")]
    [InlineData("lnbc", "no amount")]
    public async Task Fetch_L402ChallengeWithDangerousInvoice_IsRefused_NeverPays(string hrp, string expectedError)
    {
        var invoice = TestInvoices.Build(hrp);
        var (client, wallet, _) = BuildL402Client(_ =>
        {
            var challenge = new HttpResponseMessage(HttpStatusCode.PaymentRequired)
            {
                Content = new StringContent("payment required")
            };
            challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                "L402", $"macaroon=\"YWJjZGVm\", invoice=\"{invoice}\""));
            return challenge;
        });

        var result = await client.FetchWithL402Async("https://api.provider.com/premium", maxSats: 1000);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain(expectedError);
        wallet.Verify(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PayInvoiceTool_WholeBtcInvoice_IsCheckedAsWholeBtc()
    {
        var wallet = new Mock<IWalletService>();
        wallet.SetupGet(w => w.IsConfigured).Returns(true);
        var budget = new Mock<IBudgetService>();
        long? checkedAmount = null;
        budget.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Callback((long amt, CancellationToken _) => checkedAmount = amt)
            .ReturnsAsync(new ApprovalCheckResult
            {
                Level = ApprovalLevel.Deny,
                AmountSats = 100_000_000,
                DenialReason = "over limit"
            });

        var result = await PayInvoiceTool.PayInvoice(
            invoice: OneBtcInvoice,
            walletService: wallet.Object,
            budgetService: budget.Object);

        checkedAmount.Should().Be(100_000_000, "a 1 BTC invoice must be budget-checked as 1 BTC, not 1 sat");
        JsonDocument.Parse(result).RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        wallet.Verify(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PayInvoiceTool_AmountlessInvoice_IsRefused()
    {
        var wallet = new Mock<IWalletService>();
        wallet.SetupGet(w => w.IsConfigured).Returns(true);

        var result = await PayInvoiceTool.PayInvoice(invoice: AmountlessInvoice, walletService: wallet.Object);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("no amount");
        wallet.Verify(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("amountless")]
    [InlineData("bad-checksum")]
    public async Task PayL402ChallengeTool_UndecodableAmount_RefusesBeforeAnyApprovalOrConfirmation(string variant)
    {
        var invoice = variant == "amountless"
            ? AmountlessInvoice
            : TestInvoices.Tamper(TestInvoices.Build("lnbc500n"));

        // Budget that WOULD ask a human for a code if it were consulted — the tool must not
        // consult it at all for an invoice it cannot read an amount from.
        var budget = new Mock<IBudgetService>();
        budget.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalCheckResult { Level = ApprovalLevel.FormConfirm, AmountSats = 5000 });
        budget.Setup(b => b.GetUserConfiguration()).Returns(new UserBudgetConfiguration());
        LightningEnable.Mcp.Tests.Tools.ConfirmationTestSetup.SetupDelivered(
            budget, ConfirmationChannelKind.Stderr, "NOPE01", 5000, 5m, "pay_l402_challenge");
        var l402Client = new Mock<IL402HttpClient>();

        var result = await PayL402ChallengeTool.PayL402Challenge(
            invoice: invoice,
            macaroon: "YWJjZGVm",
            maxSats: 5000,
            l402Client: l402Client.Object,
            budgetService: budget.Object,
            priceService: new Mock<IPriceService>().Object);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("no amount");
        json.TryGetProperty("requiresConfirmation", out _).Should().BeFalse();
        budget.Verify(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        budget.Verify(b => b.RequestConfirmationAsync(It.IsAny<ConfirmationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        l402Client.Verify(c => c.PayChallengeAsync(
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
