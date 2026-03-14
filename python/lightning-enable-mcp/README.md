<!-- mcp-name: io.github.refined-element/lightning-enable-mcp -->

# Lightning Enable MCP Server (Python)

An MCP (Model Context Protocol) server that enables AI agents to make Lightning Network payments. 15 consumer tools are free with no subscription required. 2 producer tools (`create_l402_challenge`, `verify_l402_payment`) require an [Agentic Commerce subscription](https://lightningenable.com) (from $99/mo) and `LIGHTNING_ENABLE_API_KEY`.

## Overview

Lightning Enable MCP provides tools for AI agents (like Claude) to:

- **Pay Lightning invoices** — Send payments to any BOLT11 invoice
- **Discover APIs** — Search the L402 API registry by keyword/category, or fetch a specific API's manifest
- **Access L402-protected APIs** — Automatically handle L402 payment challenges
- **Control spending** — Set per-request and session budgets
- **Track payments** — View payment history and wallet balance
- **Sell services (L402 Producer)** — Create L402 payment challenges and verify payments, enabling agents to be full commerce participants that both buy and sell

## Installation

### Using pip

```bash
pip install lightning-enable-mcp
```

### Using uvx (recommended for Claude Desktop)

No installation needed — uvx handles it automatically.

### Using Docker

```bash
docker pull refinedelement/lightning-enable-mcp:latest
```

## Configuration

### Environment Variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `STRIKE_API_KEY` | If using Strike | - | Strike API key |
| `NWC_CONNECTION_STRING` | If using NWC | - | Nostr Wallet Connect URI |
| `OPENNODE_API_KEY` | If using OpenNode | - | OpenNode API key |
| `OPENNODE_ENVIRONMENT` | No | production | `production` or `dev` for testnet |
| `LND_REST_HOST` | If using LND | - | LND REST API host |
| `LND_MACAROON_HEX` | If using LND | - | LND admin macaroon in hex |
| `L402_MAX_SATS_PER_REQUEST` | No | 1000 | Maximum sats per single request |
| `L402_MAX_SATS_PER_SESSION` | No | 10000 | Maximum sats for entire session |
| `LIGHTNING_ENABLE_API_KEY` | For producer tools | - | API key for `create_l402_challenge` and `verify_l402_payment`. Requires Agentic Commerce subscription. |

Configure one wallet provider. If multiple are set, priority order is: LND > NWC > Strike > OpenNode.

### Wallet Options

#### Option 1: Strike (Recommended)

Best for USD balance management and easy on/off ramps. Supports L402 (returns preimage).

1. Create an account at https://strike.me
2. Get your API key from https://dashboard.strike.me
3. Fund your account with BTC

```bash
export STRIKE_API_KEY="your-api-key"
```

#### Option 2: LND (Best for L402)

Run your own Lightning node. LND always returns preimage — L402 is guaranteed to work.

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

### Claude Desktop Configuration

Add to your Claude Desktop config (`claude_desktop_config.json`):

**Using uvx (recommended):**
```json
{
  "mcpServers": {
    "lightning-enable": {
      "command": "uvx",
      "args": ["lightning-enable-mcp"],
      "env": {
        "STRIKE_API_KEY": "your-strike-api-key"
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
      "command": "uvx",
      "args": ["lightning-enable-mcp"],
      "env": {
        "LND_REST_HOST": "localhost:8080",
        "LND_MACAROON_HEX": "your-admin-macaroon-in-hex"
      }
    }
  }
}
```

**Using NWC:**
```json
{
  "mcpServers": {
    "lightning-enable": {
      "command": "uvx",
      "args": ["lightning-enable-mcp"],
      "env": {
        "NWC_CONNECTION_STRING": "nostr+walletconnect://your-pubkey?relay=wss://relay.getalby.com/v1&secret=your-secret"
      }
    }
  }
}
```

Or if installed via pip, replace `"command": "uvx", "args": ["lightning-enable-mcp"]` with just `"command": "lightning-enable-mcp"`.

**Using Docker:**
```json
{
  "mcpServers": {
    "lightning-enable": {
      "command": "docker",
      "args": ["run", "--rm", "-i", "refinedelement/lightning-enable-mcp:latest"],
      "env": {
        "NWC_CONNECTION_STRING": "nostr+walletconnect://..."
      }
    }
  }
}
```

## Available Tools

### pay_invoice

Pay a Lightning invoice directly and get the preimage as proof of payment.

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `invoice` | string | Yes | - | BOLT11 Lightning invoice string to pay |
| `max_sats` | integer | No | 1000 | Maximum sats allowed to pay |

**Returns:** JSON with `success`, `preimage` (proof of payment), and `message`

### access_l402_resource

Fetch a URL with automatic L402 payment handling. Requires a wallet that returns preimage (Strike, LND, CoinOS, CLINK).

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `url` | string | Yes | - | The URL to fetch |
| `method` | string | No | GET | HTTP method (GET, POST, PUT, DELETE) |
| `headers` | object | No | {} | Additional request headers |
| `body` | string | No | - | Request body for POST/PUT |
| `max_sats` | integer | No | 1000 | Maximum sats to pay for this request |

**Returns:** Response body text or error message

### pay_l402_challenge

Manually pay an L402 invoice and get the authorization token.

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `invoice` | string | Yes | - | BOLT11 invoice string |
| `macaroon` | string | Yes | - | Base64-encoded macaroon from L402 challenge |
| `max_sats` | integer | No | 1000 | Maximum sats allowed for this payment |

**Returns:** L402 token in format `macaroon:preimage` for use in Authorization header

### check_wallet_balance

Check the connected wallet balance.

**Parameters:** None

**Returns:** Current balance in satoshis

### get_payment_history

List recent payments made during this session.

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `limit` | integer | No | 10 | Maximum number of payments to return |
| `since` | string | No | - | ISO timestamp to filter payments from |

**Returns:** List of payments with url, amount, timestamp, and status

### configure_budget

Set spending limits for the session.

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `per_request` | integer | No | 1000 | Maximum sats per individual request |
| `per_session` | integer | No | 10000 | Maximum total sats for the session |

**Returns:** Confirmation of new limits

### get_budget_status

View current budget configuration and session spending (read-only).

**Parameters:** None

**Returns:** Budget tiers, limits, and current session spending

### create_l402_challenge (Agentic Commerce)

Create an L402 payment challenge to charge another agent or user for accessing a resource. Returns a Lightning invoice and macaroon that the payer must pay before you grant access.

**Requires:** `LIGHTNING_ENABLE_API_KEY` with an Agentic Commerce subscription (from $99/mo).

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `resource` | string | Yes | - | Resource identifier — URL, service name, or description |
| `price_sats` | integer | Yes | - | Price in satoshis to charge |
| `description` | string | No | - | Description shown on the Lightning invoice |

**Returns:** JSON with `challenge` (invoice, macaroon, paymentHash, expiresAt), resource, priceSats, and instructions for the payer.

### verify_l402_payment (Agentic Commerce)

Verify an L402 token (macaroon + preimage) to confirm payment was made. Use this after receiving an L402 token from a payer to validate they paid before granting access.

**Requires:** `LIGHTNING_ENABLE_API_KEY` with an Agentic Commerce subscription (from $99/mo).

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `macaroon` | string | Yes | - | Base64-encoded macaroon from the L402 token |
| `preimage` | string | Yes | - | Hex-encoded preimage (proof of payment) |

**Returns:** JSON with `valid` (boolean) and `resource` (the resource identifier the payment was for).

## L402 Producer Flow (Agent-to-Agent Commerce)

The producer tools enable agents to sell services, not just buy them. This makes agents full commerce participants in the L402 ecosystem.

**Flow:**
1. **Seller agent** calls `create_l402_challenge` with a resource name and price
2. **Seller agent** shares the Lightning invoice with the buyer
3. **Buyer agent** pays the invoice (using `pay_invoice` or `pay_l402_challenge`) and gets a preimage
4. **Buyer agent** sends the L402 token (macaroon + preimage) back to the seller
5. **Seller agent** calls `verify_l402_payment` to confirm payment
6. **Seller agent** grants access to the resource

**Example:**
```
Agent B: I need weather data for New York.

Agent A (seller): I'll create a payment challenge for that.
[Calls create_l402_challenge with resource="weather/new-york", price_sats=10]

Here's your invoice — pay 10 sats to get the data:
  Invoice: lnbc100n1p3...
  Macaroon: AgELbGl...

Agent B: [Pays the invoice, gets preimage]
Here's my L402 token: AgELbGl...:abc123def...

Agent A: Let me verify that payment.
[Calls verify_l402_payment with macaroon="AgELbGl...", preimage="abc123def..."]

Payment verified! Here's your weather data: Temperature: 72F, Humidity: 45%...
```

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

## How L402 Works

L402 (formerly LSAT) is a protocol for API monetization using Lightning Network:

1. Client requests a resource
2. Server returns `402 Payment Required` with a `WWW-Authenticate` header containing a macaroon and BOLT11 invoice
3. Client pays the invoice, receiving a preimage
4. Client retries the request with `Authorization: L402 <macaroon>:<preimage>`
5. Server validates and returns the resource

This MCP server handles steps 2-5 automatically when you use `access_l402_resource`.

## Security Considerations

- **Budget Limits**: Always set appropriate spending limits for your use case
- **Wallet Credentials**: Keep your NWC connection string, API keys, and macaroons secure
- **Session Isolation**: Each server instance maintains its own budget and payment history
- **Invoice Verification**: The server verifies invoice amounts before paying
- **Dedicated Wallet**: Never use your main wallet or business funds for AI agents

## Development

### Setup

```bash
git clone https://github.com/refined-element/lightning-enable-mcp
cd lightning-enable-mcp/python/lightning-enable-mcp
pip install -e ".[dev]"
```

### Running Tests

```bash
pytest
```

### Type Checking

```bash
mypy src/lightning_enable_mcp
```

### Linting

```bash
ruff check src/
ruff format src/
```

## Architecture

```
lightning_enable_mcp/
├── server.py          # Main MCP server and tool registration
├── l402_client.py     # L402 protocol implementation
├── nwc_wallet.py      # Nostr Wallet Connect client
├── budget.py          # Spending limit management
└── tools/
    ├── access_resource.py       # access_l402_resource tool
    ├── pay_challenge.py         # pay_l402_challenge tool
    ├── create_l402_challenge.py # create_l402_challenge tool (producer)
    ├── verify_l402_payment.py   # verify_l402_payment tool (producer)
    ├── wallet.py                # check_wallet_balance tool
    └── budget.py                # configure_budget, get_payment_history tools
```

## License

MIT License - see [LICENSE](LICENSE) for details.

## Support

- **Issues**: https://github.com/refined-element/lightning-enable-mcp/issues
- **Documentation**: https://docs.lightningenable.com
- **Email**: support@lightningenable.com

## Related Projects

- [Lightning Enable API](https://api.lightningenable.com) - L402-protected API server
- [Lightning Enable Store](https://store.lightningenable.com) - Live L402 commerce demo
- [Lightning Enable Docs](https://docs.lightningenable.com) - Full documentation
- [MCP Specification](https://modelcontextprotocol.io) - Model Context Protocol
- [NIP-47](https://github.com/nostr-protocol/nips/blob/master/47.md) - Nostr Wallet Connect
