using System.ComponentModel;
using System.Globalization;
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
    /// <summary>
    /// Named <see cref="IHttpClientFactory"/> client (registered in Program.cs) used
    /// for the agent-supplied manifest fetch. It carries the connect-time
    /// <see cref="Services.SsrfConnectValidator"/> SSRF guard, so discover_api cannot
    /// be used to reach a private/metadata target (F-10e).
    /// </summary>
    public const string ManifestHttpClientName = "DiscoverApi";

    private static readonly string[] WellKnownPaths =
    {
        "/.well-known/l402-manifest.json",
        "/l402-manifest.json",
        "/l402.json"
    };

    // Fallback client for the registry search (operator-controlled URL — NOT the
    // agent-supplied SSRF vector) and for unit tests that invoke the tool without a
    // DI-provided IHttpClientFactory. The manifest-fetch path uses the guarded named
    // client above instead.
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
        IHttpClientFactory? httpClientFactory = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Route: URL provided → fetch manifest (existing behavior)
            if (!string.IsNullOrWhiteSpace(url))
            {
                return await FetchAndFormatManifestAsync(url, budgetAware, budgetService, priceService, httpClientFactory, cancellationToken);
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

        var items = BuildRegistryEntries(root, budgetAware, budgetService);

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

    /// <summary>
    /// Projects the registry's response items into the tool's output shape, optionally
    /// annotating each with how many calls the remaining session budget affords.
    /// </summary>
    internal static List<Dictionary<string, object?>> BuildRegistryEntries(
        JsonElement root, bool budgetAware, IBudgetService? budgetService)
    {
        var items = new List<Dictionary<string, object?>>();
        if (!root.TryGetProperty("items", out var itemsArray) || itemsArray.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var item in itemsArray.EnumerateArray())
        {
            var entry = new Dictionary<string, object?>();
            if (item.TryGetProperty("name", out var name)) entry["name"] = name.GetString();
            if (item.TryGetProperty("description", out var desc)) entry["description"] = desc.GetString();
            if (item.TryGetProperty("parsedCategories", out var cats) && cats.ValueKind == JsonValueKind.Array)
                entry["categories"] = cats.EnumerateArray().Select(c => c.GetString()).ToList();
            if (item.TryGetProperty("endpointCount", out var epCount)) entry["endpoint_count"] = epCount.GetInt32();
            if (item.TryGetProperty("defaultPriceSats", out var price)) entry["default_price_sats"] = ReadPriceValue(price);
            if (item.TryGetProperty("manifestUrl", out var mUrl)) entry["manifest_url"] = mUrl.GetString();
            if (item.TryGetProperty("proxyBaseUrl", out var pUrl)) entry["proxy_base_url"] = pUrl.GetString();
            if (item.TryGetProperty("documentationUrl", out var docUrl)) entry["documentation_url"] = docUrl.GetString();

            // Budget annotation per result. Same rule as the manifest path: a real count only
            // for a positive whole-sats price, otherwise an explicit "unknown" — the registry
            // response is no more trusted than a manifest.
            if (budgetAware && budgetService != null)
            {
                entry.TryGetValue("default_price_sats", out var priceValue);
                entry["affordable_calls"] = TryGetPositiveSats(priceValue, out var priceSats)
                    ? Math.Max(0L, budgetService.GetConfig().RemainingSessionBudget) / priceSats
                    : (object)"unknown";
            }

            items.Add(entry);
        }

        return items;
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
        IHttpClientFactory? httpClientFactory,
        CancellationToken cancellationToken)
    {
        // SSRF pre-check (F-10e): the manifest URL is agent-supplied. Refuse a
        // private/internal/metadata target with a generic (non-echoing) error BEFORE
        // any request. This is the cheap synchronous check; the guarded client's
        // connect-time SsrfConnectValidator is the authoritative IP guard (and covers
        // the /.well-known/ variants and any redirect hops).
        var preCheckError = SsrfUrlGuard.Validate(url);
        if (preCheckError != null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = preCheckError
            });
        }

        // Guarded client for the agent-supplied fetch. Fall back to the shared client
        // only when no factory was injected (e.g. a unit test invoking the tool directly).
        var client = httpClientFactory?.CreateClient(ManifestHttpClientName) ?? SharedClient;

        try
        {
            // Try to fetch manifest from well-known locations
            var (manifestJson, manifestUrl) = await FetchManifestAsync(client, url, cancellationToken);
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
                AnnotateEndpointsWithAffordability(endpoints, remainingSats, btcPrice);

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

    /// <summary>
    /// Reads a price field out of an UNTRUSTED manifest/registry document without throwing on
    /// any JSON shape. The document's raw claim is preserved as-is (a whole number stays a
    /// number; anything else is surfaced verbatim so the caller can see what was actually
    /// published) — deciding whether that claim is a usable price is
    /// <see cref="TryGetPositiveSats"/>'s job, never this method's.
    /// </summary>
    internal static object? ReadPriceValue(JsonElement element) => element.ValueKind switch
    {
        // Fractional and Int64-overflowing numbers fall through to their raw text: still
        // reported to the caller, but not silently truncated into a wrong sats figure.
        JsonValueKind.Number => element.TryGetInt64(out var n) ? n : element.GetRawText(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.GetRawText()
    };

    /// <summary>
    /// Resolves a value read by <see cref="ReadPriceValue"/> to a POSITIVE whole number of sats.
    ///
    /// Returns false — which callers MUST render as "unknown", never "unlimited" and never
    /// "free" — for every shape a hostile document can supply that is not a usable price:
    /// missing, null, zero, negative, non-numeric, fractional, or Int64-overflowing. A manifest
    /// is attacker-authorable, so an unusable price must never widen what the agent believes it
    /// can spend. Never throws.
    /// </summary>
    internal static bool TryGetPositiveSats(object? value, out long sats)
    {
        sats = 0;
        switch (value)
        {
            case long l:
                sats = l;
                break;
            case int i:
                sats = i;
                break;
            case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                // A quoted whole number ("100") is unambiguous and cannot over-claim
                // affordability, so it is accepted rather than discarded as unknown.
                sats = parsed;
                break;
            default:
                // Includes null, bool, objects, arrays, "abc", "1.5", and overflowing numbers.
                return false;
        }

        // Zero or negative is NOT "free" or "unlimited" — it is a broken price claim.
        return sats > 0;
    }

    /// <summary>
    /// Annotates each endpoint with how many calls the remaining session budget affords,
    /// and the USD cost of a single call when a BTC price is available.
    ///
    /// Every endpoint gets an explicit verdict: a real count only when the manifest states a
    /// positive whole-sats price, otherwise "unknown". No endpoint is left unannotated (an
    /// agent could misread silence as free) and none is ever reported as "unlimited".
    /// </summary>
    internal static void AnnotateEndpointsWithAffordability(
        List<Dictionary<string, object?>> endpoints, long remainingSats, decimal? btcPrice)
    {
        foreach (var endpoint in endpoints)
        {
            object? priceObj = null;
            if (endpoint.TryGetValue("pricing", out var pricingObj) &&
                pricingObj is Dictionary<string, object?> pricing)
            {
                pricing.TryGetValue("base_price_sats", out priceObj);
            }

            if (!TryGetPositiveSats(priceObj, out var basePriceSats))
            {
                endpoint["affordable_calls"] = "unknown";
                continue;
            }

            // Clamp the remaining budget: an overspent session affords zero calls, not a
            // negative number of them.
            endpoint["affordable_calls"] = Math.Max(0L, remainingSats) / basePriceSats;

            if (btcPrice.HasValue && btcPrice.Value > 0)
            {
                var costUsd = (decimal)basePriceSats / 100_000_000m * btcPrice.Value;
                endpoint["cost_usd"] = Math.Round(costUsd, 6);
            }
        }
    }

    private static async Task<(string? Json, string? Url)> FetchManifestAsync(
        HttpClient client, string url, CancellationToken ct)
    {
        var baseUrl = url.TrimEnd('/');

        // If URL ends in .json, try it directly first
        if (baseUrl.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var json = await TryFetchAsync(client, baseUrl, ct);
            if (json != null) return (json, baseUrl);
        }

        // Try well-known paths
        foreach (var path in WellKnownPaths)
        {
            var fullUrl = baseUrl + path;
            var json = await TryFetchAsync(client, fullUrl, ct);
            if (json != null) return (json, fullUrl);
        }

        // Try the URL directly if not already tried
        if (!baseUrl.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var json = await TryFetchAsync(client, baseUrl, ct);
            if (json != null) return (json, baseUrl);
        }

        return (null, null);
    }

    private static async Task<string?> TryFetchAsync(HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            var response = await client.GetAsync(url, ct);
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

    /// <summary>
    /// Projects the manifest's service-wide l402 block into the tool's output shape.
    /// </summary>
    internal static Dictionary<string, object?> ExtractL402Info(JsonElement root)
    {
        var info = new Dictionary<string, object?>();
        if (root.TryGetProperty("l402", out var l402))
        {
            if (l402.TryGetProperty("default_price_sats", out var price))
                info["default_price_sats"] = ReadPriceValue(price);
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

    /// <summary>
    /// Projects the manifest's endpoint array into the tool's output shape.
    /// </summary>
    internal static List<Dictionary<string, object?>> ExtractEndpoints(JsonElement root)
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
                // Untrusted document: GetInt64() would throw on any non-numeric shape and take
                // the entire discovery response down with it.
                if (pricing.TryGetProperty("base_price_sats", out var basePriceProp))
                    pricingDict["base_price_sats"] = ReadPriceValue(basePriceProp);
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
