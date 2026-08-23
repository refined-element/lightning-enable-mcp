using System.Globalization;
using System.Text;
using System.Text.Json;
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
/// Two profiles share the "Payment" scheme:
/// - Legacy: invoice/amount/currency as top-level auth params (existing behavior, unchanged).
/// - Modern draft-00 (draft-httpauth-payment-00 + draft-lightning-charge-00): a base64url
///   <c>request</c> param carrying JCS JSON with the invoice inside, paired with a
///   single-use <c>Authorization: Payment &lt;base64url(JSON)&gt;</c> credential.
/// A server may send a SUPERSET header carrying both profiles; modern wins, and the
/// legacy params are only a fallback when the modern part is malformed.
/// </summary>
public record MppClientChallenge
{
    /// <summary>
    /// BOLT11 Lightning invoice that must be paid to obtain the preimage.
    /// Modern profile: decoded from the request param's methodDetails.invoice.
    /// </summary>
    public required string Invoice { get; init; }

    /// <summary>
    /// Amount in the specified currency (typically satoshis).
    /// Modern profile: the decoded request's amount (decimal string, sats).
    /// </summary>
    public string? Amount { get; init; }

    /// <summary>
    /// Payment realm identifier.
    /// </summary>
    public string? Realm { get; init; }

    /// <summary>
    /// True when this is a modern draft-00 challenge (non-empty request param).
    /// </summary>
    public bool IsModern { get; init; }

    /// <summary>Challenge id, byte-exact as received (modern profile).</summary>
    public string? Id { get; init; }

    /// <summary>Raw method param as received (echoed byte-exact in the credential).</summary>
    public string? Method { get; init; }

    /// <summary>Raw intent param as received; only "charge" is payable.</summary>
    public string? Intent { get; init; }

    /// <summary>
    /// The base64url-encoded request param EXACTLY as received. Echoed byte-exact in the
    /// credential — never decoded and re-encoded.
    /// </summary>
    public string? RequestEncoded { get; init; }

    /// <summary>RFC3339 expiry, byte-exact as received (modern profile).</summary>
    public string? Expires { get; init; }

    /// <summary>Optional digest param, byte-exact as received.</summary>
    public string? Digest { get; init; }

    /// <summary>Optional description param, byte-exact as received.</summary>
    public string? Description { get; init; }

    /// <summary>Optional opaque param, byte-exact as received.</summary>
    public string? Opaque { get; init; }

    /// <summary>Optional payment hash from the decoded request (64 lowercase hex).</summary>
    public string? PaymentHash { get; init; }

    /// <summary>Optional network from the decoded request (mainnet/regtest/signet).</summary>
    public string? Network { get; init; }

