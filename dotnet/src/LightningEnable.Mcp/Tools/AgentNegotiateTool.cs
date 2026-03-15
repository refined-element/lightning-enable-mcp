using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for requesting a service from an agent.
/// Sends a kind 38401 event referencing the provider's capability to start negotiation.
/// </summary>
[McpServerToolType]
public static class AgentNegotiateTool
{
    /// <summary>
    /// Requests a service from an agent, starting the negotiation process.
    /// </summary>
    [McpServerTool(Name = "request_agent_service"), Description(
        "Request a service from an agent. Sends a kind 38401 event referencing the provider's capability. " +
        "Starts the negotiation process. If the provider has an L402 endpoint, you can skip this step " +
        "and use settle_agent_service directly.")]
    public static async Task<string> RequestAgentService(
        [Description("Event ID of the capability to request")] string capabilityEventId,
        [Description("Maximum budget in satoshis")] int budgetSats,
        [Description("Additional parameters as JSON")] string? parameters = null,
        IAgentService? agentService = null,
        IBudgetService? budgetService = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Input validation
            if (string.IsNullOrWhiteSpace(capabilityEventId))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Capability event ID is required. Use discover_agent_services to find available capabilities."
                });
            }

            if (budgetSats <= 0)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Budget must be greater than 0 sats."
                });
            }

            // Validate parameters JSON if provided
            if (!string.IsNullOrWhiteSpace(parameters))
            {
                try
                {
                    JsonDocument.Parse(parameters);
                }
                catch (JsonException)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "Parameters must be valid JSON (e.g., '{\"text\": \"Hello\", \"targetLang\": \"es\"}')."
                    });
                }
            }

            if (agentService == null)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Agent service not available. The MCP server may not be configured correctly."
                });
            }

            // Budget check before sending request
            if (budgetService != null)
            {
                var budgetCheck = budgetService.CheckBudget(budgetSats);
                if (!budgetCheck.Allowed)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "Budget limit exceeded",
                        details = new
                        {
                            requestedSats = budgetSats,
                            remainingSats = budgetCheck.RemainingSessionBudget,
                            reason = budgetCheck.DenialReason
                        },
                        hint = "Reduce the budget amount or check get_budget_status for current limits."
                    });
                }
            }

            var result = await agentService.RequestServiceAsync(
                capabilityEventId, budgetSats, parameters, cancellationToken);

            if (!result.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage
                });
            }

            // NOTE: Budget is not deducted at request time by design. The budget is only
            // deducted at settlement time (in AgentSettleTool) when the L402 payment is actually made.
            // The CheckBudget call above validates the budget is sufficient, but no spend is recorded
            // until the agent calls settle_agent_service and the Lightning payment succeeds.

            var response = new Dictionary<string, object?>
            {
                ["success"] = true,
                ["requestEventId"] = result.RequestEventId,
                ["capabilityEventId"] = capabilityEventId,
                ["budgetSats"] = budgetSats,
                ["message"] = "Service request sent successfully."
            };

            if (result.L402Endpoint != null)
            {
                response["l402Endpoint"] = result.L402Endpoint;
                response["nextStep"] = $"The provider has an L402 endpoint. Use settle_agent_service(l402Endpoint=\"{result.L402Endpoint}\") " +
                    "to pay and access the service.";
            }
            else
            {
                response["nextStep"] = "Waiting for provider response. The provider will send a service agreement " +
                    "or direct response. Monitor for kind 38402 events referencing your request.";
            }

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Error requesting service: {ex.Message}"
            });
        }
    }
}
