using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Service for Agent Service Agreement (ASA) operations on the Nostr network.
/// Handles discovery, publishing, negotiation, and settlement of agent capabilities.
///
/// v1 implementation uses the Lightning Enable API for all operations.
/// TODO: Add direct Nostr relay WebSocket support for discovery and publishing.
/// </summary>
public interface IAgentService
{
    /// <summary>
    /// Whether the service is configured with an API key for publishing operations.
    /// Discovery works without an API key via the public registry.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Query the agent capability registry for services matching filters.
    /// </summary>
    Task<AgentDiscoveryResult> DiscoverCapabilitiesAsync(
        string? category, string[]? hashtags, string? query, int limit,
        CancellationToken ct);

    /// <summary>
    /// Publish an agent capability advertisement (kind 38400 event).
    /// Requires API key for authentication.
    /// </summary>
    Task<AgentPublishResult> PublishCapabilityAsync(
        string serviceId, string[] categories, string content,
        int priceSats, string? l402Endpoint, string? targetUrl,
        string[]? hashtags, CancellationToken ct);

    /// <summary>
    /// Send a service request referencing a provider's capability (kind 38401 event).
    /// </summary>
    Task<AgentRequestResult> RequestServiceAsync(
        string capabilityEventId, int budgetSats, string? parameters,
        CancellationToken ct);

    /// <summary>
    /// Publish an attestation/review for an agent (kind 38403 event).
    /// </summary>
    Task<AgentAttestationPublishResult> PublishAttestationAsync(
        string subjectPubkey, string agreementId, int rating, string content,
        string? proof, CancellationToken ct);

    /// <summary>
    /// Query attestations for an agent's reputation.
    /// </summary>
    Task<AgentAttestationQueryResult> GetAttestationsAsync(
        string pubkey, int limit, CancellationToken ct);
}

