using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;

namespace LightningEnable.Mcp.Tests.Tools;

public class VerifyConfirmationCodeToolTests
{
    private readonly Mock<IBudgetService> _budgetServiceMock = new();

    [Fact]
    public void VerifyConfirmationCode_EmptyNonce_ReturnsError()
    {
        var result = VerifyConfirmationCodeTool.VerifyConfirmationCode("", _budgetServiceMock.Object);
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("required");
    }

    [Fact]
    public void VerifyConfirmationCode_NoBudgetService_ReturnsError()
    {
        var result = VerifyConfirmationCodeTool.VerifyConfirmationCode("ABC123", budgetService: null);
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Budget service not available");
    }

    [Fact]
    public void VerifyConfirmationCode_InvalidNonce_ReturnsError()
    {
        _budgetServiceMock.Setup(b => b.ValidateConfirmation(It.IsAny<string>()))
            .Returns((PendingConfirmation?)null);

        var result = VerifyConfirmationCodeTool.VerifyConfirmationCode("BADNON", _budgetServiceMock.Object);
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Invalid, expired");
    }

    [Fact]
    public void VerifyConfirmationCode_ValidNonce_SaysNothingHasBeenPaid()
    {
        // The old "Payment of $X confirmed" message read as "money moved". The new
        // message must state nothing was paid and how to actually execute.
        _budgetServiceMock.Setup(b => b.ValidateConfirmation("ABC123"))
            .Returns(new PendingConfirmation
            {
                Nonce = "ABC123",
                AmountSats = 21000,
                AmountUsd = 21.00m,
                ToolName = "pay_invoice",
                Description = "lnbc...",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(2)
            });

        var result = VerifyConfirmationCodeTool.VerifyConfirmationCode("abc123", _budgetServiceMock.Object);
        var json = JsonDocument.Parse(result);

        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("valid").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("amount_sats").GetInt64().Should().Be(21000);
        json.RootElement.GetProperty("tool").GetString().Should().Be("pay_invoice");

        var message = json.RootElement.GetProperty("message").GetString();
        message.Should().Contain("NOTHING HAS BEEN PAID");
        message.Should().Contain("confirmation_nonce");
        message.Should().Contain("pay_invoice");
        // The false "Payment of $X confirmed" phrasing that implied money moved is gone.
        message.Should().NotContain("Payment of");
        message.Should().NotContain("confirmed");

        // Nonce is upper-cased before validation.
        _budgetServiceMock.Verify(b => b.ValidateConfirmation("ABC123"), Times.Once);
    }
}
