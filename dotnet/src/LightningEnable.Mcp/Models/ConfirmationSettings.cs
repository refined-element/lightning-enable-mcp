using System.Text.Json.Serialization;

namespace LightningEnable.Mcp.Models;

/// <summary>
/// Where the out-of-band confirmation code for an over-threshold payment is delivered.
/// <para/>
/// The code must never reach the model — it is the operator's approval, not the agent's.
/// A local server with a human at the terminal can print it to stderr; a hosted server
/// (claude.ai connector, Docker, a fleet) has nobody reading stderr, and on a shared host
/// the agent may even be able to read it. Hence an explicit channel.
/// </summary>
public enum ConfirmationChannelKind
{
    /// <summary>Print the code to the server console/stderr. The local default; unchanged.</summary>
    Stderr,

    /// <summary>
    /// Refuse over-threshold payments outright — no code is generated at all. The safe
    /// posture for a hosted deployment where nobody watches stderr.
    /// </summary>
    Refuse,

    /// <summary>POST the pending confirmation to an operator-controlled URL, HMAC signed.</summary>
    Webhook,

    /// <summary>Append the pending confirmation as a JSON line to a file the operator tails.</summary>
    File
}

/// <summary>
/// The <c>confirmation</c> section of <c>~/.lightning-enable/config.json</c>.
/// Every value can also come from an environment variable (env wins) — see
/// <see cref="Services.ConfirmationChannelResolver"/>.
/// </summary>
public class ConfirmationSettings
{
    /// <summary>
    /// <c>stderr</c> | <c>refuse</c> | <c>webhook</c> | <c>file</c>. Unset means "decide
    /// automatically": stderr, or refuse when <c>LIGHTNING_ENABLE_HOSTED=1</c> and stdin is
    /// not a TTY.
    /// </summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    /// <summary>Operator URL the <c>webhook</c> channel POSTs to. Required for that channel.</summary>
    [JsonPropertyName("webhookUrl")]
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Shared secret for the <c>X-LightningEnable-Signature</c> HMAC. Required for the
    /// <c>webhook</c> channel — an unsigned confirmation POST is spoofable.
    /// </summary>
    [JsonPropertyName("webhookSecret")]
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// Path the <c>file</c> channel appends to. Defaults to
    /// <c>~/.lightning-enable/confirmations.jsonl</c>. Created 0600 on POSIX.
    /// </summary>
    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    /// <summary>
    /// Seconds a confirmation code stays valid. Default 120 (right for <c>stderr</c>); 300-600
    /// is recommended for <c>webhook</c>/<c>file</c>, where a human relays asynchronously.
    /// Clamped to 30..900. Env: <c>LIGHTNING_ENABLE_CONFIRMATION_TTL_SECONDS</c>.
    /// </summary>
    [JsonPropertyName("ttlSeconds")]
    public int? TtlSeconds { get; set; }
}
