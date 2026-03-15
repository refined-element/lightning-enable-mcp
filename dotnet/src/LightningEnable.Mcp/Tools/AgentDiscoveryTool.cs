using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for discovering agent services on the Nostr network.
/// Searches for kind 38400 capability events published by agents.
/// </summary>
[McpServerToolType]
public static class AgentDiscoveryTool
{
    /// <summary>
    /// Discovers agent services by category, hashtag, or keyword search.
    /// </summary>
    [McpServerTool(Name = "discover_agent_services"), Description(
        "Discover agent services on the Nostr network. Search by category, hashtag, or keyword. " +
        "Returns capabilities published as kind 38400 events. " +
        "Use this to find agents that offer services you can pay for via L402.")]
    public static async Task<string> DiscoverAgentServices(
        [Description("Filter by service category (e.g., 'ai', 'data', 'translation')")] string? category = null,
        [Description("Filter by hashtags")] string[]? hashtags = null,
        [Description("Search query")] string? query = null,
        [Description("Maximum results to return (default: 20)")] int limit = 20,
        IAgentService? agentService = null,
        IBudgetService? budgetService = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Validate that at least one filter is provided
            if (string.IsNullOrWhiteSpace(category) &&
                (hashtags == null || hashtags.Length == 0) &&
                string.IsNullOrWhiteSpace(query))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Please provide at least one search filter: 'category', 'hashtags', or 'query'.",
                    examples = new[]
                    {
                        new { description = "Find AI services", call = "discover_agent_services(category=\"ai\")" },
                        new { description = "Search for translation", call = "discover_agent_services(query=\"translation\")" },
                        new { description = "Browse by hashtag", call = "discover_agent_services(hashtags=[\"weather\", \"forecast\"])" }
                    }
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            if (agentService == null)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Agent service not available. The MCP server may not be configured correctly."
                });
            }

            var result = await agentService.DiscoverCapabilitiesAsync(
                category, hashtags, query, limit, cancellationToken);

            if (!result.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage,
                    hint = "The agent capability registry may be temporarily unavailable. Try again later."
                });
            }

            // Format capabilities for agent consumption
            var formattedCapabilities = result.Capabilities.Select(cap =>
            {
                var entry = new Dictionary<string, object?>();
                if (cap.EventId != null) entry["event_id"] = cap.EventId;
                if (cap.ServiceId != null) entry["service_id"] = cap.ServiceId;
                if (cap.Pubkey != null) entry["pubkey"] = cap.Pubkey;
                if (cap.Content != null) entry["description"] = cap.Content;
                if (cap.Categories.Count > 0) entry["categories"] = cap.Categories;
                if (cap.Hashtags.Count > 0) entry["hashtags"] = cap.Hashtags;
                entry["price_sats"] = cap.PriceSats;
                if (cap.L402Endpoint != null) entry["l402_endpoint"] = cap.L402Endpoint;
                if (cap.CreatedAt.HasValue) entry["created_at"] = cap.CreatedAt.Value;

                // Budget annotation — guard against division by zero
                if (budgetService != null && cap.PriceSats > 0)
                {
                    var config = budgetService.GetConfig();
                    if (cap.PriceSats > 0)
                    {
                        entry["affordable_calls"] = config.RemainingSessionBudget / cap.PriceSats;
                    }
                }

                return entry;
            }).ToList();

            // Budget info
            object? budgetInfo = null;
            if (budgetService != null)
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
                query,
                category,
                hashtags,
                results = formattedCapabilities,
                total = result.Total,
                budget = budgetInfo,
                hint = formattedCapabilities.Count > 0
                    ? "Use request_agent_service(capabilityEventId=\"<event_id>\") to request a service, " +
                      "or settle_agent_service(l402Endpoint=\"<url>\") to pay and access it directly via L402."
                    : "No agent services found. Try different keywords or categories."
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Error discovering agent services: {ex.Message}"
            });
        }
    }
}
