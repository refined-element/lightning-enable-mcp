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

    /// <summary>
    /// Parses an L402 challenge from a WWW-Authenticate header value.
    /// Expected format: L402 macaroon="base64...", invoice="lnbc..."
    /// Or legacy: LSAT macaroon="base64...", invoice="lnbc..."
    /// </summary>
    public static L402ClientChallenge? Parse(string? wwwAuthenticateHeader)
    {
        if (string.IsNullOrWhiteSpace(wwwAuthenticateHeader))
            return null;

        var header = wwwAuthenticateHeader.Trim();

        // Check for L402 or LSAT scheme
        string scheme;
        string remainder;

        if (header.StartsWith("L402 ", StringComparison.OrdinalIgnoreCase))
        {
            scheme = "L402";
            remainder = header[5..].Trim();
        }
        else if (header.StartsWith("LSAT ", StringComparison.OrdinalIgnoreCase))
        {
            scheme = "LSAT";
            remainder = header[5..].Trim();
        }
        else
        {
            return null;
        }

        // Parse key="value" pairs
        var macaroon = ExtractQuotedValue(remainder, "macaroon");
        var invoice = ExtractQuotedValue(remainder, "invoice");

        if (string.IsNullOrEmpty(macaroon) || string.IsNullOrEmpty(invoice))
            return null;

        return new L402ClientChallenge
        {
            Scheme = scheme,
            MacaroonBase64 = macaroon,
            Invoice = invoice
        };
    }

    private static string? ExtractQuotedValue(string input, string key)
    {
        var keyPattern = $"{key}=\"";
        var startIndex = input.IndexOf(keyPattern, StringComparison.OrdinalIgnoreCase);

        if (startIndex < 0)
            return null;

        startIndex += keyPattern.Length;
        var endIndex = input.IndexOf('"', startIndex);

        if (endIndex < 0)
            return null;

        return input[startIndex..endIndex];
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

    /// <summary>
    /// Parses an MPP challenge from a WWW-Authenticate header value.
    /// Expected format: Payment realm="...", method="lightning", invoice="lnbc...", amount="100", currency="sat"
    /// </summary>
    public static MppClientChallenge? Parse(string? wwwAuthenticateHeader)
    {
        if (string.IsNullOrWhiteSpace(wwwAuthenticateHeader))
            return null;

        var header = wwwAuthenticateHeader.Trim();

        if (!header.StartsWith("Payment ", StringComparison.OrdinalIgnoreCase))
            return null;

        var remainder = header[8..].Trim();

        // Verify method="lightning"
        var method = ExtractQuotedValue(remainder, "method");
        if (method == null || !method.Equals("lightning", StringComparison.OrdinalIgnoreCase))
            return null;

        // Extract invoice (required)
        var invoice = ExtractQuotedValue(remainder, "invoice");
        if (string.IsNullOrEmpty(invoice))
            return null;

        // Extract optional fields
        var amount = ExtractQuotedValue(remainder, "amount");
        var realm = ExtractQuotedValue(remainder, "realm");

        return new MppClientChallenge
        {
            Invoice = invoice,
            Amount = amount,
            Realm = realm
        };
    }

    private static string? ExtractQuotedValue(string input, string key)
    {
        var keyPattern = $"{key}=\"";
        var startIndex = input.IndexOf(keyPattern, StringComparison.OrdinalIgnoreCase);

        if (startIndex < 0)
            return null;

        startIndex += keyPattern.Length;
        var endIndex = input.IndexOf('"', startIndex);

        if (endIndex < 0)
            return null;

        return input[startIndex..endIndex];
    }
}

/// <summary>
/// Selects the best payment challenge from multiple WWW-Authenticate headers.
/// Prefers L402 when both are present (caveats, no cache dependency).
/// Falls back to MPP when L402 is not available.
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
    /// </summary>
    public static ParsedChallenge ParseBest(IEnumerable<string> wwwAuthenticateHeaders)
    {
        L402ClientChallenge? l402 = null;
        MppClientChallenge? mpp = null;

        foreach (var header in wwwAuthenticateHeaders)
        {
            l402 ??= L402ClientChallenge.Parse(header);
            mpp ??= MppClientChallenge.Parse(header);
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
