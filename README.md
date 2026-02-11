# Lightning Enable MCP Server

An open-source MCP (Model Context Protocol) server that enables AI agents to make Lightning Network payments. All tools are free — no license or subscription required.

Available in **.NET** and **Python**.

## What It Does

Give your AI agent a Lightning wallet and it can:

- **Pay invoices** — Send Bitcoin via Lightning to any BOLT11 invoice
- **Access L402 APIs** — Automatically pay L402 challenges for seamless API access
- **Track spending** — Budget limits, payment history, and balance checks
- **Create invoices** — Generate invoices to receive payments
- **Exchange currency** — Convert USD/BTC (Strike wallet)

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
| **NWC (Alby)** | Connection string | Untested |
| **OpenNode** | API key | No (no preimage) |

## Try It: Lightning Enable Store

The [Lightning Enable Store](https://store.lightningenable.com) is a live L402-powered web store. Ask Claude:

```
Buy me a Lightning Enable t-shirt from store.lightningenable.com
```

## Documentation

- [.NET README](dotnet/src/LightningEnable.Mcp/README.md) — Full .NET documentation
- [Python README](python/lightning-enable-mcp/README.md) — Full Python documentation
- [Full Docs](https://docs.lightningenable.com/products/l402-microtransactions/mcp-complete-guide) — Complete guide with all 13 tools
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

## License

MIT — see [LICENSE](LICENSE).

## Links

- [Lightning Enable](https://lightningenable.com) — Payment enablement middleware
- [Documentation](https://docs.lightningenable.com) — Full docs
- [Store](https://store.lightningenable.com) — Live L402 demo
- [NuGet](https://www.nuget.org/packages/LightningEnable.Mcp) — .NET package
- [PyPI](https://pypi.org/project/lightning-enable-mcp) — Python package
- [Docker Hub](https://hub.docker.com/r/refinedelement/lightning-enable-mcp) — Docker image
