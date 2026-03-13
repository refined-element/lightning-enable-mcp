using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for creating L402 payment challenges to charge other agents or users.
/// This is the "producer" side of L402 — merchants create challenges, payers pay them.
/// </summary>
[McpServerToolType]
public static class CreateL402ChallengeTool
{
    /// <summary>
    /// Creates an L402 payment challenge (Lightning invoice + macaroon) for a resource.
    /// </summary>
    [McpServerTool(Name = "create_l402_challenge"), Description(
        "Create an L402 payment challenge to charge another agent or user for accessing a resource. " +
        "Returns a Lightning invoice and macaroon. The payer must pay the invoice and present " +
        "the L402 token (macaroon:preimage) back to you for verification. " +
        "Requires LIGHTNING_ENABLE_API_KEY with an Agentic Commerce subscription.")]
    public static async Task<string> CreateL402Challenge(
        [Description("Resource identifier - URL, service name, or description of what you're charging for")] string resource,
        [Description("Price in satoshis to charge")] long priceSats,
        [Description("Description shown on the Lightning invoice")] string? description = null,
        ILightningEnableApiService? apiService = null,
        CancellationToken cancellationToken = default)
    {
        // Input validation
        if (string.IsNullOrWhiteSpace(resource))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Resource identifier is required. Provide a URL, service name, or description of what you're charging for."
            });
        }

        if (priceSats <= 0)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Price must be greater than 0 sats"
            });
        }

        if (apiService == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Lightning Enable API service not available"
            });
        }

        if (!apiService.IsConfigured)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Lightning Enable API key not configured. " +
                        "Set LIGHTNING_ENABLE_API_KEY environment variable or add 'lightningEnableApiKey' to ~/.lightning-enable/config.json. " +
                        "Requires an Agentic Commerce subscription at https://lightningenable.com."
            });
        }

        try
        {
            var result = await apiService.CreateChallengeAsync(resource, priceSats, description, cancellationToken);

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
                challenge = new
                {
                    invoice = result.Invoice,
                    macaroon = result.Macaroon,
                    paymentHash = result.PaymentHash,
                    expiresAt = result.ExpiresAt
                },
                resource,
                priceSats,
                instructions = new
                {
                    forPayer = $"Pay the Lightning invoice, then present the L402 token: 'L402 {result.Macaroon}:<preimage>' " +
                               "where <preimage> is the proof of payment received after paying the invoice.",
                    tokenFormat = "L402 {macaroon}:{preimage}",
                    verifyWith = "After receiving the L402 token from the payer, use verify_l402_payment to confirm payment before granting access."
                },
                message = $"L402 challenge created for {priceSats} sats. Share the invoice with the payer."
            }, new JsonSerializerOptions { WriteIndented = true });
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
