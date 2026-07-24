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
/// absent from the reflected [McpServerTool] inventory (hidden from list_tools).
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
        DeprecatedAliasDispatcher.Aliases[alias].Should().Be(replacement);
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
        json.GetProperty("deprecated").GetProperty("removal").GetString().Should().Be("v2.0.0");
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
        json.GetProperty("deprecated").GetProperty("removal").GetString().Should().Be("v2.0.0");
    }

    [Fact]
    public async Task DispatchAsync_NonAlias_Throws()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var act = async () => await DeprecatedAliasDispatcher.DispatchAsync("pay_invoice", null, services);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void AliasNames_AreNotInTheAdvertisedMcpServerToolInventory()
    {
        // The aliases must be hidden: no [McpServerTool] carries an alias name, so they
        // never appear in list_tools (matching the Python port's dispatcher-only aliases).
        var registered = RegisteredMcpServerToolNames();
        foreach (var alias in DeprecatedAliasDispatcher.Aliases.Keys)
        {
            registered.Should().NotContain(alias,
                $"'{alias}' is a hidden forwarding alias and must not be an advertised [McpServerTool]");
        }
    }

    private static HashSet<string> RegisteredMcpServerToolNames()
    {
        var assembly = typeof(PayInvoiceTool).Assembly;
        var names = new HashSet<string>();
        foreach (var type in assembly.GetTypes())
        {
            var isToolType = type.GetCustomAttributes(inherit: false)
                .Any(a => a.GetType().Name == "McpServerToolTypeAttribute");
            if (!isToolType) continue;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(flags))
            {
                var attr = method.GetCustomAttributes(inherit: false)
                    .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
                if (attr is null) continue;
                var nameValue = attr.GetType().GetProperty("Name")?.GetValue(attr) as string;
                names.Add(string.IsNullOrEmpty(nameValue) ? method.Name : nameValue);
            }
        }
        return names;
    }
}
