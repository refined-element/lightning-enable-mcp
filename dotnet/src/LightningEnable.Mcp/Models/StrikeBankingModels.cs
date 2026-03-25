using System.Text.Json.Serialization;

namespace LightningEnable.Mcp.Models;

#region Request DTOs (Tool → Service)

/// <summary>
/// Request to create a bank payment method (ACH, wire, SEPA, etc.) on Strike.
/// </summary>
public class CreateBankPaymentMethodRequest
{
    /// <summary>
    /// Transfer type: ACH, US_DOMESTIC_WIRE, SEPA, FPS, or BSB.
    /// </summary>
    public required string TransferType { get; init; }

    /// <summary>
    /// Bank account number.
    /// </summary>
    public required string AccountNumber { get; init; }

    /// <summary>
    /// Routing number (required for ACH and US_DOMESTIC_WIRE).
    /// </summary>
    public string? RoutingNumber { get; init; }

    /// <summary>
    /// Account type: CHECKING or SAVINGS (ACH only).
    /// </summary>
    public string? AccountType { get; init; }

    /// <summary>
    /// Name of the bank.
    /// </summary>
    public string? BankName { get; init; }

    /// <summary>
    /// Beneficiaries associated with the payment method.
    /// </summary>
    public List<BankBeneficiary>? Beneficiaries { get; init; }
}

/// <summary>
/// Beneficiary for a bank payment method.
/// </summary>
public class BankBeneficiary
{
    /// <summary>
    /// Beneficiary type: INDIVIDUAL or COMPANY.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Full name of the beneficiary.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Address of the beneficiary.
    /// </summary>
    public BeneficiaryAddress? Address { get; init; }
}

/// <summary>
/// Address for a beneficiary.
/// </summary>
public class BeneficiaryAddress
{
    public required string Country { get; init; }
    public string? State { get; init; }
    public string? City { get; init; }
    public string? PostalCode { get; init; }
    public string? Line1 { get; init; }
}

/// <summary>
/// Request to create a payout originator on Strike.
/// </summary>
public class CreatePayoutOriginatorRequest
{
    /// <summary>
    /// Originator type: INDIVIDUAL or COMPANY.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Full name (for INDIVIDUAL type).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Company name (for COMPANY type).
    /// </summary>
    public string? CompanyName { get; init; }

    /// <summary>
    /// Address of the originator.
    /// </summary>
    public required OriginatorAddress Address { get; init; }
}

/// <summary>
/// Address for a payout originator.
/// </summary>
public class OriginatorAddress
{
    public required string Country { get; init; }
    public string? State { get; init; }
    public string? City { get; init; }
    public string? PostalCode { get; init; }
    public string? Line1 { get; init; }
}

/// <summary>
/// Request to create a payout (withdrawal) on Strike.
/// </summary>
public class CreatePayoutRequest
{
    /// <summary>
    /// ID of the approved payout originator (beneficiary entity).
    /// </summary>
    public required string PayoutOriginatorId { get; init; }

    /// <summary>
    /// ID of the bank payment method to pay out to.
    /// </summary>
    public required string PaymentMethodId { get; init; }

    /// <summary>
    /// Amount to pay out.
    /// </summary>
    public required string Amount { get; init; }

    /// <summary>
    /// Currency of the payout (e.g., USD, EUR).
    /// </summary>
    public required string Currency { get; init; }

    /// <summary>
    /// Fee policy: INCLUSIVE (fee deducted from amount) or EXCLUSIVE (fee added on top).
    /// </summary>
    public string? FeePolicy { get; init; }
}

/// <summary>
/// Request to initiate a deposit (funding) on Strike.
/// </summary>
public class InitiateDepositRequest
{
    /// <summary>
    /// ID of the bank payment method to deposit from.
    /// </summary>
    public required string PaymentMethodId { get; init; }

    /// <summary>
    /// Amount to deposit.
    /// </summary>
    public required string Amount { get; init; }

    /// <summary>
    /// Currency of the deposit (e.g., USD, EUR).
    /// </summary>
    public required string Currency { get; init; }

    /// <summary>
    /// Fee policy: INCLUSIVE or EXCLUSIVE.
    /// </summary>
    public string? FeePolicy { get; init; }
}

