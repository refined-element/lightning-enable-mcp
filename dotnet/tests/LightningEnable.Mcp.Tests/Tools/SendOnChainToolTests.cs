using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Tools;

public class SendOnChainToolTests
{
    // Standard valid mainnet bech32 test address.
    private const string ValidAddress = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";

    private static Mock<IWalletService> ConfiguredWallet()
    {
        var wallet = new Mock<IWalletService>();
        wallet.Setup(w => w.IsConfigured).Returns(true);
        return wallet;
    }

    [Fact]
    public async Task SendOnChain_NoConfirmation_AlwaysRequiresIt_AndDoesNotLeakCode()
    {
        // Even an AUTO-APPROVE-tier amount must require confirmation — on-chain is irreversible (C-2b).
        var wallet = ConfiguredWallet();
        var budget = new Mock<IBudgetService>();
        budget.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalCheckResult { Level = ApprovalLevel.AutoApprove, AmountSats = 5000, AmountUsd = 0.05m });
        budget.Setup(b => b.CreatePendingConfirmation(
                It.IsAny<long>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new PendingConfirmation
            {
                Nonce = "ONCH99",
                AmountSats = 5000,
                AmountUsd = 0.05m,
                ToolName = "send_onchain",
                Description = ValidAddress,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(2)
            });

        var result = await SendOnChainTool.SendOnChain(
            address: ValidAddress,
            amountSats: 5000,
            walletService: wallet.Object,
            budgetService: budget.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("requiresConfirmation").GetBoolean().Should().BeTrue();
        json.RootElement.TryGetProperty("nonce", out _).Should().BeFalse("the code must never be in the result");
        result.Should().NotContain("ONCH99", "the confirmation code must not leak into the model-visible result");
        wallet.Verify(w => w.SendOnChainAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendOnChain_BudgetDenied_DoesNotSend_EvenWithACode()
    {
        // An over-limit amount is refused before confirmation — a code cannot authorize
        // a payment beyond the configured ceiling (or during a price outage → Deny).
        var wallet = ConfiguredWallet();
        var budget = new Mock<IBudgetService>();
        budget.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalCheckResult
            {
                Level = ApprovalLevel.Deny,
                AmountSats = 5000,
                DenialReason = "Exceeds per-payment limit"
            });

        var result = await SendOnChainTool.SendOnChain(
            address: ValidAddress,
            amountSats: 5000,
            confirmationNonce: "ANYCODE",
            walletService: wallet.Object,
            budgetService: budget.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        wallet.Verify(w => w.SendOnChainAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendOnChain_InvalidAddress_Rejected_BeforeAnyConfirmation()
    {
        var wallet = ConfiguredWallet();

        var result = await SendOnChainTool.SendOnChain(
            address: "not-a-bitcoin-address",
            amountSats: 5000,
            walletService: wallet.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Invalid Bitcoin address");
    }

    [Fact]
    public async Task SendOnChain_NullBudgetService_FailsClosed_DoesNotSend()
    {
        // On-chain is irreversible: with no budget/confirmation service, refuse rather than
        // bypass the gate and send.
        var wallet = ConfiguredWallet();

        var result = await SendOnChainTool.SendOnChain(
            address: ValidAddress,
            amountSats: 5000,
            walletService: wallet.Object,
            budgetService: null);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("fail-closed");
        wallet.Verify(w => w.SendOnChainAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
