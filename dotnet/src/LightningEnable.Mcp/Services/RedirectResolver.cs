namespace LightningEnable.Mcp.Services;

/// <summary>
/// Single source of truth for detecting an unfollowed 3xx redirect and resolving its
/// (possibly relative) Location to an absolute URL. The clients run with
/// <c>AllowAutoRedirect = false</c> (see <c>Program.cs</c>), so every redirect surfaces
/// here rather than being followed — this helper is used by BOTH the L402 fetch path
/// (<see cref="L402HttpClient"/>, initial fetch + paid retry) and the discover_api
/// manifest fetch (<c>DiscoverApiTool.TryFetchAsync</c>). Extracted so the resolution
/// logic exists in exactly one place (the review flagged two divergent copies).
/// </summary>
internal static class RedirectResolver
{
    /// <summary>
    /// True when <paramref name="response"/> is a 3xx redirect that carries a
    /// <c>Location</c> header resolving to a valid absolute http/https URL. Resolves a
    /// relative Location against <paramref name="requestUrl"/> so the caller can surface an
    /// absolute URL for the agent to re-call with.
    /// <para/>
    /// A 304 Not Modified — and any 3xx WITHOUT a Location — is deliberately NOT a
    /// redirect: this returns <c>false</c> so the response flows through normal handling
    /// instead of being reported as a broken redirect. A Location that does NOT resolve to
    /// a valid absolute http/https URL (unparseable, a relative that will not resolve, or a
    /// non-http(s) scheme such as <c>javascript:</c> / <c>ftp:</c>) is likewise treated as
    /// NO redirect — identical to the Python port's <c>resolve_redirect_location</c>. The
    /// resolved target is surfaced verbatim; re-calling routes back through the SSRF guard,
    /// so a redirect pointing at an internal host is refused on that next call, not here.
    /// Never throws.
    /// </summary>
    public static bool TryResolve(HttpResponseMessage response, string requestUrl, out string? location)
    {
        location = null;

        var code = (int)response.StatusCode;
        if (code is < 300 or >= 400)
        {
            return false;
        }

        var loc = response.Headers.Location;
        if (loc == null)
        {
            // A 3xx with no Location (e.g. a bare 304): treat as non-redirect so it flows
            // through normal handling rather than being reported as a broken redirect.
            return false;
        }

        string? resolved;
        try
        {
            resolved = loc.IsAbsoluteUri
                ? loc.AbsoluteUri
                : Uri.TryCreate(new Uri(requestUrl), loc, out var abs)
                    ? abs.AbsoluteUri
                    : null;
        }
        catch
        {
            resolved = null;
        }

        // Only a valid absolute http/https URL counts as a redirect. Anything else
        // (unparseable, an unresolvable relative, or a non-http(s) scheme) flows through
        // normal handling instead of being surfaced as an actionable redirect. This keeps
        // the redirect decision identical to the Python port.
        if (resolved == null
            || !Uri.TryCreate(resolved, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        location = resolved;
        return true;
    }
}
