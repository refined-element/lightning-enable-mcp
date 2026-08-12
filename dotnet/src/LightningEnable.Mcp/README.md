<!-- mcp-name: io.github.refined-element/lightning-enable-mcp -->

# Lightning Enable MCP Server

[![NuGet downloads](https://img.shields.io/nuget/dt/LightningEnable.Mcp?label=NuGet%20downloads&logo=nuget)](https://www.nuget.org/packages/LightningEnable.Mcp)
[![Docker pulls](https://img.shields.io/docker/pulls/refinedelement/lightning-enable-mcp?label=Docker%20pulls&logo=docker&logoColor=white)](https://hub.docker.com/r/refinedelement/lightning-enable-mcp)

## Monetize your own API — 30-day free trial

Agents pay your API per request over Lightning — flat subscription from $99/mo, and you keep 100% of every sat.

- **[Start free trial](https://api.lightningenable.com/Checkout?plan=individual&utm_source=nuget&utm_medium=registry&utm_campaign=gtm-aug-2026)** — 30 days, no charge today
- **[Fast Lane](https://docs.lightningenable.com/getting-started/activate-with-lightning?utm_source=nuget&utm_medium=registry&utm_campaign=gtm-aug-2026)** — pay 100 sats over Lightning, no card
- **[Pricing](https://lightningenable.com/pricing?utm_source=nuget&utm_medium=registry&utm_campaign=gtm-aug-2026)**

Or sign up without leaving your agent: call the `create_lightning_enable_account` tool.

A Model Context Protocol (MCP) server that enables AI agents to make Lightning Network payments. Wallet, invoice, L402, budget, and API-discovery tools work out of the box with just a wallet (free, no subscription). Producer tools (`create_l402_challenge`, `verify_l402_payment`) and Agent Service Agreement tools for agent-to-agent commerce over Nostr unlock with an [Agentic Commerce subscription](https://lightningenable.com) (from $99/mo) and `LIGHTNING_ENABLE_API_KEY`. One out-of-the-box tool, `create_lightning_enable_account`, self-provisions that API key — an agent with a wallet pays a ~100-sat activation fee and unlocks the subscription tools on the spot. See the [MCP Complete Guide](https://docs.lightningenable.com/products/l402-microtransactions/mcp-complete-guide) for the full tool list.

## Overview

This MCP server provides tools for AI agents (like Claude) to:

- **Pay Lightning invoices** — Send payments to any BOLT11 invoice
- **Manage payment budgets** — Set per-request and per-session spending limits
- **Track payment history** — Review all payments made during a session
- **Check wallet balance** — Monitor your connected Lightning wallet
- **Discover APIs** — Search the L402 API registry by keyword/category, or fetch a specific API's manifest
- **Access L402-protected APIs** — Automatically pay L402 challenges for seamless API access
- **Create invoices** — Generate BOLT11 invoices to receive payments
- **Exchange currency** — Convert between USD and BTC (Strike)
- **Send on-chain** — Send on-chain Bitcoin payments (Strike, LND)
- **Sell services (L402 Producer)** — Create L402 payment challenges and verify payments, turning your agent into a full commerce participant that can both buy and sell

## Installation

### As a .NET global tool

```bash
dotnet tool install -g LightningEnable.Mcp
```

### Python (pip or uvx)

```bash
pip install lightning-enable-mcp
# Or use uvx for no-install execution:
uvx lightning-enable-mcp
```

### Docker

```bash
docker pull refinedelement/lightning-enable-mcp:latest
```

### From source

```bash
git clone https://github.com/refined-element/lightning-enable-mcp
cd lightning-enable-mcp/dotnet
dotnet build src/LightningEnable.Mcp
```

## Configuration

### Environment Variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `STRIKE_API_KEY` | If using Strike | - | Strike API key |
| `OPENNODE_API_KEY` | If using OpenNode | - | OpenNode API key |
| `OPENNODE_ENVIRONMENT` | No | production | `production` or `dev` for testnet |
| `NWC_CONNECTION_STRING` | If using NWC | - | Nostr Wallet Connect URI |
| `LND_REST_HOST` | If using LND | - | LND REST API host |
| `LND_MACAROON_HEX` | If using LND | - | LND admin macaroon in hex |
| `LIGHTNING_ENABLE_API_KEY` | For producer tools | - | API key for `create_l402_challenge` and `verify_l402_payment`. Requires Agentic Commerce subscription. |

Configure one wallet provider. If multiple are set, priority order is: LND > NWC > Strike > OpenNode.

### Wallet Options

#### Option 1: Strike (Recommended)

Best for users who want USD balance management, BTC price tracking, and easy on/off ramps. Supports L402 (returns preimage).

1. Create an account at https://strike.me
2. Get your API key from https://dashboard.strike.me
3. Fund your account with BTC

```bash
export STRIKE_API_KEY="your-api-key"
```

#### Option 2: LND (Best for L402)

Run your own Lightning node for full control. LND always returns preimage — L402 is guaranteed to work.

```bash
export LND_REST_HOST="localhost:8080"
export LND_MACAROON_HEX="your-admin-macaroon-in-hex"
```

#### Option 3: Nostr Wallet Connect (NWC)

NWC connects to your Lightning wallet via the Nostr protocol. L402 compatibility depends on the wallet:

- **CoinOS** (https://coinos.io) — Free, L402 works
- **CLINK** (https://clink.tools) — Nostr-native, L402 works
- **Alby Hub** (https://albyhub.com) — Self-custody, L402 verified

```bash
export NWC_CONNECTION_STRING="nostr+walletconnect://<pubkey>?relay=<relay-url>&secret=<secret>"
```

#### Option 4: OpenNode (Direct Payments Only)

Use your OpenNode account to pay invoices. **Does not return preimage — cannot be used for L402.**

```bash
export OPENNODE_API_KEY="your-api-key"
export OPENNODE_ENVIRONMENT="dev"  # Use testnet for testing
```

## Claude Desktop Integration

Add to your Claude Desktop configuration file:

**Windows:** `%APPDATA%\Claude\claude_desktop_config.json`
**macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`
**Linux:** `~/.config/claude/claude_desktop_config.json`

**Using Strike:**
```json
{
  "mcpServers": {
    "lightning-enable": {
      "command": "dotnet",
      "args": ["tool", "run", "lightning-enable-mcp"],
      "env": {
        "STRIKE_API_KEY": "your-strike-api-key"
      }
    }
  }
}
```

**Using NWC (CoinOS/CLINK):**
```json
{
  "mcpServers": {
    "lightning-enable": {
      "command": "dotnet",
      "args": ["tool", "run", "lightning-enable-mcp"],
      "env": {
        "NWC_CONNECTION_STRING": "nostr+walletconnect://your-pubkey?relay=wss://relay.getalby.com/v1&secret=your-secret"
      }
    }
  }
}
```

**Using LND:**
```json
{
  "mcpServers": {
    "lightning-enable": {
      "command": "dotnet",
      "args": ["tool", "run", "lightning-enable-mcp"],
      "env": {
        "LND_REST_HOST": "localhost:8080",
        "LND_MACAROON_HEX": "your-admin-macaroon-in-hex"
      }
    }
  }
}
```

## Available Tools

The canonical inventory is the [Tools table in the root README](https://github.com/refined-element/lightning-enable-mcp#tools): 26 tools (17 free / 9 gated). The sections below document a selected subset.

**Deprecated aliases** (accepted but unadvertised, forward to the new tool, removed in v2.0.0): `confirm_payment` → `verify_confirmation_code`; `check_wallet_balance` and `get_all_balances` → `get_balance`.

### create_lightning_enable_account

Self-bootstrapping signup: activate a Lightning Enable account with a tiny Lightning payment (~100 sats) and get back a merchant API key. Requires **NO** Lightning Enable API key (it *creates* one) — only a connected wallet. On success the API key is merged into `~/.lightning-enable/config.json` (existing keys preserved) so the producer/ASA tools unlock on the next restart. Above-threshold activation fees require a human-supplied confirmation code (same out-of-band flow as `pay_l402_challenge`).

**Parameters:**
- `email` (required): Email address to register the account under
- `maxSats`: Maximum sats to pay for activation (fee is ~100 sats). Default: 1000
- `confirmationNonce`: Human-relayed confirmation code (only for an above-threshold fee)

**Returns:** `{ success, apiKey, merchantId, planTier, subscriptionStatus, trialEndsAt, dashboardUrl, activation, config }`

### pay_invoice

Pay a Lightning invoice directly and get the preimage as proof of payment.

**Parameters:**
- `invoice` (required): BOLT11 Lightning invoice string to pay
- `maxSats`: Maximum sats allowed to pay. Default: 1000

**Returns:**
- `success`: Boolean indicating payment success
- `preimage`: Hex preimage proving payment (if successful)
- `error`: Error message (if failed)

**SECURITY WARNING:** This tool spends real Bitcoin. Always:
- Use a dedicated wallet with limited funds
- Set appropriate budget limits
- Review payment history regularly

### get_balance

Gets the connected wallet's balance. Supersedes `check_wallet_balance` and `get_all_balances`.

**Parameters:** None

**Returns:** A single superset shape — the scalar balance (`wallet.balanceSats` / `wallet.balanceMsat`), a `balances[]` array (all currencies for Strike; a single BTC entry otherwise), the session spending summary, and budget remaining.

### get_receipts

Reads the durable, append-only payment receipt log at `~/.lightning-enable/receipts.jsonl`. **Every payment through any tool** — `pay_invoice`, L402 flows, agent settlements, on-chain sends — appends exactly one `payment_receipt` line (kind `invoice`/`l402`/`onchain`, amount, wallet, status `settled`/`pending`, derived `paymentHash`, optional context + spend policy, session spend, and how to revoke the wallet). Older `l402_payment_receipt` lines (pre-seam schema with an `endpoint` field) remain readable from the same file. Unlike `get_payment_history` (in-memory, this session only), receipts persist across sessions — the audit + "pull the plug" record. Receipts never contain secrets (no preimage, BOLT11 invoice, macaroon, or connection string); `paymentHash` is SHA256 of the preimage, never the preimage itself. Every value-moving tool result also carries `receipt_written: true|false` so a failed receipt write is visible, never silent.

**Parameters:**
- `limit`: Maximum number of recent receipts to return (1-200). Default: 20

**Returns:** `{ success, count, totalSatsInView, logFile, receipts, note }` — the recent receipts plus a spend total and the log-file path.

### get_payment_history

Lists recent payments made in the session.

**Parameters:**
- `limit`: Maximum payments to return. Default: 10

**Returns:** List of payments with URL, amount, timestamp, and status

### get_budget_status

View current budget configuration and session spending (read-only).

**Parameters:** None

**Returns:** Budget tiers, limits, and current session spending

### configure_budget

Tightens the session spending limits. **Tighten-only:** an agent can only LOWER its
caps — it can never raise them above the operator's `~/.lightning-enable/config.json`
limits (or an existing tighter runtime cap). To raise limits, the operator edits the
config file. This prevents a prompt-injected agent from loosening its own caps and
then draining the wallet.

**Parameters:**
- `perRequest`: Max sats per single request. Default: 1000
- `perSession`: Max total sats for the session. Default: 10000

### create_invoice

Create a Lightning invoice to receive payments.

**Parameters:**
- `amountSats` (required): Amount in satoshis
- `memo`: Description for the invoice
- `expirySecs`: Invoice expiry in seconds. Default: 3600

### check_invoice_status

Check if a previously created invoice has been paid.

**Parameters:**
- `invoiceId` (required): Invoice ID from create_invoice

### access_l402_resource

Fetches a URL, automatically paying any L402 challenge. Requires a wallet that returns preimage (Strike, LND, CoinOS, CLINK, Alby Hub).

**Parameters:**
- `url` (required): The URL to fetch
- `method`: HTTP method (GET, POST, PUT, DELETE). Default: GET
- `headers`: Optional headers as JSON object
- `body`: Optional request body
- `maxSats`: Maximum sats to pay. Default: 1000

### test_l402_payment

Self-tests the wallet by paying the public 1-sat L402 test endpoint (`/l402/test/ping`) end to end. Proves the wallet is connected, returns a preimage, and can complete an L402 payment — the one-line answer to "is my wallet actually working?". Costs about 1 satoshi. Returns a plain verdict (`passed` / `needs_confirmation` / `inconclusive` / `failed` with a reason code). If your budget config requires confirmation for this amount, re-run with `confirmationNonce` set to the code the server prints to its console.

**Parameters:**
- `confirmationNonce`: Confirmation code from the server console, if a prior call returned `test="needs_confirmation"`. Omit on the first call.

### pay_l402_challenge

Manually pays an L402 invoice when you have the macaroon and invoice separately.

**Parameters:**
- `invoice` (required): BOLT11 invoice string
- `macaroon` (required): Base64-encoded macaroon
- `maxSats`: Maximum sats to pay. Default: 1000

**Returns:** L402 token for use in Authorization header

### get_btc_price (Strike only)

Get the current Bitcoin price in USD.

### exchange_currency (Strike only)

Convert between USD and BTC within your Strike wallet.

### send_onchain (Strike, LND)

Send an on-chain Bitcoin payment to a Bitcoin address.

### create_l402_challenge (Agentic Commerce)

Create an L402 payment challenge to charge another agent or user for accessing a resource. Returns a Lightning invoice and macaroon that the payer must pay before you grant access.

**Requires:** `LIGHTNING_ENABLE_API_KEY` with an Agentic Commerce subscription (from $99/mo).

**Parameters:**
- `resource` (required): Resource identifier — URL, service name, or description of what you're charging for
- `priceSats` (required): Price in satoshis to charge
- `description`: Description shown on the Lightning invoice

**Returns:**
- `challenge.invoice`: BOLT11 Lightning invoice for the payer
- `challenge.macaroon`: Base64-encoded macaroon
- `challenge.paymentHash`: Payment hash for tracking
- `challenge.expiresAt`: Invoice expiration time
- `instructions`: Instructions for the payer and verification steps

### verify_l402_payment (Agentic Commerce)

Verify an L402 token (macaroon + preimage) to confirm payment was made. Use this after receiving an L402 token from a payer to validate they paid before granting access.

**Requires:** `LIGHTNING_ENABLE_API_KEY` with an Agentic Commerce subscription (from $99/mo).

**Parameters:**
- `macaroon` (required): Base64-encoded macaroon from the L402 token
- `preimage` (required): Hex-encoded preimage (proof of payment)

**Returns:**
- `valid`: Boolean — whether the payment is verified
- `resource`: The resource identifier the payment was for

See [AI Spending Security](https://docs.lightningenable.com/products/l402-microtransactions/ai-spending-security) for full security guidance.

## L402 Wallet Compatibility

L402 requires the payment preimage to create credentials. Not all wallets return it:

| Wallet | Returns Preimage | L402 Works |
|--------|-----------------|------------|
| **LND** | Always | Yes |
| **Strike** | Yes | Yes |
| **CoinOS (NWC)** | Yes | Yes |
| **CLINK (NWC)** | Yes | Yes |
| **Alby (NWC)** | Yes | Yes |
| **OpenNode** | No | No |
| **Primal (NWC)** | No | No |

## Try It: Lightning Enable Store

The [Lightning Enable Store](https://store.lightningenable.com) is a live L402-powered web store where AI agents can purchase physical merchandise using Bitcoin Lightning payments.

```
Ask Claude: "Buy me a Lightning Enable t-shirt from store.lightningenable.com"
```

The store demonstrates the full L402 flow: browse catalog, checkout (get 402), pay invoice, claim with L402 credential.

## Usage Examples

### Paying a Lightning Invoice

```
You: Pay this Lightning invoice: lnbc100n1p3...

Claude: I'll pay that invoice for you.
[Calls pay_invoice with invoice="lnbc100n1p3..."]

Payment successful! Here's the preimage as proof of payment: abc123...
```

### L402 API Access

```
You: Use access_l402_resource to fetch data from https://api.paywall.example.com/data

Claude: I'll fetch that URL with L402 payment support.
[Calls access_l402_resource with url="https://api.paywall.example.com/data"]

The request required a payment of 50 sats which was automatically paid.
Here's the response: ...
```

### Selling a Service (L402 Producer Flow)

With the producer tools, your agent can charge other agents for services — making it a full commerce participant that can both buy and sell.

```
Agent B: I need weather data for New York.

Agent A (seller): I'll create a payment challenge for that.
[Calls create_l402_challenge with resource="weather/new-york", priceSats=10, description="NYC weather data"]

Here's your invoice — pay 10 sats to get the data:
  Invoice: lnbc100n1p3...
  Macaroon: AgELbGl...

Agent B: [Pays the invoice using pay_invoice or pay_l402_challenge, gets preimage]
Here's my L402 token: AgELbGl...:abc123def...

Agent A: Let me verify that payment.
[Calls verify_l402_payment with macaroon="AgELbGl...", preimage="abc123def..."]

Payment verified! Here's your weather data: Temperature: 72°F, Humidity: 45%...
```

This enables agent-to-agent commerce: any agent with an Agentic Commerce subscription can create paywalls, and any agent with a Lightning wallet can pay them.

### Setting Budget Limits

```
You: Configure the budget to allow max 500 sats per request and 5000 sats total

Claude: I'll configure those budget limits.
[Calls configure_budget with perRequest=500, perSession=5000]

Budget configured:
- Max per request: 500 sats
- Max per session: 5000 sats
- Currently spent: 0 sats
```

## Security Considerations

1. **Protect your wallet credentials**: NWC strings, API keys, and macaroons grant access to your wallet
2. **Set appropriate budget limits**: Start with low limits and increase as needed
3. **Review payment history**: Check what payments are being made
4. **Use a dedicated wallet**: Never use your main wallet or business funds for AI agents

## Troubleshooting

### "No wallet configured"
Set one of: `STRIKE_API_KEY`, `LND_REST_HOST` + `LND_MACAROON_HEX`, `NWC_CONNECTION_STRING`, or `OPENNODE_API_KEY`.

### "Budget check failed"
The requested payment exceeds your configured limits. Use `configure_budget` or `get_budget_status` to check.

### "Payment failed"
Check:
- Wallet has sufficient balance
- Invoice hasn't expired
- Wallet connection is working

### "L402 payment succeeded but access failed"
Your wallet doesn't return preimage. Switch to LND, Strike, CoinOS, or CLINK.

## Development

### Building from source

```bash
cd lightning-enable-mcp/dotnet
dotnet build src/LightningEnable.Mcp
```

### Running tests

```bash
dotnet test tests/LightningEnable.Mcp.Tests
```

### Publishing

```bash
cd dotnet/src/LightningEnable.Mcp
# Clear any prior packed artifacts BEFORE packing, so the wildcard push
# below targets only this build's output. `dotnet pack -o` writes the new
# .nupkg in but does NOT clear pre-existing files — the rm step is what
# makes the push deterministic.
rm -f ./artifacts/*.nupkg
dotnet pack -c Release -o ./artifacts
dotnet nuget push ./artifacts/LightningEnable.Mcp.*.nupkg --source nuget.org
```

## License

MIT License - see [LICENSE](../../LICENSE) for details.

## Related Projects

- [L402-Requests (Python)](https://github.com/refined-element/l402-requests) - Auto-paying L402 HTTP client for Python
- [L402-Requests (.NET)](https://github.com/refined-element/l402-dotnet) - Auto-paying L402 HTTP client for .NET
- [Lightning Enable API](https://api.lightningenable.com) - L402-protected API server
- [Lightning Enable Store](https://store.lightningenable.com) - Live L402 commerce demo
- [Lightning Enable Docs](https://docs.lightningenable.com) - Full documentation
- [Model Context Protocol](https://github.com/modelcontextprotocol) - MCP specification
- [Nostr Wallet Connect](https://github.com/nostr-protocol/nips/blob/master/47.md) - NIP-47 specification
