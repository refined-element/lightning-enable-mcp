using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for taking a published agent capability down (NIP-A5 listing
/// lifecycle). In "remove" mode it retires the L402 proxy and publishes a NIP-09
/// kind 5 deletion plus a status=removed 38400 replacement.
/// </summary>
[McpServerToolType]
public static partial class AgentUnpublishTool
{
    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Hex64();

    private static readonly string[] ValidModes = { "remove", "pause" };

    /// <summary>
    /// Takes a published agent capability down.
    /// </summary>
    [McpServerTool(Name = "unpublish_agent_capability"), Description(
        "Take a published agent capability down (listing lifecycle). " +
        "In 'remove' mode it retires the L402 proxy and publishes a NIP-09 kind 5 " +
        "deletion plus a status=removed 38400 replacement, so other agents stop " +
        "seeing a dead listing. Requires LIGHTNING_ENABLE_API_KEY.")]
    public static async Task<string> UnpublishAgentCapability(
        [Description("Nostr public key (64-hex) of the agent that owns the listing")] string pubkey,
        [Description("The listing's service identifier (its d-tag / proxy id)")] string serviceId,
        [Description("'remove' (default) withdraws the listing and retires its proxy; 'pause' is reserved")] string mode = "remove",
        [Description("Optional free-text reason recorded on the removal event")] string? reason = null,
        IAgentService? agentService = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pubkey) || !Hex64().IsMatch(pubkey.Trim()))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "A valid 64-character hex Nostr pubkey is required."
                });
            }

            if (string.IsNullOrWhiteSpace(serviceId))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Service ID is required (the d-tag of the listing to take down)."
                });
            }

            var normalizedMode = (string.IsNullOrWhiteSpace(mode) ? "remove" : mode).Trim().ToLowerInvariant();
            if (Array.IndexOf(ValidModes, normalizedMode) < 0)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"Mode must be one of [remove, pause] (got '{mode}')."
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
                            "Required for unpublishing agent capabilities."
                });
            }

            var result = await agentService.UnpublishCapabilityAsync(
                pubkey.Trim(), serviceId.Trim(), normalizedMode, reason, cancellationToken);

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
                serviceId = result.ServiceId ?? serviceId,
                proxyId = result.ProxyId,
                mode = result.Mode ?? normalizedMode,
                retired = result.Retired,
                message = $"Capability '{serviceId}' removed: the L402 proxy is retired and a " +
                          "NIP-09 deletion (+ status=removed replacement) was published to Nostr."
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Error unpublishing capability: {ex.Message}"
            });
        }
    }
}
