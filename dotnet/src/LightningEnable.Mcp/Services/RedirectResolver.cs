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
    /// <c>Location</c> header. Resolves a relative Location against
    /// <paramref name="requestUrl"/> so the caller can surface an absolute URL for the
    /// agent to re-call with.
    /// <para/>
    /// A 304 Not Modified — and any 3xx WITHOUT a Location — is deliberately NOT a
    /// redirect: this returns <c>false</c> so the response flows through normal handling
    /// instead of being reported as a broken redirect. The Location is surfaced verbatim
    /// (resolved to absolute); re-calling routes back through the SSRF guard, so a
    /// redirect that points at an internal host is refused on that next call, not here.
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

        try
        {
            location = loc.IsAbsoluteUri
                ? loc.AbsoluteUri
                : Uri.TryCreate(new Uri(requestUrl), loc, out var resolved)
                    ? resolved.AbsoluteUri
                    : loc.OriginalString;
        }
        catch
        {
            location = loc.OriginalString;
        }

        return true;
    }
}
