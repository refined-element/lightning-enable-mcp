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
    public async Task GetBalance_NotConfigured_ReturnsError()
    {
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(false);

        var result = await GetBalanceTool.GetBalance(_walletServiceMock.Object);
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("not configured");
        json.RootElement.GetProperty("configured").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetBalance_Strike_ReturnsMultiCurrencyAndScalar()
    {
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.ProviderName).Returns("Strike");
        _walletServiceMock.Setup(w => w.GetBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NwcBalanceInfo { BalanceMsat = 100_000_000 }); // 100k sats
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
        // Scalar sats/msat preserved from the old check_wallet_balance.
        json.GetProperty("wallet").GetProperty("balanceSats").GetInt64().Should().Be(100_000);
        json.GetProperty("wallet").GetProperty("balanceMsat").GetInt64().Should().Be(100_000_000);
        // Multi-currency balances[] preserved from the old get_all_balances.
        json.GetProperty("balances").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task GetBalance_Nwc_PreservesBalanceMsatAndSingleBtcEntry()
    {
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.ProviderName).Returns("NWC");
        _walletServiceMock.Setup(w => w.GetBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NwcBalanceInfo { BalanceMsat = 123_456_000 }); // 123,456 sats
        _walletServiceMock.Setup(w => w.GetAllBalancesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MultiCurrencyBalance.Succeeded(new List<CurrencyBalance>
            {
                new() { Currency = "BTC", Available = 0.00123456m, Total = 0.00123456m, Pending = 0m },
            }));

        var result = await GetBalanceTool.GetBalance(_walletServiceMock.Object);
        var json = JsonDocument.Parse(result).RootElement;

        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("walletType").GetString().Should().Be("nwc");
        json.GetProperty("wallet").GetProperty("balanceMsat").GetInt64().Should().Be(123_456_000);
        json.GetProperty("wallet").GetProperty("balanceSats").GetInt64().Should().Be(123_456);
        var balances = json.GetProperty("balances");
        balances.GetArrayLength().Should().Be(1);
        balances[0].GetProperty("currency").GetString().Should().Be("BTC");
    }

    [Fact]
    public async Task GetBalance_AllBalancesFails_FallsBackToScalarBtcEntry()
    {
        // If GetAllBalancesAsync fails, the scalar balance is still returned (loses
        // nothing check_wallet_balance gave) with a derived single BTC entry.
        _walletServiceMock.Setup(w => w.IsConfigured).Returns(true);
        _walletServiceMock.Setup(w => w.ProviderName).Returns("OpenNode");
        _walletServiceMock.Setup(w => w.GetBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NwcBalanceInfo { BalanceMsat = 50_000_000 }); // 50k sats
        _walletServiceMock.Setup(w => w.GetAllBalancesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MultiCurrencyBalance.Failed("ERR", "cannot enumerate"));

        var result = await GetBalanceTool.GetBalance(_walletServiceMock.Object);
        var json = JsonDocument.Parse(result).RootElement;

        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("wallet").GetProperty("balanceSats").GetInt64().Should().Be(50_000);
        json.GetProperty("balances").GetArrayLength().Should().Be(1);
        json.GetProperty("balances")[0].GetProperty("currency").GetString().Should().Be("BTC");
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
