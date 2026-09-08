using System.Text.Json;
using System.Text.Json.Nodes;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// Seller-side setup for <c>l402_producer</c>: API key in, monetized endpoint out.
///
/// <para><c>create</c> and <c>verify</c> handle ONE challenge each. They are only worth
/// calling once the account behind them is set up, and until now that setup was raw REST an
/// agent could not reach: point payouts at a wallet the merchant owns
/// (<c>configure_receive</c>), see where the account stands (<c>status</c>), register an
/// upstream API (<c>create_proxy</c>), price its routes (<c>add_endpoint</c>), list it so
/// other agents can find it (<c>publish</c>), and read back what was minted
/// (<c>list_challenges</c>).</para>
///
/// <para>Two rules run through all of it:</para>
/// <list type="number">
/// <item><description><b>The NWC connection string is never returned.</b> It authorises live
/// calls against the merchant's wallet, so every result says <c>&lt;set&gt;</c> — and the
/// final JSON is scrubbed of the string before it leaves, in case an upstream error body
/// quoted it (engineering standard #5).</description></item>
/// <item><description><b>Errors are surfaced whole.</b> The Lightning Enable API answers in
/// RFC 9457 <c>application/problem+json</c> with <c>type</c>/<c>detail</c> alongside the
/// legacy <c>error</c>/<c>message</c>. An agent gets the prose AND the stable slug, never
/// the API key.</description></item>
/// </list>
///
/// <para>These are plain static methods, not <c>[McpServerTool]</c> methods: they are
/// actions on the existing <c>l402_producer</c> verb, so the advertised tool count does not
/// change. Mirrors <c>python/.../tools/producer_setup.py</c>.</para>
/// </summary>
public static class ProducerSetupTools
{
    /// <summary>
    /// Fixed marker for a stored wallet credential. Never a masked prefix of the real value:
    /// the merchant already has the string in their wallet app, and no surface needs to echo
    /// it back.
    /// </summary>
    internal const string SetMarker = "<set>";

    internal const string UnsetMarker = "<unset>";

    /// <summary>Accepted <c>status</c> filters on <c>GET /api/l402/challenges</c>.</summary>
    internal static readonly string[] ChallengeStatuses = { "paid", "unpaid", "expired" };

    /// <summary>
    /// Fields copied out of a challenge row. An allowlist, not a passthrough: a future API
    /// field must be added here deliberately, so a credential-shaped one cannot ride along.
    /// </summary>
    internal static readonly string[] ChallengeFields =
    {
        "paymentHash", "resource", "amountSats", "status",
        "createdAt", "paidAt", "expiresAt", "idempotencyKey",
    };

    private const string NoApiService = "Lightning Enable API service not available";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // ── configure_receive ────────────────────────────────────────────────────

