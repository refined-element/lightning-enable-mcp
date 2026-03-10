using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for discovering L402-enabled API endpoints.
/// Supports two modes:
/// 1. Registry search: query the L402 API registry by keyword/category
/// 2. Manifest fetch: fetch a specific API's manifest from well-known locations
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
    /// Discovers L402-enabled API endpoints by searching the registry or fetching a manifest.
    /// </summary>
    [McpServerTool(Name = "discover_api"), Description(
        "Discover L402-enabled APIs. Use 'query' to search the registry for available APIs by keyword, " +
        "or use 'url' to fetch a specific API's manifest with full endpoint details and pricing. " +
        "Use 'category' to browse by category. With budget_aware=true, shows how many calls you can afford.")]
    public static async Task<string> DiscoverApi(
        [Description("Base URL of the L402-enabled API, or direct URL to the manifest JSON file. If omitted, searches the registry instead.")] string? url = null,
        [Description("Search the L402 API registry by keyword (e.g., 'weather', 'ai', 'geocoding').")] string? query = null,
        [Description("Filter registry results by category (e.g., 'ai', 'data', 'finance').")] string? category = null,
        [Description("If true, annotate endpoints with affordable call counts based on remaining budget. Default: true.")] bool budgetAware = true,
        IBudgetService? budgetService = null,
        IPriceService? priceService = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Route: URL provided → fetch manifest (existing behavior)
            if (!string.IsNullOrWhiteSpace(url))
            {
                return await FetchAndFormatManifestAsync(url, budgetAware, budgetService, priceService, cancellationToken);
            }

            // Route: query/category provided → search registry
            if (!string.IsNullOrWhiteSpace(query) || !string.IsNullOrWhiteSpace(category))
            {
                return await SearchRegistryAsync(query, category, budgetAware, budgetService, priceService, cancellationToken);
            }

            // No params → usage error
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Please provide either a 'url' to fetch an API manifest, or a 'query'/'category' to search the registry.",
                examples = new[]
                {
                    new { description = "Search for weather APIs", call = "discover_api(query=\"weather\")" },
                    new { description = "Browse AI category", call = "discover_api(category=\"ai\")" },
                    new { description = "Get full details for a specific API", call = "discover_api(url=\"https://api.example.com\")" }
                }
            }, new JsonSerializerOptions { WriteIndented = true });
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

    /// <summary>
    /// Searches the L402 API registry for available APIs matching the query/category.
    /// </summary>
    internal static async Task<string> SearchRegistryAsync(
        string? query, string? category, bool budgetAware,
        IBudgetService? budgetService, IPriceService? priceService,
        CancellationToken ct)
    {
        var registryUrl = GetRegistryBaseUrl();
        var queryParams = new List<string> { "pageSize=20" };
        if (!string.IsNullOrWhiteSpace(query))
            queryParams.Add($"q={Uri.EscapeDataString(query)}");
        if (!string.IsNullOrWhiteSpace(category))
            queryParams.Add($"category={Uri.EscapeDataString(category)}");

        var requestUrl = $"{registryUrl}/api/manifests/registry?{string.Join("&", queryParams)}";

        var response = await SharedClient.GetAsync(requestUrl, ct);
        if (!response.IsSuccessStatusCode)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Registry search failed with status {(int)response.StatusCode}.",
                registry_url = requestUrl,
                hint = "The L402 API registry may be temporarily unavailable. Try again later or use discover_api(url=...) to fetch a specific manifest directly."
            });
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var items = new List<Dictionary<string, object?>>();
        if (root.TryGetProperty("items", out var itemsArray) && itemsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsArray.EnumerateArray())
            {
                var entry = new Dictionary<string, object?>();
                if (item.TryGetProperty("name", out var name)) entry["name"] = name.GetString();
                if (item.TryGetProperty("description", out var desc)) entry["description"] = desc.GetString();
                if (item.TryGetProperty("parsedCategories", out var cats) && cats.ValueKind == JsonValueKind.Array)
                    entry["categories"] = cats.EnumerateArray().Select(c => c.GetString()).ToList();
                if (item.TryGetProperty("endpointCount", out var epCount)) entry["endpoint_count"] = epCount.GetInt32();
                if (item.TryGetProperty("defaultPriceSats", out var price)) entry["default_price_sats"] = price.GetInt32();
                if (item.TryGetProperty("manifestUrl", out var mUrl)) entry["manifest_url"] = mUrl.GetString();
                if (item.TryGetProperty("proxyBaseUrl", out var pUrl)) entry["proxy_base_url"] = pUrl.GetString();
                if (item.TryGetProperty("documentationUrl", out var docUrl)) entry["documentation_url"] = docUrl.GetString();

                // Budget annotation per result
                if (budgetAware && budgetService != null && entry.ContainsKey("default_price_sats"))
                {
                    var priceSats = Convert.ToInt64(entry["default_price_sats"]);
                    if (priceSats > 0)
                    {
                        var config = budgetService.GetConfig();
                        entry["affordable_calls"] = config.RemainingSessionBudget / priceSats;
                    }
                }

                items.Add(entry);
            }
        }

        var total = root.TryGetProperty("total", out var totalProp) ? totalProp.GetInt32() : items.Count;

        object? budgetInfo = null;
        if (budgetAware && budgetService != null)
        {
            var config = budgetService.GetConfig();
            budgetInfo = new
            {
                remaining_sats = config.RemainingSessionBudget,
                session_limit_sats = config.MaxSatsPerSession,
                session_spent_sats = config.SessionSpent
            };
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            source = "registry",
            query,
            category,
            results = items,
            total,
            budget = budgetInfo,
            hint = items.Count > 0
                ? "Call discover_api(url=\"<manifest_url>\") for full endpoint details and pricing of a specific API."
                : "No APIs found. Try different keywords or browse categories."
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string GetRegistryBaseUrl()
    {
        // Check env vars in priority order
        var url = Environment.GetEnvironmentVariable("L402_REGISTRY_URL");
        if (!string.IsNullOrWhiteSpace(url)) return url.TrimEnd('/');

        url = Environment.GetEnvironmentVariable("LIGHTNING_ENABLE_API_URL");
        if (!string.IsNullOrWhiteSpace(url)) return url.TrimEnd('/');

        return "https://api.lightningenable.com";
    }

    /// <summary>
    /// Fetches and formats a manifest from a specific URL (original discover_api behavior).
    /// </summary>
    private static async Task<string> FetchAndFormatManifestAsync(
        string url, bool budgetAware,
        IBudgetService? budgetService, IPriceService? priceService,
        CancellationToken cancellationToken)
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
                source = "manifest",
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
