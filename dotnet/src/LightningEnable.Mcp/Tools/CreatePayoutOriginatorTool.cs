using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for creating a payout originator on a Strike account.
/// </summary>
[McpServerToolType]
public static class CreatePayoutOriginatorTool
{
    private static readonly HashSet<string> ValidOriginatorTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "INDIVIDUAL", "COMPANY"
    };

    /// <summary>
    /// Creates a payout originator (required before sending payouts). Must be approved by Strike.
    /// </summary>
    [McpServerTool(Name = "create_payout_originator"), Description("Create a payout originator on your Strike account. Required before sending payouts. Must be approved by Strike.")]
    public static async Task<string> CreatePayoutOriginator(
        [Description("Originator type: INDIVIDUAL or COMPANY")] string type,
        [Description("Country code (e.g., US)")] string countryCode,
        [Description("City")] string city,
        [Description("Postal/zip code")] string postalCode,
        [Description("Street address")] string addressLine1,
        [Description("First name (required for INDIVIDUAL)")] string? firstName = null,
        [Description("Last name (required for INDIVIDUAL)")] string? lastName = null,
        [Description("Company name (required for COMPANY)")] string? companyName = null,
        [Description("State/province code")] string? state = null,
        IStrikeBankingService? bankingService = null,
        CancellationToken cancellationToken = default)
    {
        // Validate type
        if (string.IsNullOrWhiteSpace(type))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Originator type is required (INDIVIDUAL or COMPANY)"
            });
        }

        if (!ValidOriginatorTypes.Contains(type.Trim()))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Invalid originator type '{type}'. Must be INDIVIDUAL or COMPANY"
            });
        }

        // Validate name fields based on type
        var isCompany = string.Equals(type.Trim(), "COMPANY", StringComparison.OrdinalIgnoreCase);

        if (isCompany)
        {
            if (string.IsNullOrWhiteSpace(companyName))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Company name is required for COMPANY originator type"
                });
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(firstName))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "First name is required for INDIVIDUAL originator type"
                });
            }

            if (string.IsNullOrWhiteSpace(lastName))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Last name is required for INDIVIDUAL originator type"
                });
            }
        }

        // Validate required address fields
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Country code is required"
            });
        }

        if (string.IsNullOrWhiteSpace(city))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "City is required"
            });
        }

        if (string.IsNullOrWhiteSpace(postalCode))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Postal/zip code is required"
            });
        }

        if (string.IsNullOrWhiteSpace(addressLine1))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Street address is required"
            });
        }

        // Validate service availability
        if (bankingService == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Strike banking service not available"
            });
        }

        if (!bankingService.IsConfigured)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Strike API key not configured. Set STRIKE_API_KEY environment variable."
            });
        }

        try
        {
            var name = isCompany ? companyName! : $"{firstName} {lastName}";

            var request = new CreatePayoutOriginatorRequest
            {
                Type = type.ToUpperInvariant(),
                Name = name,
                Address = new OriginatorAddress
                {
                    Country = countryCode.ToUpperInvariant(),
                    State = state,
                    City = city,
                    PostalCode = postalCode,
                    Line1 = addressLine1
                }
            };

            var result = await bankingService.CreatePayoutOriginatorAsync(request, cancellationToken);

            if (!result.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = result.ErrorMessage,
                    errorCode = result.ErrorCode
                });
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                provider = "Strike",
                originator = new
                {
                    id = result.Id,
                    state = result.State,
                    type = type.ToUpperInvariant(),
                    name = result.Name
                },
                message = $"Payout originator created. State: {result.State}. Must be APPROVED before payouts can be initiated."
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
