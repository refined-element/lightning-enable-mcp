using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Service for managing spending budget limits with multi-tier approval.
/// Configuration is READ-ONLY - loaded from user config file at startup.
/// </summary>
public interface IBudgetService
{
    /// <summary>
    /// Checks what approval level is required for a payment.
    /// Uses USD-based tier thresholds converted to sats.
    /// </summary>
    /// <param name="amountSats">Amount to spend in satoshis.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Approval check result with level and details.</returns>
    Task<ApprovalCheckResult> CheckApprovalLevelAsync(long amountSats, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a given amount can be spent within current budget limits.
    /// Simple yes/no check for backward compatibility.
    /// </summary>
    /// <param name="amountSats">Amount to spend in satoshis.</param>
    /// <returns>Budget check result with allowed status and details.</returns>
    BudgetCheckResult CheckBudget(long amountSats);

    /// <summary>
    /// Atomically reserves <paramref name="amountSats"/> against the effective session cap
    /// (config + tighten-only runtime caps), evaluating settled spend PLUS all active
    /// reservations in the same critical section as the insert. This is the funds-safety
    /// primitive that closes the check-then-pay race: two concurrent payments can never
    /// both pass against the same pre-payment balance.
    ///
    /// Fails closed if the BTC price is unavailable (the caps are USD-derived). On success
    /// the caller MUST later call exactly one of <see cref="CommitReservation"/> (funds
    /// moved / may have moved) or <see cref="ReleaseReservation"/> (proven no funds moved).
    /// The reservation lock is NOT held across the wallet call.
    /// </summary>
    /// <param name="amountSats">Maximum debit to reserve, in satoshis (include a fee headroom for on-chain).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SpendReservationResult> TryReserveAsync(long amountSats, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits a reservation as settled spend of <paramref name="actualDebitSats"/> (which
    /// may be less than the reserved maximum, e.g. when the on-chain fee came in under the
    /// reserved headroom — the unused headroom is released). Idempotent: an unknown or
    /// already-resolved reservation id is a no-op. Use for settled AND pending/unknown
    /// outcomes — a payment that may have moved funds must keep consuming budget.
    /// </summary>
    void CommitReservation(string reservationId, long actualDebitSats);

    /// <summary>
    /// Releases a reservation without recording any spend. Use ONLY when the wallet or
    /// provider proves no funds moved (a hard, pre-submission failure). Idempotent.
    /// </summary>
    void ReleaseReservation(string reservationId);

    /// <summary>
    /// Records that an amount was spent, with no prior reservation. Prefer
    /// <see cref="TryReserveAsync"/> + <see cref="CommitReservation"/> on any path that
    /// calls the wallet — this remains only for callers that settle without a reservation.
    /// </summary>
    /// <param name="amountSats">Amount spent in satoshis.</param>
    void RecordSpend(long amountSats);

    /// <summary>
    /// Applies tighten-only runtime spending caps (sats). An agent may only LOWER
    /// its effective per-request / per-session limits, never raise them above the
    /// operator-set config-file limits (or an existing tighter runtime cap). This is
    /// the .NET counterpart of the Python configure_budget tool — a prompt-injected
    /// agent must not be able to loosen its own caps and then drain the wallet.
    /// </summary>
    /// <param name="perRequestSats">Requested per-request cap in satoshis.</param>
    /// <param name="perSessionSats">Requested per-session cap in satoshis.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating success (and the new effective caps) or rejection.</returns>
    Task<ConfigureBudgetResult> ConfigureBudgetAsync(long perRequestSats, long perSessionSats, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current budget configuration (runtime state).
    /// </summary>
    BudgetConfig GetConfig();

    /// <summary>
    /// Gets the user's budget configuration from config file.
    /// This is READ-ONLY and cannot be modified at runtime.
    /// </summary>
    UserBudgetConfiguration GetUserConfiguration();

    /// <summary>
    /// Resets the session spending to zero.
    /// </summary>
    void ResetSession();

    /// <summary>
    /// Checks if cooldown period has elapsed since last payment.
    /// </summary>
    bool IsCooldownElapsed();

    /// <summary>
    /// Records that a payment was just made (for cooldown tracking).
    /// </summary>
    void RecordPaymentTime();

    /// <summary>
    /// Creates a pending confirmation with a random nonce, bound to a specific amount.
    /// The nonce can be checked via a separate verify_confirmation_code tool call.
    /// </summary>
    /// <param name="amountSats">Payment amount in satoshis.</param>
    /// <param name="amountUsd">Payment amount in USD (for display).</param>
    /// <param name="toolName">Which tool requested confirmation.</param>
    /// <param name="description">Human-readable description (URL, invoice prefix) — for display only.</param>
    /// <param name="destination">The exact payment target (invoice / URL / on-chain address); bound and checked on consume so the code cannot be redirected (#21).</param>
    /// <returns>The pending confirmation with its nonce code.</returns>
    PendingConfirmation CreatePendingConfirmation(long amountSats, decimal amountUsd, string toolName, string description, string destination);

    /// <summary>
    /// Validates a nonce and checks expiry WITHOUT consuming it.
    /// Use this in verify_confirmation_code to verify the nonce is valid.
    /// Returns null if the nonce is invalid or expired.
    /// </summary>
    /// <param name="nonce">The 6-character confirmation nonce.</param>
    /// <returns>The pending confirmation, or null if invalid/expired.</returns>
    PendingConfirmation? ValidateConfirmation(string nonce);

    /// <summary>
    /// Validates a nonce, checks expiry, verifies the approved amount matches the
    /// amount about to be paid (C-3 binding), and consumes it (one-time use).
    /// Returns null if the nonce is invalid, expired, already consumed, OR the amount
    /// does not match what was approved. On an amount mismatch the nonce is NOT
    /// consumed (a correct-amount retry can still succeed); only an exact match is
    /// consumed and returned.
    /// </summary>
    /// <param name="nonce">The confirmation code.</param>
    /// <param name="expectedAmountSats">The amount about to be paid; must equal the approved amount.</param>
    /// <param name="expectedToolName">The tool consuming the code; must equal the tool the confirmation was created for (prevents cross-tool replay).</param>
    /// <param name="expectedDestination">The payment target about to be paid; must equal the approved destination (#21 anti-redirect). Compared after trimming.</param>
    /// <returns>The confirmed pending confirmation, or null if invalid / amount-, tool-, or destination-mismatched.</returns>
    PendingConfirmation? ValidateAndConsumeConfirmation(string nonce, long expectedAmountSats, string expectedToolName, string expectedDestination);

    /// <summary>
    /// Purges expired pending confirmations from memory.
    /// </summary>
    void CleanExpiredConfirmations();
}