    // Regex: "Payment" scheme (case-insensitive), followed by one or more whitespace chars (SP/HTAB)
    private static readonly Regex SchemeRegex = new(
        @"^\s*Payment\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses an MPP challenge from a WWW-Authenticate header value.
    /// Legacy format: Payment realm="...", method="lightning", invoice="lnbc...", amount="100", currency="sat"
    /// Modern format: Payment id="...", realm="...", method="lightning", intent="charge", request="&lt;b64url&gt;", expires="..."
    /// Tolerates tabs, multiple whitespace, and optional whitespace around = in params.
    /// A malformed modern challenge only falls back to legacy when the same header also
    /// carries a legacy invoice= param (intentional superset fallback) — never silently.
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

        // Modern draft-00 detection: a non-empty request param.
        var requestEncoded = AuthParamParser.ExtractQuotedValue(remainder, "request");
        if (!string.IsNullOrEmpty(requestEncoded))
        {
            var modern = TryParseModern(remainder, method, requestEncoded);
            if (modern != null)
                return modern;

            // Malformed modern part: fall back to the legacy profile ONLY when the same
            // header also carries a legacy invoice= param (superset case — the legacy
            // params are an intentional fallback). Otherwise the challenge is invalid.
            if (string.IsNullOrEmpty(AuthParamParser.ExtractQuotedValue(remainder, "invoice")))
                return null;
        }

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
            Realm = realm,
            Method = method
        };
    }

    /// <summary>
    /// Parses the modern draft-00 profile from the auth params. Returns null when the
    /// challenge is malformed (bad base64url, bad JSON, missing invoice) or fails a
    /// client-side sanity check (intent != charge, currency != sat).
    /// </summary>
    private static MppClientChallenge? TryParseModern(string remainder, string method, string requestEncoded)
    {
        // intent="charge" is the only intent this client can pay — anything else
        // (or a missing intent) must not be treated as a payable challenge.
        var intent = AuthParamParser.ExtractQuotedValue(remainder, "intent");
        if (intent == null || !intent.Equals("charge", StringComparison.OrdinalIgnoreCase))
            return null;

        string? invoice = null, amount = null, currency = null, paymentHash = null, network = null;
        try
        {
            var requestJson = Encoding.UTF8.GetString(Base64Url.Decode(requestEncoded));
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("amount", out var amountEl) && amountEl.ValueKind == JsonValueKind.String)
                amount = amountEl.GetString();
            if (root.TryGetProperty("currency", out var currencyEl) && currencyEl.ValueKind == JsonValueKind.String)
                currency = currencyEl.GetString();
            if (root.TryGetProperty("methodDetails", out var detailsEl) && detailsEl.ValueKind == JsonValueKind.Object)
            {
                if (detailsEl.TryGetProperty("invoice", out var invoiceEl) && invoiceEl.ValueKind == JsonValueKind.String)
                    invoice = invoiceEl.GetString();
                if (detailsEl.TryGetProperty("paymentHash", out var hashEl) && hashEl.ValueKind == JsonValueKind.String)
                    paymentHash = hashEl.GetString();
                if (detailsEl.TryGetProperty("network", out var networkEl) && networkEl.ValueKind == JsonValueKind.String)
                    network = networkEl.GetString();
            }
        }
        catch (FormatException)
        {
            return null; // bad base64url
        }
        catch (JsonException)
        {
            return null; // bad JSON
        }

        if (string.IsNullOrEmpty(invoice))
            return null;

        // currency, when present, must be sat (draft-lightning-charge-00).
        if (currency != null && !currency.Equals("sat", StringComparison.OrdinalIgnoreCase))
            return null;

        return new MppClientChallenge
        {
            IsModern = true,
            Invoice = invoice,
            Amount = amount,
            Realm = AuthParamParser.ExtractQuotedValue(remainder, "realm"),
            Id = AuthParamParser.ExtractQuotedValue(remainder, "id"),
            Method = method,
            Intent = intent,
            RequestEncoded = requestEncoded,
            Expires = AuthParamParser.ExtractQuotedValue(remainder, "expires"),
            Digest = AuthParamParser.ExtractQuotedValue(remainder, "digest"),
            Description = AuthParamParser.ExtractQuotedValue(remainder, "description"),
            Opaque = AuthParamParser.ExtractQuotedValue(remainder, "opaque"),
            PaymentHash = paymentHash,
            Network = network
        };
    }

    /// <summary>
    /// True when the challenge's expires timestamp is already past — an expired challenge
    /// must never be paid. No expires means the challenge does not expire; an unparseable
    /// expires fails closed (treated as expired) because this is the money path.
    /// </summary>
    public bool IsExpired(DateTimeOffset? now = null)
    {
        if (string.IsNullOrEmpty(Expires))
            return false;

        if (!DateTimeOffset.TryParse(Expires, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiresAt))
            return true;

        return (now ?? DateTimeOffset.UtcNow) >= expiresAt;
    }

    /// <summary>
    /// Builds the modern draft-00 credential: base64url (no padding) of
    /// <c>{"challenge":{...byte-exact echo...},"payload":{"preimage":"&lt;64 lowercase hex&gt;"}}</c>.
    /// Every spec-defined challenge param received is echoed byte-exact — the encoded
    /// request string is NEVER decoded/re-encoded — while legacy superset extras
    /// (invoice/amount/currency) are unknown params and are never echoed. Credentials are
    /// SINGLE-USE server-side: never cache or replay the returned value.
    /// </summary>
    public string BuildModernCredential(string preimageHex)
    {
        if (!IsModern || RequestEncoded == null)
            throw new InvalidOperationException("Modern credentials can only be built for draft-00 Payment challenges.");

        var challenge = new Dictionary<string, string>();
        if (Id != null) challenge["id"] = Id;
        if (Realm != null) challenge["realm"] = Realm;
        if (Method != null) challenge["method"] = Method;
        if (Intent != null) challenge["intent"] = Intent;
        challenge["request"] = RequestEncoded;
        if (Expires != null) challenge["expires"] = Expires;
        if (Digest != null) challenge["digest"] = Digest;
        if (Opaque != null) challenge["opaque"] = Opaque;
        if (Description != null) challenge["description"] = Description;

        var credential = new Dictionary<string, object>
        {
            ["challenge"] = challenge,
            // Wallets sometimes return uppercase hex; the spec requires lowercase.
            ["payload"] = new Dictionary<string, string> { ["preimage"] = preimageHex.Trim().ToLowerInvariant() }
        };

        return Base64Url.EncodeNoPad(JsonSerializer.SerializeToUtf8Bytes(credential));
    }
}

