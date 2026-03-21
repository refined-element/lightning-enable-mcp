using System.Text.RegularExpressions;

namespace LightningEnable.Mcp.Models;

/// <summary>
/// Represents an L402 challenge parsed from a WWW-Authenticate header.
/// Used by the MCP client to handle 402 Payment Required responses.
/// </summary>
public record L402ClientChallenge
{
    /// <summary>
    /// The authentication scheme (L402 or LSAT for legacy).
    /// </summary>
    public required string Scheme { get; init; }

    /// <summary>
    /// Base64-encoded macaroon containing payment hash and service caveats.
    /// </summary>
    public required string MacaroonBase64 { get; init; }

    /// <summary>
    /// BOLT11 Lightning invoice that must be paid to obtain the preimage.
    /// </summary>
    public required string Invoice { get; init; }

    // Regex: scheme is L402 or LSAT (case-insensitive), followed by one or more whitespace chars (SP/HTAB)
    private static readonly Regex SchemeRegex = new(
        @"^\s*(L402|LSAT)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses an L402 challenge from a WWW-Authenticate header value.
    /// Expected format: L402 macaroon="base64...", invoice="lnbc..."
    /// Or legacy: LSAT macaroon="base64...", invoice="lnbc..."
    /// Tolerates tabs, multiple whitespace, and optional whitespace around = in params.
    /// </summary>
    public static L402ClientChallenge? Parse(string? wwwAuthenticateHeader)
    {
        if (string.IsNullOrWhiteSpace(wwwAuthenticateHeader))
            return null;

        var header = wwwAuthenticateHeader.Trim();

        var schemeMatch = SchemeRegex.Match(header);
        if (!schemeMatch.Success)
            return null;

        var scheme = schemeMatch.Groups[1].Value.ToUpperInvariant();
        var remainder = header[schemeMatch.Length..].Trim();

        // Parse key="value" pairs (tolerant of whitespace around =)
        var macaroon = AuthParamParser.ExtractQuotedValue(remainder, "macaroon");
        var invoice = AuthParamParser.ExtractQuotedValue(remainder, "invoice");

        if (string.IsNullOrEmpty(macaroon) || string.IsNullOrEmpty(invoice))
            return null;

        return new L402ClientChallenge
        {
            Scheme = scheme,
            MacaroonBase64 = macaroon,
            Invoice = invoice
        };
    }
}

/// <summary>
/// Represents an MPP (Machine Payments Protocol) challenge parsed from a WWW-Authenticate header.
/// Per IETF draft-ryan-httpauth-payment. Simpler than L402 — no macaroon, just invoice + preimage.
/// </summary>
public record MppClientChallenge
{
    /// <summary>
    /// BOLT11 Lightning invoice that must be paid to obtain the preimage.
    /// </summary>
    public required string Invoice { get; init; }

    /// <summary>
    /// Amount in the specified currency (typically satoshis).
    /// </summary>
    public string? Amount { get; init; }

    /// <summary>
    /// Payment realm identifier.
    /// </summary>
    public string? Realm { get; init; }