/// <summary>
/// Result of discovering agent capabilities.
/// </summary>
public record AgentDiscoveryResult
{
    public bool Success { get; init; }
    public List<AgentCapability> Capabilities { get; init; } = new();
    public int Total { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// A single agent capability from discovery.
/// </summary>
public record AgentCapability
{
    public string? EventId { get; init; }
    public string? ServiceId { get; init; }
    public string? Pubkey { get; init; }
    public string? Content { get; init; }
    public List<string> Categories { get; init; } = new();
    public List<string> Hashtags { get; init; } = new();
    public int PriceSats { get; init; }
    public string? L402Endpoint { get; init; }
    public long? CreatedAt { get; init; }
}

/// <summary>
/// Result of publishing an agent capability.
/// </summary>
public record AgentPublishResult
{
    public bool Success { get; init; }
    public string? EventId { get; init; }
    public string? L402Endpoint { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of requesting a service from an agent.
/// </summary>
public record AgentRequestResult
{
    public bool Success { get; init; }
    public string? RequestEventId { get; init; }
    public string? L402Endpoint { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of publishing an agent attestation.
/// </summary>
public record AgentAttestationPublishResult
{
    public bool Success { get; init; }
    public string? EventId { get; init; }
    public string? AttestationId { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of querying attestations for an agent.
/// </summary>
public record AgentAttestationQueryResult
{
    public bool Success { get; init; }
    public List<AgentAttestationRecord> Attestations { get; init; } = new();
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// A single attestation/review record.
/// </summary>
public record AgentAttestationRecord
{
    public string? EventId { get; init; }
    public string? ReviewerPubkey { get; init; }
    public int Rating { get; init; }
    public string? Content { get; init; }
    public string? AgreementId { get; init; }
    public string? Proof { get; init; }
    public long? CreatedAt { get; init; }
}

/// <summary>
/// Implementation that calls the Lightning Enable API for ASA operations.
/// Uses the /api/agents/* endpoints for registration and capability management.
/// </summary>
public class AgentService : IAgentService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly string _baseUrl;
    private readonly string _agentRelayUrl;

    public AgentService(HttpClient httpClient, IBudgetConfigurationService configService)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "LightningEnable-MCP/1.0");

        // Read API key: env var -> config file
        _apiKey = Environment.GetEnvironmentVariable("LIGHTNING_ENABLE_API_KEY");
        if (string.IsNullOrEmpty(_apiKey) || _apiKey.StartsWith("${"))
            _apiKey = configService.Configuration?.LightningEnableApiKey;

        // Read API URL: env var -> default
        _baseUrl = Environment.GetEnvironmentVariable("LIGHTNING_ENABLE_API_URL")?.TrimEnd('/')
            ?? "https://api.lightningenable.com";

        // TODO: _agentRelayUrl is reserved for future direct Nostr relay WebSocket support.
        // Currently all operations go through the Lightning Enable REST API.
        // When direct relay queries are implemented, this URL will be used to connect
        // to the agent relay for publishing and subscribing to capability events.
        _agentRelayUrl = Environment.GetEnvironmentVariable("AGENT_RELAY_URL")
            ?? "wss://agents.lightningenable.com";

        if (!string.IsNullOrEmpty(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
        }
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    public async Task<AgentDiscoveryResult> DiscoverCapabilitiesAsync(
        string? category, string[]? hashtags, string? query, int limit,
        CancellationToken ct)
    {
        try
        {
            var queryParams = new List<string> { $"limit={Math.Min(limit, 100)}" };

            if (!string.IsNullOrWhiteSpace(category))
                queryParams.Add($"category={Uri.EscapeDataString(category)}");
            if (!string.IsNullOrWhiteSpace(query))
                queryParams.Add($"q={Uri.EscapeDataString(query)}");
            if (hashtags != null && hashtags.Length > 0)
                queryParams.Add($"hashtags={Uri.EscapeDataString(string.Join(",", hashtags))}");

            var requestUrl = $"{_baseUrl}/api/agents/capabilities?{string.Join("&", queryParams)}";

            var response = await _httpClient.GetAsync(requestUrl, ct);

            if (!response.IsSuccessStatusCode)
            {
                // Fall back to the manifest registry for discovery
                return await FallbackRegistryDiscoveryAsync(category, query, limit, ct);
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var capabilities = new List<AgentCapability>();

            var itemsProp = root.TryGetProperty("items", out var items) ? items
                : root.TryGetProperty("capabilities", out var caps) ? caps
                : root;

            if (itemsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in itemsProp.EnumerateArray())
                {
                    capabilities.Add(ParseCapability(item));
                }
            }

            var total = root.TryGetProperty("total", out var totalProp)
                ? totalProp.GetInt32()
                : capabilities.Count;

            return new AgentDiscoveryResult
            {
                Success = true,
                Capabilities = capabilities,
                Total = total
            };
        }
        catch (Exception ex)
        {
            return new AgentDiscoveryResult
            {
                Success = false,
                ErrorMessage = $"Discovery failed: {ex.Message}"
            };
        }
    }

    public async Task<AgentPublishResult> PublishCapabilityAsync(
        string serviceId, string[] categories, string content,
        int priceSats, string? l402Endpoint, string? targetUrl,
        string[]? hashtags, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return new AgentPublishResult
            {
                Success = false,
                ErrorMessage = "Lightning Enable API key not configured. " +
                    "Set LIGHTNING_ENABLE_API_KEY environment variable or add 'lightningEnableApiKey' to ~/.lightning-enable/config.json."
            };
        }

        try
        {
            var requestBody = new
            {
                serviceId,
                categories,
                content,
                priceSats,
                l402Endpoint,
                targetUrl,
                hashtags = hashtags ?? Array.Empty<string>()
            };

            var httpContent = new StringContent(
                JsonSerializer.Serialize(requestBody, JsonOptions),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync(
                $"{_baseUrl}/api/agents/capabilities", httpContent, ct);

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

                return new AgentPublishResult { Success = false, ErrorMessage = errorMessage };
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            return new AgentPublishResult
            {
                Success = true,
                EventId = root.TryGetProperty("eventId", out var eid) ? eid.GetString() : null,
                L402Endpoint = root.TryGetProperty("l402Endpoint", out var ep) ? ep.GetString() : l402Endpoint
            };
        }
        catch (TaskCanceledException)
        {
            return new AgentPublishResult { Success = false, ErrorMessage = "Request timed out" };
        }
        catch (HttpRequestException ex)
        {
            return new AgentPublishResult { Success = false, ErrorMessage = $"HTTP error: {ex.Message}" };
        }
    }

    public async Task<AgentRequestResult> RequestServiceAsync(
        string capabilityEventId, int budgetSats, string? parameters,
        CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return new AgentRequestResult
            {
                Success = false,
                ErrorMessage = "Lightning Enable API key not configured. " +
                    "Set LIGHTNING_ENABLE_API_KEY environment variable or add 'lightningEnableApiKey' to ~/.lightning-enable/config.json."
            };
        }

        try
        {
            var requestBody = new
            {
                capabilityEventId,
                budgetSats,
                parameters
            };

            var httpContent = new StringContent(
                JsonSerializer.Serialize(requestBody, JsonOptions),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync(
                $"{_baseUrl}/api/agents/requests", httpContent, ct);

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

                return new AgentRequestResult { Success = false, ErrorMessage = errorMessage };
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            return new AgentRequestResult
            {
                Success = true,
                RequestEventId = root.TryGetProperty("requestEventId", out var rid) ? rid.GetString() : null,
                L402Endpoint = root.TryGetProperty("l402Endpoint", out var ep) ? ep.GetString() : null
            };
        }
        catch (TaskCanceledException)
        {
            return new AgentRequestResult { Success = false, ErrorMessage = "Request timed out" };
        }
        catch (HttpRequestException ex)
        {
            return new AgentRequestResult { Success = false, ErrorMessage = $"HTTP error: {ex.Message}" };
        }
    }

    public async Task<AgentAttestationPublishResult> PublishAttestationAsync(
        string subjectPubkey, string agreementId, int rating, string content,
        string? proof, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return new AgentAttestationPublishResult
            {
                Success = false,
                ErrorMessage = "Lightning Enable API key not configured. " +
                    "Set LIGHTNING_ENABLE_API_KEY environment variable or add 'lightningEnableApiKey' to ~/.lightning-enable/config.json."
            };
        }

        try
        {
            var attestationId = $"att-{agreementId[..Math.Min(16, agreementId.Length)]}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

            var requestBody = new
            {
                subjectPubkey,
                agreementId,
                rating,
                content,
                proof,
                attestationId
            };

            var httpContent = new StringContent(
                JsonSerializer.Serialize(requestBody, JsonOptions),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync(
                $"{_baseUrl}/api/agents/attestations", httpContent, ct);

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

                return new AgentAttestationPublishResult { Success = false, ErrorMessage = errorMessage };
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            return new AgentAttestationPublishResult
            {
                Success = true,
                EventId = root.TryGetProperty("eventId", out var eid) ? eid.GetString() : null,
                AttestationId = attestationId
            };
        }
        catch (TaskCanceledException)
        {
            return new AgentAttestationPublishResult { Success = false, ErrorMessage = "Request timed out" };
        }
        catch (HttpRequestException ex)
        {
            return new AgentAttestationPublishResult { Success = false, ErrorMessage = $"HTTP error: {ex.Message}" };
        }
    }

    public async Task<AgentAttestationQueryResult> GetAttestationsAsync(
        string pubkey, int limit, CancellationToken ct)
    {
        try
        {
            var queryParams = new List<string>
            {
                $"pubkey={Uri.EscapeDataString(pubkey)}",
                $"limit={Math.Min(limit, 100)}"
            };

            var requestUrl = $"{_baseUrl}/api/agents/attestations?{string.Join("&", queryParams)}";
            var response = await _httpClient.GetAsync(requestUrl, ct);

            if (!response.IsSuccessStatusCode)
            {
                return new AgentAttestationQueryResult
                {
                    Success = false,
                    ErrorMessage = $"API returned {(int)response.StatusCode}"
                };
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var attestations = new List<AgentAttestationRecord>();

            var itemsProp = root.TryGetProperty("items", out var items) ? items
                : root.TryGetProperty("attestations", out var atts) ? atts
                : root;

            if (itemsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in itemsProp.EnumerateArray())
                {
                    attestations.Add(new AgentAttestationRecord
                    {
                        EventId = item.TryGetProperty("eventId", out var eid) ? eid.GetString() : null,
                        ReviewerPubkey = item.TryGetProperty("pubkey", out var pk) ? pk.GetString()
                            : item.TryGetProperty("reviewerPubkey", out var rpk) ? rpk.GetString() : null,
                        Rating = item.TryGetProperty("rating", out var r) ? r.GetInt32() : 0,
                        Content = item.TryGetProperty("content", out var c) ? c.GetString() : null,
                        AgreementId = item.TryGetProperty("agreementId", out var aid) ? aid.GetString() : null,
                        Proof = item.TryGetProperty("proof", out var p) ? p.GetString() : null,
                        CreatedAt = item.TryGetProperty("createdAt", out var ca) ? ca.GetInt64() : null
                    });
                }
            }

            return new AgentAttestationQueryResult
            {
                Success = true,
                Attestations = attestations
            };
        }
        catch (Exception ex)
        {
            return new AgentAttestationQueryResult
            {
                Success = false,
                ErrorMessage = $"Query failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Falls back to the manifest registry if the dedicated capabilities endpoint is not available.
    /// This allows discovery to work even before the /api/agents/capabilities endpoint is deployed.
    /// </summary>
    private async Task<AgentDiscoveryResult> FallbackRegistryDiscoveryAsync(
        string? category, string? query, int limit, CancellationToken ct)
    {
        try
        {
            var queryParams = new List<string> { $"pageSize={Math.Min(limit, 100)}" };
            if (!string.IsNullOrWhiteSpace(query))
                queryParams.Add($"q={Uri.EscapeDataString(query)}");
            if (!string.IsNullOrWhiteSpace(category))
                queryParams.Add($"category={Uri.EscapeDataString(category)}");

            var requestUrl = $"{_baseUrl}/api/manifests/registry?{string.Join("&", queryParams)}";
            var response = await _httpClient.GetAsync(requestUrl, ct);

            if (!response.IsSuccessStatusCode)
            {
                return new AgentDiscoveryResult
                {
                    Success = false,
                    ErrorMessage = $"Registry search failed with status {(int)response.StatusCode}. " +
                        "The agent capability registry may be temporarily unavailable."
                };
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var capabilities = new List<AgentCapability>();
            if (root.TryGetProperty("items", out var itemsArray) && itemsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in itemsArray.EnumerateArray())
                {
                    var cap = new AgentCapability
                    {
                        ServiceId = item.TryGetProperty("name", out var name) ? name.GetString() : null,
                        Content = item.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                        Categories = item.TryGetProperty("parsedCategories", out var cats) && cats.ValueKind == JsonValueKind.Array
                            ? cats.EnumerateArray().Select(c => c.GetString() ?? "").Where(s => s != "").ToList()
                            : new List<string>(),
                        PriceSats = item.TryGetProperty("defaultPriceSats", out var price) ? price.GetInt32() : 0,
                        L402Endpoint = item.TryGetProperty("proxyBaseUrl", out var pUrl) ? pUrl.GetString() : null
                    };
                    capabilities.Add(cap);
                }
            }

            var total = root.TryGetProperty("total", out var totalProp) ? totalProp.GetInt32() : capabilities.Count;

            return new AgentDiscoveryResult
            {
                Success = true,
                Capabilities = capabilities,
                Total = total
            };
        }
        catch (Exception ex)
        {
            return new AgentDiscoveryResult
            {
                Success = false,
                ErrorMessage = $"Fallback registry discovery failed: {ex.Message}"
            };
        }
    }

    private static AgentCapability ParseCapability(JsonElement item)
    {
        return new AgentCapability
        {
            EventId = item.TryGetProperty("eventId", out var eid) ? eid.GetString()
                : item.TryGetProperty("id", out var id) ? id.GetString()
                : null,
            ServiceId = item.TryGetProperty("serviceId", out var sid) ? sid.GetString()
                : item.TryGetProperty("dTag", out var dtag) ? dtag.GetString()
                : null,
            Pubkey = item.TryGetProperty("pubkey", out var pk) ? pk.GetString() : null,
            Content = item.TryGetProperty("content", out var content) ? content.GetString() : null,
            Categories = item.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array
                ? cats.EnumerateArray().Select(c => c.GetString() ?? "").Where(s => s != "").ToList()
                : new List<string>(),
            Hashtags = item.TryGetProperty("hashtags", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? tags.EnumerateArray().Select(t => t.GetString() ?? "").Where(s => s != "").ToList()
                : new List<string>(),
            PriceSats = item.TryGetProperty("priceSats", out var price) ? price.GetInt32() : 0,
            L402Endpoint = item.TryGetProperty("l402Endpoint", out var ep) ? ep.GetString() : null,
            CreatedAt = item.TryGetProperty("createdAt", out var ca) ? ca.GetInt64() : null
        };
    }
}