/// <summary>
/// A Payment-Receipt response header (draft-00), parsed tolerantly: a missing or
/// malformed receipt must never fail a successful payment. Contains only the payment
/// hash (reference) — no preimage — so it is safe to store and surface.
/// </summary>
public record MppPaymentReceipt
{
    public string? ChallengeId { get; init; }
    public string? Method { get; init; }
    public string? Reference { get; init; }
    public string? Status { get; init; }
    public string? Timestamp { get; init; }

    /// <summary>
    /// Parses a Payment-Receipt header value (base64url JSON, padding tolerated).
    /// Returns null on any malformed input — never throws.
    /// </summary>
    public static MppPaymentReceipt? Parse(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return null;

        try
        {
            var json = Encoding.UTF8.GetString(Base64Url.Decode(headerValue.Trim()));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            string? Get(string name) =>
                doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                    ? el.GetString()
                    : null;

            return new MppPaymentReceipt
            {
                ChallengeId = Get("challengeId"),
                Method = Get("method"),
                Reference = Get("reference"),
                Status = Get("status"),
                Timestamp = Get("timestamp")
            };
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Base64url helpers (RFC 4648 §5). Decoding accepts input with or without padding;
/// encoding always emits unpadded output (the draft-00 wire format).
/// </summary>
internal static class Base64Url
{
    public static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    public static string EncodeNoPad(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
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
        // The key must start the string or follow a comma/whitespace delimiter — a quoted
        // VALUE that happens to end with e.g. id= (description="client-id=") must never be
        // mistaken for the id param, or the byte-exact draft-00 credential echo would be
        // corrupted and rejected AFTER the invoice was paid.
        var pattern = $@"(?i)(?<![^,\s]){Regex.Escape(key)}\s*=\s*""([^""]*)""";
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
    /// Prefers L402 when available; falls back to MPP. Within the Payment scheme the
    /// modern draft-00 profile is preferred over the legacy profile (the L402-vs-Payment
    /// order is unchanged). Handles comma-separated challenges within a single header value.
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
                var candidate = MppClientChallenge.Parse(challenge);
                if (candidate != null && (mpp == null || (candidate.IsModern && !mpp.IsModern)))
                    mpp = candidate;
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
    /// Payment-Receipt response header (draft-00), when the server sent one on the paid
    /// retry. Parsed tolerantly — null when absent or malformed. Contains only the
    /// payment hash (reference), never the preimage.
    /// </summary>
    public MppPaymentReceipt? PaymentReceipt { get; init; }

    /// <summary>
    /// The URL that was fetched.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// When the fetch stopped on an unfollowed 3xx redirect, the resolved (absolute)
    /// redirect target the agent should call the tool again with. <c>null</c> otherwise.
    /// Redirects are deliberately NOT auto-followed (see <c>Program.cs</c>): following
    /// them would leak agent-supplied custom headers to a cross-origin target and, on the
    /// L402 path, pay a provider that then drops the L402 header on the host change.
    /// <para/>
    /// KNOWN LIMITATION — a redirect AFTER a paid retry (<see cref="PaidAmountSats"/> &gt; 0)
    /// is still not followed, even to the same host, because re-issuing the request could
    /// trigger a fresh 402 and a second payment. L402 providers SHOULD serve the resource
    /// directly on the paid retry rather than redirecting; when one redirects after payment,
    /// <see cref="L402Token"/> is returned so a well-behaved agent retries the target WITH
    /// the existing token instead of paying again (see the "ALREADY PAID" error message).
    /// </summary>
    public string? RedirectLocation { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static L402FetchResult Succeeded(string url, string content, int statusCode, string? contentType = null, long paidAmountSats = 0, string? l402Token = null, string? protocol = null, MppPaymentReceipt? paymentReceipt = null) =>
        new()
        {
            Success = true,
            Url = url,
            Content = content,
            StatusCode = statusCode,
            ContentType = contentType,
            PaidAmountSats = paidAmountSats,
            L402Token = l402Token,
            Protocol = protocol,
            PaymentReceipt = paymentReceipt
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

    /// <summary>
    /// Creates a result for an unfollowed 3xx redirect: a graceful, actionable failure
    /// carrying the redirect target so the caller can re-invoke the tool with it. No
    /// payment is ever made on this path (the redirect is seen before any 402), and no
    /// agent-supplied header is sent to the redirect host (we do not follow it).
    /// </summary>
    public static L402FetchResult Redirected(string url, int statusCode, string? location) =>
        new()
        {
            Success = false,
            Url = url,
            StatusCode = statusCode,
            RedirectLocation = location,
            ErrorMessage = location != null
                ? $"Resource redirected to {location}. Call this tool again with that URL."
                : $"Resource returned an HTTP {statusCode} redirect with no Location header."
        };
}
