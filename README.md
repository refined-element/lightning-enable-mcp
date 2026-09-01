<!-- mcp-name: io.github.refined-element/lightning-enable-mcp -->

Part of [Lightning Enable](https://lightningenable.com) — infrastructure for agent commerce over Lightning.

# Lightning Enable MCP Server

[![Discord](https://img.shields.io/discord/1405389254892195951?label=community&logo=discord&color=5865F2)](https://discord.gg/rX7NxHY8vx)
[![PyPI downloads](https://img.shields.io/pypi/dm/lightning-enable-mcp?label=PyPI%20downloads&logo=pypi&logoColor=white)](https://pypi.org/project/lightning-enable-mcp/)
[![NuGet downloads](https://img.shields.io/nuget/dt/LightningEnable.Mcp?label=NuGet%20downloads&logo=nuget)](https://www.nuget.org/packages/LightningEnable.Mcp)
[![Docker pulls](https://img.shields.io/docker/pulls/refinedelement/lightning-enable-mcp?label=Docker%20pulls&logo=docker&logoColor=white)](https://hub.docker.com/r/refinedelement/lightning-enable-mcp)

## Monetize your own API — 30-day free trial

Agents pay your API per request over Lightning — flat subscription at $49/mo, and you keep 100% of every sat.

- **[Start free trial](https://api.lightningenable.com/Checkout?plan=individual&utm_source=github&utm_medium=readme&utm_campaign=gtm-aug-2026)** — 30 days, no charge today
- **[Fast Lane](https://docs.lightningenable.com/getting-started/activate-with-lightning?utm_source=github&utm_medium=readme&utm_campaign=gtm-aug-2026)** — pay 100 sats over Lightning, no card
- **[Pricing](https://lightningenable.com/pricing?utm_source=github&utm_medium=readme&utm_campaign=gtm-aug-2026)**

Or sign up without leaving your agent: call the `create_lightning_enable_account` tool.

An open-source MCP (Model Context Protocol) server that enables AI agents to make Lightning Network payments and participate in agent-to-agent commerce. Wallet, invoice, L402, budget, and API-discovery tools work out of the box with just a wallet. Producer tools (sell access via L402) and Agent Service Agreement (ASA) tools (agent-to-agent discovery, request, settlement, and attestation over Nostr) unlock with an [Agentic Commerce subscription](https://lightningenable.com). See the [full tool list](https://docs.lightningenable.com/products/l402-microtransactions/mcp-complete-guide).

Available in **.NET** and **Python**.

## What It Does

Give your AI agent a Lightning wallet and it can:

- **Pay invoices** — Send Bitcoin via Lightning to any BOLT11 invoice
- **Access L402 APIs** — Automatically pay L402 challenges for seamless API access
- **Discover APIs** — Search the L402 API registry to find paid APIs by keyword or category, or fetch a specific API's manifest for full endpoint details and pricing
- **Track spending** — Budget limits, payment history, and balance checks
- **Create invoices** — Generate invoices to receive payments
- **Get BTC price** — Real-time Bitcoin price from Strike
- **Exchange currency** — Convert between USD/BTC/EUR and more (Strike wallet)
- **Send on-chain** — Send Bitcoin on-chain (Strike/LND)
- **Self-bootstrap a Lightning Enable account** — `create_lightning_enable_account` pays a ~100-sat activation fee over L402 and returns a merchant API key: the free→paid signup form that *is* the protocol, unlocking the producer + ASA tools with no browser or checkout page.
- **Sell services (L402 Producer)** — Create L402 payment challenges and verify payments, enabling agents to be full commerce participants that both buy and sell
- **Agent commerce (ASA)** — Discover, request, settle, and review agent-to-agent services on Nostr

## Quick Start

### 1. Install

```bash
# .NET
dotnet tool install -g LightningEnable.Mcp

# Python
pip install lightning-enable-mcp

# Python (no install)
uvx lightning-enable-mcp

# Docker
docker pull refinedelement/lightning-enable-mcp:latest
```

### 2. Configure one L402-capable wallet

L402 — the whole point of this server — needs a wallet that returns the payment **preimage**. Set exactly one of these (as an env var, e.g. in the [Claude Desktop config](#claude-desktop-config) below):

- **Strike** (easiest to start) — `STRIKE_API_KEY`, from https://dashboard.strike.me
- **NWC** (self-custody, Nostr) — `NWC_CONNECTION_STRING`, from CoinOS / CLINK / Alby Hub
- **LND** (your own node — always returns a preimage) — `LND_REST_HOST` + `LND_MACAROON_HEX`

> ⚠️ **OpenNode** (`OPENNODE_API_KEY`) works for **invoicing / direct payments only — it never returns a preimage, so it cannot pay L402 challenges.** Don't make it your only wallet if you want L402 (the core use case).

If several are set, priority is: **LND > NWC > Strike > OpenNode**. See [Supported Wallets](#supported-wallets) for the full compatibility matrix.

### 3. Prove the whole loop works — `test_l402_payment`

Ask your agent:

```
Run test_l402_payment
```

This pays a public **1-sat** L402 endpoint end to end, proving your wallet is connected, returns a preimage, and can complete a real L402 payment. It's the one-line answer to "is my wallet actually working?" — and it costs about 1 satoshi.

### 4. Then have some fun — buy a t-shirt

Once the loop works, try the [Lightning Enable Store](https://store.lightningenable.com), a live L402-powered web store. Ask Claude:

```
Buy me a Lightning Enable t-shirt from store.lightningenable.com
```

(This one needs a funded wallet and a shipping address, which is why `test_l402_payment` — one sat, no shipping — is the faster first proof.)

## Claude Desktop Config

Add to your `claude_desktop_config.json`:

**.NET:**
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

**Python:**
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

Config file locations:
- **Windows:** `%APPDATA%\Claude\claude_desktop_config.json`
- **macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`
- **Linux:** `~/.config/claude/claude_desktop_config.json`

## Supported Wallets

| Wallet | Setup | L402 Support |
|--------|-------|-------------|
| **Strike** | API key | Yes |
| **LND** | REST + macaroon | Yes (guaranteed) |
| **NWC (CoinOS)** | Connection string | Yes |
| **NWC (CLINK)** | Connection string | Yes |
| **NWC (Alby Hub)** | Connection string | Yes |
| **OpenNode** | API key | No (no preimage) |

## Tools

**Canonical inventory: 26 tools — 17 free (out of the box, just a wallet) + 9 that require `LIGHTNING_ENABLE_API_KEY`** (an [Agentic Commerce subscription](https://lightningenable.com); 2 L402 Producer + 7 Agent Service Agreement). This table is the single source of truth every advertised count derives from — it is pinned to the code by the tool-inventory guard tests in both ports (drift fails CI).

> **ASA availability note.** The L402/producer tools, `discover_agent_services`, `settle_agent_service`, and `unpublish_agent_capability` work against the hosted API today. The agent-to-agent coordination tools — `publish_agent_capability`, `request_agent_service`, `publish_agent_attestation`, `get_agent_reputation` — use the agent capability backend, which is **not yet enabled on the hosted Lightning Enable API** (calls there currently return an error) and are in preview. Marketplace listings are published today via the Lightning Enable dashboard / L402 proxy pipeline.

**Deprecated aliases** (accepted but unadvertised, forward to the new tool, removed in v2.0.0): `confirm_payment` → `verify_confirmation_code`; `check_wallet_balance`, `get_all_balances` → `get_balance`.

| Tool | Access | What it does |
|------|--------|--------------|
| `pay_invoice` | Free | Pay a BOLT11 Lightning invoice directly, get the preimage |
| `pay_l402_challenge` | Free | Pay an L402 challenge (invoice + macaroon), get the token |
| `access_l402_resource` | Free | Fetch a URL, auto-paying any L402 challenge |
| `test_l402_payment` | Free | Self-test the wallet against a public 1-sat L402 endpoint |
| `discover_api` | Free | Search the L402 API registry / fetch an API manifest |
| `create_invoice` | Free | Create a BOLT11 invoice to receive payment |
| `check_invoice_status` | Free | Check whether a created invoice was paid |
| `get_balance` | Free | Wallet balance: sats, all currencies (Strike), and wallet info |
| `exchange_currency` | Free | Convert between USD and BTC (Strike) |
| `send_onchain` | Free | Send an on-chain Bitcoin payment (Strike, LND) |
| `get_btc_price` | Free | Current Bitcoin price in USD |
| `get_payment_history` | Free | List payments made this session (in-memory) |
| `get_receipts` | Free | Read the durable, append-only receipt log |
| `get_budget_status` | Free | View budget config and session spend (read-only) |
| `configure_budget` | Free | Tighten runtime spending caps (tighten-only) |
| `verify_confirmation_code` | Free | Verify an out-of-band payment confirmation code (verification only — never pays) |
| `create_lightning_enable_account` | Free | Self-bootstrap signup: pay ~100 sats, get a merchant API key |
| `create_l402_challenge` | Agentic Commerce | L402 Producer: create a challenge to charge for a resource |
| `verify_l402_payment` | Agentic Commerce | L402 Producer: verify an L402 token (macaroon + preimage) |
| `discover_agent_services` | Agentic Commerce | ASA: search for agent capabilities on Nostr |
| `publish_agent_capability` | Agentic Commerce | ASA: publish your agent's services (kind 38400) |
| `unpublish_agent_capability` | Agentic Commerce | ASA: take a listing down — retire the proxy + NIP-09 removal |
| `request_agent_service` | Agentic Commerce | ASA: request a service from another agent (kind 38401) |
| `settle_agent_service` | Agentic Commerce | ASA: pay for an agent service via L402 settlement |
| `publish_agent_attestation` | Agentic Commerce | ASA: leave a review/rating for an agent (kind 38403) |
| `get_agent_reputation` | Agentic Commerce | ASA: check an agent's reputation from attestations |

`create_lightning_enable_account` is free and *self-provisions* the API key the 8 gated tools need — an agent with a wallet pays a ~100-sat activation fee and unlocks them on the spot.

## Documentation

- [.NET README](dotnet/src/LightningEnable.Mcp/README.md) — Full .NET documentation
- [Python README](python/lightning-enable-mcp/README.md) — Full Python documentation
- [Full Docs](https://docs.lightningenable.com/products/l402-microtransactions/mcp-complete-guide) — Complete guide to every tool
- [AI Spending Security](https://docs.lightningenable.com/products/l402-microtransactions/ai-spending-security) — Budget controls and safety

## Repository Structure

```
lightning-enable-mcp/
├── dotnet/
│   ├── src/LightningEnable.Mcp/         # .NET MCP server
│   ├── tests/LightningEnable.Mcp.Tests/  # .NET tests
│   └── LightningEnable.Mcp.sln          # Solution file
├── python/
│   └── lightning-enable-mcp/             # Python MCP server
├── .github/workflows/publish-mcp.yml     # CI/CD
├── LICENSE                               # MIT
└── README.md                             # This file
```

## Agent Service Agreement (ASA) Tools

These tools enable agent-to-agent commerce on Nostr:

All seven ASA tools require `LIGHTNING_ENABLE_API_KEY` (an [Agentic Commerce subscription](https://lightningenable.com)); `settle_agent_service` additionally spends your wallet balance, subject to budget limits. For the authoritative access level of every tool, see the canonical [Tools](#tools) table above — it is the single source of truth pinned to the code by the drift guard.

| Tool | Description |
|------|-------------|
| `discover_agent_services` | Search for agent capabilities by category, hashtag, or keyword |
| `publish_agent_capability` | Publish your agent's services to the Nostr network (kind 38400) |
| `unpublish_agent_capability` | Take a published listing down: retire the L402 proxy and emit a NIP-09 removal |
| `request_agent_service` | Request a service from another agent (kind 38401) |
| `settle_agent_service` | Pay for an agent service via L402 Lightning settlement |
| `publish_agent_attestation` | Leave a review/rating for an agent after service completion (kind 38403) |
| `get_agent_reputation` | Check an agent's reputation score from on-protocol attestations |

### How Agent Commerce Works

1. **Discover** — `discover_agent_services(category="translation")` finds agents offering translation
2. **Request** — `request_agent_service(capability_id, budget_sats=100)` sends a service request
3. **Settle** — `settle_agent_service(l402_endpoint)` pays via Lightning and receives the result
4. **Review** — `publish_agent_attestation(pubkey, agreement_id, rating=5)` builds on-protocol reputation

For dynamic pricing, providers use `create_l402_challenge` to generate invoices at the agreed price. Requesters pay and providers verify with `verify_l402_payment`.

## Related Projects

- [le-agent-sdk (Python)](https://github.com/refined-element/le-agent-sdk-python) — `pip install le-agent-sdk`
- [le-agent-sdk (TypeScript)](https://github.com/refined-element/le-agent-sdk-ts) — `npm install le-agent-sdk`
- [le-agent-sdk (.NET)](https://github.com/refined-element/le-agent-sdk-dotnet) — `dotnet add package LightningEnable.AgentSdk`

## Privacy

Lightning Enable does not hold funds — the connected wallet or payment provider (Strike, OpenNode, LND, or an NWC wallet) does. The MCP server runs locally and talks to the wallet/provider you configure and, for L402 discovery, the L402 API registry. Wallet credentials you supply stay on your machine (or, for the hosted API, are encrypted at rest). See the full [Privacy Policy](https://docs.lightningenable.com/legal/privacy-policy) for what data is collected, third parties involved, retention, and contact (privacy@lightningenable.com).

## License

MIT — see [LICENSE](LICENSE).

## Links

- [Lightning Enable](https://lightningenable.com) — Payment enablement middleware
- [Documentation](https://docs.lightningenable.com) — Full docs
- [Store](https://store.lightningenable.com) — Live L402 demo
- [NuGet](https://www.nuget.org/packages/LightningEnable.Mcp) — .NET package
- [PyPI](https://pypi.org/project/lightning-enable-mcp) — Python package
- [Docker Hub](https://hub.docker.com/r/refinedelement/lightning-enable-mcp) — Docker image
