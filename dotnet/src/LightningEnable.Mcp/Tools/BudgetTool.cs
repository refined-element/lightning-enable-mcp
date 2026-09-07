using System.ComponentModel;
using System.Text.Json.Serialization;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

// Enum members are the literal wire values, so they are spelled exactly as they appear
// in the tool's JSON schema `enum` (JsonStringEnumMemberName is .NET 9+ and this package
// still targets net8.0, so the member name IS the contract).
#pragma warning disable CA1707, IDE1006

/// <summary>What <c>budget</c> should do.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BudgetAction>))]
public enum BudgetAction
{
    /// <summary>Read the limits, tiers and session spend.</summary>
    status,

    /// <summary>Lower the runtime sats caps.</summary>
    tighten,
}

#pragma warning restore CA1707, IDE1006

/// <summary>
/// The consolidated budget verb: <c>get_budget_status</c> + <c>configure_budget</c>.
///
/// Both actions delegate to the original implementations unchanged, so the tighten-only
/// rule (an agent can lower its caps but never raise them above the operator's
/// <c>~/.lightning-enable/config.json</c>) is enforced by exactly the same code as before.
/// </summary>
[McpServerToolType]
public static class BudgetTool
{
    /// <summary>Views or tightens the session's spending limits.</summary>
    [McpServerTool(
        Name = "budget",
        Title = "Budget",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "View or tighten this session's spending limits. Tightening is one-way - an agent "
        + "can lower its caps but never raise the operator's config limits.")]
    public static async Task<string> Budget(
        [Description("status: read limits, tiers and session spend. tighten: lower the runtime sats caps.")]
        BudgetAction action,
        [Description("tighten: max sats per request")] long perRequest = 1000,
        [Description("tighten: max sats per session")] long perSession = 10000,
        IBudgetService? budgetService = null,
        IPriceService? priceService = null,
        IBudgetConfigurationService? configService = null,
        CancellationToken cancellationToken = default)
        => action switch
        {
            BudgetAction.status => await GetBudgetStatusTool.GetBudgetStatus(
                budgetService, priceService, configService, cancellationToken),
            BudgetAction.tighten => await ConfigureBudgetTool.ConfigureBudget(
                perRequest, perSession, budgetService, cancellationToken),
            _ => ActionErrors.Unknown("budget", "action", action, BudgetAction.status, BudgetAction.tighten),
        };
}
