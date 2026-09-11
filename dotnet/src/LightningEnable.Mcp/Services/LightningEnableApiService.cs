using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Service for calling the Lightning Enable API to create L402 challenges and verify payments.
/// Used by merchants/producers who want AI agents to charge other agents for access.
/// </summary>
public interface ILightningEnableApiService
{
    /// <summary>
    /// Whether the service is configured with an API key.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// The Lightning Enable API this service talks to, with no trailing slash. Used to turn
    /// the proxy-relative paths the API returns into URLs an agent can actually call.
    /// </summary>
    string BaseUrl { get; }

    /// <summary>
    /// Creates an L402 challenge (invoice + macaroon) for a resource.
    /// </summary>
    Task<CreateChallengeResult> CreateChallengeAsync(string resource, long priceSats, string? description, CancellationToken ct);

    /// <summary>
    /// Verifies an L402 token (macaroon + preimage) to confirm payment was made.
    /// </summary>
    Task<VerifyTokenResult> VerifyTokenAsync(string macaroon, string preimage, CancellationToken ct);

    // ── Producer setup ───────────────────────────────────────────────────────
    // Everything a merchant has to do BEFORE minting a challenge is worth anything.
    // Each maps to one route on the Lightning Enable API and answers with the shared
    // ApiCallResult so the tool layer surfaces the API's own error members verbatim.

    /// <summary>
    /// <c>PUT /api/merchant/nwc-connection</c> — stores the wallet that RECEIVES L402
    /// payments. The argument is a live wallet credential: sent, never returned, never
    /// logged, never placed in an error message.
    /// </summary>
    Task<ApiCallResult> SaveNwcConnectionAsync(string nwcConnectionString, CancellationToken ct);

    /// <summary><c>PUT /api/merchant/payment-provider</c> — the lane invoices are minted on.</summary>
    Task<ApiCallResult> SetPaymentProviderAsync(string provider, CancellationToken ct);

    /// <summary><c>GET /api/merchant/me</c> — plan, entitlements and onboarding flags.</summary>
    Task<ApiCallResult> GetMerchantAccountAsync(CancellationToken ct);

    /// <summary><c>GET /api/merchant/quickstart</c> — the onboarding checklist, when present.</summary>
    Task<ApiCallResult> GetQuickStartAsync(CancellationToken ct);

    /// <summary><c>GET /api/l402/challenges</c> — this merchant's minted challenges.</summary>
    Task<ApiCallResult> ListChallengesAsync(string? status, int limit, int offset, CancellationToken ct);

    /// <summary><c>POST /api/proxy</c> — put an upstream API behind L402.</summary>
    Task<ApiCallResult> CreateProxyAsync(
        string name, string targetBaseUrl, string? description, int defaultPriceSats, CancellationToken ct);

    /// <summary>
    /// <c>PUT /api/proxy/{proxyId}</c> — set the proxy name. The manifest's
    /// <c>service.name</c> IS the proxy's name, so publishing under a different service
    /// name is a proxy update, not a manifest-settings field.
    /// </summary>
    Task<ApiCallResult> RenameProxyAsync(string proxyId, string name, CancellationToken ct);

    /// <summary><c>POST /api/proxy/{proxyId}/manifest/endpoints</c> — price one route.</summary>
    Task<ApiCallResult> CreateManifestEndpointAsync(
        string proxyId, string endpointId, string path, string httpMethod, string? summary,
        int basePriceSats, CancellationToken ct);

    /// <summary>
    /// <c>PUT /api/proxy/{proxyId}/manifest/settings</c> — enable the manifest and list it
    /// publicly.
    /// </summary>
    Task<ApiCallResult> UpdateManifestSettingsAsync(
        string proxyId, string? serviceDescription, IReadOnlyList<string>? categories, CancellationToken ct);
}

/// <summary>
/// One Lightning Enable API call, as the producer-setup tools see it.
/// </summary>
/// <remarks>
/// <para>
/// On failure this carries the API's OWN error members. The API answers in RFC 9457
/// <c>application/problem+json</c> with <c>type</c>/<c>title</c>/<c>detail</c> ALONGSIDE the
/// legacy <c>error</c>/<c>message</c> members, and an agent needs both halves: the prose to
/// act on, and the stable slug to branch on. The API key is a request header and is never
/// part of this.
/// </para>
/// <para>
/// <see cref="Data"/> is a detached <see cref="JsonNode"/> rather than a
/// <c>JsonElement</c> on purpose — a <c>JsonElement</c> is only valid while its owning
/// <c>JsonDocument</c> is alive, which it would not be by the time a caller reads it.
/// </para>
/// </remarks>
public sealed record ApiCallResult
{
    /// <summary>Whether the API answered 2xx.</summary>
    public bool Success { get; init; }

