namespace LightningEnable.Mcp.Tools;

/// <summary>How much of the tool surface the server advertises in <c>list_tools</c>.</summary>
public enum ToolProfile
{
    /// <summary>The tools an agent needs to get a wallet, spend, and stay inside its budget.</summary>
    Lite,

    /// <summary>The default: the consolidated verb set.</summary>
    Standard,

    /// <summary>The consolidated set plus every pre-consolidation tool name.</summary>
    Full,
}

/// <summary>
/// Tool profiles — how much of the surface <c>list_tools</c> advertises.
///
/// Every advertised tool's JSON schema is loaded into the agent's context at the start
/// of each session, so a wide surface costs tokens on every single turn. A profile trims
/// what is <em>advertised</em>; it never removes capability.
///
/// Selected with the <c>LIGHTNING_ENABLE_TOOL_PROFILE</c> environment variable. Unset or
/// unrecognized falls back to <see cref="ToolProfile.Standard"/> (unrecognized also warns
/// on stderr) — never to an empty surface.
///
/// <para><b>Profiles are listing-only.</b> A tool absent from the advertised list stays
/// callable by name, and the deprecated aliases still dispatch, in every profile — see
/// <see cref="ToolSurface"/>. Narrowing the profile can never strand a caller that knows
/// the tool exists; it only stops the schema from being pushed into the model's context.</para>
///
/// Keep in lockstep with the Python port
/// (<c>python/lightning-enable-mcp/src/lightning_enable_mcp/tools/profiles.py</c>).
/// </summary>
public static class ToolProfiles
{
    /// <summary>Environment variable that selects the profile.</summary>
    public const string EnvironmentVariable = "LIGHTNING_ENABLE_TOOL_PROFILE";

    /// <summary>Profile used when the variable is unset, blank, or unrecognized.</summary>
    public const ToolProfile DefaultProfile = ToolProfile.Standard;

    /// <summary>The consolidated verb set — what <see cref="ToolProfile.Standard"/> advertises.</summary>
    public static readonly IReadOnlyList<string> StandardToolNames = new[]
    {
        "setup_wallet",
        "access_l402_resource",
        "pay_invoice",
        "pay_l402_challenge",
        "test_l402_payment",
        "get_balance",
        "budget",
        "receipts",
        "create_invoice",
        "check_invoice_status",
        "verify_confirmation_code",
        "discover_api",
        "create_lightning_enable_account",
        "wallet_ops",
        "l402_producer",
        "agent_services",
    };

    /// <summary>
    /// The minimal spend-and-stay-in-budget surface. <c>setup_wallet</c> is here because
    /// none of the rest works without a wallet, and an agent on the smallest surface has no
    /// other way to find that out or fix it.
    /// </summary>
    public static readonly IReadOnlyList<string> LiteToolNames = new[]
    {
        "setup_wallet",
        "access_l402_resource",
        "pay_invoice",
        "get_balance",
        "budget",
        "receipts",
    };

    /// <summary>
    /// Pre-consolidation tool names that <see cref="ToolProfile.Full"/> re-advertises: the
    /// tools folded into <c>budget</c> / <c>receipts</c> / <c>wallet_ops</c> /
    /// <c>l402_producer</c> / <c>agent_services</c>. They remain callable in every profile
    /// as deprecated aliases (see <see cref="DeprecatedAliasDispatcher.Aliases"/>).
    ///
    /// The three v1 aliases that predate this consolidation (<c>confirm_payment</c>,
    /// <c>check_wallet_balance</c>, <c>get_all_balances</c>) are deliberately NOT here:
    /// they were already unadvertised before profiles existed, and <c>full</c> is not a
    /// reason to start advertising them again.
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyToolNames = new[]
    {
        "get_budget_status",
        "configure_budget",
        "get_receipts",
        "get_payment_history",
        "get_btc_price",
        "exchange_currency",
        "send_onchain",
        "create_l402_challenge",
        "verify_l402_payment",
        "discover_agent_services",
        "publish_agent_capability",
        "unpublish_agent_capability",
        "request_agent_service",
        "publish_agent_attestation",
        "get_agent_reputation",
        "settle_agent_service",
    };

    /// <summary>
    /// Normalizes a raw <see cref="EnvironmentVariable"/> value to a known profile.
    /// Unset/blank resolves quietly; an unrecognized value resolves to the default but
    /// reports through <paramref name="warn"/>, so a typo ("standrd", "minimal") is
    /// visible instead of silently changing the agent's tool surface.
    /// </summary>
    public static ToolProfile Resolve(string? raw, Action<string>? warn = null)
    {
        var normalized = raw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            return DefaultProfile;
        }

        switch (normalized)
        {
            case "lite": return ToolProfile.Lite;
            case "standard": return ToolProfile.Standard;
            case "full": return ToolProfile.Full;
        }

        warn?.Invoke(
            $"Unrecognized {EnvironmentVariable}='{raw}' - falling back to 'standard'. "
            + "Valid profiles: lite, standard, full.");
        return DefaultProfile;
    }

    /// <summary>Tool names advertised under <paramref name="profile"/>.</summary>
    public static IReadOnlySet<string> AdvertisedNames(ToolProfile profile)
    {
        var names = profile switch
        {
            ToolProfile.Lite => LiteToolNames,
            ToolProfile.Full => StandardToolNames.Concat(LegacyToolNames).ToList(),
            _ => StandardToolNames,
        };
        return new HashSet<string>(names, StringComparer.Ordinal);
    }
}
