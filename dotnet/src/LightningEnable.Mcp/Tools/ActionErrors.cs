using System.Text.Json;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// Descriptive errors for the consolidated <c>action</c>-style tools.
///
/// The discriminator is a required schema <c>enum</c>, so a well-behaved client is
/// rejected before the handler runs. This is the defence behind that: a client that skips
/// schema validation must still get a named tool, the offending value and the valid
/// choices — never a silent no-op or a blank response.
/// </summary>
internal static class ActionErrors
{
    /// <summary>Builds the JSON error for an out-of-range action value.</summary>
    internal static string Unknown<TAction>(string tool, string key, TAction value, params TAction[] allowed)
        where TAction : struct, Enum
    {
        var names = allowed.Select(a => a.ToString()).ToArray();
        return JsonSerializer.Serialize(new
        {
            success = false,
            error = $"{tool}: {key} is '{value}'. Set {key} to one of: {string.Join(", ", names)}.",
            tool,
            validActions = names,
        });
    }
}
