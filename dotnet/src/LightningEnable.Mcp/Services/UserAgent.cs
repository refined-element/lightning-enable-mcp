using System.Reflection;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// The <c>User-Agent</c> sent on every outbound HTTP call the server makes
/// (<c>LightningEnable-MCP/&lt;version&gt;</c>). Server-side analytics attribute traffic by it,
/// so it carries the real package version rather than a hand-maintained literal.
/// </summary>
internal static class UserAgent
{
    /// <summary>Product token. Keep exactly this spelling: server-side analytics match on it.</summary>
    public const string ProductToken = "LightningEnable-MCP";

    /// <summary>
    /// Used only when the assembly carries no version at all. The csproj derives the assembly
    /// version from the package version, so this is a last resort. A test fails if it drifts
    /// from the csproj package version.
    /// </summary>
    internal const string FallbackVersion = "2.1.1"; // bump with <Version>

    /// <summary>The package version, without any <c>+commit</c> source-revision suffix.</summary>
    public static string Version { get; } = ResolveVersion(typeof(UserAgent).Assembly);

    /// <summary>The full header value: <c>LightningEnable-MCP/&lt;version&gt;</c>.</summary>
    public static string Value { get; } = $"{ProductToken}/{Version}";

    internal static string ResolveVersion(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            var trimmed = (plus >= 0 ? informational[..plus] : informational).Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        var assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion is not null)
        {
            return assemblyVersion.ToString(3);
        }

        return FallbackVersion;
    }
}
