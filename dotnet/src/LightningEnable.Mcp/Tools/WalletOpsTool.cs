using System.ComponentModel;
using System.Text.Json.Serialization;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

// Enum members are the literal wire values (see BudgetTool for why).
#pragma warning disable CA1707, IDE1006

/// <summary>Which wallet operation <c>wallet_ops</c> should perform.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WalletOpsAction>))]
public enum WalletOpsAction
{
    /// <summary>Current BTC/USD price.</summary>
    price,

    /// <summary>Convert between currencies held in the wallet.</summary>
    exchange,

    /// <summary>Irreversible on-chain send. Always requires a confirmation code.</summary>
    send_onchain,
}

#pragma warning restore CA1707, IDE1006

/// <summary>
/// The consolidated wallet verb: <c>get_btc_price</c> + <c>exchange_currency</c> +
/// <c>send_onchain</c>.
///
/// Each action delegates to the original implementation unchanged. In particular
/// <see cref="WalletOpsAction.send_onchain"/> still goes through
/// <see cref="SendOnChainTool.SendOnChain"/>, which ALWAYS requires an out-of-band
/// confirmation code and fails closed when the budget service is unavailable — the funds-
/// safety behaviour is the same code, reached by a different name.
/// </summary>
[McpServerToolType]
public static class WalletOpsTool
{
    /// <summary>Runs a price, exchange, or on-chain send operation on the wallet.</summary>
    [McpServerTool(
        Name = "wallet_ops",
        Title = "Wallet operations",
        ReadOnly = false,
        Destructive = true)]
    [Description(
        "Wallet operations beyond invoices: BTC price, currency exchange, on-chain sends. "
        + "Strike only (send_onchain also works on LND).")]
    public static async Task<string> WalletOps(
        [Description(
            "price: BTC/USD. exchange: convert currency in your wallet. send_onchain: "
            + "irreversible - ALWAYS needs confirmationNonce, so the first call only "
            + "prints a code to the console.")]
        WalletOpsAction action,
        [Description("exchange: from, USD or BTC")] string? sourceCurrency = null,
        [Description("exchange: to, BTC or USD")] string? targetCurrency = null,
        [Description("exchange: amount to convert")] decimal amount = 0,
        [Description("send_onchain: destination address")] string? address = null,
        [Description("send_onchain: satoshis to send")] long amountSats = 0,
        [Description("Code the human reads off the server console (never returned to you). Omit to request one.")]
        string? confirmationNonce = null,
        [Description("send_onchain: " + SendOnChainTool.IntentIdDescription)]
        string? intentId = null,
        IWalletService? walletService = null,
        IBudgetService? budgetService = null,
        IOperationLedger? operationLedger = null,
        CancellationToken cancellationToken = default)
        => action switch
        {
            WalletOpsAction.price => await GetBtcPriceTool.GetBtcPrice(
                walletService, cancellationToken),
            WalletOpsAction.exchange => await ExchangeCurrencyTool.ExchangeCurrency(
                sourceCurrency ?? string.Empty, targetCurrency ?? string.Empty, amount,
                walletService, cancellationToken),
            WalletOpsAction.send_onchain => await SendOnChainTool.SendOnChain(
                address ?? string.Empty, amountSats, confirmationNonce, intentId,
                walletService, budgetService, operationLedger, cancellationToken),
            _ => ActionErrors.Unknown(
                "wallet_ops", "action", action,
                WalletOpsAction.price, WalletOpsAction.exchange, WalletOpsAction.send_onchain),
        };
}