/// <summary>
/// Request to estimate deposit fees on Strike.
/// </summary>
public class EstimateDepositFeeRequest
{
    /// <summary>
    /// ID of the bank payment method.
    /// </summary>
    public required string PaymentMethodId { get; init; }

    /// <summary>
    /// Amount to estimate fees for.
    /// </summary>
    public required string Amount { get; init; }

    /// <summary>
    /// Currency of the deposit (e.g., USD, EUR).
    /// </summary>
    public required string Currency { get; init; }
}

#endregion

#region Result Types (Service → Tool)

/// <summary>
/// Result of creating or retrieving a bank payment method.
/// </summary>
public class BankPaymentMethodResult
{
    public bool Success { get; init; }
    public string? Id { get; init; }
    public string? TransferType { get; init; }
    public string? AccountNumber { get; init; }
    public string? State { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static BankPaymentMethodResult Succeeded(string id, string transferType, string? accountNumber, string? state)
        => new() { Success = true, Id = id, TransferType = transferType, AccountNumber = accountNumber, State = state };

    public static BankPaymentMethodResult Failed(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };
}

/// <summary>
/// Info about a single bank payment method, used in list results.
/// </summary>
public class BankPaymentMethodInfo
{
    public required string Id { get; init; }
    public required string TransferType { get; init; }
    public string? AccountNumber { get; init; }
    public string? State { get; init; }
    public string? Currency { get; init; }
    public List<string>? SupportedActions { get; init; }
}

/// <summary>
/// Result of listing bank payment methods.
/// </summary>
public class ListBankPaymentMethodsResult
{
    public bool Success { get; init; }
    public List<BankPaymentMethodInfo>? Items { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static ListBankPaymentMethodsResult Succeeded(List<BankPaymentMethodInfo> items)
        => new() { Success = true, Items = items };

    public static ListBankPaymentMethodsResult Failed(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };
}

/// <summary>
/// Result of creating or retrieving a payout originator.
/// </summary>
public class PayoutOriginatorResult
{
    public bool Success { get; init; }
    public string? Id { get; init; }
    public string? State { get; init; }
    public string? Name { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static PayoutOriginatorResult Succeeded(string id, string? state, string? name)
        => new() { Success = true, Id = id, State = state, Name = name };

    public static PayoutOriginatorResult Failed(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };
}

/// <summary>
/// Info about a single payout originator, used in list results.
/// </summary>
public class PayoutOriginatorInfo
{
    public required string Id { get; init; }
    public string? State { get; init; }
    public string? Type { get; init; }
    public string? Name { get; init; }
}

/// <summary>
/// Result of listing payout originators.
/// </summary>
public class ListPayoutOriginatorsResult
{
    public bool Success { get; init; }
    public List<PayoutOriginatorInfo>? Items { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static ListPayoutOriginatorsResult Succeeded(List<PayoutOriginatorInfo> items)
        => new() { Success = true, Items = items };

    public static ListPayoutOriginatorsResult Failed(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };
}

/// <summary>
/// Result of creating or retrieving a payout.
/// </summary>
public class PayoutResult
{
    public bool Success { get; init; }
    public string? Id { get; init; }
    public string? State { get; init; }
    public string? Amount { get; init; }
    public string? Currency { get; init; }
    public string? Fee { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static PayoutResult Succeeded(string id, string? state, string? amount, string? currency, string? fee = null)
        => new() { Success = true, Id = id, State = state, Amount = amount, Currency = currency, Fee = fee };

    public static PayoutResult Failed(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };
}

/// <summary>
/// Info about a single payout, used in list results.
/// </summary>
public class PayoutInfo
{
    public required string Id { get; init; }
    public string? State { get; init; }
    public string? Amount { get; init; }
    public string? Currency { get; init; }
    public string? Fee { get; init; }
    public string? Created { get; init; }
    public string? Completed { get; init; }
}

/// <summary>
/// Result of listing payouts.
/// </summary>
public class ListPayoutsResult
{
    public bool Success { get; init; }
    public List<PayoutInfo>? Items { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static ListPayoutsResult Succeeded(List<PayoutInfo> items)
        => new() { Success = true, Items = items };

    public static ListPayoutsResult Failed(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };
}

/// <summary>
/// Result of initiating or retrieving a deposit.
/// </summary>
public class DepositResult
{
    public bool Success { get; init; }
    public string? Id { get; init; }
    public string? State { get; init; }
    public string? Amount { get; init; }
    public string? Currency { get; init; }
    public string? Fee { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static DepositResult Succeeded(string id, string? state, string? amount, string? currency, string? fee = null)
        => new() { Success = true, Id = id, State = state, Amount = amount, Currency = currency, Fee = fee };

    public static DepositResult Failed(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };
}

/// <summary>
/// Result of estimating deposit fees.
/// </summary>
public class DepositFeeResult
{
    public bool Success { get; init; }
    public string? FeeAmount { get; init; }
    public string? FeeCurrency { get; init; }
    public string? TotalAmount { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static DepositFeeResult Succeeded(string feeAmount, string feeCurrency, string? totalAmount)
        => new() { Success = true, FeeAmount = feeAmount, FeeCurrency = feeCurrency, TotalAmount = totalAmount };

    public static DepositFeeResult Failed(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };
}

#endregion

#region Strike API Response Models (Internal — for deserializing Strike HTTP responses)

/// <summary>
/// Reusable amount object returned by Strike API (amount + currency).
/// </summary>
internal class StrikeAmountResponse
{
    [JsonPropertyName("amount")]
    public string? Amount { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }
}

/// <summary>
/// Address object returned by Strike API.
/// </summary>
internal class StrikeAddressResponse
{
    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("postCode")]
    public string? PostCode { get; set; }

