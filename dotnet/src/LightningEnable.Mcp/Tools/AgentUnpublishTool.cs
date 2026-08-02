using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for taking a published listing down. Retires the L402 proxy and
/// publishes a NIP-09 kind 5 deletion plus a status=removed 38400 replacement,
/// via the ungated proxy management API used by the live marketplace listings.
/// </summary>
[McpServerToolType]
public static class AgentUnpublishTool
{
    /// <summary>
    /// Takes a published listing down.
    /// </summary>
    [McpServerTool(Name = "unpublish_agent_capability"), Description(
        "Take a published listing down. Retires the L402 proxy and publishes a NIP-09 " +
        "kind 5 deletion plus a status=removed 38400 replacement, so other agents stop " +
        "seeing a dead listing. Works for marketplace listings created via the L402 " +
        "proxy/dashboard pipeline. Requires LIGHTNING_ENABLE_API_KEY.")]
    public static async Task<string> UnpublishAgentCapability(
        [Description("The listing's identifier — its Nostr d-tag / proxy id (the value after the last ':' in the card's nw: footer)")] string serviceId,
        [Description("Optional free-text reason recorded on the removal event")] string? reason = null,
        IAgentService? agentService = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(serviceId))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Service ID is required (the listing's d-tag / proxy id to take down)."
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
                            "Required for unpublishing listings. " +
                            "Get an API key: 30-day free trial at " +
                            "https://api.lightningenable.com/Checkout?plan=individual&utm_source=mcp&utm_medium=tool-hint&utm_campaign=gtm-aug-2026 " +
                            "— or call the `create_lightning_enable_account` tool to sign up right here."
                });
            }

            var result = await agentService.UnpublishCapabilityAsync(
                serviceId.Trim(), reason, cancellationToken);

            if (!result.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage
                });
            }

            var already = result.AlreadyRetired == true;
            return JsonSerializer.Serialize(new
            {
                success = true,
                serviceId,
                proxyId = result.ProxyId ?? serviceId,
                retired = result.Retired,
                alreadyRetired = result.AlreadyRetired,
                message = already
                    ? $"Listing '{serviceId}' was already retired."
                    : $"Listing '{serviceId}' removed: the L402 proxy is retired and a " +
                      "NIP-09 deletion (+ status=removed replacement) was published to Nostr."
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Error unpublishing listing: {ex.Message}"
            });
        }
    }
}
