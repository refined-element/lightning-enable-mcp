using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>The channel that was chosen, where the choice came from, and any startup warning.</summary>
/// <param name="Kind">The resolved channel.</param>
/// <param name="Source">"env", "config", or "auto" — plus "-invalid" when a value was rejected.</param>
/// <param name="Warning">One line for the server console, or null when nothing is worth saying.</param>
public record ConfirmationChannelResolution(ConfirmationChannelKind Kind, string Source, string? Warning);

/// <summary>
/// Decides WHERE an over-threshold confirmation code goes. Pure and side-effect free so the
/// whole matrix is unit-testable; the caller does the env/TTY reading and the warning print.
/// <para/>
/// Precedence: environment variable &gt; config file &gt; automatic.
/// <para/>
/// Automatic means: keep the historical stderr behaviour, UNLESS stdin is not a TTY and the
/// operator has explicitly declared a hosted posture with <c>LIGHTNING_ENABLE_HOSTED=1</c>, in
/// which case over-threshold payments are refused rather than gated on a code nobody will
/// read. A non-TTY stdin alone never flips the default — an stdio MCP server always has a
/// piped stdin — it only earns a warning.
/// <para/>
/// An unparseable value fails CLOSED (refuse), not open: a typo in the channel name must not
/// silently restore the posture the operator was trying to move away from.
/// Kept in sync with the Python resolver in <c>confirmation_channel.py</c>.
/// </summary>
public static class ConfirmationChannelResolver
{
    /// <summary>Env var that overrides <c>confirmation.channel</c>.</summary>
    public const string ChannelEnvironmentVariable = "LIGHTNING_ENABLE_CONFIRMATION_CHANNEL";

    /// <summary>Env var that opts a deployment into the hosted (refuse-by-default) posture.</summary>
    public const string HostedEnvironmentVariable = "LIGHTNING_ENABLE_HOSTED";

    /// <summary>Env var override for <c>confirmation.webhookUrl</c>.</summary>
    public const string WebhookUrlEnvironmentVariable = "LIGHTNING_ENABLE_CONFIRMATION_WEBHOOK_URL";

    /// <summary>Env var override for <c>confirmation.webhookSecret</c>.</summary>
    public const string WebhookSecretEnvironmentVariable = "LIGHTNING_ENABLE_CONFIRMATION_WEBHOOK_SECRET";

    /// <summary>Env var override for <c>confirmation.filePath</c>.</summary>
    public const string FilePathEnvironmentVariable = "LIGHTNING_ENABLE_CONFIRMATION_FILE";

    /// <summary>The accepted channel names, for error messages.</summary>
    public const string ValidChannels = "stderr | refuse | webhook | file";

    /// <summary>Resolve the channel from the three inputs. Never throws.</summary>
    /// <param name="envChannel">Raw value of <see cref="ChannelEnvironmentVariable"/>.</param>
    /// <param name="configChannel">Raw value of <c>confirmation.channel</c>.</param>
    /// <param name="stdinIsTty">Whether a human could be watching this console.</param>
    /// <param name="hostedFlag">Raw value of <see cref="HostedEnvironmentVariable"/>.</param>
    public static ConfirmationChannelResolution Resolve(
        string? envChannel, string? configChannel, bool stdinIsTty, string? hostedFlag)
    {
        if (!string.IsNullOrWhiteSpace(envChannel))
        {
            return TryParse(envChannel, out var envKind)
                ? new ConfirmationChannelResolution(envKind, "env", null)
                : new ConfirmationChannelResolution(
                    ConfirmationChannelKind.Refuse,
                    "env-invalid",
                    $"{ChannelEnvironmentVariable}=\"{envChannel.Trim()}\" is not a valid approval channel "
                    + $"({ValidChannels}). Over-threshold payments will be REFUSED until it is corrected.");
        }

        if (!string.IsNullOrWhiteSpace(configChannel))
        {
            return TryParse(configChannel, out var configKind)
                ? new ConfirmationChannelResolution(configKind, "config", null)
                : new ConfirmationChannelResolution(
                    ConfirmationChannelKind.Refuse,
                    "config-invalid",
                    $"confirmation.channel=\"{configChannel.Trim()}\" in ~/.lightning-enable/config.json is not a "
                    + $"valid approval channel ({ValidChannels}). Over-threshold payments will be REFUSED until it is corrected.");
        }

        if (stdinIsTty)
        {
            // A human is at the terminal: stderr is genuinely out-of-band for the model.
            return new ConfirmationChannelResolution(ConfirmationChannelKind.Stderr, "auto", null);
        }

        if (IsHosted(hostedFlag))
        {
            return new ConfirmationChannelResolution(
                ConfirmationChannelKind.Refuse,
                "auto",
                $"{HostedEnvironmentVariable}=1 with no approval channel configured: over-threshold payments are "
                + "REFUSED, because a confirmation code on the stderr of a hosted server is one nobody reads (and, on a "
                + "shared host, one the agent may read). Set confirmation.channel to webhook or file to approve them out of band.");
        }

        return new ConfirmationChannelResolution(
            ConfirmationChannelKind.Stderr,
            "auto",
            "stdin is not a TTY: over-threshold confirmation codes go to this server's stderr, so nobody approves a "
            + "payment unless a human is reading its console or logs. Set confirmation.channel "
            + $"({ValidChannels}), or {HostedEnvironmentVariable}=1 to refuse such payments instead.");
    }

    /// <summary>Env var override for <c>confirmation.ttlSeconds</c>.</summary>
    public const string TtlEnvironmentVariable = "LIGHTNING_ENABLE_CONFIRMATION_TTL_SECONDS";

    /// <summary>Default code lifetime: right for stderr, where the human is at the console.</summary>
    public const int DefaultTtlSeconds = 120;

    /// <summary>Shortest allowed code lifetime.</summary>
    public const int MinTtlSeconds = 30;

    /// <summary>Longest allowed code lifetime (15 minutes).</summary>
    public const int MaxTtlSeconds = 900;

    /// <summary>
    /// Code lifetime in seconds: env &gt; config &gt; <see cref="DefaultTtlSeconds"/>, clamped to
    /// <see cref="MinTtlSeconds"/>..<see cref="MaxTtlSeconds"/>. An unparseable env value is
    /// ignored. For the asynchronous webhook and file channels, 300-600 is a sensible value.
    /// Kept in sync with <c>resolve_confirmation_ttl_seconds</c> in Python.
    /// </summary>
    public static int ResolveTtlSeconds(string? envTtl, int? configTtl)
    {
        var ttl = DefaultTtlSeconds;
        if (!string.IsNullOrWhiteSpace(envTtl)
            && int.TryParse(envTtl.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var envValue))
        {
            ttl = envValue;
        }
        else if (configTtl.HasValue)
        {
            ttl = configTtl.Value;
        }

        return Math.Clamp(ttl, MinTtlSeconds, MaxTtlSeconds);
    }

    /// <summary>Parse a channel name, case- and whitespace-insensitively.</summary>
    public static bool TryParse(string? value, out ConfirmationChannelKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "stderr": kind = ConfirmationChannelKind.Stderr; return true;
            case "refuse": kind = ConfirmationChannelKind.Refuse; return true;
            case "webhook": kind = ConfirmationChannelKind.Webhook; return true;
            case "file": kind = ConfirmationChannelKind.File; return true;
            default: kind = ConfirmationChannelKind.Refuse; return false;
        }
    }

    /// <summary>True when the operator declared a hosted deployment ("1" or "true").</summary>
    public static bool IsHosted(string? hostedFlag)
    {
        var value = hostedFlag?.Trim();
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
