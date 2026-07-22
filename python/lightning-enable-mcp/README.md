<!-- mcp-name: io.github.refined-element/lightning-enable-mcp -->

# Lightning Enable MCP Server (Python)

An MCP (Model Context Protocol) server that enables AI agents to make Lightning Network payments. The package exposes **26 tools**: **18 work out of the box** (free, no subscription); the other **8 unlock with an [Agentic Commerce subscription](https://lightningenable.com)** (from $99/mo) and `LIGHTNING_ENABLE_API_KEY` — 2 producer tools (`create_l402_challenge`, `verify_l402_payment`) and 6 Agent Service Agreement (ASA) tools for agent-to-agent commerce over Nostr. One of the out-of-the-box tools, `create_lightning_enable_account`, even *self-provisions* that API key: an agent with a wallet can pay a ~100-sat activation fee and unlock the subscription tools on the spot.

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

The base install works on **every platform, including Windows**, with no compiler toolchain — and that now includes **NWC wallets** (Nostr Wallet Connect: CoinOS, CLINK, Alby Hub). The Schnorr/ECDH crypto NWC needs comes from `coincurve`, which ships prebuilt wheels for Linux, macOS, and Windows, so it installs as a normal base dependency with nothing to compile.

> The old `[nwc]` extra (which pulled in the `secp256k1` C-extension, with no Windows wheel) is no longer required. `pip install lightning-enable-mcp[nwc]` still resolves for back-compat, but the extra is now empty — the base install already covers NWC.

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
| `LIGHTNING_ENABLE_API_KEY` | For producer + ASA tools | - | API key for the 8 subscription tools. Requires Agentic Commerce subscription. |

Spending limits are a **single source of truth: `BudgetService`**, configured by USD-denominated approval tiers in `~/.lightning-enable/config.json`. These tiers drive the out-of-band confirmation flow. An agent can additionally **tighten** the runtime per-request / per-session sats caps via the `configure_budget` tool (tighten-only — it can never raise a limit above the config file). See `configure_budget` and `get_budget_status` below.

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

### create_lightning_enable_account

Self-bootstrapping signup: activate a Lightning Enable account with a tiny Lightning payment (~100 sats) and get back a merchant API key. Requires **NO** Lightning Enable API key (it *creates* one) — only a connected wallet. On success the API key is merged into `~/.lightning-enable/config.json` (existing keys preserved) so the producer/ASA tools unlock on the next restart. Above-threshold activation fees require an out-of-band confirmation code, exactly like `pay_l402_challenge`.

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `email` | string | Yes | - | Email address to register the account under |
| `max_sats` | integer | No | 1000 | Maximum sats to pay for activation (fee is ~100 sats) |
| `confirmation_nonce` | string | No | - | Human-relayed confirmation code (only for an above-threshold fee) |

**Returns:** JSON with `apiKey`, `merchantId`, `planTier`, `subscriptionStatus`, `trialEndsAt`, `dashboardUrl`, and the config-write status.

### pay_invoice

Pay a Lightning invoice directly and get the preimage as proof of payment.

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `invoice` | string | Yes | - | BOLT11 Lightning invoice string to pay |
| `max_sats` | integer | No | 1000 | Maximum sats allowed to pay |

**Returns:** JSON with `success`, `preimage` (proof of payment), and `message`

### access_l402_resource

Fetch a URL with automatic L402 payment handling. Requires a wallet that returns preimage (Strike, LND, CoinOS, CLINK, Alby Hub).

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

Tighten the runtime sats spending caps on `BudgetService`. **Tighten-only:** an agent can
only LOWER its per-request / per-session caps — it can never raise them above the operator's
`~/.lightning-enable/config.json` limits (converted USD→sats) or an existing tighter runtime
cap. To raise limits, the operator edits the config file. This prevents a prompt-injected
agent from loosening its own caps and then draining the wallet.

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `per_request` | integer | No | 1000 | Maximum sats per individual request (cannot exceed the runtime cap above) |
| `per_session` | integer | No | 10000 | Maximum total sats for the session (cannot exceed the runtime cap above) |

**Returns:** Confirmation of the (tightened) new limits

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
| **Alby (NWC)** | Yes | Yes |
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

- **Out-of-band confirmation**: Payments that fall in a confirmation tier (above the
  auto-approve and log-and-approve thresholds) require human confirmation; payments in the
  log-and-approve band proceed but are logged. When confirmation is required, the server
  prints a confirmation code to its **console / stderr** —
  where the human operator can see it — and **never** returns the code in a tool result.
  The agent must ask the **human** for the code, then re-call the original payment tool with
  its `confirmation_nonce` parameter to proceed. (The separate `confirm_payment` tool only
  *verifies* a code — it does not execute the payment.) This closes a self-approval hole: a prompt-injected
  agent cannot read or generate its own confirmation code. Codes are bound to the exact
  amount **and** tool approved, so they can't be reused across a different payment. Applies to
  `pay_invoice`, `access_l402_resource`, `pay_l402_challenge`, and `settle_agent_service`.
  `send_onchain` always requires confirmation because it is irreversible, and fails closed if
  no budget service is configured.
  - **Threat-model assumption:** this rests on the AI runtime not being able to read the
    server's console/stderr or centralized logs (the common setup — the client launches the
    server as a subprocess whose stderr the model never sees). If the agent shares a shell/host
    with the server and can read its logs, run the server somewhere it can't (separate
    user/host/container).
- **Budget Limits**: Spending limits live in one place — `BudgetService`, configured by the
  USD approval tiers in `~/.lightning-enable/config.json` (these drive the confirmation flow
  and fail closed if the BTC price feed is unavailable). An agent can `configure_budget` to
  *tighten* the runtime sats caps but can never raise them above the config-file limits.
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
