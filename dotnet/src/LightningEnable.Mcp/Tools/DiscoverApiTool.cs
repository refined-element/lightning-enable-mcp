using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for discovering L402-enabled API endpoints from a manifest.
/// Fetches and parses the L402 manifest from well-known locations,
/// optionally annotating endpoints with budget-aware affordability info.
/// </summary>
[McpServerToolType]
public static class DiscoverApiTool
{
    private static readonly string[] WellKnownPaths =
    {
        "/.well-known/l402-manifest.json",
        "/l402-manifest.json",
        "/l402.json"
    };

    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders =
        {
            { "Accept", "application/json" },
            { "User-Agent", "LightningEnable-MCP/1.0" }
        }
    };

    /// <summary>
    /// Discovers L402-enabled API endpoints from a manifest URL.
    /// </summary>
    /// <param name="url">Base URL of the L402-enabled API, or direct URL to the manifest JSON.</param>
    /// <param name="budgetAware">If true, annotates endpoints with affordable call counts based on remaining budget.</param>
    /// <param name="budgetService">Injected budget service for affordability calculations.</param>
    /// <param name="priceService">Injected price service for USD conversion.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON with service info, capabilities, endpoints, and optional budget annotations.</returns>
    [McpServerTool(Name = "discover_api"), Description(
        "Discover L402-enabled API endpoints from a manifest. " +
        "Pass the base URL of an L402 API to find all available endpoints, their pricing, and capabilities. " +
        "With budget_aware=true, shows how many calls you can afford with your remaining budget.")]
    public static async Task<string> DiscoverApi(
        [Description("Base URL of the L402-enabled API (e.g., 'https://api.example.com/l402/proxy/my-api'), or direct URL to the manifest JSON file.")] string url,
        [Description("If true, annotate endpoints with affordable call counts based on remaining budget. Default: true.")] bool budgetAware = true,
        IBudgetService? budgetService = null,
        IPriceService? priceService = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Try to fetch manifest from well-known locations
            var (manifestJson, manifestUrl) = await FetchManifestAsync(url, cancellationToken);
            if (manifestJson == null)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Could not find an L402 manifest at the given URL or any well-known locations.",
                    tried_urls = GetTriedUrls(url),
                    hint = "The API may not have an L402 manifest enabled. Try the URL with /.well-known/l402-manifest.json appended."
                });
            }

            // Parse the manifest
            using var doc = JsonDocument.Parse(manifestJson);
            var root = doc.RootElement;

            // Extract service info
            var serviceInfo = ExtractServiceInfo(root);
            var l402Info = ExtractL402Info(root);
            var endpoints = ExtractEndpoints(root);

            // Budget annotations
            object? budgetInfo = null;
            if (budgetAware && budgetService != null)
            {
                var config = budgetService.GetConfig();
                var remainingSats = config.RemainingSessionBudget;

                decimal? btcPrice = null;
                if (priceService != null)
                {
                    try { btcPrice = await priceService.GetBtcPriceAsync(cancellationToken); }
                    catch { /* price unavailable, skip USD conversion */ }
                }

                // Annotate endpoints with affordability
                foreach (var endpoint in endpoints)
                {
                    if (endpoint.TryGetValue("pricing", out var pricingObj) &&
                        pricingObj is Dictionary<string, object?> pricing &&
                        pricing.TryGetValue("base_price_sats", out var priceObj))
                    {
                        var basePriceSats = Convert.ToInt64(priceObj);
                        if (basePriceSats > 0)
                        {
                            endpoint["affordable_calls"] = remainingSats / basePriceSats;
                            if (btcPrice.HasValue && btcPrice.Value > 0)
                            {
                                var costUsd = (decimal)basePriceSats / 100_000_000m * btcPrice.Value;
                                endpoint["cost_usd"] = Math.Round(costUsd, 6);
                            }
                        }
                        else
                        {
                            endpoint["affordable_calls"] = "unlimited";
                        }
                    }
                }

                budgetInfo = new
                {
                    remaining_sats = remainingSats,
                    session_limit_sats = config.MaxSatsPerSession,
                    session_spent_sats = config.SessionSpent,
                    remaining_usd = btcPrice.HasValue && btcPrice.Value > 0
                        ? Math.Round((decimal)remainingSats / 100_000_000m * btcPrice.Value, 4)
                        : (decimal?)null
                };
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                manifest_url = manifestUrl,
                service = serviceInfo,
                l402 = l402Info,
                endpoints,
                budget = budgetInfo,
                endpoint_count = endpoints.Count
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Failed to parse manifest JSON: {ex.Message}"
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Error discovering API: {ex.Message}"
            });
        }
    }

    private static async Task<(string? Json, string? Url)> FetchManifestAsync(
        string url, CancellationToken ct)
    {
        var baseUrl = url.TrimEnd('/');

        // If URL ends in .json, try it directly first
        if (baseUrl.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var json = await TryFetchAsync(baseUrl, ct);
            if (json != null) return (json, baseUrl);
        }

        // Try well-known paths
        foreach (var path in WellKnownPaths)
        {
            var fullUrl = baseUrl + path;
            var json = await TryFetchAsync(fullUrl, ct);
            if (json != null) return (json, fullUrl);
        }

        // Try the URL directly if not already tried
        if (!baseUrl.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var json = await TryFetchAsync(baseUrl, ct);
            if (json != null) return (json, baseUrl);
        }

        return (null, null);
    }

    private static async Task<string?> TryFetchAsync(string url, CancellationToken ct)
    {
        try
        {
            var response = await SharedClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync(ct);

            // Quick validation: must be JSON with expected structure
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("endpoints", out _) ||
                doc.RootElement.TryGetProperty("l402", out _) ||
                doc.RootElement.TryGetProperty("service", out _))
            {
                return content;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> GetTriedUrls(string url)
    {
        var baseUrl = url.TrimEnd('/');
        var urls = new List<string>();

        if (baseUrl.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            urls.Add(baseUrl);

        foreach (var path in WellKnownPaths)
            urls.Add(baseUrl + path);

        if (!baseUrl.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            urls.Add(baseUrl);

        return urls;
    }

    private static Dictionary<string, object?> ExtractServiceInfo(JsonElement root)
    {
        var info = new Dictionary<string, object?>();
        if (root.TryGetProperty("service", out var service))
        {
            if (service.TryGetProperty("name", out var name)) info["name"] = name.GetString();
            if (service.TryGetProperty("description", out var desc)) info["description"] = desc.GetString();
            if (service.TryGetProperty("base_url", out var baseUrl)) info["base_url"] = baseUrl.GetString();
            if (service.TryGetProperty("documentation_url", out var docUrl)) info["documentation_url"] = docUrl.GetString();
            if (service.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
            {
                info["categories"] = cats.EnumerateArray().Select(c => c.GetString()).ToList();
            }
        }
        return info;
    }

    private static Dictionary<string, object?> ExtractL402Info(JsonElement root)
    {
        var info = new Dictionary<string, object?>();
        if (root.TryGetProperty("l402", out var l402))
        {
            if (l402.TryGetProperty("default_price_sats", out var price))
                info["default_price_sats"] = price.GetInt32();
            if (l402.TryGetProperty("payment_flow", out var flow))
                info["payment_flow"] = flow.GetString();
            if (l402.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object)
            {
                var capsDict = new Dictionary<string, object?>();
                if (caps.TryGetProperty("preimage_in_response", out var preimage))
                    capsDict["preimage_in_response"] = preimage.GetBoolean();
                if (caps.TryGetProperty("supported_currencies", out var currencies) &&
                    currencies.ValueKind == JsonValueKind.Array)
                {
                    capsDict["supported_currencies"] = currencies.EnumerateArray()
                        .Select(c => c.GetString()).ToList();
                }
                info["capabilities"] = capsDict;
            }
        }
        return info;
    }

    private static List<Dictionary<string, object?>> ExtractEndpoints(JsonElement root)
    {
        var endpoints = new List<Dictionary<string, object?>>();
        if (!root.TryGetProperty("endpoints", out var endpointsArray) ||
            endpointsArray.ValueKind != JsonValueKind.Array)
            return endpoints;

        foreach (var ep in endpointsArray.EnumerateArray())
        {
            var endpoint = new Dictionary<string, object?>();

            if (ep.TryGetProperty("id", out var id)) endpoint["id"] = id.GetString();
            if (ep.TryGetProperty("path", out var path)) endpoint["path"] = path.GetString();
            if (ep.TryGetProperty("method", out var method)) endpoint["method"] = method.GetString();
            if (ep.TryGetProperty("summary", out var summary)) endpoint["summary"] = summary.GetString();
            if (ep.TryGetProperty("description", out var desc)) endpoint["description"] = desc.GetString();
            if (ep.TryGetProperty("l402_enabled", out var l402Enabled)) endpoint["l402_enabled"] = l402Enabled.GetBoolean();

            if (ep.TryGetProperty("pricing", out var pricing) && pricing.ValueKind == JsonValueKind.Object)
            {
                var pricingDict = new Dictionary<string, object?>();
                if (pricing.TryGetProperty("model", out var model)) pricingDict["model"] = model.GetString();
                if (pricing.TryGetProperty("base_price_sats", out var basePriceProp))
                    pricingDict["base_price_sats"] = basePriceProp.GetInt64();
                endpoint["pricing"] = pricingDict;
            }

            if (ep.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                endpoint["tags"] = tags.EnumerateArray().Select(t => t.GetString()).ToList();
            }

            if (ep.TryGetProperty("deprecated", out var deprecated) && deprecated.GetBoolean())
                endpoint["deprecated"] = true;

            endpoints.Add(endpoint);
        }

        return endpoints;
    }
}
