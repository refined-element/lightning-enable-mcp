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
        const string expectedPreimage = "abcd1234567890";

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
        const string expectedPreimage = "abcd1234567890";

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
        const string expectedPreimage = "preimage123";

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
        const string expectedPreimage = "preimage123";

        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NwcPaymentResult.Succeeded(expectedPreimage));

        // Act
        await PayInvoiceTool.PayInvoice(
            invoice: TestInvoice,
            walletService: _walletServiceMock.Object,
            paymentHistory: _paymentHistoryMock.Object);

        // Assert
        _paymentHistoryMock.Verify(h => h.RecordPayment(
            It.IsAny<string>(),
            "PAY",
            It.IsAny<long>(), // Amount extracted from invoice
            It.IsAny<string>(),
            expectedPreimage,
            null,
            200), Times.Once);
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

        // Assert - Verify spend was recorded (amount extracted from invoice)
        _budgetServiceMock.Verify(b => b.RecordSpend(It.IsAny<long>()), Times.Once);
    }

    #endregion

    #region JSON Response Tests

    [Fact]
    public async Task PayInvoice_Success_ReturnsValidJson()
    {
        // Arrange
        const string longInvoice = "lnbc1000n1p3abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnop";
        const string expectedPreimage = "preimage123";

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
        const string expectedPreimage = "preimage123";

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
}
