using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Tools;

public class PayInvoiceToolTests
{
    private readonly Mock<IWalletService> _walletServiceMock;
    private readonly Mock<IBudgetService> _budgetServiceMock;
    private readonly Mock<IPriceService> _priceServiceMock;
    private readonly Mock<IPaymentHistoryService> _paymentHistoryMock;

    // Test invoice with amount encoded (100 sats = 1000n = 1000 nano-BTC)
    private const string TestInvoice = "lnbc1000n1p3abcdef";

    public PayInvoiceToolTests()
    {
        _walletServiceMock = new Mock<IWalletService>();
        _budgetServiceMock = new Mock<IBudgetService>();
        _priceServiceMock = new Mock<IPriceService>();
        _paymentHistoryMock = new Mock<IPaymentHistoryService>();

        // Default price service setup (100k USD/BTC = 1 sat = $0.001)
        _priceServiceMock.Setup(p => p.SatsToUsdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long sats, CancellationToken _) => sats / 100000m);

        // Default: the atomic spend reservation succeeds. The tool now reserves before
        // paying and commits/releases after; tests that expect a payment to proceed need
        // the reservation to be granted. Denial-path tests short-circuit at the approval
        // check before reaching here, so this blanket setup is safe for them too.
        _budgetServiceMock.Setup(b => b.TryReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long amt, CancellationToken _) => SpendReservationResult.Reserved("test-reservation", amt));
    }

    #region Input Validation Tests

    [Fact]
    public async Task PayInvoice_EmptyInvoice_ReturnsError()
    {
        // Arrange
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);

        // Act
        var result = await PayInvoiceTool.PayInvoice(
            invoice: "",
            walletService: _walletServiceMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("required");
    }

    [Fact]
    public async Task PayInvoice_NullInvoice_ReturnsError()
    {
        // Arrange
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);

        // Act
        var result = await PayInvoiceTool.PayInvoice(
            invoice: null!,
            walletService: _walletServiceMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("required");
    }

    [Fact]
    public async Task PayInvoice_WhitespaceInvoice_ReturnsError()
    {
        // Arrange
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);

        // Act
        var result = await PayInvoiceTool.PayInvoice(
            invoice: "   ",
            walletService: _walletServiceMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("invalid_invoice")]
    [InlineData("bitcoin:abc123")] // Wrong format
    [InlineData("lnurl1abc123")] // LNURL, not invoice
    public async Task PayInvoice_InvalidInvoiceFormat_ReturnsError(string invalidInvoice)
    {
        // Arrange
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);

        // Act
        var result = await PayInvoiceTool.PayInvoice(
            invoice: invalidInvoice,
            walletService: _walletServiceMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Invalid invoice format");
    }

    [Fact]
    public async Task PayInvoice_WalletServiceNull_ReturnsError()
    {
        // Act
        var result = await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: null);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Wallet service not available");
    }

    [Fact]
    public async Task PayInvoice_WalletNotConfigured_ReturnsError()
    {
        // Arrange
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(false);

        // Act
        var result = await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("not configured");
    }

    #endregion

    #region Budget Tests

    [Fact]
    public async Task PayInvoice_BudgetDenied_ReturnsError()
    {
        // Arrange
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _budgetServiceMock.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalCheckResult
            {
                Level = ApprovalLevel.Deny,
                AmountSats = 1000,
                AmountUsd = 0.01m,
                DenialReason = "Exceeds per-payment limit",
                RemainingSessionBudgetUsd = 5.00m
            });

        // Act
        var result = await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            budgetService: _budgetServiceMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Exceeds");
    }

    [Fact]
    public async Task PayInvoice_ExceedsSessionBudget_ReturnsError()
    {
        // Arrange
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _budgetServiceMock.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalCheckResult
            {
                Level = ApprovalLevel.Deny,
                AmountSats = 1000,
                AmountUsd = 0.01m,
                DenialReason = "Would exceed session budget",
                RemainingSessionBudgetUsd = 0.005m
            });

        // Act
        var result = await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            budgetService: _budgetServiceMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.TryGetProperty("budget", out _).Should().BeTrue();
    }

    [Fact]
    public async Task PayInvoice_WithinBudget_Succeeds()
    {
        // Arrange
        const string expectedPreimage = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded(expectedPreimage));

        _budgetServiceMock.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalCheckResult
            {
                Level = ApprovalLevel.AutoApprove,
                AmountSats = 1000,
                AmountUsd = 0.01m,
                RemainingSessionBudgetUsd = 99.99m
            });

        // Act
        var result = await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            budgetService: _budgetServiceMock.Object,
            priceService: _priceServiceMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("preimage").GetString().Should().Be(expectedPreimage);
    }

    [Fact]
    public async Task PayInvoice_NoBudgetService_SkipsBudgetCheck()
    {
        // Arrange
        const string expectedPreimage = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded(expectedPreimage));

        // Act
        var result = await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            budgetService: null);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    #endregion

    #region Payment Tests

    [Fact]
    public async Task PayInvoice_PaymentSucceeds_ReturnsPreimage()
    {
        // Arrange
        const string expectedPreimage = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded(expectedPreimage));

        // Act
        var result = await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("preimage").GetString().Should().Be(expectedPreimage);
        json.RootElement.GetProperty("message").GetString().Should().Contain("successful");
    }

    [Fact]
    public async Task PayInvoice_PaymentFails_ReturnsErrorDetails()
    {
        // Arrange
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Failed("INSUFFICIENT_BALANCE", "Not enough funds"));

        // Act
        var result = await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Not enough funds");
        json.RootElement.GetProperty("errorCode").GetString().Should().Be("INSUFFICIENT_BALANCE");
    }

    [Fact]
    public async Task PayInvoice_PaymentException_ReturnsError()
    {
        // Arrange
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Network timeout"));

        // Act
        var result = await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Network timeout");
    }

    [Theory]
    [InlineData("LNBC1000N1P3ABCDEF", "lnbc1000n1p3abcdef")]
    [InlineData("  lnbc1000n1p3abcdef  ", "lnbc1000n1p3abcdef")]
    [InlineData("LnBc1000N1P3AbCdEf", "lnbc1000n1p3abcdef")]
    public async Task PayInvoice_NormalizesInvoiceToLowercase(string input, string expected)
    {
        // Arrange
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(expected, It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded("preimage123"));

        // Act
        await PayInvoiceTool.PayInvoice(
            invoice: input,
            walletService: _walletServiceMock.Object);

        // Assert
        _walletServiceMock.Verify(w => w.PayInvoiceAsync(expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PayInvoice_TestnetInvoice_AcceptsLntbPrefix()
    {
        // Arrange
        const string testInvoice = "lntb1000n1p3abcdef"; // Testnet prefix with amount
        const string expectedPreimage = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded(expectedPreimage));

        // Act
        var result = await PayInvoiceTool.PayInvoice(
            invoice: testInvoice,
            walletService: _walletServiceMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    #endregion

    #region History Tracking Tests

    [Fact]
    public async Task PayInvoice_Success_RecordsInHistory()
    {
        // Arrange
        const string expectedPreimage = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded(expectedPreimage));

        // Act
        await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            paymentHistory: _paymentHistoryMock.Object);

        // Assert
        // Moq expression trees can't rely on optional arguments, so status/errorMessage
        // are named explicitly — a settled payment is the one case that IS a success.
        _paymentHistoryMock.Verify(h => h.RecordPayment(
            It.IsAny<string>(),
            "PAY",
            It.IsAny<long>(), // Amount extracted from invoice
            It.IsAny<string>(),
            expectedPreimage,
            null,
            200,
            PaymentStatus.Success,
            null), Times.Once);
    }

    [Fact]
    public async Task PayInvoice_Failure_RecordsFailedInHistory()
    {
        // Arrange
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Failed("FAILED", "Payment failed"));

        // Act
        await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            paymentHistory: _paymentHistoryMock.Object);

        // Assert
        _paymentHistoryMock.Verify(h => h.RecordFailedPayment(
            It.IsAny<string>(),
            "PAY",
            It.IsAny<long>(),
            "Payment failed",
            It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task PayInvoice_NoHistoryService_DoesNotThrow()
    {
        // Arrange
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded("preimage123"));

        // Act
        var act = async () => await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            paymentHistory: null);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PayInvoice_Success_RecordsSpendInBudget()
    {
        // Arrange
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded("preimage123"));
        _budgetServiceMock.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalCheckResult
            {
                Level = ApprovalLevel.AutoApprove,
                AmountSats = 1000,
                AmountUsd = 0.01m,
                RemainingSessionBudgetUsd = 99.99m
            });

        // Act
        await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            budgetService: _budgetServiceMock.Object);

        // Assert - the reservation is committed as spend (amount extracted from invoice)
        _budgetServiceMock.Verify(b => b.CommitReservation("test-reservation", It.IsAny<long>()), Times.Once);
        _budgetServiceMock.Verify(b => b.RecordSpend(It.IsAny<long>()), Times.Never);
    }

    #endregion

    #region JSON Response Tests

    [Fact]
    public async Task PayInvoice_Success_ReturnsValidJson()
    {
        // Arrange
        const string longInvoice = "lnbc1000n1p3abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnop";
        const string expectedPreimage = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded(expectedPreimage));

        // Act
        var result = await PayInvoiceTool.PayInvoice(
            invoice: longInvoice,
            walletService: _walletServiceMock.Object,
            priceService: _priceServiceMock.Object);

        // Assert
        var act = () => JsonDocument.Parse(result);
        act.Should().NotThrow();

        var json = JsonDocument.Parse(result);
        json.RootElement.TryGetProperty("success", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("preimage", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("message", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("payment", out _).Should().BeTrue();
    }

    [Fact]
    public async Task PayInvoice_Error_ReturnsValidJson()
    {
        // Arrange
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Failed("ERROR_CODE", "Error message"));

        // Act
        var result = await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object);

        // Assert
        var act = () => JsonDocument.Parse(result);
        act.Should().NotThrow();

        var json = JsonDocument.Parse(result);
        json.RootElement.TryGetProperty("success", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public async Task PayInvoice_TruncatesLongInvoiceInResponse()
    {
        // Arrange
        const string longInvoice = "lnbc1000n1p3abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnop";
        const string expectedPreimage = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded(expectedPreimage));

        // Act
        var result = await PayInvoiceTool.PayInvoice(
            invoice: longInvoice,
            walletService: _walletServiceMock.Object,
            priceService: _priceServiceMock.Object);

        // Assert
        var json = JsonDocument.Parse(result);
        var payment = json.RootElement.GetProperty("payment");
        var invoiceInResponse = payment.GetProperty("invoice").GetString();
        invoiceInResponse.Should().EndWith("...");
        invoiceInResponse!.Length.Should().BeLessThan(longInvoice.Length);
    }

    #endregion

    #region Out-of-band confirmation Tests

    [Fact]
    public async Task PayInvoice_RequiresConfirmation_DoesNotLeakCodeInResult()
    {
        // Arrange — budget says RequiresConfirmation, elicitation unavailable (no server)
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _budgetServiceMock.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalCheckResult
            {
                Level = ApprovalLevel.FormConfirm,
                AmountSats = 1000,
                AmountUsd = 5.00m,
                RemainingSessionBudgetUsd = 95.00m
            });
        _budgetServiceMock.Setup(b => b.CreatePendingConfirmation(
                It.IsAny<long>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new PendingConfirmation
            {
                Nonce = "ABC123",
                AmountSats = 1000,
                AmountUsd = 5.00m,
                ToolName = "pay_invoice",
                Description = "lnbc1000n1p3abcdef...",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(2)
            });
        _budgetServiceMock.Setup(b => b.GetUserConfiguration())
            .Returns(new UserBudgetConfiguration());

        // Act — no McpServer, so elicitation can't work → out-of-band (stderr) path
        var result = await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            budgetService: _budgetServiceMock.Object,
            priceService: _priceServiceMock.Object);

        // Assert — confirmation is requested, but the CODE must NOT appear anywhere in
        // the model-visible result (it goes to stderr only). This is the core security
        // property: a prompt-injected agent can't read its own confirmation code.
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("requiresConfirmation").GetBoolean().Should().BeTrue();
        json.RootElement.TryGetProperty("nonce", out _).Should().BeFalse("the code must never be in the result");
        result.Should().NotContain("ABC123", "the confirmation code must not leak into the model-visible result");
        json.RootElement.TryGetProperty("howToConfirm", out _).Should().BeTrue();
        json.RootElement.GetProperty("howToConfirm").GetString().Should().NotContain("ABC123");
        json.RootElement.GetProperty("expiresInSeconds").GetInt32().Should().Be(120);
        json.RootElement.GetProperty("amount").GetProperty("sats").GetInt64().Should().Be(100); // lnbc1000n = 100 sats
        json.RootElement.GetProperty("amount").GetProperty("usd").GetDecimal().Should().Be(5.00m); // contract: USD still surfaced
    }

    #endregion

    #region Pending / No-Preimage Tests

    private void SetupAutoApprove()
    {
        _budgetServiceMock.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalCheckResult
            {
                Level = ApprovalLevel.AutoApprove,
                AmountSats = 100,
                AmountUsd = 0.01m,
                RemainingSessionBudgetUsd = 99.99m
            });
    }

    [Fact]
    public async Task PayInvoice_PendingPayment_IsNotReportedAsSuccess()
    {
        // A payment still in flight may FAIL. Reporting "Payment successful" makes
        // the agent proceed believing it paid.
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Pending("withdrawal-456", "still settling"));
        SetupAutoApprove();

        var result = await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            budgetService: _budgetServiceMock.Object,
            priceService: _priceServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("status").GetString().Should().Be("pending");
        json.RootElement.GetProperty("trackingId").GetString().Should().Be("withdrawal-456");
        result.Should().NotContain("Payment successful");
    }

    [Fact]
    public async Task PayInvoice_PendingPayment_StillCountsAgainstBudget()
    {
        // The funds are committed. Not counting them lets an agent retry past its cap.
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Pending("withdrawal-456", "still settling"));
        SetupAutoApprove();

        await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            budgetService: _budgetServiceMock.Object,
            priceService: _priceServiceMock.Object);

        _budgetServiceMock.Verify(b => b.CommitReservation("test-reservation", 100), Times.Once);
    }

    [Fact]
    public async Task PayInvoice_PendingPayment_NeverExposesTrackingIdAsPreimage()
    {
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Pending("withdrawal-456", "still settling"));
        SetupAutoApprove();

        var result = await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            budgetService: _budgetServiceMock.Object,
            priceService: _priceServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.TryGetProperty("preimage", out var preimage).Should().BeFalse(
            "a pending payment has no preimage at all");
    }

    [Fact]
    public async Task PayInvoice_PendingPayment_IsNotRecordedAsSuccessfulInHistory()
    {
        // The tool surface correctly says {success:false, status:"pending"} — but the
        // audit trail must agree with it. Driven with the REAL PaymentHistoryService
        // (a mock would make the stamping invisible): if OpenNode later FAILS this
        // withdrawal, a record claiming success is a permanently false audit trail.
        var history = new PaymentHistoryService();
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Pending("withdrawal-456", "still settling"));
        SetupAutoApprove();

        await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            budgetService: _budgetServiceMock.Object,
            priceService: _priceServiceMock.Object,
            paymentHistory: history);

        var summary = history.GetSummary();
        summary.TotalPayments.Should().Be(1, "the in-flight payment is still recorded");
        summary.Payments[0].Status.Should().Be(PaymentStatus.Pending);
        summary.Payments[0].Success.Should().BeFalse(
            "a payment that may still fail was never a success");
        summary.SuccessfulPayments.Should().Be(0,
            "nothing has settled — counting it inflates the agent's success record");
    }

    [Fact]
    public async Task PayInvoice_PendingPayment_StillCountsTowardTotalSatsSpent()
    {
        // The funds ARE committed. Pending must not be successful, but the sats must
        // still be counted — under-counting them lets an agent retry past its cap.
        var history = new PaymentHistoryService();
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Pending("withdrawal-456", "still settling"));
        SetupAutoApprove();

        await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            budgetService: _budgetServiceMock.Object,
            priceService: _priceServiceMock.Object,
            paymentHistory: history);

        history.GetSummary().TotalSatsSpent.Should().Be(100,
            "committed funds count even though the payment has not settled");
    }

    [Fact]
    public async Task PayInvoice_PendingPayment_IsNotRecordedAsFailed()
    {
        // Pending is its own outcome: not a success, but not a failure either — a
        // failure count invites the retry that pays twice.
        var history = new PaymentHistoryService();
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Pending("withdrawal-456", "still settling"));
        SetupAutoApprove();

        await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            budgetService: _budgetServiceMock.Object,
            priceService: _priceServiceMock.Object,
            paymentHistory: history);

        var summary = history.GetSummary();
        summary.FailedPayments.Should().Be(0, "it has not failed — it has not settled");
        summary.PendingPayments.Should().Be(1);
    }

    [Fact]
    public async Task PayInvoice_PendingPayment_GetPaymentHistoryAgreesWithTheToolResult()
    {
        // The reported symptom, end to end: pay_invoice said {success:false,
        // status:"pending"} while get_payment_history said {success:true, error:null}
        // for the SAME payment, and incremented successfulPayments. The two surfaces
        // must tell the agent the same story.
        var history = new PaymentHistoryService();
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Pending("withdrawal-456", "still settling"));
        SetupAutoApprove();

        var payResult = await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            budgetService: _budgetServiceMock.Object,
            priceService: _priceServiceMock.Object,
            paymentHistory: history);

        var historyResult = GetPaymentHistoryTool.GetPaymentHistory(historyService: history);

        var pay = JsonDocument.Parse(payResult).RootElement;
        var hist = JsonDocument.Parse(historyResult).RootElement;
        var payment = hist.GetProperty("payments")[0];
        var summary = hist.GetProperty("summary");

        // Both surfaces agree the payment has NOT succeeded.
        pay.GetProperty("success").GetBoolean().Should().BeFalse();
        payment.GetProperty("success").GetBoolean().Should().BeFalse(
            "the audit trail must not claim a settled payment the tool refused to claim");
        payment.GetProperty("status").GetString().Should().Be("pending");
        payment.GetProperty("error").ValueKind.Should().NotBe(JsonValueKind.Null,
            "a pending record must carry context, not a null error implying a clean success");

        summary.GetProperty("successfulPayments").GetInt32().Should().Be(0);
        summary.GetProperty("pendingPayments").GetInt32().Should().Be(1);
        // ...but the committed funds are still counted.
        summary.GetProperty("totalSatsSpent").GetInt64().Should().Be(100);
    }

    [Fact]
    public async Task PayInvoice_SettledWithoutPreimage_ReportsSuccessWithNullPreimage()
    {
        // The money IS gone, so this is a success — but unprovable, and the tool must
        // say so rather than invent proof.
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.SucceededWithoutPreimage("withdrawal-123", "OpenNode returns no preimage"));
        SetupAutoApprove();

        var result = await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            budgetService: _budgetServiceMock.Object,
            priceService: _priceServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("preimage").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("trackingId").GetString().Should().Be("withdrawal-123");
        json.RootElement.GetProperty("warning").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PayInvoice_WalletReturnsNonPreimageValue_IsNotPassedOffAsProof()
    {
        // Regression guard for the Coinos UUID / withdrawal-ID class of bug at the
        // agent-facing surface: whatever the wallet hands back, only a real preimage
        // may appear in the preimage field.
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded("b5f9e0c2-1234-4a56-8901-abcdef123456"));
        SetupAutoApprove();

        var result = await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            budgetService: _budgetServiceMock.Object,
            priceService: _priceServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("preimage").ValueKind.Should().Be(JsonValueKind.Null);
        result.Should().NotContain("b5f9e0c2-1234-4a56-8901-abcdef123456");
    }

    #endregion
}
