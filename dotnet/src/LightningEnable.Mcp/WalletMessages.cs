namespace LightningEnable.Mcp;

/// <summary>
/// Canonical, user-facing wallet guidance strings shared across the tools.
/// Single source of truth so the per-tool "wallet not configured" errors can't
/// drift back to recommending OpenNode for L402 (which it cannot do). Mirrors the
/// Python port's wallet_messages module.
/// </summary>
internal static class WalletMessages
{
    /// <summary>
    /// Returned by the payment/invoice tools when no wallet is configured. Leads with the
    /// L402-capable wallets; OpenNode is called out as receiving/invoicing only. Existing
    /// tests assert on substrings including "not configured" and the OpenNode
    /// "receiving/invoicing only" caveat — preserve this exact wording.
    /// </summary>
    public const string NotConfigured =
        "Wallet not configured. Set one L402-capable wallet — STRIKE_API_KEY, NWC_CONNECTION_STRING, or LND_REST_HOST+LND_MACAROON_HEX. (OPENNODE_API_KEY is receiving/invoicing only — it cannot pay L402 challenges.) Then run test_l402_payment to confirm the wallet works.";
}
