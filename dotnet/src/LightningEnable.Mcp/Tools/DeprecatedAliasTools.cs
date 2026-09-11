using System.Text.Json;
using System.Text.Json.Nodes;
using LightningEnable.Mcp.Services;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace LightningEnable.Mcp.Tools;

/// <summary>Where a deprecated tool name forwards to.</summary>
/// <param name="Tool">Name of the tool that supersedes the alias.</param>
/// <param name="Use">Human-readable call form, e.g. <c>budget(action="status")</c>.</param>
public sealed record AliasTarget(string Tool, string Use)
{
    /// <summary>An alias that maps to a tool with no action argument.</summary>
    public AliasTarget(string tool) : this(tool, tool) { }

    /// <summary>An alias that maps to one action of a consolidated tool.</summary>
    public static AliasTarget Action(string tool, string action, string key = "action")
        => new(tool, $"{tool}({key}=\"{action}\")");
}

/// <summary>
/// Accepted-but-unadvertised forwarding aliases for the renamed/merged tools.
///
/// Two kinds of alias live here:
///
/// <list type="number">
/// <item><description><b>Consolidation aliases</b> (the 16 pre-consolidation names folded
/// into <c>budget</c> / <c>receipts</c> / <c>wallet_ops</c> / <c>l402_producer</c> /
/// <c>agent_services</c>). These ARE still <c>[McpServerTool]</c> methods, but
/// <see cref="ToolSurface"/> pulls them out of the advertised collection and
/// <c>Program</c>'s <c>CallToolHandler</c> invokes them from there, stamping the result —
/// so no per-alias argument plumbing is needed and the deprecation marker is applied on
/// exactly one code path.</description></item>
/// <item><description><b>v1 renames</b> (<c>confirm_payment</c>,
/// <c>check_wallet_balance</c>, <c>get_all_balances</c>). These have no
/// <c>[McpServerTool]</c> of their own, so <see cref="DispatchAsync"/> forwards them by
/// hand.</description></item>
/// </list>
///
/// Either way the old name stays callable but never appears in <c>list_tools</c> — except
/// under the <c>full</c> profile, which re-advertises the consolidation aliases (listing
/// only; the call still routes through here). Slated for removal in v3.0.0.
/// </summary>
public static class DeprecatedAliasDispatcher
{
    /// <summary>Version in which these aliases are removed.</summary>
    public const string Removal = "v3.0.0";

    /// <summary>Old tool name → the tool that supersedes it.</summary>
    public static readonly IReadOnlyDictionary<string, AliasTarget> Aliases =
        new Dictionary<string, AliasTarget>(StringComparer.Ordinal)
        {
            // v1 renames — dispatched by hand below (no [McpServerTool] of their own).
            ["confirm_payment"] = new("verify_confirmation_code"),
            ["check_wallet_balance"] = new("get_balance"),
            ["get_all_balances"] = new("get_balance"),

            // Tool-surface consolidation — served from ToolSurface.
            ["get_budget_status"] = AliasTarget.Action("budget", "status"),
            ["configure_budget"] = AliasTarget.Action("budget", "tighten"),
            ["get_receipts"] = AliasTarget.Action("receipts", "durable", "source"),
            ["get_payment_history"] = AliasTarget.Action("receipts", "session", "source"),
            ["get_btc_price"] = AliasTarget.Action("wallet_ops", "price"),
            ["exchange_currency"] = AliasTarget.Action("wallet_ops", "exchange"),
            ["send_onchain"] = AliasTarget.Action("wallet_ops", "send_onchain"),
            ["create_l402_challenge"] = AliasTarget.Action("l402_producer", "create"),
            ["verify_l402_payment"] = AliasTarget.Action("l402_producer", "verify"),
            ["discover_agent_services"] = AliasTarget.Action("agent_services", "discover"),
            ["request_agent_service"] = AliasTarget.Action("agent_services", "request"),
            ["settle_agent_service"] = AliasTarget.Action("agent_services", "settle"),
            ["publish_agent_capability"] = AliasTarget.Action("agent_services", "publish"),
            ["unpublish_agent_capability"] = AliasTarget.Action("agent_services", "unpublish"),
            ["publish_agent_attestation"] = AliasTarget.Action("agent_services", "attest"),
            ["get_agent_reputation"] = AliasTarget.Action("agent_services", "reputation"),
        };

    /// <summary>Whether <paramref name="name"/> is one of the deprecated aliases.</summary>
    public static bool IsAlias(string? name) => name != null && Aliases.ContainsKey(name);

    /// <summary>
    /// Forwards a v1-rename alias call to its replacement tool, resolving the tools'
    /// dependencies from <paramref name="services"/>, and returns the replacement's JSON
    /// result annotated with a <c>deprecated</c> marker.
    ///
    /// Only the three v1 renames need this: the consolidation aliases are real
    /// <c>[McpServerTool]</c> methods, invoked through <see cref="ToolSurface"/>.
    /// </summary>
    public static async Task<string> DispatchAsync(
        string name,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        if (!Aliases.TryGetValue(name, out var target))
        {
            throw new ArgumentException($"'{name}' is not a deprecated alias", nameof(name));
        }

        var budgetService = services.GetService<IBudgetService>();

        string json = name switch
        {
            "confirm_payment" => VerifyConfirmationCodeTool.VerifyConfirmationCode(
                GetStringArgument(arguments, "nonce"), budgetService),
            "check_wallet_balance" or "get_all_balances" => await GetBalanceTool.GetBalance(
                services.GetService<IWalletService>(), budgetService, cancellationToken),
            _ => throw new ArgumentException(
                $"'{name}' is a consolidation alias served by ToolSurface, not by DispatchAsync",
                nameof(name)),
        };

        return WithDeprecation(json, target);
    }

    /// <summary>Injects a <c>deprecated</c> marker into a tool's JSON result.</summary>
    internal static string WithDeprecation(string json, AliasTarget target)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // A non-JSON payload (shouldn't happen) still forwards unchanged so the
            // alias never breaks a caller the new tool would have served.
            return json;
        }

        if (node is not JsonObject obj)
        {
            return json;
        }

        obj["deprecated"] = new JsonObject
        {
            ["replaced_by"] = target.Tool,
            ["use"] = target.Use,
            ["removal"] = Removal,
        };
        return obj.ToJsonString();
    }

    /// <summary>
    /// Stamps every text block of a forwarded <see cref="CallToolResult"/> with the
    /// deprecation marker. Used by the <c>CallToolHandler</c> for the consolidation
    /// aliases, which are invoked as real tools rather than forwarded by hand.
    /// </summary>
    internal static CallToolResult WithDeprecation(CallToolResult result, string aliasName)
    {
        if (!Aliases.TryGetValue(aliasName, out var target))
        {
            return result;
        }

        foreach (var block in result.Content)
        {
            if (block is TextContentBlock text)
            {
                text.Text = WithDeprecation(text.Text, target);
            }
        }

        return result;
    }

    private static string GetStringArgument(IReadOnlyDictionary<string, JsonElement>? arguments, string key)
    {
        if (arguments != null
            && arguments.TryGetValue(key, out var element)
            && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }
        return string.Empty;
    }
}
