using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// Applies a <see cref="ToolProfile"/> to the server's advertised tool collection, and
/// keeps everything it removed so those tools stay callable.
///
/// <para><c>WithToolsFromAssembly()</c> discovers every <c>[McpServerTool]</c> in the
/// assembly — the consolidated verbs AND the pre-consolidation names. This class then
/// takes two kinds of tool back out of the advertised <c>ToolCollection</c>:</para>
///
/// <list type="number">
/// <item><description><b>Deprecated aliases, always.</b> Every legacy name is served from
/// here rather than from the collection, so a call to one ALWAYS comes back with the
/// <c>deprecated</c> marker — including under the <c>full</c> profile, where the name is
/// re-advertised (listing only, via <see cref="ExtraListedTools"/>).</description></item>
/// <item><description><b>Anything the profile does not advertise.</b> Hidden from
/// <c>list_tools</c> so its schema stays out of the model's context, but still invocable
/// by name.</description></item>
/// </list>
///
/// <para>The SDK consults the <c>CallToolHandler</c> only for names absent from the
/// collection, which is exactly this set — so <c>Program</c>'s handler can serve them
/// with no per-tool plumbing.</para>
/// </summary>
public sealed class ToolSurface
{
    private readonly Dictionary<string, McpServerTool> _hidden = new(StringComparer.Ordinal);
    private readonly List<Tool> _extraListed = new();

    /// <summary>Creates a surface for <paramref name="profile"/>.</summary>
    public ToolSurface(ToolProfile profile) => Profile = profile;

    /// <summary>The active profile.</summary>
    public ToolProfile Profile { get; }

    /// <summary>
    /// Tool definitions a <c>ListToolsHandler</c> should append to the collection's own —
    /// the legacy names under the <c>full</c> profile, empty otherwise. Listing only: the
    /// call still routes through the alias path.
    /// </summary>
    public IReadOnlyList<Tool> ExtraListedTools => _extraListed;

    /// <summary>Names removed from the advertised collection but still callable.</summary>
    public IReadOnlyCollection<string> HiddenToolNames => _hidden.Keys;

    /// <summary>Looks up a tool that was removed from the advertised collection.</summary>
    public bool TryGetHidden(string name, out McpServerTool tool)
    {
        if (!string.IsNullOrEmpty(name) && _hidden.TryGetValue(name, out var found))
        {
            tool = found;
            return true;
        }

        tool = null!;
        return false;
    }

    /// <summary>
    /// Removes from <paramref name="collection"/> every tool this profile does not
    /// advertise (and every deprecated alias regardless of profile), retaining each one so
    /// it stays callable. Idempotent.
    /// </summary>
    public void ApplyTo(McpServerPrimitiveCollection<McpServerTool>? collection)
    {
        if (collection is null)
        {
            return;
        }

        var advertised = ToolProfiles.AdvertisedNames(Profile);

        foreach (var tool in collection.ToArray())
        {
            var name = tool.ProtocolTool.Name;
            var isAlias = DeprecatedAliasDispatcher.IsAlias(name);

            if (!isAlias && advertised.Contains(name))
            {
                continue;
            }

            collection.Remove(tool);
            _hidden[name] = tool;

            // Under `full`, a legacy name is still LISTED — the call is served from
            // _hidden so the deprecation marker is applied on one code path only.
            if (isAlias && advertised.Contains(name))
            {
                _extraListed.Add(tool.ProtocolTool);
            }
        }
    }
}
