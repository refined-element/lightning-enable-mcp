using System.ComponentModel;
using System.Text.Json.Serialization;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

// Enum members are the literal wire values (see BudgetTool for why).
#pragma warning disable CA1707, IDE1006

/// <summary>Which Agent Service Agreement operation to run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentServicesAction>))]
public enum AgentServicesAction
{
    /// <summary>Search kind 38400 listings.</summary>
    discover,

    /// <summary>Send a kind 38401 service request.</summary>
    request,

    /// <summary>Pay an agreement's L402 endpoint.</summary>
    settle,

    /// <summary>Publish your own kind 38400 listing.</summary>
    publish,

    /// <summary>Take one of your listings down.</summary>
    unpublish,

    /// <summary>Publish a kind 38403 review of a finished agreement.</summary>
    attest,

    /// <summary>Read an agent's attestations.</summary>
    reputation,
}

#pragma warning restore CA1707, IDE1006

/// <summary>
/// The consolidated Agent Service Agreement verb: the seven ASA tools behind one schema.
///
/// Each action delegates to the original implementation unchanged — including
/// <see cref="AgentServicesAction.settle"/>, which still runs the same L402 auto-pay flow
/// with the same budget checks, so the only thing that changed is the name it is reached by.
/// </summary>
[McpServerToolType]
public static class AgentServicesTool
{
    /// <summary>Discovers, requests, settles, publishes, or reviews an agent service.</summary>
    [McpServerTool(
        Name = "agent_services",
        Title = "Agent services",
        ReadOnly = false,
        Destructive = true)]
    [Description(
        "Agent Service Agreements over Nostr: discover, request, settle, publish and review "
        + "agent services. Requires LIGHTNING_ENABLE_API_KEY.")]
    public static async Task<string> AgentServices(
        [Description(
            "discover: search listings. request: ask for a service. settle: PAY the "
            + "agreement L402 endpoint. publish: list your service. unpublish: remove it. "
            + "attest: review a finished agreement. reputation: read reviews. Pass only "
            + "the arguments tagged with your action.")]
        AgentServicesAction action,
        [Description("discover: category")] string? category = null,
        [Description("discover: keyword")] string? query = null,
        [Description("discover/publish: hashtags")] string[]? hashtags = null,
        [Description("discover/reputation: max results")] int limit = 20,
        [Description("request: capability id")] string? capabilityEventId = null,
        [Description("request: max sats")] int budgetSats = 0,
        [Description("request: extra params, JSON string")] string? parameters = null,
        [Description("settle/publish: L402 endpoint")] string? l402Endpoint = null,
        [Description("settle: HTTP method")] string method = "GET",
        [Description("settle: request body")] string? body = null,
        [Description("settle/attest: agreement id")] string? agreementId = null,
        [Description("settle: max sats")] int maxSats = 1000,
        [Description("publish/unpublish: listing d-tag")] string? serviceId = null,
        [Description("publish: categories")] string[]? categories = null,
        [Description("publish/attest: text")] string? content = null,
        [Description("publish: price per request")] int priceSats = 0,
        [Description("publish: API URL to wrap in a proxy")] string? targetUrl = null,
        [Description("unpublish: reason")] string? reason = null,
        [Description("attest: reviewed agent")] string? subjectPubkey = null,
        [Description("attest: 1-5")] int rating = 0,
        [Description("attest: preimage hash")] string? proof = null,
        [Description("reputation: agent pubkey")] string? pubkey = null,
        IAgentService? agentService = null,
        ILightningEnableApiService? apiService = null,
        IBudgetService? budgetService = null,
        IL402HttpClient? l402Client = null,
        IPaymentHistoryService? paymentHistoryService = null,
        CancellationToken cancellationToken = default)
        => action switch
        {
            AgentServicesAction.discover => await AgentDiscoveryTool.DiscoverAgentServices(
                category, hashtags, query, limit, agentService, budgetService, cancellationToken),
            AgentServicesAction.request => await AgentNegotiateTool.RequestAgentService(
                capabilityEventId ?? string.Empty, budgetSats, parameters,
                agentService, budgetService, cancellationToken),
            AgentServicesAction.settle => await AgentSettleTool.SettleAgentService(
                l402Endpoint ?? string.Empty, method, body, agreementId, maxSats,
                l402Client, budgetService, paymentHistoryService, cancellationToken),
            AgentServicesAction.publish => await AgentPublishTool.PublishAgentCapability(
                serviceId ?? string.Empty, categories ?? Array.Empty<string>(),
                content ?? string.Empty, priceSats, l402Endpoint, targetUrl, hashtags,
                agentService, apiService, cancellationToken),
            AgentServicesAction.unpublish => await AgentUnpublishTool.UnpublishAgentCapability(
                serviceId ?? string.Empty, reason, agentService, cancellationToken),
            AgentServicesAction.attest => await AgentAttestationTool.PublishAgentAttestation(
                subjectPubkey ?? string.Empty, agreementId ?? string.Empty, rating,
                content ?? string.Empty, proof, agentService, cancellationToken),
            AgentServicesAction.reputation => await AgentAttestationTool.GetAgentReputation(
                pubkey ?? string.Empty, limit, agentService, cancellationToken),
            _ => ActionErrors.Unknown(
                "agent_services", "action", action,
                AgentServicesAction.discover, AgentServicesAction.request,
                AgentServicesAction.settle, AgentServicesAction.publish,
                AgentServicesAction.unpublish, AgentServicesAction.attest,
                AgentServicesAction.reputation),
        };
}
