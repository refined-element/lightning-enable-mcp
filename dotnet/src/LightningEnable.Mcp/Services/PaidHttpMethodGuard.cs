namespace LightningEnable.Mcp.Services;

/// <summary>
/// The single rule for which HTTP methods a GENERIC paid fetch (caller-chosen URL, agent-
/// chosen method, auto-paid 402) may use: <c>GET</c> and <c>HEAD</c> only.
/// <para/>
/// A model-controlled POST / PUT / PATCH / DELETE to an arbitrary origin is a state-changing
/// request that the operator never reviewed — and the L402 flow REPLAYS it after paying, so
/// a single tool call could both spend sats and mutate a third-party resource. Restricting
/// the generic path to safe, idempotent methods removes that class of abuse. The rule is
/// enforced twice on purpose: in the tool (so the refusal is cheap and happens before any
/// budget/approval work) and again in <see cref="L402HttpClient.FetchWithL402Async"/> (so no
/// tool can bypass it). First-party, fixed-origin POSTs (account bootstrap) use the separate,
/// non-model-controllable <see cref="IL402HttpClient.PostFirstPartyAsync"/> path instead.
/// </summary>
public static class PaidHttpMethodGuard
{
    /// <summary>Allowed methods, in the order they are named in error messages.</summary>
    public static readonly IReadOnlyList<string> AllowedMethods = new[] { "GET", "HEAD" };

    /// <summary>
    /// Normalises <paramref name="method"/> (trim + upper-case) and checks it against the
    /// allow-list. Returns <c>true</c> with the canonical method when allowed; otherwise
    /// <c>false</c> with a descriptive, non-blank refusal that names the offending method
    /// and the allowed ones. Never throws.
    /// </summary>
    public static bool TryNormalize(string? method, string toolName, out string normalized, out string error)
    {
        var candidate = (method ?? string.Empty).Trim().ToUpperInvariant();
        var allowed = string.Join(" and ", AllowedMethods);

        if (candidate.Length == 0)
        {
            normalized = string.Empty;
            error = $"HTTP method is required and must be {allowed} — {toolName} only performs safe, " +
                    "read-only requests. Omit the method parameter to use GET.";
            return false;
        }

        foreach (var m in AllowedMethods)
        {
            if (string.Equals(candidate, m, StringComparison.Ordinal))
            {
                normalized = m;
                error = string.Empty;
                return true;
            }
        }

        normalized = string.Empty;
        // Show the caller's value verbatim (escaped) so " post " vs "POST" is visible, and
        // cap it so a hostile value cannot bloat the result.
        var shown = method ?? string.Empty;
        if (shown.Length > 32) shown = shown.Substring(0, 32) + "...";
        shown = shown.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        error = $"HTTP method '{shown}' is not allowed. {toolName} only performs {allowed} requests — " +
                "state-changing methods (POST, PUT, PATCH, DELETE, ...) to caller-chosen URLs are refused " +
                "before any request is sent and before any payment is made. Nothing was fetched or paid.";
        return false;
    }
}
