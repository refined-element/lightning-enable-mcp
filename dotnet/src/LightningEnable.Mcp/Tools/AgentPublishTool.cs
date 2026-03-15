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
    [McpServerTool(Name = "publish_agent_capability"), Description(
        "Publish an agent capability advertisement to the Nostr network. " +
        "Makes your agent discoverable by other agents. Creates a kind 38400 event. " +
        "Optionally creates an L402 proxy for payment settlement. " +
        "Requires LIGHTNING_ENABLE_API_KEY.")]
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
                            "Required for publishing agent capabilities."
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
                    discovery = $"Other agents can find this via: discover_agent_services(category=\"{categories[0]}\")",
                    settlement = result.L402Endpoint != null
                        ? $"Payments will be settled via L402 at: {result.L402Endpoint}"
                        : "No L402 endpoint configured. Add one for automatic payment settlement.",
                    update = $"Republish with the same serviceId ('{serviceId}') to update the capability."
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
}
