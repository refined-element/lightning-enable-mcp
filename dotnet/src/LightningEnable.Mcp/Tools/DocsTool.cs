using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

// Enum members are the literal wire values (see BudgetTool for why).
#pragma warning disable CA1707, IDE1006

/// <summary>What <c>docs</c> should do.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DocsAction>))]
public enum DocsAction
{
    /// <summary>Rank sections matching a free-text query.</summary>
    search,

    /// <summary>Return a page, or one section of it, as text.</summary>
    read,
}

#pragma warning restore CA1707, IDE1006

/// <summary>
/// Search and read the Lightning Enable documentation, so an agent can answer "how do I set
/// up NWC?" or "what does this error mean?" without web access. The corpus is
/// <c>llms-full.txt</c> (every docs page in one file), fetched once and cached for an hour
/// by <see cref="DocsService"/>. No API key and no wallet are needed. Mirrors Python's
/// <c>tools/docs.py</c>.
/// </summary>
[McpServerToolType]
public static class DocsTool
{
    private static readonly JsonSerializerOptions Output = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Searches or reads the docs.</summary>
    [McpServerTool(
        Name = "docs",
        Title = "Lightning Enable docs",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true)]
    [Description(
        "Search or read the Lightning Enable docs. "
        + "search: query -> sections. read: path, optional section -> text.")]
    public static async Task<string> Docs(
        DocsAction action,
        string? query = null,
        [Description("default 5, max 20")] int? limit = null,
        [Description("e.g. /nwc-setup/account-setup")] string? path = null,
        [Description("heading text")] string? section = null,
        IDocsService? docsService = null,
        CancellationToken cancellationToken = default)
    {
        JsonObject result;
        if (action is not (DocsAction.search or DocsAction.read))
        {
            return ActionErrors.Unknown("docs", "action", action, DocsAction.search, DocsAction.read);
        }

        if (docsService is null)
        {
            result = new JsonObject
            {
                ["success"] = false,
                ["error"] = "The docs service is not available in this server instance. "
                    + $"Open {DocsService.DefaultSourceUrl} directly.",
            };
        }
        else if (action == DocsAction.search)
        {
            result = await docsService.SearchAsync(query, limit, cancellationToken);
        }
        else
        {
            result = await docsService.ReadAsync(path, section, cancellationToken);
        }

        return result.ToJsonString(Output);
    }
}
