using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// Wires a <see cref="ToolProfile"/> into an MCP server: trims the advertised tool
/// collection, re-lists the legacy names under <c>full</c>, and serves everything that was
/// trimmed (plus the v1 renames) through the call-tool fallback.
///
/// Program and the tests both go through here, so a profile test exercises the real
/// composition rather than a copy of it.
/// </summary>
public static class McpToolSurfaceExtensions
{
    /// <summary>
    /// Applies <paramref name="profile"/> to the tools already registered on
    /// <paramref name="mcp"/> (call AFTER <c>WithToolsFromAssembly()</c>) and installs the
    /// list/call handlers that keep hidden tools and deprecated aliases callable.
    /// </summary>
    /// <returns>The surface, so a caller can inspect what was hidden.</returns>
    public static ToolSurface WithToolSurface(this IMcpServerBuilder mcp, ToolProfile profile)
    {
        ArgumentNullException.ThrowIfNull(mcp);

        var surface = new ToolSurface(profile);
        mcp.Services.AddSingleton(surface);

        // Trim the advertised collection AFTER the SDK has populated it from DI.
        mcp.Services.PostConfigure<McpServerOptions>(options => surface.ApplyTo(options.ToolCollection));

        if (profile == ToolProfile.Full)
        {
            // `full` re-advertises the pre-consolidation names for prompts written against
            // the old surface. Listing only — a call still routes through the alias path,
            // so it always comes back carrying the deprecation marker.
            mcp.WithListToolsHandler((context, cancellationToken) =>
                ValueTask.FromResult(new ListToolsResult { Tools = surface.ExtraListedTools.ToList() }));
        }

        mcp.WithCallToolHandler((context, cancellationToken) =>
            DispatchAsync(surface, context, cancellationToken));

        return surface;
    }

    /// <summary>
    /// Serves a tool the SDK could not find in the advertised collection: one the profile
    /// hid, a consolidation alias, or a v1 rename. Anything else is genuinely unknown.
    /// </summary>
    internal static async ValueTask<CallToolResult> DispatchAsync(
        ToolSurface surface,
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken)
    {
        var name = context.Params?.Name ?? string.Empty;

        // A tool the profile hid, or a consolidation alias: invoke the real tool that
        // ToolSurface took out of the collection, then stamp the deprecation marker if the
        // caller used an old name.
        if (surface.TryGetHidden(name, out var hiddenTool))
        {
            var result = await hiddenTool.InvokeAsync(context, cancellationToken);
            return DeprecatedAliasDispatcher.IsAlias(name)
                ? DeprecatedAliasDispatcher.WithDeprecation(result, name)
                : result;
        }

        // The three v1 renames have no [McpServerTool] of their own.
        if (DeprecatedAliasDispatcher.IsAlias(name))
        {
            var json = await DeprecatedAliasDispatcher.DispatchAsync(
                name,
                context.Params?.Arguments as IReadOnlyDictionary<string, JsonElement>,
                context.Services!,
                cancellationToken);

            return new CallToolResult
            {
                Content = { new TextContentBlock { Text = json } },
            };
        }

        // Advertised tools are served from the ToolCollection before this handler runs, so
        // anything still reaching here is a genuinely unknown tool.
        return new CallToolResult
        {
            IsError = true,
            Content =
            {
                new TextContentBlock
                {
                    Text = $"Unknown tool: {name}. Call tools/list to see the tools this server "
                        + $"advertises, or set {ToolProfiles.EnvironmentVariable}=full to also "
                        + "advertise the pre-consolidation names.",
                },
            },
        };
    }
}
