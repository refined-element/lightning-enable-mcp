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
    /// Format: macaroon:preimage
    /// </summary>
    public string? L402Token { get; init; }

    /// <summary>
    /// The URL that was fetched.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static L402FetchResult Succeeded(string url, string content, int statusCode, string? contentType = null, long paidAmount = 0, string? token = null) =>
        new()
        {
            Success = true,
            Url = url,
            Content = content,
            StatusCode = statusCode,
            ContentType = contentType,
            PaidAmountSats = paidAmount,
            L402Token = token
        };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static L402FetchResult Failed(string url, string error, int statusCode = 0) =>
        new()
        {
            Success = false,
            Url = url,
            ErrorMessage = error,
            StatusCode = statusCode
        };
}
