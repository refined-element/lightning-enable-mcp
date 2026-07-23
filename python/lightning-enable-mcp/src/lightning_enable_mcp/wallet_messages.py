"""Canonical wallet-configuration guidance shared across the tools.

Single source of truth so the per-tool "wallet not configured" errors can't drift
back to recommending OpenNode for L402 (which it cannot do). Mirrors the .NET port's
WalletMessages (dotnet/src/LightningEnable.Mcp/WalletMessages.cs) and the guidance in
server.py's no-wallet guard and startup warning.
"""

# Returned by the payment/invoice tools when no wallet is configured. Leads with the
# L402-capable wallets; OpenNode is called out as receiving/invoicing only.
# Must begin with "Wallet not configured" — tool tests assert on that substring.
WALLET_NOT_CONFIGURED = (
    "Wallet not configured. Set one L402-capable wallet: STRIKE_API_KEY, "
    "NWC_CONNECTION_STRING, or LND_REST_HOST+LND_MACAROON_HEX. "
    "(OPENNODE_API_KEY is receiving/invoicing only — it cannot pay L402 "
    "challenges.) Then run test_l402_payment to confirm the wallet works."
)
