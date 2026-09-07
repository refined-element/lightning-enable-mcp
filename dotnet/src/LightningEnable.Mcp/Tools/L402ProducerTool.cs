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
}

#pragma warning restore CA1707, IDE1006

/// <summary>
/// The consolidated producer verb: <c>create_l402_challenge</c> + <c>verify_l402_payment</c>.
/// Both actions delegate to the original implementations unchanged.
/// </summary>
[McpServerToolType]
public static class L402ProducerTool
{
    /// <summary>Mints an L402 challenge, or verifies a payer's token.</summary>
    [McpServerTool(
        Name = "l402_producer",
        Title = "L402 producer",
        ReadOnly = false,
        Destructive = false)]
    [Description(
        "Sell access with L402: mint a payment challenge, then verify the token a payer "
        + "presents. Requires LIGHTNING_ENABLE_API_KEY.")]
    public static async Task<string> L402Producer(
        [Description("create: mint an invoice + macaroon challenge. verify: check a payer's token before granting access.")]
        L402ProducerAction action,
        [Description("create: URL or name you are charging for")] string? resource = null,
        [Description("create: price in sats")] long priceSats = 0,
        [Description("create: text on the invoice")] string? description = null,
        [Description("verify: base64 macaroon")] string? macaroon = null,
        [Description("verify: hex preimage")] string? preimage = null,
        ILightningEnableApiService? apiService = null,
        CancellationToken cancellationToken = default)
        => action switch
        {
            L402ProducerAction.create => await CreateL402ChallengeTool.CreateL402Challenge(
                resource ?? string.Empty, priceSats, description, apiService, cancellationToken),
            L402ProducerAction.verify => await VerifyL402PaymentTool.VerifyL402Payment(
                macaroon ?? string.Empty, preimage ?? string.Empty, apiService, cancellationToken),
            _ => ActionErrors.Unknown(
                "l402_producer", "action", action,
                L402ProducerAction.create, L402ProducerAction.verify),
        };
}
