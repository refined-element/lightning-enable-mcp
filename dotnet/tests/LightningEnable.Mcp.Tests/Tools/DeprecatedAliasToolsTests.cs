using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// The renamed/merged tools' OLD names remain accepted-but-unadvertised forwarding
/// aliases: still callable, forwarding to the new tool with a deprecated marker, but
/// absent from the advertised list_tools inventory.
///
/// This class covers the three v1 renames, which DeprecatedAliasDispatcher.DispatchAsync
/// forwards by hand. The 16 consolidation aliases are real [McpServerTool] methods that
/// ToolSurface pulls out of the advertised collection; they are covered end to end, over
/// the real protocol, by DeprecatedAliasDispatchTests.
/// </summary>
public class DeprecatedAliasTests
{
    [Theory]
    [InlineData("confirm_payment", "verify_confirmation_code")]
    [InlineData("check_wallet_balance", "get_balance")]
    [InlineData("get_all_balances", "get_balance")]
    public void Aliases_MapOldNameToReplacement(string alias, string replacement)
    {
        DeprecatedAliasDispatcher.IsAlias(alias).Should().BeTrue();
        DeprecatedAliasDispatcher.Aliases[alias].Tool.Should().Be(replacement);
    }

    [Theory]
    [InlineData("pay_invoice")]
    [InlineData("get_balance")]
    [InlineData("verify_confirmation_code")]
    [InlineData(null)]
    public void IsAlias_False_ForNonAliases(string? name)
    {
        DeprecatedAliasDispatcher.IsAlias(name).Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmPaymentAlias_ForwardsToVerify_AndCarriesDeprecatedMarker()
    {
        var budgetMock = new Mock<IBudgetService>();
        budgetMock.Setup(b => b.ValidateConfirmation("ABC123"))
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

        var services = new ServiceCollection()
            .AddSingleton(budgetMock.Object)
            .BuildServiceProvider();

        var args = new Dictionary<string, JsonElement>
        {
            ["nonce"] = JsonSerializer.SerializeToElement("abc123"),
        };

        var result = await DeprecatedAliasDispatcher.DispatchAsync("confirm_payment", args, services);
        var json = JsonDocument.Parse(result).RootElement;

        // Forwarded to verify_confirmation_code (real result) ...
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("valid").GetBoolean().Should().BeTrue();
        json.GetProperty("tool").GetString().Should().Be("pay_invoice");
        json.GetProperty("message").GetString().Should().Contain("NOTHING HAS BEEN PAID");
        // ... plus the deprecation marker.
        json.GetProperty("deprecated").GetProperty("replaced_by").GetString().Should().Be("verify_confirmation_code");
        json.GetProperty("deprecated").GetProperty("removal").GetString().Should().Be("v3.0.0");
    }

    [Theory]
    [InlineData("check_wallet_balance")]
    [InlineData("get_all_balances")]
    public async Task BalanceAlias_ForwardsToGetBalance_AndCarriesDeprecatedMarker(string alias)
    {
        var walletMock = new Mock<IWalletService>();
        walletMock.Setup(w => w.IsConfigured).Returns(true);
        walletMock.Setup(w => w.ProviderName).Returns("NWC");
        walletMock.Setup(w => w.GetBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NwcBalanceInfo { BalanceMsat = 50_000_000 });
        walletMock.Setup(w => w.GetAllBalancesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MultiCurrencyBalance.Succeeded(new List<CurrencyBalance>
            {
                new() { Currency = "BTC", Available = 0.0005m, Total = 0.0005m, Pending = 0m },
            }));

        var services = new ServiceCollection()
            .AddSingleton(walletMock.Object)
            .BuildServiceProvider();

        var result = await DeprecatedAliasDispatcher.DispatchAsync(alias, arguments: null, services);
        var json = JsonDocument.Parse(result).RootElement;

        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("wallet").GetProperty("balanceSats").GetInt64().Should().Be(50_000);
        json.GetProperty("balances").GetArrayLength().Should().Be(1);
        json.GetProperty("deprecated").GetProperty("replaced_by").GetString().Should().Be("get_balance");
        json.GetProperty("deprecated").GetProperty("removal").GetString().Should().Be("v3.0.0");
    }

    [Fact]
    public async Task DispatchAsync_NonAlias_Throws()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var act = async () => await DeprecatedAliasDispatcher.DispatchAsync("pay_invoice", null, services);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AliasNames_AreNotAdvertisedByTheDefaultProfile()
    {
        // The aliases must be hidden: none may appear in list_tools on the default
        // profile (matching the Python port's dispatcher-only aliases).
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var advertised = await host.AdvertisedNamesAsync();

        foreach (var alias in DeprecatedAliasDispatcher.Aliases.Keys)
        {
            advertised.Should().NotContain(alias,
                $"'{alias}' is a hidden forwarding alias and must not be advertised by default");
        }
    }

    [Fact]
    public async Task ConsolidationAliases_AreHeldByTheToolSurface_SoTheyStayCallable()
    {
        // The 16 consolidation aliases are served from ToolSurface rather than the
        // advertised collection; if one stopped being held there it would fall through to
        // the unknown-tool branch.
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);

        foreach (var alias in ToolProfiles.LegacyToolNames)
        {
            host.Surface.TryGetHidden(alias, out _).Should().BeTrue(
                $"'{alias}' must stay callable after the consolidation");
        }
    }
}
