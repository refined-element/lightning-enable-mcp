"""Canonical wallet-configuration guidance shared across the tools.

Single source of truth so the per-tool "wallet not configured" errors can't drift.

There are TWO messages because OpenNode has ASYMMETRIC capability:
  - It fully supports receiving/invoicing/info tools (create_invoice,
    check_invoice_status, get_all_balances, check_wallet_balance), so those list
    OPENNODE_API_KEY as valid.
  - It cannot pay L402 challenges (it never returns a preimage), so the L402-paying
    tools (pay_invoice, pay_l402_challenge, access_l402_resource, test_l402_payment)
    demote/exclude it.

Both strings intentionally begin with "Wallet not configured" — tool tests assert on
that substring.

KEEP IN SYNC with the .NET port's WalletMessages
(dotnet/src/LightningEnable.Mcp/WalletMessages.cs). The two ports must present
identical guidance. server.py's own no-wallet-at-all guard and startup warning are the
"nothing configured" global path and stay L402-centric.
"""

# Returned by the L402-PAYING tools (pay_invoice, and any tool that must spend to
# satisfy an L402 challenge) when no wallet is configured. Leads with the L402-capable
# wallets; OpenNode is called out as receiving/invoicing only because it cannot produce
# the preimage L402 requires.
WALLET_NOT_CONFIGURED_FOR_PAYMENT = (
    "Wallet not configured. Set one L402-capable wallet — STRIKE_API_KEY, "
    "NWC_CONNECTION_STRING, or LND_REST_HOST+LND_MACAROON_HEX. "
    "(OPENNODE_API_KEY is receiving/invoicing only — it cannot pay L402 "
    "challenges.) Then run test_l402_payment to confirm the wallet works."
)

# Returned by the RECEIVING / INVOICING / INFO tools (create_invoice,
# check_invoice_status, get_all_balances, check_wallet_balance, get_balance) when no
# wallet is configured. These
# operations work with OpenNode, so OPENNODE_API_KEY is listed as a valid option here
# (restoring the guidance these tools gave before the messages were consolidated),
# while still noting it can't pay L402. A balance read makes NO L402/payment claim.
WALLET_NOT_CONFIGURED_FOR_RECEIVING = (
    "Wallet not configured. Set any wallet — STRIKE_API_KEY, OPENNODE_API_KEY, "
    "NWC_CONNECTION_STRING, or LND_REST_HOST+LND_MACAROON_HEX — to create/check "
    "invoices and read balances. (OPENNODE_API_KEY works for these; for paying L402 "
    "challenges you need STRIKE_API_KEY, NWC_CONNECTION_STRING, or LND instead.)"
)
