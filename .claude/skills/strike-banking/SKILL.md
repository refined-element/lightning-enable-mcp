---
name: strike-banking
description: Work with Strike banking tools (ACH, wire, payouts, deposits)
---

Context-aware guide for Strike banking MCP tools. Use $ARGUMENTS for the specific banking operation needed.

## Available Strike Banking Tools

The MCP server includes tools for Strike banking operations. These tools move real money — always confirm with the user before executing.

### Typical Flows

**ACH Deposit (fund Strike account):**
1. `create_ach_payment_method` — link bank account
2. `create_deposit` — initiate ACH deposit
3. Wait for settlement (1-3 business days)

**ACH Payout (send to bank):**
1. `create_ach_payment_method` — link destination bank
2. `create_originator` — set up payout originator
3. `create_payout` — create payout request
4. `initiate_payout` — execute the payout (MOVES MONEY)

**Wire Transfer:**
- Similar to ACH but uses wire payment method
- Faster settlement, higher fees

### Safety Guardrails

- **ALWAYS confirm before executing** any tool that moves funds (`initiate_payout`, `create_deposit`)
- Display the amount, currency, and destination before confirming
- Strike supports multi-currency: USD, EUR, GBP, AUD, BTC, USDT
- Exchange operations use `exchange_currency` tool (separate from banking)

### Environment Setup
- Requires Strike API key with banking permissions
- Strike API base URL: `https://api.strike.me/v1` (prod) or `https://api.dev.strike.me/v1` (sandbox)
- **Use sandbox for testing banking flows**

### Common Issues
- Insufficient permissions on Strike API key
- Payment method not verified yet
- Payout amount exceeds balance
- ACH routing number validation failures
