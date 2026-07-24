using System.Text.Json;
using LightningEnable.Mcp;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests;

/// <summary>
/// Pins the split "wallet not configured" guidance: OpenNode is a VALID option for the
/// receiving/invoicing/info tools, but is demoted for the L402-paying tools (it cannot
/// produce a preimage). Guards against the two messages being re-merged, which is the
/// review finding this fixes.
/// </summary>
public class WalletMessagesTests
{
    // ── Constant-level contract ────────────────────────────────────────────────

    [Fact]
    public void BothMessages_BeginWithWalletNotConfigured_ForTestAndClassifierSubstring()
    {
        // Tool tests and the test_l402_payment classifier key off "not configured".
        WalletMessages.NotConfiguredForPayment.Should().StartWith("Wallet not configured.");
        WalletMessages.NotConfiguredForReceiving.Should().StartWith("Wallet not configured.");
    }

    [Fact]
    public void ReceivingMessage_ListsOpenNodeAsValid()
    {
        // OpenNode fully supports receiving/invoicing/balance operations.
        WalletMessages.NotConfiguredForReceiving.Should().Contain("OPENNODE_API_KEY");
        WalletMessages.NotConfiguredForReceiving.Should().Contain("OPENNODE_API_KEY works for these");
    }

    [Fact]
    public void PaymentMessage_DemotesOpenNode_ForL402()
    {
        // OpenNode cannot pay L402 challenges (no preimage) — must be called out, and the
        // message must NOT present OpenNode as a usable wallet for this class of tool.
        WalletMessages.NotConfiguredForPayment.Should().Contain("L402-capable wallet");
        WalletMessages.NotConfiguredForPayment.Should().Contain("cannot pay L402 challenges");
        WalletMessages.NotConfiguredForPayment.Should().NotContain("OPENNODE_API_KEY works for these");
    }

    // ── Tool-level wiring ──────────────────────────────────────────────────────

    private static Mock<IWalletService> Unconfigured()
    {
        var mock = new Mock<IWalletService>();
        mock.Setup(w => w.IsConfigured).Returns(false);
        return mock;
    }

    private static string Error(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()!;

    [Fact]
    public async Task CreateInvoice_Unconfigured_UsesReceivingMessage_ListingOpenNode()
    {
        var result = await CreateInvoiceTool.CreateInvoice(amountSats: 1000, walletService: Unconfigured().Object);
        Error(result).Should().Contain("OPENNODE_API_KEY works for these");
    }

    [Fact]
    public async Task CheckInvoiceStatus_Unconfigured_UsesReceivingMessage_ListingOpenNode()
    {
        var result = await CheckInvoiceStatusTool.CheckInvoiceStatus(invoiceId: "inv-1", walletService: Unconfigured().Object);
        Error(result).Should().Contain("OPENNODE_API_KEY works for these");
    }

    [Fact]
    public async Task GetAllBalances_Unconfigured_UsesReceivingMessage_ListingOpenNode()
    {
        var result = await GetAllBalancesTool.GetAllBalances(walletService: Unconfigured().Object);
        Error(result).Should().Contain("OPENNODE_API_KEY works for these");
    }

    [Fact]
    public async Task CheckWalletBalance_Unconfigured_UsesReceivingMessage_ListingOpenNode()
    {
        var result = await CheckWalletBalanceTool.CheckWalletBalance(walletService: Unconfigured().Object);
        Error(result).Should().Contain("OPENNODE_API_KEY works for these");
    }

    [Fact]
    public async Task PayInvoice_Unconfigured_UsesPaymentMessage_DemotingOpenNode()
    {
        var result = await PayInvoiceTool.PayInvoice(invoice: "lnbc1000n1p3abcdef", walletService: Unconfigured().Object);
        var error = Error(result);
        error.Should().Contain("cannot pay L402 challenges");
        error.Should().NotContain("OPENNODE_API_KEY works for these");
    }
}
