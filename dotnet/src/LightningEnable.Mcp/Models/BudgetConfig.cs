namespace LightningEnable.Mcp.Models;

/// <summary>
/// Configuration for spending budget limits.
/// </summary>
public class BudgetConfig
{
    /// <summary>
    /// Maximum satoshis allowed per individual request.
    /// Default: 1000 sats
    /// </summary>
    public long MaxSatsPerRequest { get; set; } = 1000;

    /// <summary>
    /// Maximum satoshis allowed for the entire session.
    /// Default: 10000 sats
    /// </summary>
    public long MaxSatsPerSession { get; set; } = 10000;

    /// <summary>
    /// Current amount spent in this session.
    /// </summary>
    public long SessionSpent { get; set; } = 0;

    /// <summary>
    /// Number of requests made in this session.
    /// </summary>
    public int RequestCount { get; set; } = 0;

    /// <summary>
    /// When the session started.
    /// </summary>
    public DateTime SessionStarted { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Remaining budget for this session.
    /// </summary>
    public long RemainingSessionBudget => MaxSatsPerSession - SessionSpent;

    /// <summary>
    /// Whether the session budget is exhausted.
    /// </summary>
    public bool IsBudgetExhausted => SessionSpent >= MaxSatsPerSession;

    /// <summary>
    /// Informational mirror of the effective per-request cap in sats (NOT a separate,
    /// independently enforced ceiling — that claim used to be here and was false). The
    /// real protections are the USD per-payment / per-session limits, the approval
    /// tiers, out-of-band confirmation for large amounts, the tighten-only runtime caps,
    /// and fail-closed-when-no-BTC-price.
    /// </summary>
    public long HardMaxSatsPerRequest { get; set; } = 10000;

    /// <summary>
    /// Informational mirror of the effective per-session cap in sats. See the note on
    /// <see cref="HardMaxSatsPerRequest"/> — this is not a separately enforced ceiling.
    /// </summary>
    public long HardMaxSatsPerSession { get; set; } = 100000;

    /// <summary>
    /// Runtime per-request cap (sats) set by the agent via configure_budget.
    /// Null when no runtime cap is in effect. Can only TIGHTEN (lower) the
    /// effective limit — never raise it above the operator's config-file limits.
    /// </summary>
    public long? RuntimeMaxPerRequestSats { get; set; }

    /// <summary>
    /// Runtime per-session cap (sats) set by the agent via configure_budget.
    /// Null when no runtime cap is in effect. Tighten-only.
    /// </summary>
    public long? RuntimeMaxPerSessionSats { get; set; }
}

/// <summary>
/// Result of a configure_budget (tighten-only) operation.
/// </summary>
public record ConfigureBudgetResult
{
    /// <summary>Whether the new (tighter) limits were applied.</summary>
    public bool Success { get; init; }

    /// <summary>Reason the request was rejected (e.g. an attempt to raise limits).</summary>
    public string? Error { get; init; }

    /// <summary>Effective per-request cap (sats) after the operation.</summary>
    public long EffectivePerRequestSats { get; init; }

    /// <summary>Effective per-session cap (sats) after the operation.</summary>
    public long EffectivePerSessionSats { get; init; }

    public static ConfigureBudgetResult Ok(long perRequest, long perSession) =>
        new() { Success = true, EffectivePerRequestSats = perRequest, EffectivePerSessionSats = perSession };

    public static ConfigureBudgetResult Fail(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>
/// Result of a budget check operation.
/// </summary>
public record BudgetCheckResult
{
    /// <summary>
    /// Whether the requested amount is within budget.
    /// </summary>
    public bool Allowed { get; init; }

    /// <summary>
    /// Reason if the request was denied.
    /// </summary>
    public string? DenialReason { get; init; }

    /// <summary>
    /// Amount remaining in session budget.
    /// </summary>
    public long RemainingSessionBudget { get; init; }

    /// <summary>
    /// Maximum allowed for a single request.
    /// </summary>
    public long MaxPerRequest { get; init; }

    /// <summary>
    /// Creates an allowed result.
    /// </summary>
    public static BudgetCheckResult Allow(long remaining, long maxPerRequest) =>
        new() { Allowed = true, RemainingSessionBudget = remaining, MaxPerRequest = maxPerRequest };

    /// <summary>
    /// Creates a denied result.
    /// </summary>
    public static BudgetCheckResult Deny(string reason, long remaining, long maxPerRequest) =>
        new() { Allowed = false, DenialReason = reason, RemainingSessionBudget = remaining, MaxPerRequest = maxPerRequest };
}
