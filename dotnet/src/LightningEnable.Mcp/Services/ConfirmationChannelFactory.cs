using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Builds the approval channel for this process from config + environment + whether a human
/// could be watching this console. Misconfiguration always resolves to a REFUSING channel with
/// a reason that names the missing setting — never a silent fallback to stderr, because
/// "silently fall back to a code nobody reads" is the exact failure this feature exists to fix.
/// </summary>
public static class ConfirmationChannelFactory
{
    /// <summary>Named <see cref="HttpClient"/> for the approval webhook (SSRF-guarded, no redirects).</summary>
    public const string WebhookHttpClientName = "confirmation-webhook";

    /// <summary>
    /// Create the channel. <paramref name="warn"/> receives at most one startup line.
    /// <paramref name="stdinIsTty"/> and <paramref name="environment"/> are test seams.
    /// </summary>
    public static IConfirmationChannel Create(
        ConfirmationSettings? settings,
        Func<HttpClient>? webhookClientFactory = null,
        Action<string>? warn = null,
        bool? stdinIsTty = null,
        Func<string, string?>? environment = null)
    {
        var env = environment ?? Environment.GetEnvironmentVariable;
        var isTty = stdinIsTty ?? !Console.IsInputRedirected;

        var resolution = ConfirmationChannelResolver.Resolve(
            env(ConfirmationChannelResolver.ChannelEnvironmentVariable),
            settings?.Channel,
            isTty,
            env(ConfirmationChannelResolver.HostedEnvironmentVariable));

        if (resolution.Warning != null)
        {
            warn?.Invoke(resolution.Warning);
        }

        switch (resolution.Kind)
        {
            case ConfirmationChannelKind.Stderr:
                return new StderrConfirmationChannel();

            case ConfirmationChannelKind.Webhook:
                return BuildWebhook(settings, webhookClientFactory, warn, env);

            case ConfirmationChannelKind.File:
                var path = FirstNonBlank(
                    env(ConfirmationChannelResolver.FilePathEnvironmentVariable),
                    settings?.FilePath) ?? FileConfirmationChannel.DefaultPath;
                return new FileConfirmationChannel(path);

            case ConfirmationChannelKind.Refuse:
            default:
                return new RefusingConfirmationChannel(RefusalReasonFor(resolution));
        }
    }

    private static IConfirmationChannel BuildWebhook(
        ConfirmationSettings? settings,
        Func<HttpClient>? webhookClientFactory,
        Action<string>? warn,
        Func<string, string?> env)
    {
        var url = FirstNonBlank(env(ConfirmationChannelResolver.WebhookUrlEnvironmentVariable), settings?.WebhookUrl);
        var secret = FirstNonBlank(env(ConfirmationChannelResolver.WebhookSecretEnvironmentVariable), settings?.WebhookSecret);

        if (url == null)
        {
            return Misconfigured(warn,
                "the webhook approval channel is selected but no webhook URL is set "
                + $"(confirmation.webhookUrl, or {ConfirmationChannelResolver.WebhookUrlEnvironmentVariable})");
        }

        // The URL is operator-configured, but it still goes through the same SSRF pre-check as
        // agent-supplied URLs — a confirmation POST carries a live approval code, and the
        // connect-time guard on this client would refuse a private/loopback target at send time
        // anyway. Catching it here turns a per-payment failure into one startup line. Consequence
        // to know about: the approval webhook must be reachable at a PUBLIC address.
        var guardError = SsrfUrlGuard.Validate(url);
        if (guardError != null)
        {
            return Misconfigured(warn,
                $"the webhook approval URL was rejected by the SSRF guard ({guardError}); it must be a public "
                + "http(s) endpoint, not a private, loopback, or metadata address");
        }

        if (secret == null)
        {
            return Misconfigured(warn,
                "the webhook approval channel is selected but no signing secret is set "
                + $"(confirmation.webhookSecret, or {ConfirmationChannelResolver.WebhookSecretEnvironmentVariable}); "
                + "an unsigned approval POST is spoofable, so it is refused");
        }

        if (webhookClientFactory == null)
        {
            return Misconfigured(warn, "the webhook approval channel has no HTTP client available in this process");
        }

        return new WebhookConfirmationChannel(webhookClientFactory, url, secret);
    }

    private static IConfirmationChannel Misconfigured(Action<string>? warn, string problem)
    {
        warn?.Invoke($"Approval channel misconfigured: {problem}. Over-threshold payments will be REFUSED.");
        return new RefusingConfirmationChannel(
            $"This payment needs human approval, but the approval channel is misconfigured: {problem}. "
            + "It was refused rather than approved. The OPERATOR must fix the configuration (or raise "
            + "tiers.autoApprove in ~/.lightning-enable/config.json) — the agent cannot resolve this.");
    }

    private static string RefusalReasonFor(ConfirmationChannelResolution resolution)
    {
        if (resolution.Source.EndsWith("-invalid", StringComparison.Ordinal))
        {
            return $"This payment needs human approval, but the approval channel is misconfigured: {resolution.Warning} "
                + "The OPERATOR must fix it; the agent cannot.";
        }

        if (resolution.Source == "auto")
        {
            return "This payment needs human approval, but this server runs in hosted mode "
                + $"({ConfirmationChannelResolver.HostedEnvironmentVariable}=1) with no approval channel configured, so it "
                + "was refused rather than approved. No confirmation code exists for the agent to ask for. To allow "
                + "payments this size, the OPERATOR must either raise tiers.autoApprove in ~/.lightning-enable/config.json "
                + "or set confirmation.channel to \"webhook\" or \"file\".";
        }

        return RefusingConfirmationChannel.Configured().RefusalReason!;
    }

    private static string? FirstNonBlank(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }
}
