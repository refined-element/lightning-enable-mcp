using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;

namespace LightningEnable.Mcp.Tests.Tools;

public class GetBalanceToolTests
{
    private readonly Mock<IWalletService> _walletServiceMock = new();

    [Fact]
    public async Task GetBalance_WalletServiceNull_ReturnsError()
    {
        var result = await GetBalanceTool.GetBalance(walletService: null);
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Wallet service not available");
    }

    [Fact]
    public async Task GetBalance_NotConfigured_UsesReceivingMessageNotPaymentMessage()
    {
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(false);

        var result = await GetBalanceTool.GetBalance(_walletServiceMock.Object);
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("configured").GetBoolean().Should().BeFalse();

        var error = json.RootElement.GetProperty("error").GetString();
        error.Should().Contain("not configured");
        // Read-only balance tool: OpenNode is a valid balance backend and must be offered,
        // and the payment-oriented "cannot pay L402" caveat must NOT appear.
        error.Should().Contain("OPENNODE_API_KEY");
        error.Should().NotContain("cannot pay");
        error.Should().Be(WalletMessages.NotConfiguredForReceiving);
    }

    [Fact]
    public async Task GetBalance_Strike_ReportsScalarHeadlineAndMultiCurrency_SingleRoundTrip()
    {
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.ProviderName).Returns("Strike");
        // If the tool wrongly took a second round-trip via GetBalanceAsync, this throw would
        // surface — success:true proves the scalar came from the multi-currency call.
        _walletServiceMock.Setup(w => w.GetBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("GetBalanceAsync must not be called on the Strike path"));
        _walletServiceMock.Setup(w => w.GetAllBalancesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MultiCurrencyBalance.Succeeded(new List<CurrencyBalance>
            {
                new() { Currency = "BTC", Available = 0.00100000m, Total = 0.00100000m, Pending = 0m },
                new() { Currency = "USD", Available = 45.10m, Total = 45.10m, Pending = 0m },
            }));

        var result = await GetBalanceTool.GetBalance(_walletServiceMock.Object);
        var json = JsonDocument.Parse(result).RootElement;

        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("walletType").GetString().Should().Be("strike");
        json.GetProperty("provider").GetString().Should().Be("Strike");
        // Headline scalar derived from the BTC entry of the single multi-currency call.
        json.GetProperty("wallet").GetProperty("balanceSats").GetInt64().Should().Be(100_000);
        json.GetProperty("wallet").GetProperty("balanceMsat").GetInt64().Should().Be(100_000_000);
        // Multi-currency balances[] preserved as supplementary detail.
        json.GetProperty("balances").GetArrayLength().Should().Be(2);

        // Exactly one balance round-trip for Strike, and it is the multi-currency one.
        _walletServiceMock.Verify(w => w.GetAllBalancesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _walletServiceMock.Verify(w => w.GetBalanceAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetBalance_Strike_UsdOnly_StillReturnsScalar()
    {
        // A Strike account holding only USD has no BTC entry. The scalar must still be
        // present (honest 0 sats — they hold no BTC), never dropped.
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.ProviderName).Returns("Strike");
        _walletServiceMock.Setup(w => w.GetAllBalancesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MultiCurrencyBalance.Succeeded(new List<CurrencyBalance>
            {
                new() { Currency = "USD", Available = 45.10m, Total = 45.10m, Pending = 0m },
            }));

        var result = await GetBalanceTool.GetBalance(_walletServiceMock.Object);
        var json = JsonDocument.Parse(result).RootElement;

        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("wallet").GetProperty("balanceSats").GetInt64().Should().Be(0);
        json.GetProperty("wallet").GetProperty("balanceMsat").GetInt64().Should().Be(0);
        json.GetProperty("balances").GetArrayLength().Should().Be(1);
        json.GetProperty("balances")[0].GetProperty("currency").GetString().Should().Be("USD");
    }

    [Fact]
    public async Task GetBalance_Nwc_SingleCurrency_DoesExactlyOneRoundTrip()
    {
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.ProviderName).Returns("NWC");
        _walletServiceMock.Setup(w => w.GetBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NwcBalanceInfo { BalanceMsat = 123_456_000 }); // 123,456 sats

        var result = await GetBalanceTool.GetBalance(_walletServiceMock.Object);
        var json = JsonDocument.Parse(result).RootElement;

        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("walletType").GetString().Should().Be("nwc");
        json.GetProperty("wallet").GetProperty("balanceMsat").GetInt64().Should().Be(123_456_000);
        json.GetProperty("wallet").GetProperty("balanceSats").GetInt64().Should().Be(123_456);
        var balances = json.GetProperty("balances");
        balances.GetArrayLength().Should().Be(1);
        balances[0].GetProperty("currency").GetString().Should().Be("BTC");

        // A single-currency wallet does exactly ONE balance round-trip: GetAllBalancesAsync
        // on NWC/LND/OpenNode just re-calls GetBalanceAsync, so it must not be invoked.
        _walletServiceMock.Verify(w => w.GetBalanceAsync(It.IsAny<CancellationToken>()), Times.Once);
        _walletServiceMock.Verify(w => w.GetAllBalancesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetBalance_OpenNodeBalanceUnavailable_ReturnsHonestErrorNotPhantomBalance()
    {
        // OpenNode has no balance endpoint: GetBalanceAsync returns the -1 sats sentinel.
        // The tool must report this honestly, NEVER fabricate a success with a negative
        // balance.
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.ProviderName).Returns("OpenNode");
        _walletServiceMock.Setup(w => w.GetBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NwcBalanceInfo { BalanceMsat = -1000 }); // -1 sats sentinel

        var result = await GetBalanceTool.GetBalance(_walletServiceMock.Object);
        var json = JsonDocument.Parse(result).RootElement;

        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("errorCode").GetString().Should().Be("BALANCE_UNAVAILABLE");
        json.GetProperty("error").GetString().Should().Contain("not available");
        // No fabricated/negative balance leaks out.
        json.TryGetProperty("wallet", out _).Should().BeFalse();
        json.TryGetProperty("balances", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetBalance_ZeroBalance_IsSuccessNotUnavailable()
    {
        // A genuine zero balance is distinct from "unavailable": success:true, 0 sats.
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.ProviderName).Returns("NWC");
        _walletServiceMock.Setup(w => w.GetBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NwcBalanceInfo { BalanceMsat = 0 });

        var result = await GetBalanceTool.GetBalance(_walletServiceMock.Object);
        var json = JsonDocument.Parse(result).RootElement;

        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("wallet").GetProperty("balanceSats").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task GetBalance_Strike_MultiCurrencyFails_ReturnsHonestError()
    {
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.ProviderName).Returns("Strike");
        _walletServiceMock.Setup(w => w.GetAllBalancesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MultiCurrencyBalance.Failed("AUTH_ERROR", "Unauthorized"));

        var result = await GetBalanceTool.GetBalance(_walletServiceMock.Object);
        var json = JsonDocument.Parse(result).RootElement;

        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("Unauthorized");
        json.GetProperty("errorCode").GetString().Should().Be("AUTH_ERROR");
    }

    [Fact]
    public async Task GetBalance_BalanceThrows_ReturnsError()
    {
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.ProviderName).Returns("NWC");
        _walletServiceMock.Setup(w => w.GetBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection refused"));

        var result = await GetBalanceTool.GetBalance(_walletServiceMock.Object);
        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("Connection refused");
    }
}
