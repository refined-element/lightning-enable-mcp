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
    /// Hard maximum satoshis allowed per individual request.
    /// This is a system-enforced limit that cannot be exceeded at runtime.
    /// </summary>
    public long HardMaxSatsPerRequest { get; set; } = 10000;

    /// <summary>
    /// Hard maximum satoshis allowed for the entire session.
    /// This is a system-enforced limit that cannot be exceeded at runtime.
    /// </summary>
    public long HardMaxSatsPerSession { get; set; } = 100000;
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