    /// <summary>
    /// Points the merchant's L402 payouts at a wallet they control: stores the Nostr Wallet
    /// Connect string, then switches the account's payment lane to it.
    /// </summary>
    /// <remarks>
    /// With no argument, reuses the wallet THIS server pays with — but only when that wallet
    /// IS an NWC wallet. An LND / Strike / OpenNode server is refused with a message naming
    /// its wallet rather than sent something that cannot be a receiving connection.
    /// </remarks>
    public static async Task<string> ConfigureReceiveAsync(
        string? nwcConnectionString,
        ILightningEnableApiService? apiService,
        IWalletOnboardingService? onboarding,
        CancellationToken cancellationToken)
    {
        var gate = ApiKeyGate(apiService);
        if (gate is not null)
        {
            return gate;
        }

        var usedMcpWallet = false;
        var source = "the nwcConnectionString argument";

        if (string.IsNullOrWhiteSpace(nwcConnectionString))
        {
            if (onboarding is null)
            {
                return Fail(
                    "No NWC connection string was given, and wallet onboarding is not "
                    + "available in this server instance to fall back to. Pass "
                    + "nwcConnectionString=... (copy it from your wallet app — it starts with "
                    + "nostr+walletconnect://).");
            }

            WalletSetupState state;
            string? resolved;
            try
            {
                state = onboarding.Describe();
                resolved = onboarding.ResolveOwnNwcConnectionString();
            }
            catch (Exception ex)
            {
                return Fail($"Could not read this server's wallet configuration: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(resolved))
            {
                return Fail(!state.Configured
                    ? "No NWC connection string was given, and no wallet is configured on this "
                      + "MCP server to fall back to. Pass nwcConnectionString=... (copy it from "
                      + "your wallet app — it starts with nostr+walletconnect://), or call "
                      + "setup_wallet first."
                    : $"No NWC connection string was given, and this MCP server's wallet is "
                      + $"{state.Provider}, which cannot be handed to Lightning Enable as a "
                      + "receiving wallet — only a Nostr Wallet Connect string can. Pass "
                      + "nwcConnectionString=... from a wallet app that supports NWC "
                      + "(CoinOS, Alby Hub, CLINK).");
            }

            nwcConnectionString = resolved;
            usedMcpWallet = true;
            source = $"this MCP server's own NWC wallet, from {state.Source}";
        }

        // Validate locally so an obvious typo costs no round trip — and so a malformed
        // credential is never put on the wire. The parse error names the bad field and never
        // quotes the value.
        try
        {
            NwcConfig.Parse(nwcConnectionString!);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }

        var connection = nwcConnectionString!.Trim();

        // Last line of defence: the credential never leaves, whoever put it in the text.
        // Both spellings are checked, because the serializer's default encoder escapes the
        // `+` and `&` an NWC string always contains (`nostr+walletconnect://…&secret=`),
        // so a literal match against the raw string alone would sail straight past it.
        var escaped = JsonSerializer.Serialize(connection).Trim('"');
        string Scrub(string text) => text
            .Replace(connection, SetMarker, StringComparison.Ordinal)
            .Replace(escaped, SetMarker, StringComparison.Ordinal);

        try
        {
            var saved = await apiService!.SaveNwcConnectionAsync(connection, cancellationToken);
            if (!saved.Success)
            {
                return Scrub(FromApi(saved, new Dictionary<string, object?>
                {
                    ["hint"] = "The connection string was NOT stored and the payment provider "
                        + "was left unchanged.",
                }));
            }

            var switched = await apiService.SetPaymentProviderAsync("nwc", cancellationToken);
            if (!switched.Success)
            {
                return Scrub(FromApi(switched, new Dictionary<string, object?>
                {
                    ["nwcConnectionString"] = SetMarker,
                    ["hint"] = "The connection string WAS stored, but the account's payment "
                        + "provider was not switched to nwc. Retry "
                        + "l402_producer action=configure_receive to finish.",
                }));
            }

            return Scrub(Json(new
            {
                success = true,
                provider = "nwc",
                nwcConnectionString = SetMarker,
                usedMcpWallet,
                source,
                message = "Lightning Enable will mint L402 invoices on your own wallet and poll "
                    + "it for payment — no webhook needed. Lightning Enable does not hold the "
                    + "funds. Next: l402_producer action=create_proxy to monetize an API, or "
                    + "action=create to mint a one-off challenge.",
            }));
        }
        catch (Exception ex)
        {
            return Fail(Scrub(ex.Message));
        }
    }

    // ── status ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Where the seller account stands: plan, receiving wallet, onboarding checklist and the
    /// most recent mints.
    /// </summary>
    /// <remarks>
    /// The checklist is best-effort — an account whose quickstart is unavailable still gets
    /// the rest, with the reason in <c>checklistError</c>.
    /// </remarks>
    public static async Task<string> StatusAsync(
        int limit,
        ILightningEnableApiService? apiService,
        CancellationToken cancellationToken)
    {
        var gate = ApiKeyGate(apiService);
        if (gate is not null)
        {
            return gate;
        }

        var take = Math.Clamp(limit <= 0 ? 5 : limit, 1, 50);

        try
        {
            var accountResult = await apiService!.GetMerchantAccountAsync(cancellationToken);
            if (!accountResult.Success)
            {
                return FromApi(accountResult, new Dictionary<string, object?>
                {
                    ["hint"] = "Could not read the account. Check LIGHTNING_ENABLE_API_KEY.",
                });
            }

            var account = accountResult.Data ?? new JsonObject();
            var onboarding = account["onboarding"] as JsonObject ?? new JsonObject();

            var quickStart = await apiService.GetQuickStartAsync(cancellationToken);
            object? checklist = null;
            string? checklistError = null;
            if (quickStart.Success)
            {
                var guide = quickStart.Data ?? new JsonObject();
                checklist = new
                {
                    completedSteps = Number(guide, "completedSteps"),
                    totalSteps = Number(guide, "totalSteps"),
                    requiredStepsCompleted = Number(guide, "requiredStepsCompleted"),
                    requiredStepsTotal = Number(guide, "requiredStepsTotal"),
                    isReadyForProduction = Bool(guide, "isReadyForProduction"),
                    steps = (guide["steps"] as JsonArray ?? new JsonArray())
                        .OfType<JsonObject>()
                        .Select(step => new
                        {
                            stepNumber = Number(step, "stepNumber"),
                            title = Text(step, "title"),
                            isCompleted = Bool(step, "isCompleted"),
                            isRequired = Bool(step, "isRequired"),
                        })
                        .ToList(),
                };
            }
            else
            {
                checklistError = quickStart.ErrorMessage;
            }

            var challengesResult = await apiService.ListChallengesAsync(null, take, 0, cancellationToken);
            var recent = new List<Dictionary<string, object?>>();
            long? challengeTotal = null;
            string? challengesError = null;
            if (challengesResult.Success)
            {
                var data = challengesResult.Data ?? new JsonObject();
                recent = Summaries(data);
                challengeTotal = Number(data, "total");
            }
            else
            {
                challengesError = challengesResult.ErrorMessage;
            }

            return Json(new
            {
                success = true,
                merchant = new
                {
                    merchantId = Number(account, "merchantId"),
                    name = Text(account, "name"),
                    planTier = Text(account, "planTier"),
                    subscriptionStatus = Text(account, "subscriptionStatus"),
                    isActive = Bool(account, "isActive"),
                    l402Enabled = Bool(account["features"] as JsonObject ?? new JsonObject(), "l402Enabled"),
                },
                receive = new
                {
                    provider = DeriveProvider(account, onboarding),
                    configured = Bool(onboarding, "isFullyConfigured") ?? false,
                    nwcConnectionString = Bool(onboarding, "hasNwcConnection") == true
                        ? SetMarker
                        : UnsetMarker,
                    hasStrikeKey = Bool(onboarding, "hasStrikeKey") ?? false,
                    hasOpenNodeKey = Bool(onboarding, "hasOpenNodeKey") ?? false,
                },
                proxies = new
                {
                    count = Number(onboarding, "proxyCount"),
                    hasActive = Bool(onboarding, "hasActiveProxy") ?? false,
                },
                checklist,
                checklistError,
                recentChallenges = recent,
                challengeTotal,
                challengesError,
            });
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    // ── create_proxy ─────────────────────────────────────────────────────────

    /// <summary>Registers an upstream API so Lightning Enable can charge for it.</summary>
    public static async Task<string> CreateProxyAsync(
        string? name,
        string? targetBaseUrl,
        string? description,
        int defaultPriceSats,
        ILightningEnableApiService? apiService,
        CancellationToken cancellationToken)
    {
        var gate = ApiKeyGate(apiService);
        if (gate is not null)
        {
            return gate;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return Fail("A name is required — it becomes the service name agents see.");
        }
        if (string.IsNullOrWhiteSpace(targetBaseUrl))
        {
            return Fail(
                "targetBaseUrl is required: the https:// base URL of the API you are "
                + "monetizing. Lightning Enable forwards paid requests to it.");
        }
        if (defaultPriceSats <= 0)
        {
            return Fail("defaultPriceSats must be greater than 0.");
        }

        try
        {
            var result = await apiService!.CreateProxyAsync(
                name.Trim(), targetBaseUrl.Trim(), description, defaultPriceSats, cancellationToken);
            if (!result.Success)
            {
                return FromApi(result);
            }

            var proxy = result.Data ?? new JsonObject();
            var proxyId = Text(proxy, "proxyId");
            var proxyUrl = Text(proxy, "proxyUrl")
                ?? (proxyId is null ? null : $"/l402/proxy/{proxyId}");

            return Json(new
            {
                success = true,
                proxyId,
                name = Text(proxy, "name"),
                description = Text(proxy, "description"),
                targetBaseUrl = Text(proxy, "targetBaseUrl"),
                defaultPriceSats = Number(proxy, "defaultPriceSats"),
                proxyUrl,
                publicBaseUrl = proxyUrl is null ? null : PublicUrl(apiService, proxyUrl),
                message = "Proxy registered. Every request under the public base URL now "
                    + "answers 402 until it is paid.",
                nextStep = $"l402_producer action=add_endpoint proxyId={proxyId} to price a "
                    + "route, then action=publish to list it.",
            });
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    // ── add_endpoint ─────────────────────────────────────────────────────────

    /// <summary>Prices one route on a proxy and puts it in the published manifest.</summary>
    public static async Task<string> AddEndpointAsync(
        string? proxyId,
        string? endpointId,
        string? path,
        string? httpMethod,
        string? summary,
        long priceSats,
        ILightningEnableApiService? apiService,
        CancellationToken cancellationToken)
    {
        var gate = ApiKeyGate(apiService);
        if (gate is not null)
        {
            return gate;
        }

        if (string.IsNullOrWhiteSpace(proxyId))
        {
            return Fail(
                "proxyId is required. Create one with l402_producer action=create_proxy, or "
                + "read your existing ones with action=status.");
        }
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return Fail(
                "endpointId is required: a short stable id for this route, unique on the proxy.");
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            return Fail("path is required, like /forecast — the route on the upstream API.");
        }
        if (priceSats < 0)
        {
            return Fail("priceSats cannot be negative.");
        }

        var slug = proxyId.Trim();
        var method = (string.IsNullOrWhiteSpace(httpMethod) ? "GET" : httpMethod).Trim().ToUpperInvariant();
        var route = path.Trim();

        try
        {
            var result = await apiService!.CreateManifestEndpointAsync(
                slug, endpointId.Trim(), route, method, summary, (int)priceSats, cancellationToken);
            if (!result.Success)
            {
                return FromApi(result);
            }

            var endpoint = result.Data ?? new JsonObject();
            var resolvedPath = Text(endpoint, "path") ?? route;
            var url = PublicUrl(apiService, $"/l402/proxy/{slug}{resolvedPath}");

            return Json(new
            {
                success = true,
                proxyId = slug,
                endpointId = Text(endpoint, "endpointId"),
                path = resolvedPath,
                httpMethod = Text(endpoint, "httpMethod") ?? method,
                summary = Text(endpoint, "summary"),
                basePriceSats = Number(endpoint, "basePriceSats"),
                url,
                message = $"{Text(endpoint, "httpMethod") ?? method} {url} now answers 402 until "
                    + "the payer presents a valid L402 token.",
                nextStep = $"l402_producer action=publish proxyId={slug} to list the service so "
                    + "other agents can discover it.",
            });
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    // ── publish ──────────────────────────────────────────────────────────────

    /// <summary>Turns the manifest on and lists the service publicly.</summary>
    /// <remarks>
    /// <paramref name="serviceName"/> renames the proxy first: the manifest's
    /// <c>service.name</c> is the proxy's own name, not a manifest-settings field.
    /// </remarks>
    public static async Task<string> PublishAsync(
        string? proxyId,
        string? serviceName,
        string? serviceDescription,
        IReadOnlyList<string>? categories,
        ILightningEnableApiService? apiService,
        CancellationToken cancellationToken)
    {
        var gate = ApiKeyGate(apiService);
        if (gate is not null)
        {
            return gate;
        }

        if (string.IsNullOrWhiteSpace(proxyId))
        {
            return Fail("proxyId is required. Create one with l402_producer action=create_proxy.");
        }

        var slug = proxyId.Trim();

        try
        {
            if (!string.IsNullOrWhiteSpace(serviceName))
            {
                var renamed = await apiService!.RenameProxyAsync(slug, serviceName.Trim(), cancellationToken);
                if (!renamed.Success)
                {
                    return FromApi(renamed, new Dictionary<string, object?>
                    {
                        ["hint"] = "The service was NOT published — the rename failed first, so "
                            + "nothing was changed.",
                    });
                }
            }

            var result = await apiService!.UpdateManifestSettingsAsync(
                slug, serviceDescription, categories, cancellationToken);
            if (!result.Success)
            {
                return FromApi(result);
            }

            var settings = result.Data ?? new JsonObject();
            var manifestPath = Text(settings, "manifestUrl")
                ?? $"/l402/proxy/{slug}/.well-known/l402-manifest.json";

            return Json(new
            {
                success = true,
                proxyId = slug,
                manifestEnabled = Bool(settings, "manifestEnabled"),
                publiclyListed = Bool(settings, "manifestPubliclyListed"),
                serviceDescription = Text(settings, "serviceDescription"),
                categories = (settings["categories"] as JsonArray ?? new JsonArray())
                    .Select(c => c?.GetValue<string>())
                    .Where(c => !string.IsNullOrEmpty(c))
                    .ToList(),
                openapiUrl = PublicUrl(apiService, $"/l402/proxy/{slug}/openapi.json"),
                manifestUrl = PublicUrl(apiService, manifestPath),
                message = "Listed. Agents can now find this service through discover_api and "
                    + "read its pricing from the manifest.",
            });
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    // ── list_challenges ──────────────────────────────────────────────────────

    /// <summary>Reads back the challenges this merchant minted.</summary>
    /// <remarks>
    /// Never a macaroon, never a preimage — the payment hash is the correlation handle.
    /// </remarks>
    public static async Task<string> ListChallengesAsync(
        string? status,
        int limit,
        int offset,
        ILightningEnableApiService? apiService,
        CancellationToken cancellationToken)
    {
        var gate = ApiKeyGate(apiService);
        if (gate is not null)
        {
            return gate;
        }

        var normalized = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        if (normalized is not null && !ChallengeStatuses.Contains(normalized, StringComparer.Ordinal))
        {
            return Fail(
                $"status must be one of: {string.Join(", ", ChallengeStatuses)}. Omit it to "
                + "list every challenge.");
        }

        var take = Math.Clamp(limit <= 0 ? 20 : limit, 1, 200);
        var skip = Math.Max(0, offset);

        try
        {
            var result = await apiService!.ListChallengesAsync(normalized, take, skip, cancellationToken);
            if (!result.Success)
            {
                return FromApi(result);
            }

            var data = result.Data ?? new JsonObject();
            return Json(new
            {
                success = true,
                challenges = Summaries(data),
                total = Number(data, "total"),
                limit = Number(data, "limit") ?? take,
                offset = Number(data, "offset") ?? skip,
                status = Text(data, "status") ?? normalized,
            });
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    /// <summary>The gate every producer action shares: a service, holding a merchant API key.</summary>
    private static string? ApiKeyGate(ILightningEnableApiService? apiService)
    {
        if (apiService is null)
        {
            return Fail(NoApiService);
        }
        return apiService.IsConfigured
            ? null
            : Fail(LightningEnableApiService.ProducerApiKeyRequired);
    }

    /// <summary>An absolute URL on the Lightning Enable API for a proxy-relative path.</summary>
    private static string PublicUrl(ILightningEnableApiService apiService, string path)
    {
        var baseUrl = string.IsNullOrWhiteSpace(apiService.BaseUrl)
            ? "https://api.lightningenable.com"
            : apiService.BaseUrl.TrimEnd('/');
        return $"{baseUrl}{path}";
    }

    /// <summary>
    /// Which lane the account receives on.
    /// </summary>
    /// <remarks>
    /// <c>GET /api/merchant/me</c> reports credential PRESENCE, not the provider column, so
    /// this derives it from the onboarding flags in the same priority the API's own factory
    /// uses. An explicit <c>paymentProvider</c> member wins if the API ever returns one.
    /// </remarks>
    private static string? DeriveProvider(JsonObject account, JsonObject onboarding)
    {
        var explicitProvider = Text(account, "paymentProvider");
        if (!string.IsNullOrWhiteSpace(explicitProvider))
        {
            return explicitProvider.Trim().ToLowerInvariant();
        }

        if (Bool(onboarding, "hasNwcConnection") == true) return "nwc";
        if (Bool(onboarding, "hasStrikeKey") == true) return "strike";
        if (Bool(onboarding, "hasOpenNodeKey") == true) return "opennode";
        return null;
    }

    /// <summary>Allowlisted challenge rows out of a list response.</summary>
    private static List<Dictionary<string, object?>> Summaries(JsonObject data)
        => (data["challenges"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(row =>
            {
                var summary = new Dictionary<string, object?>();
                foreach (var field in ChallengeFields)
                {
                    if (row.ContainsKey(field))
                    {
                        summary[field] = Scalar(row[field]);
                    }
                }
                return summary;
            })
            .ToList();

    private static object? Scalar(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        JsonValue value when value.TryGetValue<bool>(out var b) => b,
        JsonValue value when value.TryGetValue<long>(out var l) => l,
        JsonValue value when value.TryGetValue<double>(out var d) => d,
        _ => node.ToJsonString(),
    };

    private static string? Text(JsonObject? node, string key)
        => node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? Bool(JsonObject? node, string key)
        => node?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static long? Number(JsonObject? node, string key)
        => node?[key] is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    /// <summary>A descriptive failure result. Never blank, never a bare status code.</summary>
    internal static string Fail(string error) => Json(new { success = false, error });

    /// <summary>
    /// Turns a failed API call into a tool result, keeping the API's own error members.
    /// </summary>
    private static string FromApi(ApiCallResult result, IDictionary<string, object?>? extra = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["success"] = false,
            ["error"] = result.ErrorMessage ?? "The Lightning Enable API call failed.",
        };
        if (result.ErrorType is not null) payload["errorType"] = result.ErrorType;
        if (result.ErrorCode is not null) payload["errorCode"] = result.ErrorCode;
        if (result.HttpStatus is not null) payload["httpStatus"] = result.HttpStatus;
        if (result.ValidationErrors is not null)
        {
            payload["validationErrors"] = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                result.ValidationErrors.ToJsonString());
        }

        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                payload[key] = value;
            }
        }

        return Json(payload);
    }

    private static string Json(object payload) => JsonSerializer.Serialize(payload, Indented);
}
