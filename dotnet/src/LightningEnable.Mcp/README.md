<!-- mcp-name: io.github.refined-element/lightning-enable-mcp -->

# Lightning Enable MCP Server

A Model Context Protocol (MCP) server that enables AI agents to make Lightning Network payments. All tools are free — no license or subscription required.

## Overview

This MCP server provides tools for AI agents (like Claude) to:

- **Pay Lightning invoices** — Send payments to any BOLT11 invoice
- **Manage payment budgets** — Set per-request and per-session spending limits
- **Track payment history** — Review all payments made during a session
- **Check wallet balance** — Monitor your connected Lightning wallet
- **Access L402-protected APIs** — Automatically pay L402 challenges for seamless API access
- **Create invoices** — Generate BOLT11 invoices to receive payments
- **Exchange currency** — Convert between USD and BTC (Strike)
- **Send on-chain** — Send on-chain Bitcoin payments (Strike, LND)

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
| `L402_MAX_SATS_PER_REQUEST` | No | 1000 | Maximum sats per single request |
| `L402_MAX_SATS_PER_SESSION` | No | 10000 | Maximum sats for entire session |

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
- **Alby Hub** (https://albyhub.com) — Self-custody, untested for L402

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

### check_wallet_balance

Checks the connected wallet balance and session spending.

**Parameters:** None

**Returns:** Wallet balance in satoshis, session spending summary, budget remaining

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

Sets spending limits for the session.

**Parameters:**
- `perRequest`: Max sats per request. Default: 1000
- `perSession`: Max sats for session. Default: 10000
- `resetSession`: Reset session spending. Default: false

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

Fetches a URL, automatically paying any L402 challenge. Requires a wallet that returns preimage (Strike, LND, CoinOS, CLINK).

**Parameters:**
- `url` (required): The URL to fetch
- `method`: HTTP method (GET, POST, PUT, DELETE). Default: GET
- `headers`: Optional headers as JSON object
- `body`: Optional request body
- `maxSats`: Maximum sats to pay. Default: 1000

### pay_l402_challenge

Manually pays an L402 invoice when you have the macaroon and invoice separately.

**Parameters:**
- `invoice` (required): BOLT11 invoice string
- `macaroon` (required): Base64-encoded macaroon
- `maxSats`: Maximum sats to pay. Default: 1000

**Returns:** L402 token for use in Authorization header

### get_btc_price (Strike only)

Get the current Bitcoin price in USD.

### get_all_balances (Strike only)

Get all currency balances (USD and BTC).

### exchange_currency (Strike only)

Convert between USD and BTC within your Strike wallet.

### send_onchain (Strike, LND)

Send an on-chain Bitcoin payment to a Bitcoin address.

See [AI Spending Security](https://docs.lightningenable.com/products/l402-microtransactions/ai-spending-security) for full security guidance.

## L402 Wallet Compatibility

L402 requires the payment preimage to create credentials. Not all wallets return it:

| Wallet | Returns Preimage | L402 Works |
|--------|-----------------|------------|
| **LND** | Always | Yes |
| **Strike** | Yes | Yes |
| **CoinOS (NWC)** | Yes | Yes |
| **CLINK (NWC)** | Yes | Yes |
| **Alby (NWC)** | Untested | Untested |
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
dotnet pack -c Release
dotnet nuget push bin/Release/LightningEnable.Mcp.1.6.1.nupkg --source nuget.org
```

## License

MIT License - see [LICENSE](../../LICENSE) for details.

## Related Projects

- [Lightning Enable API](https://api.lightningenable.com) - L402-protected API server
- [Lightning Enable Store](https://store.lightningenable.com) - Live L402 commerce demo
- [Lightning Enable Docs](https://docs.lightningenable.com) - Full documentation
- [Model Context Protocol](https://github.com/modelcontextprotocol) - MCP specification
- [Nostr Wallet Connect](https://github.com/nostr-protocol/nips/blob/master/47.md) - NIP-47 specification
