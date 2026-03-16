<!-- mcp-name: io.github.refined-element/lightning-enable-mcp -->

Part of [Lightning Enable](https://lightningenable.com) — infrastructure for agent commerce over Lightning.

# Lightning Enable MCP Server

An open-source MCP (Model Context Protocol) server that enables AI agents to make Lightning Network payments and participate in agent-to-agent commerce. 23 tools total: 15 free wallet tools, 2 producer tools, and 6 Agent Service Agreement (ASA) tools for discovering, negotiating, and settling services between agents on Nostr. Producer and ASA tools require an [Agentic Commerce subscription](https://lightningenable.com).

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
- **Sell services (L402 Producer)** — Create L402 payment challenges and verify payments, enabling agents to be full commerce participants that both buy and sell
- **Agent commerce (ASA)** — Discover, request, settle, and review agent-to-agent services on Nostr

## Quick Install

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

## Try It: Lightning Enable Store

The [Lightning Enable Store](https://store.lightningenable.com) is a live L402-powered web store. Ask Claude:

```
Buy me a Lightning Enable t-shirt from store.lightningenable.com
```

## Documentation

- [.NET README](dotnet/src/LightningEnable.Mcp/README.md) — Full .NET documentation
- [Python README](python/lightning-enable-mcp/README.md) — Full Python documentation
- [Full Docs](https://docs.lightningenable.com/products/l402-microtransactions/mcp-complete-guide) — Complete guide with all 23 tools
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

| Tool | Description | Subscription |
|------|-------------|-------------|
| `discover_agent_services` | Search for agent capabilities by category, hashtag, or keyword | Free |
| `publish_agent_capability` | Publish your agent's services to the Nostr network (kind 38400) | Agentic Commerce |
| `request_agent_service` | Request a service from another agent (kind 38401) | Agentic Commerce |
| `settle_agent_service` | Pay for an agent service via L402 Lightning settlement | Free* |
| `publish_agent_attestation` | Leave a review/rating for an agent after service completion (kind 38403) | Agentic Commerce |
| `get_agent_reputation` | Check an agent's reputation score from on-protocol attestations | Free |

*settle uses wallet balance, subject to budget limits

### How Agent Commerce Works

1. **Discover** — `discover_agent_services(category="translation")` finds agents offering translation
2. **Request** — `request_agent_service(capability_id, budget_sats=100)` sends a service request
3. **Settle** — `settle_agent_service(l402_endpoint)` pays via Lightning and receives the result
4. **Review** — `publish_agent_attestation(pubkey, agreement_id, rating=5)` builds on-protocol reputation

For dynamic pricing, providers use `create_l402_challenge` to generate invoices at negotiated prices. Requesters pay and providers verify with `verify_l402_payment`.

## Related Projects

- [le-agent-sdk (Python)](https://github.com/refined-element/le-agent-sdk-python) — `pip install le-agent-sdk`
- [le-agent-sdk (TypeScript)](https://github.com/refined-element/le-agent-sdk-ts) — `npm install le-agent-sdk`
- [le-agent-sdk (.NET)](https://github.com/refined-element/le-agent-sdk-dotnet) — `dotnet add package LightningEnable.AgentSdk`

## License

MIT — see [LICENSE](LICENSE).

## Links

- [Lightning Enable](https://lightningenable.com) — Payment enablement middleware
- [Documentation](https://docs.lightningenable.com) — Full docs
- [Store](https://store.lightningenable.com) — Live L402 demo
- [NuGet](https://www.nuget.org/packages/LightningEnable.Mcp) — .NET package
- [PyPI](https://pypi.org/project/lightning-enable-mcp) — Python package
- [Docker Hub](https://hub.docker.com/r/refinedelement/lightning-enable-mcp) — Docker image
