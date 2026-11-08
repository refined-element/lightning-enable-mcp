using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for publishing agent capability advertisements to the Nostr network.
/// Creates kind 38400 events that make the agent discoverable by other agents.
/// </summary>
[McpServerToolType]
public static class AgentPublishTool
{
    /// <summary>
    /// Publishes an agent capability to the Nostr network.
    /// </summary>
    [McpServerTool(Name = "publish_agent_capability", Title = "Publish capability (deprecated)", ReadOnly = false, Destructive = false), Description(
        "Publish an agent capability advertisement to the Nostr network. " +
        "Makes your agent discoverable by other agents as a kind 38400 listing, published via " +
        "Lightning Enable's L402 proxy pipeline. Provide targetUrl — an L402 proxy is created to " +
        "back the listing and handle payment. The event is signed by the Lightning Enable platform " +
        "key. Each call creates a new listing, so publish once; to change a listing, unpublish it " +
        "and publish again. A third-party target is listed only after ownership verification: the " +
        "result then says published=false and returns a one-time token to publish before " +
        "l402_producer(action=verify). Requires LIGHTNING_ENABLE_API_KEY.")]
    public static async Task<string> PublishAgentCapability(
        [Description("Unique service identifier (used as d-tag)")] string serviceId,
        [Description("Service categories (e.g., ['ai', 'translation'])")] string[] categories,
        [Description("Description of the service")] string content,
        [Description("Price per request in satoshis")] int priceSats,
        [Description("L402 endpoint URL for payment settlement")] string? l402Endpoint = null,
        [Description("Target API URL (if auto-creating L402 proxy via Lightning Enable)")] string? targetUrl = null,
        [Description("Hashtags for discoverability")] string[]? hashtags = null,
        IAgentService? agentService = null,
        ILightningEnableApiService? apiService = null,
        IBudgetConfigurationService? configService = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Input validation
            if (string.IsNullOrWhiteSpace(serviceId))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Service ID is required. Provide a unique identifier for your service (e.g., 'my-translation-service')."
                });
            }

            if (categories == null || categories.Length == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "At least one category is required (e.g., ['ai', 'translation'])."
                });
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Service description (content) is required."
                });
            }

            if (priceSats <= 0)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Price must be greater than 0 sats."
                });
            }

            if (agentService == null)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Agent service not available. The MCP server may not be configured correctly."
                });
            }

            if (!agentService.IsConfigured)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Lightning Enable API key not configured. " +
                            "Set LIGHTNING_ENABLE_API_KEY environment variable or add 'lightningEnableApiKey' to ~/.lightning-enable/config.json. " +
                            "Required for publishing agent capabilities. " +
                            "Get an API key: 30-day free trial at " +
                            "https://api.lightningenable.com/Checkout?plan=individual&utm_source=mcp&utm_medium=tool-hint&utm_campaign=gtm-aug-2026 " +
                            "— or call the `create_lightning_enable_account` tool to sign up right here."
                });
            }

            var result = await agentService.PublishCapabilityAsync(
                serviceId, categories, content, priceSats,
                l402Endpoint, targetUrl, hashtags, cancellationToken);

            if (!result.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage
                });
            }

            if (result.Published == false)
            {
                return Unpublished(result, serviceId, categories, priceSats, configService?.ConfigFilePath);
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                eventId = result.EventId,
                serviceId,
                categories,
                priceSats,
                l402Endpoint = result.L402Endpoint,
                message = $"Agent capability '{serviceId}' published successfully as kind 38400 event.",
                nextSteps = new
                {
                    discovery = $"Other agents can find this via: agent_services(action=\"discover\", category=\"{categories[0]}\")",
                    settlement = result.L402Endpoint != null
                        ? $"Payments will be settled via L402 at: {result.L402Endpoint}"
                        : "No L402 endpoint configured. Add one for automatic payment settlement.",
                    update = "Publish once: each publish creates a new listing and a new L402 proxy. To change this listing, unpublish it (pass the proxy id at the end of l402Endpoint) and publish again, or edit it in the Lightning Enable dashboard."
                }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Error publishing capability: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// The server created the capability's proxy but did not list it: listing a third-party
    /// target needs ownership verification first. Says so plainly and hands back the one-time
    /// verification token (never logged) with what to do next.
    /// </summary>
    private static string Unpublished(
        AgentPublishResult result, string serviceId, string[] categories, int priceSats, string? configPath)
    {
        var proxyId = result.ProxyId ?? ProxyIdFrom(result.VerifyEndpoint) ?? ProxyIdFrom(result.L402Endpoint);
        var proxyArg = proxyId is null ? "<proxy id>" : proxyId;
        var reason = result.Reason ?? "ownership_verification_required";

        var payload = new Dictionary<string, object?>
        {
            ["success"] = true,
            ["published"] = false,
            ["reason"] = reason,
            ["serviceId"] = serviceId,
            ["proxyId"] = proxyId,
            ["categories"] = categories,
            ["priceSats"] = priceSats,
            ["l402Endpoint"] = result.L402Endpoint,
            ["message"] = $"Capability '{serviceId}' was created but NOT published: it is not on Nostr or in the "
                + "registry yet, because listing a third-party target requires proof that you own it. Its L402 "
                + "endpoint already works by its direct URL.",
        };

        var verification = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(result.VerificationToken))
        {
            // Kept like sell_url's API key: shown once here, stored owner-only beside config.json
            // so l402_producer(action="verify") finds it.
            var storage = proxyId is null
                ? new ProducerVerification.TokenStorage(false, "(no proxy id)", "the server sent no proxy id")
                : ProducerVerification.StoreToken(configPath, proxyId, result.VerificationToken, "publish_agent_capability");
            verification["token"] = result.VerificationToken;
            verification["tokenNote"] = "This token is shown only this once here, and Lightning Enable keeps only "
                + $"a hash of it. {ProducerVerification.StorageNote(storage)} If you lose it, issue a new one with "
                + $"l402_producer(action=\"verification_token\", proxy_id=\"{proxyArg}\").";
            verification["tokenStorage"] = ProducerVerification.StorageJson(storage);
        }
        if (result.VerificationInstructions?["tokenPlaceholder"]?.GetValue<string>() is { } placeholder)
        {
            verification["tokenPlaceholder"] = placeholder;
        }
        verification["methods"] = ProducerVerification.MethodsFrom(result.VerificationInstructions);
        payload["verification"] = verification;

        payload["nextSteps"] = new List<string>
        {
            string.IsNullOrEmpty(result.VerificationToken)
                ? $"Get a token with l402_producer(action=\"verification_token\", proxy_id=\"{proxyArg}\")."
                : "Publish the token by one of the methods above, replacing <token>.",
            $"Run the check with l402_producer(action=\"verify\", proxy_id=\"{proxyArg}\").",
            $"Once verified, list it with l402_producer(action=\"publish\", proxy_id=\"{proxyArg}\").",
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The proxy id from <c>/api/proxy/{id}/verify</c> or <c>.../l402/proxy/{id}</c>.</summary>
    private static string? ProxyIdFrom(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var segments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var at = Array.IndexOf(segments, "proxy");
        return at >= 0 && at + 1 < segments.Length ? segments[at + 1] : null;
    }
}