    // Regex: "Payment" scheme (case-insensitive), followed by one or more whitespace chars (SP/HTAB)
    private static readonly Regex SchemeRegex = new(
        @"^\s*Payment\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses an MPP challenge from a WWW-Authenticate header value.
    /// Expected format: Payment realm="...", method="lightning", invoice="lnbc...", amount="100", currency="sat"
    /// Tolerates tabs, multiple whitespace, and optional whitespace around = in params.
    /// </summary>
    public static MppClientChallenge? Parse(string? wwwAuthenticateHeader)
    {
        if (string.IsNullOrWhiteSpace(wwwAuthenticateHeader))
            return null;

        var header = wwwAuthenticateHeader.Trim();

        var schemeMatch = SchemeRegex.Match(header);
        if (!schemeMatch.Success)
            return null;

        var remainder = header[schemeMatch.Length..].Trim();

        // Verify method="lightning" (tolerant of whitespace around =)
        var method = AuthParamParser.ExtractQuotedValue(remainder, "method");
        if (method == null || !method.Equals("lightning", StringComparison.OrdinalIgnoreCase))
            return null;

        // Extract invoice (required)
        var invoice = AuthParamParser.ExtractQuotedValue(remainder, "invoice");
        if (string.IsNullOrEmpty(invoice))
            return null;

        // Extract optional fields
        var amount = AuthParamParser.ExtractQuotedValue(remainder, "amount");
        var realm = AuthParamParser.ExtractQuotedValue(remainder, "realm");

        return new MppClientChallenge
        {
            Invoice = invoice,
            Amount = amount,
            Realm = realm
        };
    }
}

/// <summary>
/// Shared helper for parsing RFC 7235 auth-param key="value" pairs.
/// Tolerates optional whitespace around '=' per the HTTP auth-param grammar.
/// </summary>
internal static class AuthParamParser
{
    // Matches: key (optional whitespace) = (optional whitespace) "value"
    // Case-insensitive key matching handled by building the pattern dynamically.
    public static string? ExtractQuotedValue(string input, string key)
    {
        // Build a regex that matches key with optional whitespace around = and quoted value
        // This handles: key="value", key ="value", key= "value", key = "value"
        var pattern = $@"(?i){Regex.Escape(key)}\s*=\s*""([^""]*)""";
        var match = Regex.Match(input, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Splits a single WWW-Authenticate header value that may contain multiple
    /// comma-separated challenges into individual challenge strings.
    /// Detects boundaries at known auth scheme tokens (L402, LSAT, Payment).
    /// </summary>
    public static List<string> ExpandChallenges(string headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return [];

        // Find boundaries where a new scheme starts, handling comma-separated challenges.
        // Scheme tokens: L402, LSAT, Payment (case-insensitive), preceded by start-of-string or comma.
        var boundaryRegex = new Regex(
            @"(?:^|,\s*)(?=(?:l402|lsat|payment)\s)", RegexOptions.IgnoreCase);
        var matches = boundaryRegex.Matches(headerValue);

        if (matches.Count <= 1)
            return [headerValue.Trim()];

        var challenges = new List<string>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            // Skip leading comma and whitespace
            while (start < headerValue.Length && (headerValue[start] == ',' || headerValue[start] == ' '))
                start++;

            var end = i + 1 < matches.Count ? matches[i + 1].Index : headerValue.Length;
            var segment = headerValue[start..end].Trim().TrimEnd(',').Trim();
            if (!string.IsNullOrEmpty(segment))
                challenges.Add(segment);
        }

        return challenges;
    }
}

/// <summary>
/// Selects the best payment challenge from multiple WWW-Authenticate headers.
/// Prefers L402 when both are present (caveats, no cache dependency).
/// Falls back to MPP when L402 is not available.
/// Handles comma-separated challenges within a single header value.
/// </summary>
public static class PaymentChallengeParser
{
    /// <summary>
    /// Result of parsing payment challenge headers.
    /// </summary>
    public record ParsedChallenge
    {
        public L402ClientChallenge? L402 { get; init; }
        public MppClientChallenge? Mpp { get; init; }

        /// <summary>Whether any valid challenge was found.</summary>
        public bool HasChallenge => L402 != null || Mpp != null;

        /// <summary>Whether the MPP protocol was selected (no L402 available).</summary>
        public bool IsMpp => L402 == null && Mpp != null;

        /// <summary>The invoice to pay (from whichever protocol was selected).</summary>
        public string? Invoice => L402?.Invoice ?? Mpp?.Invoice;
    }

    /// <summary>
    /// Parses the best challenge from a collection of WWW-Authenticate header values.
    /// Prefers L402 when available; falls back to MPP.
    /// Handles comma-separated challenges within a single header value.
    /// </summary>
    public static ParsedChallenge ParseBest(IEnumerable<string> wwwAuthenticateHeaders)
    {
        L402ClientChallenge? l402 = null;
        MppClientChallenge? mpp = null;

        foreach (var headerValue in wwwAuthenticateHeaders)
        {
            // Expand comma-separated challenges within a single header value
            var challenges = AuthParamParser.ExpandChallenges(headerValue);
            foreach (var challenge in challenges)
            {
                l402 ??= L402ClientChallenge.Parse(challenge);
                mpp ??= MppClientChallenge.Parse(challenge);
            }
        }

        return new ParsedChallenge { L402 = l402, Mpp = mpp };
    }
}

/// <summary>
/// Result of fetching a URL with L402 support.
/// </summary>
public record L402FetchResult
{
    /// <summary>
    /// Whether the request was successful (2xx status code).
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// HTTP status code of the final response.
    /// </summary>
    public int StatusCode { get; init; }

    /// <summary>
    /// Response body content (if successful).
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Content type of the response.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Error message (if failed).
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Amount paid in satoshis (0 if no payment was required).
    /// </summary>
    public long PaidAmountSats { get; init; }

    /// <summary>
    /// The L402 token used for authentication (if payment was made).
    /// Format: macaroon:preimage (L402) or just preimage (MPP)
    /// </summary>
    public string? L402Token { get; init; }

    /// <summary>
    /// The protocol used for payment (L402 or MPP).
    /// </summary>
    public string? Protocol { get; init; }

    /// <summary>
    /// The URL that was fetched.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static L402FetchResult Succeeded(string url, string content, int statusCode, string? contentType = null, long paidAmountSats = 0, string? l402Token = null, string? protocol = null) =>
        new()
        {
            Success = true,
            Url = url,
            Content = content,
            StatusCode = statusCode,
            ContentType = contentType,
            PaidAmountSats = paidAmountSats,
            L402Token = l402Token,
            Protocol = protocol
        };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static L402FetchResult Failed(string url, string error, int statusCode = 0, long paidAmountSats = 0, string? l402Token = null, string? protocol = null) =>
        new()
        {
            Success = false,
            Url = url,
            ErrorMessage = error,
            StatusCode = statusCode,
            PaidAmountSats = paidAmountSats,
            L402Token = l402Token,
            Protocol = protocol
        };
}
