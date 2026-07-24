using System.Text.Json;
using System.Text.Json.Nodes;
using LightningEnable.Mcp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// Accepted-but-unadvertised forwarding aliases for the renamed/merged tools.
///
/// These are deliberately NOT <c>[McpServerTool]</c> methods. They are dispatched by a
/// custom <c>CallToolHandler</c> wired in <see cref="Program"/>, which the SDK consults
/// only for tool names that are ABSENT from the advertised <c>ToolCollection</c>
/// (populated by <c>WithToolsFromAssembly()</c>). The net effect: the old names stay
/// callable but never appear in <c>list_tools</c> — true hidden aliases, matching the
/// Python port exactly (dispatcher entries not in the advertised list).
///
/// Each alias forwards to its replacement tool and stamps the result with
/// <c>deprecated: { replaced_by, removal }</c>. Slated for removal in v2.0.0.
/// </summary>
public static class DeprecatedAliasDispatcher
{
    /// <summary>Version in which these aliases are removed.</summary>
    public const string Removal = "v2.0.0";

    /// <summary>Old tool name → the new tool that supersedes it.</summary>
    public static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["confirm_payment"] = "verify_confirmation_code",
            ["check_wallet_balance"] = "get_balance",
            ["get_all_balances"] = "get_balance",
        };

    /// <summary>Whether <paramref name="name"/> is one of the deprecated aliases.</summary>
    public static bool IsAlias(string? name) => name != null && Aliases.ContainsKey(name);

    /// <summary>
    /// Forwards a deprecated-alias call to its replacement tool, resolving the tools'
    /// dependencies from <paramref name="services"/>, and returns the replacement's JSON
    /// result annotated with a <c>deprecated</c> marker.
    /// </summary>
    public static async Task<string> DispatchAsync(
        string name,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        if (!Aliases.TryGetValue(name, out var replacedBy))
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
            _ => throw new ArgumentException($"Unhandled alias '{name}'", nameof(name)),
        };

        return WithDeprecation(json, replacedBy);
    }

    /// <summary>Injects a <c>deprecated: { replaced_by, removal }</c> marker into a tool's JSON result.</summary>
    internal static string WithDeprecation(string json, string replacedBy)
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
            ["replaced_by"] = replacedBy,
            ["removal"] = Removal,
        };
        return obj.ToJsonString();
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
