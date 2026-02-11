using System.Text.Json.Serialization;

namespace LightningEnable.Mcp.Models;

/// <summary>
/// User-configurable budget settings stored in ~/.lightning-enable/config.json.
/// These settings are READ-ONLY at runtime - AI cannot modify them.
/// </summary>
public class UserBudgetConfiguration
{
    /// <summary>
    /// Currency for threshold values. Currently only "USD" is supported.
    /// </summary>
    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "USD";

    /// <summary>
    /// Tier thresholds in the configured currency (USD).
    /// </summary>
    [JsonPropertyName("tiers")]
    public TierThresholds Tiers { get; set; } = new();

    /// <summary>
    /// Maximum payment limits.
    /// </summary>
    [JsonPropertyName("limits")]
    public PaymentLimits Limits { get; set; } = new();

    /// <summary>
    /// Session-level settings.
    /// </summary>
    [JsonPropertyName("session")]
    public SessionSettings Session { get; set; } = new();

    /// <summary>
    /// Wallet connection settings.
    /// These can be set here instead of environment variables.
    /// </summary>
    [JsonPropertyName("wallets")]
    public WalletSettings Wallets { get; set; } = new();
}

/// <summary>
/// Wallet connection settings.
/// Values here are used if environment variables are not set.
/// </summary>
public class WalletSettings
{
    /// <summary>
    /// NWC (Nostr Wallet Connect) connection string.
    /// Format: nostr+walletconnect://pubkey?relay=...&secret=...
    /// Recommended for L402 - always returns preimage.
    /// </summary>
    [JsonPropertyName("nwcConnectionString")]
    public string? NwcConnectionString { get; set; }

    /// <summary>
    /// Strike API key from https://dashboard.strike.me/
    /// Strike returns preimage via lightning.preImage - L402 supported.
    /// </summary>
    [JsonPropertyName("strikeApiKey")]
    public string? StrikeApiKey { get; set; }

    /// <summary>
    /// OpenNode API key.
    /// WARNING: OpenNode does NOT return preimage - L402 will not work.
    /// </summary>
    [JsonPropertyName("openNodeApiKey")]
    public string? OpenNodeApiKey { get; set; }

    /// <summary>
    /// OpenNode environment: "production" (mainnet) or "dev" (testnet).
    /// Default: production
    /// </summary>
    [JsonPropertyName("openNodeEnvironment")]
    public string? OpenNodeEnvironment { get; set; }

    /// <summary>
    /// LND REST API host (e.g., "localhost:8080" or "mynode.local:8080").
    /// Recommended for L402 - always returns preimage.
    /// </summary>
    [JsonPropertyName("lndRestHost")]
    public string? LndRestHost { get; set; }

    /// <summary>
    /// LND macaroon in hex format (admin.macaroon).
    /// Required for LND wallet to make payments.
    /// Get with: xxd -ps -c 1000 ~/.lnd/data/chain/bitcoin/mainnet/admin.macaroon
    /// </summary>
    [JsonPropertyName("lndMacaroonHex")]
    public string? LndMacaroonHex { get; set; }

    /// <summary>
    /// Preferred wallet priority: "lnd", "nwc", "strike", or "opennode".
    /// Default priority: LND > NWC > Strike > OpenNode
    /// For L402: Use "lnd", "nwc", or "strike" - these return preimage.
    /// </summary>
    [JsonPropertyName("priority")]
    public string? Priority { get; set; }

    /// <summary>
    /// Wallets that do NOT support L402 (no preimage returned).
    /// Default: ["opennode"]. Override to allow/block wallets for L402.
    /// Set to empty array [] to allow all wallets.
    /// </summary>
    [JsonPropertyName("incompatibleL402Wallets")]
    public string[]? IncompatibleL402Wallets { get; set; }
}

/// <summary>
/// Tier thresholds that determine what level of approval is required.
/// All values are in USD.
///
/// NOTE: FormConfirm and UrlConfirm ideally use MCP elicitation for confirmation.
/// When elicitation is unavailable (e.g., Claude Code), a nonce-based confirmation
/// flow is used: the tool returns a one-time nonce, the user must call
/// confirm_payment(nonce) as a separate tool call (visible in the UI), then
/// retry the original tool with the confirmed nonce.
/// </summary>
public class TierThresholds
{
    /// <summary>
    /// Payments at or below this amount are auto-approved without any prompt.
    /// Default: $1.00 (raised from $0.10 for better UX since most clients lack elicitation)
    /// </summary>
    [JsonPropertyName("autoApprove")]
    public decimal AutoApprove { get; set; } = 1.00m;

