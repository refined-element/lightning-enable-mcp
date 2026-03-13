using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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
    /// Creates an L402 challenge (invoice + macaroon) for a resource.
    /// </summary>
    Task<CreateChallengeResult> CreateChallengeAsync(string resource, long priceSats, string? description, CancellationToken ct);

    /// <summary>
    /// Verifies an L402 token (macaroon + preimage) to confirm payment was made.
    /// </summary>
    Task<VerifyTokenResult> VerifyTokenAsync(string macaroon, string preimage, CancellationToken ct);
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
}
