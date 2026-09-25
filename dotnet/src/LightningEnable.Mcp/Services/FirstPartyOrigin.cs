namespace LightningEnable.Mcp.Services;

/// <summary>
/// Resolves the ONE trusted first-party API origin (Lightning Enable) that internal,
/// non-model-controllable flows are allowed to POST to through the paid HTTP client.
/// The origin comes from operator configuration only (<c>LIGHTNING_ENABLE_API_URL</c>, or
/// the production default) — never from a tool argument — so the model cannot steer it.
/// </summary>
public static class FirstPartyOrigin
{
    public const string DefaultApiBaseUrl = "https://api.lightningenable.com";

    /// <summary>The configured API base URL with no trailing slash.</summary>
    public static string ResolveApiBaseUrl()
    {
        var baseUrl = Environment.GetEnvironmentVariable("LIGHTNING_ENABLE_API_URL")?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl) || baseUrl.StartsWith("${", StringComparison.Ordinal))
            baseUrl = DefaultApiBaseUrl;
        return baseUrl;
    }

    /// <summary>
    /// True when <paramref name="url"/> is an absolute http(s) URL whose scheme, host and
    /// port exactly match the configured first-party API origin. Never throws.
    /// </summary>
    public static bool IsFirstParty(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) return false;
        if (!Uri.TryCreate(ResolveApiBaseUrl(), UriKind.Absolute, out var origin)) return false;

        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(target.UserInfo)) return false;

        return string.Equals(target.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
            && target.Port == origin.Port;
    }
}
