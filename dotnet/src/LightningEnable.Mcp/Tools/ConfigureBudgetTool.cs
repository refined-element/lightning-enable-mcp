using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool to TIGHTEN (lower) the session's spending limits at runtime.
/// An agent can only reduce its caps — it can never raise them above the
/// operator's config-file limits (or an existing tighter runtime cap). To raise
/// limits, the operator edits ~/.lightning-enable/config.json. This mirrors the
/// Python package's configure_budget so the two implementations expose the same
/// tool surface.
/// </summary>
[McpServerToolType]
public static class ConfigureBudgetTool
{
    /// <summary>
    /// Tightens the per-request / per-session spending caps (sats). Tighten-only.
    /// </summary>
    /// <param name="perRequest">Maximum sats per single request.</param>
    /// <param name="perSession">Maximum total sats for the whole session.</param>
    /// <param name="budgetService">Injected budget service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON result with the new effective caps, or an error.</returns>
    [McpServerTool(Name = "configure_budget"), Description("Tighten (lower) the session spending limits in sats. Can ONLY lower caps, never raise them above the operator's config — to raise limits, edit ~/.lightning-enable/config.json.")]
    public static async Task<string> ConfigureBudget(
        [Description("Maximum sats per single request (must be <= current effective cap and <= perSession)")] long perRequest = 1000,
        [Description("Maximum total sats for the whole session (must be <= current effective cap)")] long perSession = 10000,
        IBudgetService? budgetService = null,
        CancellationToken cancellationToken = default)
    {
        if (budgetService == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Budget service not available"
            });
        }

        try
        {
            var result = await budgetService.ConfigureBudgetAsync(perRequest, perSession, cancellationToken);
            if (!result.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.Error
                });
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                limits = new
                {
                    perRequestSats = result.EffectivePerRequestSats,
                    perSessionSats = result.EffectivePerSessionSats
                },
                message = $"Budget tightened to {result.EffectivePerRequestSats:N0} sats/request and " +
                          $"{result.EffectivePerSessionSats:N0} sats/session. These runtime caps can only be " +
                          "lowered further; raising them requires editing ~/.lightning-enable/config.json."
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message
            });
        }
    }
}