    /// <summary>The parsed success body, when there was one.</summary>
    public JsonObject? Data { get; init; }

    /// <summary>Human-readable failure text, taken from the API's own body.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>RFC 9457 <c>type</c> URI — a stable identifier, not a fetchable endpoint.</summary>
    public string? ErrorType { get; init; }

    /// <summary>The legacy <c>error</c> slug, e.g. <c>plan_proxy_limit</c>.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The HTTP status, when the call reached the API at all.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>
    /// ASP.NET model-validation failures, keyed by field. Without these an agent only sees
    /// "One or more validation errors occurred" and cannot tell which field it got wrong.
    /// </summary>
    public JsonObject? ValidationErrors { get; init; }

    /// <summary>A transport-level failure, with no HTTP status to report.</summary>
    public static ApiCallResult Failed(string message) =>
        new() { Success = false, ErrorMessage = message };
}

/// <summary>
/// Result of creating an L402 challenge.
/// </summary>
public record CreateChallengeResult
{
    public bool Success { get; init; }
    public string? Invoice { get; init; }
    public string? Macaroon { get; init; }
    public string? PaymentHash { get; init; }
    public string? ExpiresAt { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of verifying an L402 token.
/// </summary>
public record VerifyTokenResult
{
    public bool Success { get; init; }
    public bool Valid { get; init; }
    public string? Resource { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Implementation that calls the Lightning Enable API for L402 producer operations.
/// </summary>
public class LightningEnableApiService : ILightningEnableApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly string _baseUrl;

    public LightningEnableApiService(HttpClient httpClient, IBudgetConfigurationService configService)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "LightningEnable-MCP/1.0");

        // Read API key: env var → config file
        _apiKey = Environment.GetEnvironmentVariable("LIGHTNING_ENABLE_API_KEY");
        if (string.IsNullOrEmpty(_apiKey) || _apiKey.StartsWith("${"))
            _apiKey = configService.Configuration?.LightningEnableApiKey;

        // Read API URL: env var → default
        _baseUrl = Environment.GetEnvironmentVariable("LIGHTNING_ENABLE_API_URL")?.TrimEnd('/')
            ?? "https://api.lightningenable.com";

        if (!string.IsNullOrEmpty(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
        }
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    /// <inheritdoc />
    public string BaseUrl => _baseUrl;

    public async Task<CreateChallengeResult> CreateChallengeAsync(string resource, long priceSats, string? description, CancellationToken ct)
    {
        var requestBody = new
        {
            resource,
            priceSats,
            description
        };

        var content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        try
        {
            var response = await _httpClient.PostAsync($"{_baseUrl}/api/l402/challenges", content, ct);

            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = $"API returned {(int)response.StatusCode}";
                try
                {
                    using var errorDoc = JsonDocument.Parse(responseBody);
                    if (errorDoc.RootElement.TryGetProperty("message", out var msg))
                        errorMessage = msg.GetString() ?? errorMessage;
                    else if (errorDoc.RootElement.TryGetProperty("error", out var err))
                        errorMessage = err.GetString() ?? errorMessage;
                }
                catch { /* use default error message */ }

                return new CreateChallengeResult { Success = false, ErrorMessage = errorMessage };
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            return new CreateChallengeResult
            {
                Success = true,
                Invoice = root.TryGetProperty("invoice", out var inv) ? inv.GetString() : null,
                Macaroon = root.TryGetProperty("macaroon", out var mac) ? mac.GetString() : null,
                PaymentHash = root.TryGetProperty("paymentHash", out var ph) ? ph.GetString() : null,
                ExpiresAt = root.TryGetProperty("expiresAt", out var exp) ? exp.GetString() : null
            };
        }
        catch (TaskCanceledException)
        {
            return new CreateChallengeResult { Success = false, ErrorMessage = "Request timed out" };
        }
        catch (HttpRequestException ex)
        {
            return new CreateChallengeResult { Success = false, ErrorMessage = $"HTTP error: {ex.Message}" };
        }
    }

    public async Task<VerifyTokenResult> VerifyTokenAsync(string macaroon, string preimage, CancellationToken ct)
    {
        var requestBody = new
        {
            macaroon,
            preimage
        };

        var content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        try
        {
            var response = await _httpClient.PostAsync($"{_baseUrl}/api/l402/challenges/verify", content, ct);

            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = $"API returned {(int)response.StatusCode}";
                try
                {
                    using var errorDoc = JsonDocument.Parse(responseBody);
                    if (errorDoc.RootElement.TryGetProperty("message", out var msg))
                        errorMessage = msg.GetString() ?? errorMessage;
                    else if (errorDoc.RootElement.TryGetProperty("error", out var err))
                        errorMessage = err.GetString() ?? errorMessage;
                }
                catch { /* use default error message */ }

                return new VerifyTokenResult { Success = false, ErrorMessage = errorMessage };
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            return new VerifyTokenResult
            {
                Success = true,
                Valid = root.TryGetProperty("valid", out var valid) && valid.GetBoolean(),
                Resource = root.TryGetProperty("resource", out var res) ? res.GetString() : null
            };
        }
        catch (TaskCanceledException)
        {
            return new VerifyTokenResult { Success = false, ErrorMessage = "Request timed out" };
        }
        catch (HttpRequestException ex)
        {
            return new VerifyTokenResult { Success = false, ErrorMessage = $"HTTP error: {ex.Message}" };
        }
    }

    // ── Producer setup ───────────────────────────────────────────────────────
    // The two methods above keep their own narrower error handling so their published
    // result shape cannot shift. Everything below goes through SendAsync, which is
    // RFC 9457-aware.

    /// <inheritdoc />
    public Task<ApiCallResult> SaveNwcConnectionAsync(string nwcConnectionString, CancellationToken ct)
        => SendAsync(HttpMethod.Put, "/api/merchant/nwc-connection",
            new { nwcConnectionString }, ct);

    /// <inheritdoc />
    public Task<ApiCallResult> SetPaymentProviderAsync(string provider, CancellationToken ct)
        => SendAsync(HttpMethod.Put, "/api/merchant/payment-provider", new { provider }, ct);

    /// <inheritdoc />
    public Task<ApiCallResult> GetMerchantAccountAsync(CancellationToken ct)
        => SendAsync(HttpMethod.Get, "/api/merchant/me", null, ct);

    /// <inheritdoc />
    public Task<ApiCallResult> GetQuickStartAsync(CancellationToken ct)
        => SendAsync(HttpMethod.Get, "/api/merchant/quickstart", null, ct);

    /// <inheritdoc />
    public Task<ApiCallResult> ListChallengesAsync(string? status, int limit, int offset, CancellationToken ct)
    {
        var query = $"?limit={limit}&offset={offset}";
        if (!string.IsNullOrWhiteSpace(status))
        {
            query += $"&status={Uri.EscapeDataString(status)}";
        }

        return SendAsync(HttpMethod.Get, $"/api/l402/challenges{query}", null, ct);
    }

    /// <inheritdoc />
    public Task<ApiCallResult> CreateProxyAsync(
        string name, string targetBaseUrl, string? description, int defaultPriceSats, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["targetBaseUrl"] = targetBaseUrl,
            ["defaultPriceSats"] = defaultPriceSats,
        };
        if (!string.IsNullOrWhiteSpace(description))
        {
            body["description"] = description;
        }

        return SendAsync(HttpMethod.Post, "/api/proxy", body, ct);
    }

    /// <inheritdoc />
    public Task<ApiCallResult> RenameProxyAsync(string proxyId, string name, CancellationToken ct)
        => SendAsync(HttpMethod.Put, $"/api/proxy/{Uri.EscapeDataString(proxyId)}", new { name }, ct);

    /// <inheritdoc />
    public Task<ApiCallResult> CreateManifestEndpointAsync(
        string proxyId, string endpointId, string path, string httpMethod, string? summary,
        int basePriceSats, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["endpointId"] = endpointId,
            ["path"] = path,
            ["httpMethod"] = httpMethod,
            ["basePriceSats"] = basePriceSats,
        };
        if (!string.IsNullOrWhiteSpace(summary))
        {
            body["summary"] = summary;
        }

        return SendAsync(
            HttpMethod.Post,
            $"/api/proxy/{Uri.EscapeDataString(proxyId)}/manifest/endpoints",
            body,
            ct);
    }

    /// <inheritdoc />
    public Task<ApiCallResult> UpdateManifestSettingsAsync(
        string proxyId, string? serviceDescription, IReadOnlyList<string>? categories, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["manifestEnabled"] = true,
            ["manifestPubliclyListed"] = true,
        };
        if (!string.IsNullOrWhiteSpace(serviceDescription))
        {
            body["serviceDescription"] = serviceDescription;
        }
        if (categories is { Count: > 0 })
        {
            body["categories"] = categories;
        }

        return SendAsync(
            HttpMethod.Put,
            $"/api/proxy/{Uri.EscapeDataString(proxyId)}/manifest/settings",
            body,
            ct);
    }

