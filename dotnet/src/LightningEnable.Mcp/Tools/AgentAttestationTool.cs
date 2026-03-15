using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for publishing and querying agent attestations/reviews (kind 38403).
/// Enables reputation building on the Nostr network.
/// </summary>
[McpServerToolType]
public static class AgentAttestationTool
{
    /// <summary>
    /// Publishes an attestation/review for an agent after a completed agreement.
    /// </summary>
    [McpServerTool(Name = "publish_agent_attestation"), Description(
        "Publish an attestation (review) for an agent after a completed agreement. " +
        "Creates a kind 38403 event that builds the agent's on-protocol reputation. " +
        "Requires LIGHTNING_ENABLE_API_KEY.")]
    public static async Task<string> PublishAgentAttestation(
        [Description("Pubkey of the agent being reviewed")] string subjectPubkey,
        [Description("Event ID of the agreement this review is for")] string agreementId,
        [Description("Rating from 1-5")] int rating,
        [Description("Free-text review content")] string content,
        [Description("Optional: hash of L402 payment preimage as proof of real transaction")] string? proof = null,
        IAgentService? agentService = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Input validation
            if (string.IsNullOrWhiteSpace(subjectPubkey))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Subject pubkey is required. This is the pubkey of the agent you are reviewing."
                });
            }

            if (string.IsNullOrWhiteSpace(agreementId))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Agreement ID is required. This is the event ID of the agreement this review is for."
                });
            }

            if (rating < 1 || rating > 5)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Rating must be between 1 and 5."
                });
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Review content is required."
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
                            "Set LIGHTNING_ENABLE_API_KEY environment variable or add 'lightningEnableApiKey' to ~/.lightning-enable/config.json."
                });
            }

            var result = await agentService.PublishAttestationAsync(
                subjectPubkey, agreementId, rating, content, proof, cancellationToken);

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
                attestationId = result.AttestationId,
                subjectPubkey,
                agreementId,
                rating,
                proof = proof != null ? "included" : "none",
                message = $"Attestation published successfully as kind 38403 event. Rating: {rating}/5.",
                nextSteps = new
                {
                    viewReputation = $"Use get_agent_reputation(pubkey=\"{subjectPubkey}\") to see the agent's full reputation.",
                    discover = "Other agents will see this attestation when evaluating the reviewed agent."
                }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Error publishing attestation: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Queries attestations for an agent and returns their reputation score.
    /// </summary>
    [McpServerTool(Name = "get_agent_reputation"), Description(
        "Get an agent's reputation score and reviews. " +
        "Queries kind 38403 attestation events for the given pubkey. " +
        "Returns average rating and individual reviews.")]
    public static async Task<string> GetAgentReputation(
        [Description("Pubkey of the agent to query reputation for")] string pubkey,
        [Description("Maximum number of attestations to return (default: 20)")] int limit = 20,
        IAgentService? agentService = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pubkey))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Agent pubkey is required."
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

            var result = await agentService.GetAttestationsAsync(pubkey, limit, cancellationToken);

            if (!result.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage
                });
            }

            // Compute average rating
            var ratedAttestations = result.Attestations
                .Where(a => a.Rating >= 1 && a.Rating <= 5)
                .ToList();

            double? averageRating = ratedAttestations.Count > 0
                ? ratedAttestations.Average(a => a.Rating)
                : null;

            var formattedAttestations = result.Attestations.Select(att => new
            {
                eventId = att.EventId,
                reviewerPubkey = att.ReviewerPubkey,
                rating = att.Rating,
                content = att.Content,
                agreementId = att.AgreementId,
                hasProof = att.Proof != null,
                createdAt = att.CreatedAt
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                success = true,
                pubkey,
                averageRating = averageRating.HasValue ? Math.Round(averageRating.Value, 2) : (double?)null,
                totalReviews = result.Attestations.Count,
                ratedReviews = ratedAttestations.Count,
                verifiedReviews = result.Attestations.Count(a => a.Proof != null),
                attestations = formattedAttestations,
                hint = averageRating.HasValue
                    ? $"Agent has a {averageRating.Value:F1}/5.0 rating from {ratedAttestations.Count} review(s)."
                    : "No rated reviews found for this agent."
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Error querying agent reputation: {ex.Message}"
            });
        }
    }
}
