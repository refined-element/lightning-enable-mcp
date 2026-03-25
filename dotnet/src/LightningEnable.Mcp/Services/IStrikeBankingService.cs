using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Service for Strike banking operations: bank payment methods, payouts, and deposits.
/// Enables ACH/wire transfers to and from Strike accounts.
///
/// Configuration: Set STRIKE_API_KEY environment variable (same key as StrikeWalletService).
/// Get your API key from: https://dashboard.strike.me/
/// </summary>
public interface IStrikeBankingService
{
    /// <summary>
    /// Whether the Strike API key is configured.
    /// </summary>
    bool IsConfigured { get; }

    // Payment Methods

    /// <summary>
    /// Creates a bank payment method (ACH or wire) linked to the Strike account.
    /// </summary>
    Task<BankPaymentMethodResult> CreateBankPaymentMethodAsync(CreateBankPaymentMethodRequest request, CancellationToken ct = default);

    /// <summary>
    /// Lists all bank payment methods on the Strike account.
    /// </summary>
    Task<ListBankPaymentMethodsResult> ListBankPaymentMethodsAsync(CancellationToken ct = default);

    // Payout Originators

    /// <summary>
    /// Creates a payout originator (beneficiary entity for payouts).
    /// </summary>
    Task<PayoutOriginatorResult> CreatePayoutOriginatorAsync(CreatePayoutOriginatorRequest request, CancellationToken ct = default);

    /// <summary>
    /// Lists all payout originators on the Strike account.
    /// </summary>
    Task<ListPayoutOriginatorsResult> ListPayoutOriginatorsAsync(CancellationToken ct = default);

    // Payouts

    /// <summary>
    /// Creates a payout (withdrawal to bank account). Must be initiated separately.
    /// </summary>
    Task<PayoutResult> CreatePayoutAsync(CreatePayoutRequest request, CancellationToken ct = default);

    /// <summary>
    /// Initiates a previously created payout, triggering the bank transfer.
    /// </summary>
    Task<PayoutResult> InitiatePayoutAsync(string payoutId, CancellationToken ct = default);

    /// <summary>
    /// Lists all payouts on the Strike account.
    /// </summary>
    Task<ListPayoutsResult> ListPayoutsAsync(CancellationToken ct = default);

    // Deposits

    /// <summary>
    /// Initiates a deposit (pull funds from bank into Strike account).
    /// </summary>
    Task<DepositResult> InitiateDepositAsync(InitiateDepositRequest request, CancellationToken ct = default);

    /// <summary>
    /// Estimates the fee for a deposit before initiating it.
    /// </summary>
    Task<DepositFeeResult> EstimateDepositFeeAsync(EstimateDepositFeeRequest request, CancellationToken ct = default);
}