    /// <summary>
    /// One producer-setup API call. Never throws at the caller and never carries the API
    /// key: the key rides in a default request header set once in the constructor.
    /// </summary>
    private async Task<ApiCallResult> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return ApiCallResult.Failed(ProducerApiKeyRequired);
        }

        using var request = new HttpRequestMessage(method, $"{_baseUrl}{path}");
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        string responseBody;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
            responseBody = await response.Content.ReadAsStringAsync(ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ApiCallResult.Failed($"Request timed out calling {method} {path}");
        }
        catch (HttpRequestException ex)
        {
            return ApiCallResult.Failed($"HTTP error calling {method} {path}: {ex.Message}");
        }

        using (response)
        {
            JsonObject? parsed = null;
            try
            {
                parsed = JsonNode.Parse(responseBody) as JsonObject;
            }
            catch (JsonException)
            {
                // A non-JSON body is still a usable outcome: the status code decides, and
                // the error path below falls back to naming the status.
            }

            if (!response.IsSuccessStatusCode)
            {
                return ProblemFrom(parsed, (int)response.StatusCode);
            }

            return new ApiCallResult
            {
                Success = true,
                Data = parsed,
                HttpStatus = (int)response.StatusCode,
            };
        }
    }

    /// <summary>
    /// The API's own error members, as a result an agent can act on.
    /// </summary>
    /// <remarks>
    /// Prefers RFC 9457 <c>detail</c> (the prose about THIS occurrence), then the legacy
    /// <c>message</c>/<c>error</c>, then <c>title</c>. <c>type</c> and the <c>error</c> slug
    /// are carried separately so a caller can branch on a stable identifier instead of prose.
    /// </remarks>
    private static ApiCallResult ProblemFrom(JsonObject? body, int status)
    {
        var message = $"API returned {status}";
        string? type = null;
        string? code = null;
        JsonObject? validation = null;

        if (body is not null)
        {
            foreach (var key in new[] { "detail", "message", "error", "title" })
            {
                var value = ReadString(body, key);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    message = value;
                    break;
                }
            }

            type = ReadString(body, "type");
            code = ReadString(body, "error");
            validation = body["errors"] as JsonObject;
        }

        return new ApiCallResult
        {
            Success = false,
            ErrorMessage = message,
            ErrorType = type,
            ErrorCode = code,
            HttpStatus = status,
            ValidationErrors = validation is null ? null : (JsonObject)validation.DeepClone(),
        };
    }

    private static string? ReadString(JsonObject body, string key)
        => body[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text)
            ? text
            : null;

    /// <summary>
    /// Shown when a producer call is made without a merchant API key. Names both places the
    /// key can live and both ways to get one, because an agent that hits this has no other
    /// route forward.
    /// </summary>
    internal const string ProducerApiKeyRequired =
        "Lightning Enable API key not configured. "
        + "Set LIGHTNING_ENABLE_API_KEY environment variable or add 'lightningEnableApiKey' to "
        + "~/.lightning-enable/config.json. "
        + "Requires an Agentic Commerce subscription at https://lightningenable.com. "
        + "Get an API key: 30-day free trial at "
        + "https://api.lightningenable.com/Checkout?plan=individual&utm_source=mcp&utm_medium=tool-hint&utm_campaign=gtm-aug-2026 "
        + "— or call the `create_lightning_enable_account` tool to sign up right here.";
}
