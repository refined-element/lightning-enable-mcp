using System.Net;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Cheap, synchronous SSRF pre-check shared by the tools that fetch
/// attacker-influenceable URLs (<c>access_l402_resource</c> and
/// <c>discover_api</c>). It performs ONLY the checks that need no network:
/// <list type="bullet">
///   <item>scheme must be <c>http</c>/<c>https</c>;</item>
///   <item>an IP-literal host is classified directly with
///     <see cref="PrivateIpAddressDetector"/> (no DNS);</item>
///   <item>a small set of internal hostnames / suffixes is refused up front.</item>
/// </list>
/// <para/>
/// It deliberately does NOT resolve DNS. The authoritative IP guard is the
/// connect-time <see cref="SsrfConnectValidator"/> wired onto the HTTP clients in
/// <c>Program.cs</c>, which validates the ACTUAL socket IP (closing the DNS-rebind
/// window) and re-validates every auto-redirect hop. Resolving here as well would
/// double every lookup and block a threadpool thread, and — on an overlong
/// (&gt;255-char) host — throw <see cref="ArgumentOutOfRangeException"/> out of the
/// tool, leaking an internal error. This method never throws.
/// <para/>
/// The blocked-hostname set is kept in sync with the Python guard's
/// <c>_BLOCKED_HOSTNAMES</c> in <c>tools/_ssrf_guard.py</c> — the two ports MUST
/// block the same union set; update both together.
/// </summary>
public static class SsrfUrlGuard
{
    // Hostnames refused regardless of what they resolve to. Kept in sync with the
    // Python guard's _BLOCKED_HOSTNAMES (tools/_ssrf_guard.py). Suffixes (.internal,
    // .localhost) are handled separately below.
    private static readonly HashSet<string> BlockedHostnames = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "metadata",
        "metadata.google.internal",
        "metadata.goog",
        "metadata.azure.com",
    };

    /// <summary>
    /// Returns a generic (never host/IP-echoing) error string when <paramref name="url"/>
    /// must be refused by the cheap pre-check, or <c>null</c> when it passes. Never throws.
    /// </summary>
    public static string? Validate(string? url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return "Invalid URL format";
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return "Only HTTP and HTTPS URLs are allowed";
            }

            var host = uri.Host.ToLowerInvariant();

            // IP-literal host → classify directly (cheap, no DNS). Strip IPv6 brackets
            // ("[::1]" → "::1") so IPAddress.TryParse accepts the literal.
            var literalCandidate = host.Length > 1 && host[0] == '[' && host[^1] == ']'
                ? host[1..^1]
                : host;
            if (IPAddress.TryParse(literalCandidate, out var ipLiteral) &&
                PrivateIpAddressDetector.IsPrivate(ipLiteral))
            {
                return "Access to private or internal networks is not allowed";
            }

            if (IsBlockedHostname(host))
            {
                return "Access to internal or private hosts is not allowed";
            }

            return null;
        }
        catch
        {
            // Never let a parse edge case (e.g. a pathological host) throw out of the
            // tool as an internal error / stack trace — fail closed with a generic message.
            return "Invalid URL format";
        }
    }

    /// <summary>True if <paramref name="host"/> is an always-blocked internal name.</summary>
    public static bool IsBlockedHostname(string host) =>
        BlockedHostnames.Contains(host)
        || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}
