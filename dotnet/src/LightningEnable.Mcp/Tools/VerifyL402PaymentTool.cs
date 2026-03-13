using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for verifying L402 tokens (macaroon + preimage) to confirm payment.
/// This is the "producer" side — verify that the payer has actually paid before granting access.
/// </summary>
[McpServerToolType]
public static class VerifyL402PaymentTool
{
    /// <summary>
    /// Verifies an L402 token to confirm payment was made.
    /// </summary>
    [McpServerTool(Name = "verify_l402_payment"), Description(
        "Verify an L402 token (macaroon + preimage) to confirm payment was made. " +
        "Use this after receiving an L402 token from a payer to validate they paid " +
        "before granting access to the resource. " +
        "Requires LIGHTNING_ENABLE_API_KEY with an Agentic Commerce subscription.")]
    public static async Task<string> VerifyL402Payment(
        [Description("Base64-encoded macaroon from the L402 token")] string macaroon,
        [Description("Hex-encoded preimage (proof of payment)")] string preimage,
        ILightningEnableApiService? apiService = null,
        CancellationToken cancellationToken = default)
    {
        // Input validation
        if (string.IsNullOrWhiteSpace(macaroon))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Macaroon is required. This is the base64-encoded macaroon from the L402 token."
            });
        }

        if (string.IsNullOrWhiteSpace(preimage))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Preimage is required. This is the hex-encoded proof of payment from the L402 token."
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
            var result = await apiService.VerifyTokenAsync(macaroon.Trim(), preimage.Trim(), cancellationToken);

            if (!result.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage
                });
            }

            if (result.Valid)
            {
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    valid = true,
                    resource = result.Resource,
                    message = "Payment verified. The payer has paid — you can now grant access to the resource."
                });
            }
            else
            {
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    valid = false,
                    message = "Payment verification failed. The token is invalid or the invoice has not been paid. Do NOT grant access."
                });
            }
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