    /// <summary>
    /// Payments above autoApprove but at or below this amount are logged but approved.
    /// Default: $5.00 (raised from $1.00)
    /// </summary>
    [JsonPropertyName("logAndApprove")]
    public decimal LogAndApprove { get; set; } = 5.00m;

    /// <summary>
    /// Payments above logAndApprove but at or below this amount require confirmation.
    /// With elicitation: User sees a prompt in the AI interface.
    /// Without elicitation: Nonce-based flow - user approves via separate confirm_payment tool call.
    /// Default: $25.00 (raised from $10.00)
    /// </summary>
    [JsonPropertyName("formConfirm")]
    public decimal FormConfirm { get; set; } = 25.00m;

    /// <summary>
    /// Payments above formConfirm but at or below this amount require strong confirmation.
    /// With elicitation: User must type the exact amount to confirm.
    /// Without elicitation: Nonce-based flow - user approves via separate confirm_payment tool call.
    /// Default: $100.00
    /// </summary>
    [JsonPropertyName("urlConfirm")]
    public decimal UrlConfirm { get; set; } = 100.00m;
}

/// <summary>
/// Maximum payment limits.
/// </summary>
public class PaymentLimits
{
    /// <summary>
    /// Maximum amount per single payment, even with URL confirmation.
    /// Payments above this are always denied.
    /// Default: $500.00 (set to null for no limit with URL confirmation)
    /// </summary>
    [JsonPropertyName("maxPerPayment")]
    public decimal? MaxPerPayment { get; set; } = 500.00m;

    /// <summary>
    /// Maximum total spending per session.
    /// Default: $100.00
    /// </summary>
    [JsonPropertyName("maxPerSession")]
    public decimal? MaxPerSession { get; set; } = 100.00m;
}

/// <summary>
/// Session-level settings.
/// </summary>
public class SessionSettings
{
    /// <summary>
    /// If true, require confirmation for the very first payment of the session.
    /// Default: false (disabled because most MCP clients don't support elicitation yet)
    /// </summary>
    [JsonPropertyName("requireApprovalForFirstPayment")]
    public bool RequireApprovalForFirstPayment { get; set; } = false;

    /// <summary>
    /// Minimum seconds between payments (prevents rapid-fire attacks).
    /// Default: 2 seconds
    /// </summary>
    [JsonPropertyName("cooldownSeconds")]
    public int CooldownSeconds { get; set; } = 2;
}

/// <summary>
/// Approval level required for a payment.
/// </summary>
public enum ApprovalLevel
{
    /// <summary>
    /// Payment is auto-approved, no user interaction needed.
    /// </summary>
    AutoApprove,

    /// <summary>
    /// Payment is approved but logged for user awareness.
    /// </summary>
    LogAndApprove,

    /// <summary>
    /// Payment requires user confirmation via MCP form elicitation.
    /// User sees prompt in AI interface.
    /// </summary>
    FormConfirm,

    /// <summary>
    /// Payment requires out-of-band confirmation via URL.
    /// User must open link in browser (AI cannot intercept).
    /// </summary>
    UrlConfirm,

    /// <summary>
    /// Payment is denied (exceeds limits).
    /// </summary>
    Deny
}

/// <summary>
/// Extended budget check result with approval level.
/// </summary>
public record ApprovalCheckResult
{
    /// <summary>
    /// The approval level required for this payment.
    /// </summary>
    public ApprovalLevel Level { get; init; }

    /// <summary>
    /// The amount in satoshis.
    /// </summary>
    public long AmountSats { get; init; }

    /// <summary>
    /// The amount in USD (for display).
    /// </summary>
    public decimal AmountUsd { get; init; }

    /// <summary>
    /// Reason if the payment is denied.
    /// </summary>
    public string? DenialReason { get; init; }

    /// <summary>
    /// Message to show user for confirmation prompts.
    /// </summary>
    public string? ConfirmationMessage { get; init; }

    /// <summary>
    /// URL for out-of-band confirmation (if Level == UrlConfirm).
    /// </summary>
    public string? ConfirmationUrl { get; init; }

    /// <summary>
    /// Remaining session budget in USD.
    /// </summary>
    public decimal RemainingSessionBudgetUsd { get; init; }

    /// <summary>
    /// Whether this payment can proceed (with or without confirmation).
    /// </summary>
    public bool CanProceed => Level != ApprovalLevel.Deny;

    /// <summary>
    /// Whether this payment requires any form of user confirmation.
    /// </summary>
    public bool RequiresConfirmation => Level == ApprovalLevel.FormConfirm || Level == ApprovalLevel.UrlConfirm;
}
