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

    // Client for the registry search ONLY — that URL is operator-controlled
    // (L402_REGISTRY_URL / LIGHTNING_ENABLE_API_URL / the api.lightningenable.com
    // default), NOT the agent-supplied SSRF vector. The agent-supplied manifest fetch
    // must NEVER use this unguarded client; it goes through the DI-provided guarded
    // named client, or — when no factory is injected (e.g. a unit test) — through an
    // inline guarded client from CreateGuardedManifestClient(). There is deliberately
    // no unguarded manifest-fetch path (F-10e follow-up).
    //
    // AllowAutoRedirect = false (parity with Python's follow_redirects=False on the
    // registry client): even though the registry is operator-configured, a
    // compromised / MITM'd / misconfigured registry that answers 302 → 169.254.169.254
    // would otherwise be SILENTLY FOLLOWED by the default handler (AllowAutoRedirect
    // defaults true) — an SSRF pivot and a cross-port parity gap. With auto-follow off a
    // registry 3xx is a first-class response we reject as a "registry returned a
    // redirect" error rather than chasing. We stop at disabling auto-follow (no
    // connect-time SSRF guard here) to match Python and to keep a deliberately
    // operator-configured localhost/private registry usable in dev.
    private static readonly HttpClient SharedClient = new(CreateRegistrySearchHandler())
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders =
        {
            { "Accept", "application/json" },
            { "User-Agent", "LightningEnable-MCP/1.0" }
        }
    };

    /// <summary>
    /// Builds the handler for the registry-search client with <c>AllowAutoRedirect =
    /// false</c> so a registry 3xx is never silently followed (see <see cref="SharedClient"/>).
    /// Exposed <c>internal</c> so a unit test can assert the no-follow posture directly.
    /// </summary>
    internal static SocketsHttpHandler CreateRegistrySearchHandler() => new()
    {
        AllowAutoRedirect = false,
    };

    /// <summary>
    /// Builds a one-off HttpClient carrying the SAME connect-time SSRF guard
    /// (<see cref="SsrfConnectValidator.ConnectAsync"/>, AllowAutoRedirect = false) as the
    /// DI-registered manifest client in <c>Program.cs</c>. Used only on the fallback path
    /// where no <see cref="IHttpClientFactory"/> was injected (unit tests / direct tool
    /// invocation), so that even the fallback manifest fetch is guarded — the agent-supplied
    /// URL never reaches an unguarded client. The caller owns and disposes it.
    /// </summary>
    internal static HttpClient CreateGuardedManifestClient() =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = SsrfConnectValidator.ConnectAsync,
        })
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
        CancellationToken ct, HttpClient? httpClient = null)
    {
        var registryUrl = GetRegistryBaseUrl();
        var queryParams = new List<string> { "pageSize=20" };
        if (!string.IsNullOrWhiteSpace(query))
            queryParams.Add($"q={Uri.EscapeDataString(query)}");
        if (!string.IsNullOrWhiteSpace(category))
            queryParams.Add($"category={Uri.EscapeDataString(category)}");

        var requestUrl = $"{registryUrl}/api/manifests/registry?{string.Join("&", queryParams)}";

        // Production uses the no-auto-redirect SharedClient; tests may inject a stub.
        var response = await (httpClient ?? SharedClient).GetAsync(requestUrl, ct);

        // The registry client does NOT auto-follow (AllowAutoRedirect = false). A 3xx is
        // therefore surfaced here as a clean, explicit error — NEVER silently followed to
        // wherever the Location points (which a compromised/misconfigured registry could
        // aim at an internal/metadata host).
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Registry returned an HTTP {status} redirect. Not following it — the registry endpoint should serve results directly.",
                registry_url = requestUrl,
                hint = "Check the configured registry URL (L402_REGISTRY_URL / LIGHTNING_ENABLE_API_URL), or use discover_api(url=...) to fetch a specific manifest directly."
            });
        }

        if (!response.IsSuccessStatusCode)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Registry search failed with status {status}.",
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

        // Guarded client for the agent-supplied fetch. Prefer the DI-registered guarded
        // named client; when no factory was injected (unit test / direct invocation) build
        // an inline guarded client — NEVER the unguarded SharedClient. `ownedClient` is
        // non-null only when we created it, so only then do we dispose it (a factory client
        // is owned by the factory).
        HttpClient? ownedClient = httpClientFactory == null ? CreateGuardedManifestClient() : null;
        var client = httpClientFactory?.CreateClient(ManifestHttpClientName) ?? ownedClient!;

        try
        {
            // Try to fetch manifest from well-known locations
            var (manifestJson, manifestUrl, redirectLocation) = await FetchManifestAsync(client, url, cancellationToken);
            if (manifestJson == null)
            {
                // A 3xx redirect (not followed — AllowAutoRedirect = false) on the USER's
                // explicit url argument is surfaced as actionable rather than a generic
                // "not found", so the agent can re-call with the target. We never follow it
                // here (header-leak / redirect-hop reasons). A redirect seen only on a
                // synthesized /.well-known/ probe is NOT promoted (see FetchManifestAsync) —
                // it flows to the not-found + tried_urls result below.
                if (redirectLocation != null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Resource redirected to {redirectLocation}. Call this tool again with that URL.",
                        redirect_location = redirectLocation
                    });
                }

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
        finally
        {
            // Dispose only the client we created inline; a factory client is owned by the factory.
            ownedClient?.Dispose();
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

    private static async Task<(string? Json, string? Url, string? Redirect)> FetchManifestAsync(
        HttpClient client, string url, CancellationToken ct)
    {
        var baseUrl = url.TrimEnd('/');
        var endsInJson = baseUrl.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        // ONLY a 3xx on the exact user-supplied URL (`baseUrl`) is surfaced as an
        // actionable redirect. A redirect on one of our SYNTHESIZED /.well-known/ probe
        // paths is NOT the user's URL — promoting it would send the agent chasing a
        // catch-all/login page and suppress the honest "no manifest here, tried_urls"
        // result. So a well-known-probe redirect is treated as "no manifest at this path"
        // (recorded via GetTriedUrls) and we continue to the next probe.
        string? primaryRedirect = null;

        // If URL ends in .json, try it directly first
        if (endsInJson)
        {
            var (json, redirect) = await TryFetchAsync(client, baseUrl, ct);
            if (json != null) return (json, baseUrl, null);
            primaryRedirect ??= redirect;
        }

        // Try well-known paths. A redirect here is deliberately discarded (see above).
        foreach (var path in WellKnownPaths)
        {
            var (json, _) = await TryFetchAsync(client, baseUrl + path, ct);
            if (json != null) return (json, baseUrl + path, null);
        }

        // Try the URL directly if not already tried
        if (!endsInJson)
        {
            var (json, redirect) = await TryFetchAsync(client, baseUrl, ct);
            if (json != null) return (json, baseUrl, null);
            primaryRedirect ??= redirect;
        }

        return (null, null, primaryRedirect);
    }

    /// <summary>
    /// Fetches one candidate URL. Returns the manifest JSON when the response is a valid
    /// manifest; a resolved redirect target (2nd tuple slot) when the response is an
    /// unfollowed 3xx with a Location (AllowAutoRedirect = false — we surface it rather
    /// than pivot through it); or (null, null) otherwise.
    /// </summary>
    private static async Task<(string? Json, string? Redirect)> TryFetchAsync(
        HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            var response = await client.GetAsync(url, ct);

            // Unfollowed 3xx with a Location: surface the (resolved absolute) target as
            // actionable instead of silently failing over to the next candidate. Uses the
            // shared RedirectResolver so the resolution rules (304 excluded, relative
            // resolved against the request URI) match the L402 fetch path exactly.
            if (RedirectResolver.TryResolve(response, url, out var redirect))
            {
                return (null, redirect);
            }

            if (!response.IsSuccessStatusCode) return (null, null);

            var content = await response.Content.ReadAsStringAsync(ct);

            // Quick validation: must be JSON with expected structure
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("endpoints", out _) ||
                doc.RootElement.TryGetProperty("l402", out _) ||
                doc.RootElement.TryGetProperty("service", out _))
            {
                return (content, null);
            }

            return (null, null);
        }
        catch
        {
            return (null, null);
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
