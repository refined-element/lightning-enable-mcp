namespace LightningEnable.Mcp.Services;

/// <summary>
/// Shared URL redaction — the single source of truth for turning a request URL into a
/// display-safe form before it is printed to stderr, logged, or stored in the session
/// payment history. The query string, fragment, and userinfo can carry secrets
/// (<c>?token=...</c>, <c>user:pass@</c>), so only <c>scheme://host[:port]/path</c> is
/// kept and the result is marked when anything was dropped (engineering standard #3 —
/// never log/store secrets). Used by the tools (console prompts), the receipt scope, and
/// <see cref="PaymentHistoryService"/> so the surfaces never diverge. Mirrors the Python
/// port's <c>_url_redact.redact_url_for_display</c>.
/// </summary>
public static class UrlRedaction
{
    /// <summary>Length cap applied to the URL part before the "(redacted)" marker.</summary>
    public const int MaxLength = 80;

    /// <summary>
    /// Returns a display-safe URL with credentials stripped: scheme://host[:port]/path,
    /// length-capped, with " (redacted)" appended when userinfo, query, or fragment were
    /// removed. Never throws — a URL that does not parse is scrubbed by hand.
    /// </summary>
    public static string RedactUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        var (safe, dropped) = BuildRedactedUrl(url);
        if (safe.Length > MaxLength)
        {
            safe = safe.Substring(0, MaxLength) + "...";  // cap the URL part; marker added after
        }
        return dropped ? safe + " (redacted)" : safe;
    }

    private static (string safe, bool dropped) BuildRedactedUrl(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            {
                // uri.Host already brackets IPv6 literals (e.g. "[2001:db8::1]"), so
                // scheme://host:port stays unambiguous without extra handling.
                var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
                var safe = $"{uri.Scheme}://{uri.Host}{port}{uri.AbsolutePath}";
                var dropped = !string.IsNullOrEmpty(uri.Query)
                    || !string.IsNullOrEmpty(uri.Fragment)
                    || !string.IsNullOrEmpty(uri.UserInfo);
                return (safe, dropped);
            }
        }
        catch
        {
            // fall through to the hand-rolled fallback below
        }

        // Parse failed (or a non-URL identifier such as "direct-invoice"): strip query,
        // fragment, AND userinfo by hand so we never leave `user:pass@host`, and report
        // whether anything was removed.
        var s = url.Split('?')[0].Split('#')[0];
        var strippedQueryOrFragment = s.Length != url.Length;
        var schemeIdx = s.IndexOf("//", StringComparison.Ordinal);
        var strippedUserInfo = false;
        if (schemeIdx >= 0)
        {
            var atIdx = s.IndexOf('@', schemeIdx + 2);
            if (atIdx >= 0)
            {
                s = s.Substring(0, schemeIdx + 2) + s.Substring(atIdx + 1);
                strippedUserInfo = true;
            }
        }
        return (s, strippedQueryOrFragment || strippedUserInfo);
    }
}
