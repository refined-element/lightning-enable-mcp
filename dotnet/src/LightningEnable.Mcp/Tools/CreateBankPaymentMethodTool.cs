using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for linking a bank account to a Strike account for ACH/wire/SEPA/FPS/BSB transfers.
/// </summary>
[McpServerToolType]
public static class CreateBankPaymentMethodTool
{
    private static readonly HashSet<string> ValidTransferTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACH", "US_DOMESTIC_WIRE", "SEPA", "FPS", "BSB"
    };

    private static readonly HashSet<string> RoutingRequiredTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACH", "US_DOMESTIC_WIRE"
    };

    private static readonly HashSet<string> ValidBeneficiaryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "INDIVIDUAL", "COMPANY"
    };

    /// <summary>
    /// Creates a bank payment method (ACH, wire, SEPA, FPS, or BSB) linked to the Strike account.
    /// </summary>
    [McpServerTool(Name = "create_bank_payment_method"), Description("Link a bank account to your Strike account for ACH deposits/payouts, wire transfers, SEPA, FPS, or BSB.")]
    public static async Task<string> CreateBankPaymentMethod(
        [Description("Transfer type: ACH, US_DOMESTIC_WIRE, SEPA, FPS, or BSB")] string transferType,
        [Description("Bank account number (or IBAN for SEPA)")] string accountNumber,
        [Description("Name of the account holder or company")] string beneficiaryName,
        [Description("Type of beneficiary: INDIVIDUAL or COMPANY")] string beneficiaryType,
        [Description("Bank routing number (required for ACH and US_DOMESTIC_WIRE). Sort code for FPS, BSB code for BSB, BIC for SEPA.")] string? routingNumber = null,
        [Description("Account type: CHECKING or SAVINGS (required for ACH only)")] string? accountType = null,
        [Description("Name of the bank")] string? bankName = null,
        [Description("Beneficiary country code (e.g., US, GB)")] string? countryCode = null,
        IStrikeBankingService? bankingService = null,
        CancellationToken cancellationToken = default)
    {
        // Validate transferType
        if (string.IsNullOrWhiteSpace(transferType))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Transfer type is required (ACH, US_DOMESTIC_WIRE, SEPA, FPS, or BSB)"
            });
        }

        if (!ValidTransferTypes.Contains(transferType.Trim()))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Invalid transfer type '{transferType}'. Must be one of: ACH, US_DOMESTIC_WIRE, SEPA, FPS, BSB"
            });
        }

        // Validate accountNumber
        if (string.IsNullOrWhiteSpace(accountNumber))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Account number is required"
            });
        }

        // Validate beneficiaryName
        if (string.IsNullOrWhiteSpace(beneficiaryName))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Beneficiary name is required"
            });
        }

        // Validate beneficiaryType
        if (string.IsNullOrWhiteSpace(beneficiaryType))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Beneficiary type is required (INDIVIDUAL or COMPANY)"
            });
        }

        if (!ValidBeneficiaryTypes.Contains(beneficiaryType.Trim()))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Invalid beneficiary type '{beneficiaryType}'. Must be INDIVIDUAL or COMPANY"
            });
        }

        // Validate routingNumber required for ACH and US_DOMESTIC_WIRE
        if (RoutingRequiredTypes.Contains(transferType.Trim()) && string.IsNullOrWhiteSpace(routingNumber))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Routing number is required for {transferType.ToUpperInvariant()} transfers"
            });
        }

        // Validate accountType required for ACH
        if (string.Equals(transferType.Trim(), "ACH", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(accountType))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Account type is required for ACH transfers (CHECKING or SAVINGS)"
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
            var request = new CreateBankPaymentMethodRequest
            {
                TransferType = transferType.ToUpperInvariant(),
                AccountNumber = accountNumber,
                RoutingNumber = routingNumber,
                AccountType = accountType?.ToUpperInvariant(),
                BankName = bankName,
                Beneficiaries = new List<BankBeneficiary>
                {
                    new BankBeneficiary
                    {
                        Type = beneficiaryType.ToUpperInvariant(),
                        Name = beneficiaryName,
                        Address = countryCode != null ? new BeneficiaryAddress { Country = countryCode.ToUpperInvariant() } : null
                    }
                }
            };

            var result = await bankingService.CreateBankPaymentMethodAsync(request, cancellationToken);

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
                paymentMethod = new
                {
                    id = result.Id,
                    transferType = result.TransferType,
                    accountNumber = result.AccountNumber,
                    state = result.State
                },
                message = $"Bank payment method created successfully. State: {result.State}"
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
