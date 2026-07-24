using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Tools;

public class AgentSettleToolTests
{
    private readonly Mock<IL402HttpClient> _l402ClientMock;
    private readonly Mock<IBudgetService> _budgetServiceMock;
    private readonly Mock<IPaymentHistoryService> _paymentHistoryMock;

    private const string TestEndpoint = "https://api.example.com/l402/translate";

    public AgentSettleToolTests()
    {
        _l402ClientMock = new Mock<IL402HttpClient>();
        _budgetServiceMock = new Mock<IBudgetService>();
        _paymentHistoryMock = new Mock<IPaymentHistoryService>();
    }

    // Single source of truth — on a successful paid settlement the tool is PASSIVE: it
    // formats the client's result but records NOTHING. The client (L402HttpClient) already
    // recorded the spend + payment + cooldown exactly once inside FetchWithL402Async, so a
    // tool-level RecordSpend/RecordPaymentTime/RecordPayment here would DOUBLE-COUNT the
    // budget (the pre-existing defect c). This test asserts the tool no longer records.
    [Fact]
    public async Task SettleAgentService_ValidL402_ReturnsSuccess_AndDoesNotRecord()
    {
        // Arrange
        _budgetServiceMock.Setup(b => b.CheckBudget(1000))
            .Returns(BudgetCheckResult.Allow(8000, 1000));

        _l402ClientMock.Setup(c => c.FetchWithL402Async(
                TestEndpoint, "GET", null, null, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(L402FetchResult.Succeeded(
                TestEndpoint,
                "{\"translation\": \"Hola mundo\"}",
                200,
                "application/json",
                paidAmountSats: 100,
                l402Token: "macaroon123:preimage456"));

        // Act
        var result = await AgentSettleTool.SettleAgentService(
            l402Endpoint: TestEndpoint,
            l402Client: _l402ClientMock.Object,
            budgetService: _budgetServiceMock.Object,
            paymentHistoryService: _paymentHistoryMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("settlement").GetProperty("paid").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("settlement").GetProperty("amountSats").GetInt64().Should().Be(100);
        json.RootElement.GetProperty("response").GetProperty("statusCode").GetInt32().Should().Be(200);
        json.RootElement.GetProperty("response").GetProperty("content").GetString().Should().Contain("Hola mundo");

        // The tool must NOT record — the client is the single source of truth (no double-count).
        _budgetServiceMock.Verify(b => b.RecordSpend(It.IsAny<long>()), Times.Never);
        _budgetServiceMock.Verify(b => b.RecordPaymentTime(), Times.Never);
        _paymentHistoryMock.Verify(h => h.RecordPayment(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(),
            It.IsAny<PaymentStatus>(), It.IsAny<string?>()), Times.Never);
        _paymentHistoryMock.Verify(h => h.RecordFailedPayment(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    // FIX a — a paid settlement whose authorized retry returns HTTP 500 (non-2xx, non-
    // redirect) is NOT a settlement failure. The client already paid + recorded once. The
    // tool must surface the paid amount + token + an "ALREADY PAID — do NOT pay again"
    // message, and must NEVER record a failed payment (which would contradict the client's
    // settled RecordPayment and invite a double-pay).
    [Fact]
    public async Task SettleAgentService_PaidThen500_SurfacesTokenAndAlreadyPaid_NoFailedRecord()
    {
        _budgetServiceMock.Setup(b => b.CheckBudget(1000))
            .Returns(BudgetCheckResult.Allow(8000, 1000));

        _l402ClientMock.Setup(c => c.FetchWithL402Async(
                TestEndpoint, "GET", null, null, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(L402FetchResult.Failed(
                TestEndpoint,
                "Request failed after payment: HTTP 500: boom",
                statusCode: 500,
                paidAmountSats: 175,
                l402Token: "macaroon123:preimage456",
                protocol: "L402"));

        var result = await AgentSettleTool.SettleAgentService(
            l402Endpoint: TestEndpoint,
            l402Client: _l402ClientMock.Object,
            budgetService: _budgetServiceMock.Object,
            paymentHistoryService: _paymentHistoryMock.Object,
            cancellationToken: CancellationToken.None);

        var root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("alreadyPaid").GetBoolean().Should().BeTrue();
        root.GetProperty("statusCode").GetInt32().Should().Be(500);
        root.GetProperty("payment").GetProperty("paid").GetBoolean().Should().BeTrue();
        root.GetProperty("payment").GetProperty("amountSats").GetInt64().Should().Be(175);
        root.GetProperty("payment").GetProperty("l402Token").GetString().Should().Be("macaroon123:preimage456");
        var message = root.GetProperty("message").GetString()!;
        message.Should().Contain("ALREADY PAID");
        message.Should().Contain("do NOT pay again");

        // MUST NOT record a failed payment for a settlement whose invoice was paid.
        _paymentHistoryMock.Verify(h => h.RecordFailedPayment(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        // And no double-record of the spend/payment (the client already did).
        _budgetServiceMock.Verify(b => b.RecordSpend(It.IsAny<long>()), Times.Never);
        _budgetServiceMock.Verify(b => b.RecordPaymentTime(), Times.Never);
        _paymentHistoryMock.Verify(h => h.RecordPayment(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(),
            It.IsAny<PaymentStatus>(), It.IsAny<string?>()), Times.Never);
    }

    // Genuine pre-payment failure (no invoice paid — PaidAmountSats == 0): the tool returns a
    // clean not-paid error and records NOTHING (the client already recorded the failed
    // payment if an actual attempt failed; a challenge/endpoint error simply returns clean).
    [Fact]
    public async Task SettleAgentService_GenuineFailureNoPayment_ReturnsCleanError_NoRecord()
    {
        _budgetServiceMock.Setup(b => b.CheckBudget(1000))
            .Returns(BudgetCheckResult.Allow(8000, 1000));

        _l402ClientMock.Setup(c => c.FetchWithL402Async(
                TestEndpoint, "GET", null, null, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(L402FetchResult.Failed(
                TestEndpoint, "Payment failed: insufficient balance", statusCode: 402));

        var result = await AgentSettleTool.SettleAgentService(
            l402Endpoint: TestEndpoint,
            l402Client: _l402ClientMock.Object,
            budgetService: _budgetServiceMock.Object,
            paymentHistoryService: _paymentHistoryMock.Object,
            cancellationToken: CancellationToken.None);

        var root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.TryGetProperty("alreadyPaid", out _).Should().BeFalse();
        root.GetProperty("error").GetString().Should().Contain("insufficient balance");

        _paymentHistoryMock.Verify(h => h.RecordFailedPayment(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        _budgetServiceMock.Verify(b => b.RecordSpend(It.IsAny<long>()), Times.Never);
        _budgetServiceMock.Verify(b => b.RecordPaymentTime(), Times.Never);
    }

    [Fact]
    public async Task SettleAgentService_MissingEndpoint_ReturnsError()
    {
        // Act
        var result = await AgentSettleTool.SettleAgentService(
            l402Endpoint: "",
            l402Client: _l402ClientMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("L402 endpoint URL is required");
    }

    // FIX 1 — a paid-retry redirect is NOT a settlement failure. The L402 client already
    // paid (and recorded the spend + a successful payment in history) before the resource
    // redirected. The settle tool must NOT record a failed payment (contradicts the client's
    // success record), must NOT re-record the spend (no double-record), and must surface the
    // token + redirect target with an explicit "already paid, do not re-pay" message so the
    // agent retries the target WITH the token instead of paying twice.
    [Fact]
    public async Task SettleAgentService_PaidThenRedirect_SurfacesTokenAndDoesNotInviteRepay()
    {
        _budgetServiceMock.Setup(b => b.CheckBudget(1000))
            .Returns(BudgetCheckResult.Allow(8000, 1000));

        _l402ClientMock.Setup(c => c.FetchWithL402Async(
                TestEndpoint, "GET", null, null, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new L402FetchResult
            {
                Success = false,
                Url = TestEndpoint,
                StatusCode = 302,
                RedirectLocation = "https://cdn.example.com/delivered-asset",
                PaidAmountSats = 150,
                L402Token = "macaroon123:preimage456",
                Protocol = "L402",
                ErrorMessage = "Payment succeeded (150 sats). The resource redirected to " +
                               "https://cdn.example.com/delivered-asset. You have ALREADY PAID — do NOT pay again. " +
                               "Retry the redirect target with the returned L402 token."
            });

        var result = await AgentSettleTool.SettleAgentService(
            l402Endpoint: TestEndpoint,
            l402Client: _l402ClientMock.Object,
            budgetService: _budgetServiceMock.Object,
            paymentHistoryService: _paymentHistoryMock.Object,
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(result);
        var root = json.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeFalse();
        // The paid amount + credential + redirect target are surfaced so the agent can reuse them.
        root.GetProperty("payment").GetProperty("paid").GetBoolean().Should().BeTrue();
        root.GetProperty("payment").GetProperty("amountSats").GetInt64().Should().Be(150);
        root.GetProperty("payment").GetProperty("l402Token").GetString().Should().Be("macaroon123:preimage456");
        root.GetProperty("redirect_location").GetString().Should().Be("https://cdn.example.com/delivered-asset");
        // The message must warn the agent off a double-pay.
        var message = root.GetProperty("message").GetString()!;
        message.Should().Contain("ALREADY PAID");
        message.Should().Contain("do NOT pay again");
        message.Should().Contain("https://cdn.example.com/delivered-asset");

        // MUST NOT record a failed payment — the payment SUCCEEDED (the client recorded it).
        _paymentHistoryMock.Verify(h => h.RecordFailedPayment(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        // MUST NOT re-record the spend/payment — the client already did (no double-record).
        _budgetServiceMock.Verify(b => b.RecordSpend(It.IsAny<long>()), Times.Never);
        _budgetServiceMock.Verify(b => b.RecordPaymentTime(), Times.Never);
        _paymentHistoryMock.Verify(h => h.RecordPayment(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(),
            It.IsAny<PaymentStatus>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task SettleAgentService_BudgetExceeded_ReturnsError()
    {
        // Arrange
        _budgetServiceMock.Setup(b => b.CheckBudget(1000))
            .Returns(BudgetCheckResult.Deny("Would exceed session budget", 500, 1000));

        // Act
        var result = await AgentSettleTool.SettleAgentService(
            l402Endpoint: TestEndpoint,
            l402Client: _l402ClientMock.Object,
            budgetService: _budgetServiceMock.Object,
            paymentHistoryService: _paymentHistoryMock.Object,
            cancellationToken: CancellationToken.None);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Budget limit exceeded");
        json.RootElement.TryGetProperty("details", out var details).Should().BeTrue();
        details.GetProperty("remainingSats").GetInt64().Should().Be(500);

        // The tool is PASSIVE on recording: a budget denial attempts no payment, so it must
        // NOT record a failed payment (the client is the single source of truth). The client
        // is never even reached on a pre-flight budget denial.
        _paymentHistoryMock.Verify(h => h.RecordFailedPayment(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        _l402ClientMock.Verify(c => c.FetchWithL402Async(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