    [JsonPropertyName("line1")]
    public string? Line1 { get; set; }
}

/// <summary>
/// Beneficiary object returned by Strike API.
/// </summary>
internal class StrikeBeneficiaryResponse
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("dateOfBirth")]
    public string? DateOfBirth { get; set; }

    [JsonPropertyName("address")]
    public StrikeAddressResponse? Address { get; set; }
}

/// <summary>
/// Bank payment method response from Strike API.
/// </summary>
internal class StrikeBankPaymentMethodResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("transferType")]
    public string? TransferType { get; set; }

    [JsonPropertyName("accountNumber")]
    public string? AccountNumber { get; set; }

    [JsonPropertyName("routingNumber")]
    public string? RoutingNumber { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("beneficiaries")]
    public List<StrikeBeneficiaryResponse>? Beneficiaries { get; set; }

    [JsonPropertyName("supportedActions")]
    public List<string>? SupportedActions { get; set; }
}

/// <summary>
/// Payout originator response from Strike API.
/// </summary>
internal class StrikePayoutOriginatorResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("address")]
    public StrikeAddressResponse? Address { get; set; }
}

/// <summary>
/// Payout response from Strike API.
/// </summary>
internal class StrikePayoutResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("amount")]
    public StrikeAmountResponse? Amount { get; set; }

    [JsonPropertyName("fee")]
    public StrikeAmountResponse? Fee { get; set; }

    [JsonPropertyName("created")]
    public string? Created { get; set; }

    [JsonPropertyName("completed")]
    public string? Completed { get; set; }
}

/// <summary>
/// Deposit response from Strike API.
/// </summary>
internal class StrikeDepositResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("amount")]
    public StrikeAmountResponse? Amount { get; set; }

    [JsonPropertyName("fee")]
    public StrikeAmountResponse? Fee { get; set; }

    [JsonPropertyName("totalAmount")]
    public StrikeAmountResponse? TotalAmount { get; set; }

    [JsonPropertyName("created")]
    public string? Created { get; set; }

    [JsonPropertyName("settledAt")]
    public string? SettledAt { get; set; }
}

/// <summary>
/// Deposit fee estimate response from Strike API.
/// </summary>
internal class StrikeDepositFeeResponse
{
    [JsonPropertyName("fee")]
    public StrikeAmountResponse? Fee { get; set; }

    [JsonPropertyName("totalAmount")]
    public StrikeAmountResponse? TotalAmount { get; set; }
}

#endregion
