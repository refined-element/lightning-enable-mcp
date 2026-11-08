using System.ComponentModel;
using System.Text.Json.Serialization;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

// Enum members are the literal wire values (see BudgetTool for why).
#pragma warning disable CA1707, IDE1006

/// <summary>Which side of the L402 producer flow to run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<L402ProducerAction>))]
public enum L402ProducerAction
{
    /// <summary>Mint an invoice + macaroon challenge for a resource.</summary>
    create,

    /// <summary>Check a payer's macaroon + preimage before granting access.</summary>
    verify,

    /// <summary>Point L402 payouts at a wallet the merchant controls.</summary>
    configure_receive,

    /// <summary>Read the seller account: plan, wallet, checklist, recent mints.</summary>
    status,

    /// <summary>Put an upstream API behind L402.</summary>
    create_proxy,

    /// <summary>Price one route on a proxy.</summary>
    add_endpoint,

    /// <summary>Enable the manifest and list the service publicly.</summary>
    publish,

    /// <summary>Read back the challenges this merchant minted.</summary>
    list_challenges,

    /// <summary>Turn a URL plus a Lightning Address into a paid endpoint (pays the claim).</summary>
    sell_url,
}

#pragma warning restore CA1707, IDE1006

/// <summary>
/// The consolidated producer verb — the whole seller side of L402 behind one tool.
///
/// <para><c>create</c> and <c>verify</c> are the original
/// <c>create_l402_challenge</c> / <c>verify_l402_payment</c> tools, delegating to the same
/// implementations unchanged. The six setup actions
/// (<c>configure_receive</c>, <c>status</c>, <c>create_proxy</c>, <c>add_endpoint</c>,
/// <c>publish</c>, <c>list_challenges</c>) close the gap that used to force an agent out to
/// raw REST between "I have an API key" and "I have a monetized endpoint receiving payments
/// on my own wallet". They live in <see cref="ProducerSetupTools"/>.</para>
///
/// <para><c>sell_url</c> is the one-call path for an operator with no account: a URL and a
/// Lightning Address in, a paid proxy and an API key out, through the producer claim endpoint
/// (<see cref="ProducerSellUrl"/>). It needs no API key; it pays the claim from this server's
/// wallet through the same budget and confirmation path as <c>pay_l402_challenge</c>.</para>
///
/// <para><b>Read vs write.</b> MCP annotations are per TOOL, so the widest action decides:
/// <c>sell_url</c> can spend the wallet, so the tool is destructive and not read-only.
/// <c>status</c> and <c>list_challenges</c> are the read-only actions and say so in the
/// action description the model reads; the other seven write.</para>
/// </summary>
[McpServerToolType]
public static class L402ProducerTool
{
    /// <summary>Runs one action of the L402 seller flow.</summary>
    [McpServerTool(
        Name = "l402_producer",
        Title = "L402 producer",
        ReadOnly = false,
        Destructive = true)]
    [Description(
        "Sell access with L402: charge for your API or monetize an endpoint. sell_url turns a URL "
        + "and a Lightning Address into a paid endpoint in one call, paid from this wallet, no "
        + "account needed. Other actions need LIGHTNING_ENABLE_API_KEY.")]
    public static async Task<string> L402Producer(
        [Description(
            "create: mint an invoice + macaroon challenge. verify: check a payer's token "
            + "before granting access. configure_receive: point payouts at your own NWC "
            + "wallet. status: plan, wallet, checklist and recent mints (read-only). "
            + "create_proxy: put an API behind L402. add_endpoint: price one route. publish: "
            + "list the service publicly. list_challenges: read what you minted (read-only). "
            + "sell_url: pay once to put targetUrl behind L402, paid to lightningAddress. "
            + "Pass only the arguments tagged with your action.")]
        L402ProducerAction action,
        [Description("create: URL or name you are charging for")] string? resource = null,
        [Description("create/add_endpoint: price in sats")] long priceSats = 0,
        [Description("create: invoice text; create_proxy/sell_url: what the API does")] string? description = null,
        [Description("verify: base64 macaroon")] string? macaroon = null,
        [Description("verify: hex preimage")] string? preimage = null,
        [Description("configure_receive: nostr+walletconnect:// string; omit to reuse this server's own NWC wallet")]
        string? nwcConnectionString = null,
        [Description("create_proxy/sell_url: service name")] string? name = null,
        [Description("create_proxy: https:// base URL to monetize")] string? targetBaseUrl = null,
        [Description("create_proxy/sell_url: per-call price buyers pay")] int? defaultPriceSats = null,
        [Description("add_endpoint/publish: id from create_proxy")] string? proxyId = null,
        [Description("add_endpoint: stable id for the route")] string? endpointId = null,
        [Description("add_endpoint: route path like /forecast")] string? path = null,
        [Description("add_endpoint: GET, POST, ... (default GET)")] string? httpMethod = null,
        [Description("add_endpoint: one-line summary")] string? summary = null,
        [Description("publish: rename the listed service")] string? serviceName = null,
        [Description("publish: registry description")] string? serviceDescription = null,
        [Description("publish: registry categories")] string[]? categories = null,
        [Description("list_challenges: paid, unpaid or expired; omit for all")] string? challengeStatus = null,
        [Description("status/list_challenges: max rows")] int limit = 0,
        [Description("list_challenges: rows to skip")] int offset = 0,
        [Description("sell_url: https URL to sell")] string? targetUrl = null,
        [Description("sell_url: name@domain paid per call")] string? lightningAddress = null,
        [Description("sell_url: recovery email")] string? recoveryEmail = null,
        [Description("sell_url: recovery Nostr pubkey, hex")] string? recoveryNostrPubkey = null,
        [Description("sell_url: list once verified")] bool listPublicly = false,
        [Description("sell_url: terms version the operator accepted; omit for the quote")] string? acceptTerms = null,
        [Description("sell_url: code the human reads off the server console; omit to request one")]
        string? confirmationNonce = null,
        ILightningEnableApiService? apiService = null,
        IWalletOnboardingService? onboarding = null,
        IProducerClaimClient? claimClient = null,
        IL402HttpClient? l402Client = null,
        IWalletService? walletService = null,
        IOperationLedger? operationLedger = null,
        IBudgetService? budgetService = null,
        IPriceService? priceService = null,
        IPaymentHistoryService? paymentHistory = null,
        IBudgetConfigurationService? configService = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
        => action switch
        {
            L402ProducerAction.create => await CreateL402ChallengeTool.CreateL402Challenge(
                resource ?? string.Empty, priceSats, description, apiService, cancellationToken),
            L402ProducerAction.verify => await VerifyL402PaymentTool.VerifyL402Payment(
                macaroon ?? string.Empty, preimage ?? string.Empty, apiService, cancellationToken),
            L402ProducerAction.configure_receive => await ProducerSetupTools.ConfigureReceiveAsync(
                nwcConnectionString, apiService, onboarding, cancellationToken),
            L402ProducerAction.status => await ProducerSetupTools.StatusAsync(
                limit <= 0 ? 5 : limit, apiService, cancellationToken),
            L402ProducerAction.create_proxy => await ProducerSetupTools.CreateProxyAsync(
                name, targetBaseUrl, description, defaultPriceSats ?? 10, apiService, cancellationToken),
            L402ProducerAction.add_endpoint => await ProducerSetupTools.AddEndpointAsync(
                proxyId, endpointId, path, httpMethod, summary, priceSats, apiService, cancellationToken),
            L402ProducerAction.publish => await ProducerSetupTools.PublishAsync(
                proxyId, serviceName, serviceDescription, categories, apiService, cancellationToken),
            L402ProducerAction.list_challenges => await ProducerSetupTools.ListChallengesAsync(
                challengeStatus, limit <= 0 ? 20 : limit, offset, apiService, cancellationToken),
            L402ProducerAction.sell_url => await ProducerSellUrl.SellUrlAsync(
                targetUrl, lightningAddress, defaultPriceSats, name, description, recoveryEmail, recoveryNostrPubkey,
                listPublicly, acceptTerms, confirmationNonce, claimClient, l402Client, walletService, operationLedger,
                budgetService, priceService, paymentHistory, configService, server, null, cancellationToken),
            _ => ActionErrors.Unknown(
                "l402_producer", "action", action,
                L402ProducerAction.create, L402ProducerAction.verify,
                L402ProducerAction.configure_receive, L402ProducerAction.status,
                L402ProducerAction.create_proxy, L402ProducerAction.add_endpoint,
                L402ProducerAction.publish, L402ProducerAction.list_challenges,
                L402ProducerAction.sell_url),
        };
}
