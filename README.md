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
- **Track spending** — Budget limits, a durable receipt log, and balance checks
- **Create invoices** — Generate invoices to receive payments
- **Run wallet operations** — BTC price, currency exchange, and on-chain sends (Strike; on-chain also LND)
- **Self-bootstrap a Lightning Enable account** — `create_lightning_enable_account` pays a ~100-sat activation fee over L402 and returns a merchant API key: the free→paid signup form that *is* the protocol, unlocking the producer + ASA tools with no browser or checkout page.
- **Sell services (L402 Producer)** — Set up the seller account, put an API behind L402, price its routes, list it publicly, mint payment challenges and verify payer tokens — all from one tool, so an agent can go from an API key to a paid endpoint without touching REST
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

### 2. First run — `setup_wallet`

Nothing else works without a wallet, so start there. Ask your agent:

```
Run setup_wallet
```

With no arguments it reports which wallet is configured and where the credential came from — the provider and the source, never the credential itself. If there is no wallet, it hands back the setup steps.

**The shortest path is NWC.** In your wallet app (Alby Hub, CoinOS, or any NIP-47 wallet) create a Nostr Wallet Connect connection, copy the `nostr+walletconnect://` string, and give it to the agent:

```
Run setup_wallet with this connection string: nostr+walletconnect://...
```

`setup_wallet` validates the string, connects to the wallet to prove it answers, then writes it to `~/.lightning-enable/config.json` with the file's permissions restricted to your user. It reports the wallet's declared methods and balance. Restart the server afterwards so it picks up the new wallet.

**Or set an environment variable** (in the [Claude Desktop config](#claude-desktop-config) below, say). L402 — the whole point of this server — needs a wallet that returns the payment **preimage**:

- **Strike** — `STRIKE_API_KEY`, from https://dashboard.strike.me
- **NWC** — `NWC_CONNECTION_STRING`, from CoinOS / CLINK / Alby Hub
- **LND** (your own node — always returns a preimage) — `LND_REST_HOST` + `LND_MACAROON_HEX`
  - Self-signed node cert? Set `LND_TLS_CERT_PATH` to the node's `tls.cert` (pins TLS to that cert). `LND_SKIP_TLS_VERIFY=true` is the escape hatch, not the recommendation. Optional: `LND_FEE_LIMIT_SATS`, `LND_PAYMENT_TIMEOUT_SECONDS`.

> ⚠️ **OpenNode** (`OPENNODE_API_KEY`) works for **invoicing / direct payments only — it never returns a preimage, so it cannot pay L402 challenges.** Don't make it your only wallet if you want L402 (the core use case).

If several are set, priority is: **LND > NWC > Strike > OpenNode**, and **environment variables win over the config file** — `setup_wallet` refuses to write a connection string that an env var would override, and tells you which variable to unset. See [Supported Wallets](#supported-wallets) for the full compatibility matrix.

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

## Claude Desktop manual install (MCPB bundle)

For a one-click install with no config-file editing, grab the packaged [MCPB](https://github.com/modelcontextprotocol/mcpb) bundle from the [`mcpb/`](mcpb/) directory instead of hand-editing JSON: build it locally with `npx -y @anthropic-ai/mcpb pack mcpb lightning-enable-mcp.mcpb` (or download the `.mcpb` artifact from a [`mcpb.yml`](.github/workflows/mcpb.yml) CI run), then double-click the resulting `.mcpb` file or drag it into Claude Desktop's Settings → Extensions — Claude Desktop prompts for your wallet credentials (Strike, NWC, or LND) and optional Lightning Enable API key through its own settings UI instead of a text config file. See [`mcpb/README.md`](mcpb/README.md) for what's in the bundle and [`mcpb/SUBMISSION.md`](mcpb/SUBMISSION.md) for its Anthropic MCP Directory submission status.

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

## Deploying hosted

A payment above your auto-approve threshold needs a human's approval: the server mints a
short confirmation code, sends it to **you**, and the agent can only proceed once you read it
back. The code never appears in a tool result, so a prompt-injected agent can't approve its
own spending.

That only works if the code reaches a human. On your laptop it goes to the server's console
(stderr) and you read it there. On a hosted server — a claude.ai connector, a Docker
container, a fleet — nobody is watching stderr, and on a shared host the agent might read it
itself. So you choose the **approval channel**.

| Channel | What happens above the threshold | Use it when |
|---------|----------------------------------|-------------|
| `stderr` (default) | The code is printed to the server console. | You run the server locally and watch its output. |
| `refuse` | The payment is refused. No code is minted at all. | You run it hosted and no one can receive a code. |
| `webhook` | The pending confirmation is POSTed to your URL, HMAC signed. You relay the code to the agent. | You have an ops channel — chat, pager, an approval app. |
| `file` | The same JSON line is appended to a file you tail. | You collect approvals from logs. |

Whatever the channel, two rules hold: the code is never returned to the agent, and a payment
is never approved because its notification failed to send — a delivery failure refuses the
payment and withdraws the code.

### Choosing a channel

Precedence: environment variable, then config file, then automatic.

```json
{
  "confirmation": {
    "channel": "webhook",
    "webhookUrl": "https://ops.example.com/lightning-approvals",
    "webhookSecret": "<shared secret>",
    "filePath": "/var/log/lightning-enable/confirmations.jsonl"
  }
}
```

| Environment variable | Overrides |
|----------------------|-----------|
| `LIGHTNING_ENABLE_CONFIRMATION_CHANNEL` | `confirmation.channel` (`stderr` \| `refuse` \| `webhook` \| `file`) |
| `LIGHTNING_ENABLE_CONFIRMATION_WEBHOOK_URL` | `confirmation.webhookUrl` |
| `LIGHTNING_ENABLE_CONFIRMATION_WEBHOOK_SECRET` | `confirmation.webhookSecret` |
| `LIGHTNING_ENABLE_CONFIRMATION_FILE` | `confirmation.filePath` (default `~/.lightning-enable/confirmations.jsonl`) |
| `LIGHTNING_ENABLE_HOSTED` | Set to `1` to declare a hosted deployment (see below) |

With no channel set, the server decides:

| stdin | `LIGHTNING_ENABLE_HOSTED` | Channel | Startup warning |
|-------|---------------------------|---------|-----------------|
| A TTY | anything | `stderr` | none |
| Not a TTY | unset or `0` | `stderr` | yes — nobody may be reading it |
| Not a TTY | `1` | `refuse` | yes — over-threshold payments are refused |

An stdio MCP server always has a piped stdin, so the warning is a nudge, not a diagnosis:
if a human really is watching the console, ignore it. `LIGHTNING_ENABLE_HOSTED=1` is the
explicit opt-in that turns the safe posture on — nothing flips your behavior automatically.

A channel name the server can't parse fails closed to `refuse` and says so at startup, so a
typo never silently restores a code nobody reads.

### The webhook payload

`POST` with `Content-Type: application/json` and an HMAC-SHA256 signature over
`{timestamp}.{body}`, the same scheme the Lightning Enable API signs merchant webhooks with:

```
X-LightningEnable-Signature: t=1757203200,v1=9f86d081...
```

```json
{
  "type": "payment.confirmation_required",
  "nonce": "AB12CD",
  "tool": "pay_invoice",
  "amountSats": 50000,
  "amountUsd": 12.34,
  "destination": "lnbc500u1pj9npjpp5abcdefghijklmnopqr...",
  "description": "lnbc500u1pj9npjpp5...",
  "summary": "pay_invoice — $12.34 (50,000 sats), invoice lnbc500u1pj9npjpp5...",
  "createdAt": "2026-09-07T12:00:00Z",
  "expiresAt": "2026-09-07T12:02:00Z",
  "expiresInSeconds": 120
}
```

The `file` channel writes this exact object, one JSON line per confirmation, and pins the
file to `0600` on POSIX — it holds live approval codes.

Two things to know about the webhook:

- The URL must be **public**. It goes through the same connect-time SSRF guard as
  agent-supplied URLs, so a private, loopback, or metadata address is refused at startup.
- Redirects are never followed. A `3xx` answer is a delivery failure, so a signed approval
  only ever reaches the URL you configured.

Codes expire after 120 seconds and are bound to the exact amount, tool, and destination they
were approved for.

## Supported Wallets

| Wallet | Setup | L402 Support |
|--------|-------|-------------|
| **Strike** | API key | Yes |
| **LND** | REST + macaroon | Yes (guaranteed) |
| **NWC (CoinOS)** | Connection string | Yes |
| **NWC (CLINK)** | Connection string | Yes |
| **NWC (Alby Hub)** | Connection string | Yes |
| **OpenNode** | API key | No (no preimage) |

## Spending limits

Limits live in `~/.lightning-enable/config.json` and are read-only at runtime — an agent can tighten its own caps but never raise them. You can set them in **USD**, in **satoshis**, or both.

```json
{
  "limits": {
    "maxPerPayment": 500.00,
    "maxPerSession": 100.00,
    "maxPerPaymentSats": 5000,
    "maxPerSessionSats": 50000,
    "autoApproveSats": 100
  }
}
```

| Key | Env var | Meaning |
|-----|---------|---------|
| `maxPerPayment` | — | Max USD per payment |
| `maxPerSession` | — | Max USD per session |
| `maxPerPaymentSats` | `LIGHTNING_ENABLE_MAX_PER_PAYMENT_SATS` | Max satoshis per payment |
| `maxPerSessionSats` | `LIGHTNING_ENABLE_MAX_PER_SESSION_SATS` | Max satoshis per session |
| `autoApproveSats` | `LIGHTNING_ENABLE_AUTO_APPROVE_SATS` | Satoshis a payment may spend **without confirmation** while the BTC price is unavailable |

**Why set the sats ones.** A USD limit has to be converted at the current BTC price, so when every price source is down the payment cannot be checked and is refused — correct, but it stops the agent. A satoshi limit needs no conversion, so a sats-budgeted agent keeps working through a price outage. Set **both** `maxPer…Sats` keys to close every gap.

When both denominations are set, the **stricter** cap wins on every check. `budget(action="status")` reports which one is binding (`bindingDenomination`), the effective cap in sats, whether a price was available, and whether outage mode is active.

### What happens during a price outage

The satoshi **ceilings** still bound the spend. But a ceiling says *"never more than this"* — it does not say *"this much is fine unattended"*, and the USD tier ladder that normally says the second thing cannot be evaluated without a price. So approval fails closed:

- **`autoApproveSats` set** — payments at or below it are auto-approved; anything above takes the normal confirmation flow (the agent asks the human for the code printed to the server console).
- **`autoApproveSats` not set** — every payment needs confirmation until a price source recovers.

`autoApproveSats` is a tier, not a ceiling: it can never widen `maxPerPaymentSats` or `maxPerSessionSats`, which are checked first. It is ignored entirely while a price is available — then the USD tiers decide as usual.

> The auto-pay paths (L402 auto-payment, `send_onchain`, `agent_services action=settle`) refuse anything that needs confirmation rather than prompting. During an outage that means they only proceed under `autoApproveSats`.

## Tools

**Canonical inventory: 16 tools — 14 free (out of the box, just a wallet) + 2 that require `LIGHTNING_ENABLE_API_KEY`** (an [Agentic Commerce subscription](https://lightningenable.com)). This table is the single source of truth every advertised count derives from — it is pinned to the code by the tool-inventory guard tests in both ports (drift fails CI).

Five of these are *action* tools: pass `action` (or `source` for `receipts`) to pick the operation. They replace 16 single-purpose tools whose schemas used to be loaded into the agent's context on every session — see [Tool profiles and old tool names](#tool-profiles-and-old-tool-names). **Every old name still works.**

> **ASA availability note.** `agent_services` with `action` `discover`, `settle` or `unpublish` works against the hosted API today, as do `l402_producer`'s `create` and `verify`. The agent-to-agent coordination actions — `publish`, `request`, `attest`, `reputation` — use the agent capability backend, which is **not yet enabled on the hosted Lightning Enable API** (calls there currently return an error) and are in preview. Marketplace listings are published today via the Lightning Enable dashboard / L402 proxy pipeline.

| Tool | Access | What it does |
|------|--------|--------------|
| `setup_wallet` | Free | Report the configured wallet, or connect one by pasting an NWC connection string |
| `pay_invoice` | Free | Pay a BOLT11 Lightning invoice directly, get the preimage |
| `pay_l402_challenge` | Free | Pay an L402 challenge (invoice + macaroon), get the token |
| `access_l402_resource` | Free | Fetch a URL, auto-paying any L402 challenge |
| `test_l402_payment` | Free | Self-test the wallet against a public 1-sat L402 endpoint |
| `discover_api` | Free | Search the L402 API registry / fetch an API manifest |
| `create_invoice` | Free | Create a BOLT11 invoice to receive payment |
| `check_invoice_status` | Free | Check whether a created invoice was paid |
| `get_balance` | Free | Wallet balance: sats, all currencies (Strike), and wallet info |
| `budget` | Free | `action`: `status` (read limits and session spend) or `tighten` (lower the runtime caps) |
| `receipts` | Free | `source`: `durable` (the append-only receipt log) or `session` (this session's payments) |
| `wallet_ops` | Free | `action`: `price`, `exchange`, or `send_onchain` (Strike; `send_onchain` also LND) |
| `verify_confirmation_code` | Free | Verify an out-of-band payment confirmation code (verification only — never pays) |
| `create_lightning_enable_account` | Free | Self-bootstrap signup: pay ~100 sats, get a merchant API key |
| `l402_producer` | Agentic Commerce | `action`: `configure_receive`, `status`, `create_proxy`, `add_endpoint`, `publish`, `list_challenges`, `create`, `verify` — the whole seller side |
| `agent_services` | Agentic Commerce | `action`: `discover`, `request`, `settle`, `publish`, `unpublish`, `attest`, `reputation` |

`create_lightning_enable_account` is free and *self-provisions* the API key the 2 gated tools need — an agent with a wallet pays a ~100-sat activation fee and unlocks them on the spot.

Every tool carries MCP annotations: a human-readable title and an explicit `readOnlyHint`, plus `destructiveHint` on anything that can spend the wallet. Action tools are annotated for their **widest** action, so `budget` is not read-only (because `tighten` writes) and `wallet_ops` is destructive (because `send_onchain` is).

## Resources

The durable receipt log is also exposed as MCP **resources**, so a client can attach or display it without the model spending a turn on a tool call:

| URI | Contents |
|-----|----------|
| `lightning-enable://receipts` | The most recent 200 receipts as JSONL (`application/x-ndjson`), oldest first |
| `lightning-enable://receipts/{paymentHash}` | Every receipt recorded for one payment hash |

Both read through the same path as the `receipts` tool, which redacts credential-shaped fields at the read boundary — **a preimage never leaves the process**. The payment hash is the safe reference; the preimage is the proof of payment and is not a receipt field. Resources are unaffected by the tool profile: they cost no schema bytes in the model's context.

## Tool profiles and old tool names

Every advertised tool's JSON schema is loaded into the agent's context at the start of each session, so the tool surface is a token cost on every turn. Pick how much of it to advertise with `LIGHTNING_ENABLE_TOOL_PROFILE`:

| Profile | Tools | Use it when |
|---------|-------|-------------|
| `lite` | 6 — `setup_wallet`, `pay_invoice`, `access_l402_resource`, `get_balance`, `budget`, `receipts` | The agent only needs to get a wallet, spend, and stay inside its budget |
| `standard` *(default)* | 16 — the table above | Everything, at about 40% less schema than the old surface |
| `full` | 32 — `standard` plus every pre-consolidation name | You have prompts or scripts written against the old tool names |

```json
{
  "mcpServers": {
    "lightning-enable": {
      "command": "uvx",
      "args": ["lightning-enable-mcp"],
      "env": {
        "STRIKE_API_KEY": "your-strike-api-key",
        "LIGHTNING_ENABLE_TOOL_PROFILE": "lite"
      }
    }
  }
}
```

**Profiles are listing-only.** A tool the profile does not advertise is still callable by name, and every old name still dispatches, under every profile. Narrowing the profile trims the schemas pushed into the model's context; it never removes capability.

### Old name → new call

Old names are accepted but unadvertised (except under `full`). Each forwards to the new tool and its result carries a `deprecated` marker naming the replacement. **Removed in v3.0.0** — move to the new names.

| Old tool | New call |
|----------|----------|
| get_budget_status | `budget(action="status")` |
| configure_budget | `budget(action="tighten")` |
| get_receipts | `receipts(source="durable")` |
| get_payment_history | `receipts(source="session")` |
| get_btc_price | `wallet_ops(action="price")` |
| exchange_currency | `wallet_ops(action="exchange")` |
| send_onchain | `wallet_ops(action="send_onchain")` |
| create_l402_challenge | `l402_producer(action="create")` |
| verify_l402_payment | `l402_producer(action="verify")` |
| discover_agent_services | `agent_services(action="discover")` |
| request_agent_service | `agent_services(action="request")` |
| settle_agent_service | `agent_services(action="settle")` |
| publish_agent_capability | `agent_services(action="publish")` |
| unpublish_agent_capability | `agent_services(action="unpublish")` |
| publish_agent_attestation | `agent_services(action="attest")` |
| get_agent_reputation | `agent_services(action="reputation")` |
| confirm_payment | `verify_confirmation_code` |
| check_wallet_balance | `get_balance` |
| get_all_balances | `get_balance` |

The last three predate this consolidation and stay hidden in every profile, `full` included.

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

## Selling with L402 (the `l402_producer` tool)

The whole seller side behind one tool. Pass `action` to pick the operation. Every action
needs `LIGHTNING_ENABLE_API_KEY` (an [Agentic Commerce subscription](https://lightningenable.com));
no action can spend your wallet.

> **API version.** `create`, `verify`, `create_proxy`, `add_endpoint` and `publish` work
> against every Lightning Enable API build. `configure_receive` needs the NWC receiving lane
> and `list_challenges` needs the challenge-listing route, both of which ship with the API
> release this version targets. Run `l402_producer(action="status")` first — it degrades
> gracefully and tells you what the deployment you are pointed at supports.

| Action | Arguments | What it does |
|--------|-----------|--------------|
| `configure_receive` | `nwc_connection_string` *(optional)* | Point L402 payouts at a wallet you control |
| `status` | `limit` | Plan, receiving wallet, onboarding checklist, recent mints *(read-only)* |
| `create_proxy` | `name`, `target_base_url`, `description`, `default_price_sats` | Put an existing API behind L402 |
| `add_endpoint` | `proxy_id`, `endpoint_id`, `path`, `http_method`, `summary`, `price_sats` | Price one route |
| `publish` | `proxy_id`, `service_name`, `service_description`, `categories` | List the service so other agents can find it |
| `list_challenges` | `challenge_status`, `limit`, `offset` | Read back what you minted *(read-only)* |
| `create` | `resource`, `price_sats`, `description` | Mint a one-off invoice + macaroon challenge |
| `verify` | `macaroon`, `preimage` | Check a payer's token before granting access |

### From an API key to a paid endpoint

No dashboard, no REST client, no human in the loop. Each step is one tool call:

1. **Get an API key.** `create_lightning_enable_account` — pays a ~100-sat activation fee
   from your wallet and writes the key to `~/.lightning-enable/config.json`. Restart the
   server so the gated tools unlock. *(Skip if you already have a key.)*
2. **Point payouts at your own wallet.**
   `l402_producer(action="configure_receive")` — with no argument it reuses the NWC wallet
   this server already pays with; pass `nwc_connection_string` to use a different one. The
   string is never echoed back, only `<set>`.
3. **Check where you stand.** `l402_producer(action="status")` — plan, receiving wallet, and
   the onboarding checklist, so the agent can see what is still missing.
4. **Put your API behind L402.**
   `l402_producer(action="create_proxy", name="Weather API", target_base_url="https://api.example.com", default_price_sats=25)`
   — returns a `proxy_id` and the public base URL. Every request under it now answers `402`
   until it is paid.
5. **Price each route.**
   `l402_producer(action="add_endpoint", proxy_id="weather-api", endpoint_id="forecast", path="/forecast", http_method="GET", price_sats=10)`
   — repeat per route.
6. **List it.**
   `l402_producer(action="publish", proxy_id="weather-api", service_description="Five-day forecasts for agents", categories=["weather"])`
   — returns the OpenAPI and manifest URLs. Other agents find it through `discover_api`.
7. **Watch the money arrive.**
   `l402_producer(action="list_challenges", challenge_status="paid")` — payment hash,
   resource, amount and status. Never a macaroon, never a preimage.

For a resource you are not proxying — a file, a report, a one-off answer — skip steps 4-6 and
use `create` / `verify` directly.

Payouts settle on **your** wallet. Lightning Enable does not hold the funds.

## Agent Service Agreements (the `agent_services` tool)

Agent-to-agent commerce on Nostr, behind one tool. Pass `action` to pick the operation.
`agent_services` requires `LIGHTNING_ENABLE_API_KEY` (an [Agentic Commerce subscription](https://lightningenable.com)); `action="settle"` additionally spends your wallet balance, subject to budget limits.

| Action | Description |
|--------|-------------|
| discover | Search for agent capabilities by category, hashtag, or keyword |
| publish | Publish your agent's services to the Nostr network (kind 38400) |
| unpublish | Take a published listing down: retire the L402 proxy and emit a NIP-09 removal |
| request | Request a service from another agent (kind 38401) |
| settle | Pay for an agent service via L402 Lightning settlement |
| attest | Leave a review/rating for an agent after service completion (kind 38403) |
| reputation | Check an agent's reputation score from on-protocol attestations |

### How Agent Commerce Works

1. **Discover** — `agent_services(action="discover", category="translation")` finds agents offering translation
2. **Request** — `agent_services(action="request", capability_event_id=..., budget_sats=100)` sends a service request
3. **Settle** — `agent_services(action="settle", l402_endpoint=...)` pays via Lightning and receives the result
4. **Review** — `agent_services(action="attest", subject_pubkey=..., agreement_id=..., rating=5)` builds on-protocol reputation

For dynamic pricing, providers use `l402_producer(action="create")` to generate invoices at the agreed price. Requesters pay and providers verify with `l402_producer(action="verify")`.

The seven old tool names (`discover_agent_services`, `settle_agent_service`, …) still work — see [Old name → new call](#old-name--new-call).

## Related Projects

- [le-agent-sdk (Python)](https://github.com/refined-element/le-agent-sdk-python) — `pip install le-agent-sdk`
- [le-agent-sdk (TypeScript)](https://github.com/refined-element/le-agent-sdk-ts) — `npm install le-agent-sdk`
- [le-agent-sdk (.NET)](https://github.com/refined-element/le-agent-sdk-dotnet) — `dotnet add package LightningEnable.AgentSdk`

## Privacy

Lightning Enable does not hold funds — the connected wallet or payment provider (Strike, OpenNode, LND, or an NWC wallet) does. The MCP server runs locally and talks to the wallet/provider you configure and, for L402 discovery, the L402 API registry. Wallet credentials you supply stay on your machine (or, for the hosted API, are encrypted at rest). See the full [Privacy Policy](https://docs.lightningenable.com/legal/privacy-policy) for what data is collected, third parties involved, retention, and contact (privacy@lightningenable.com).

## Support

If something's broken or not working, email support@lightningenable.com — a real person reads it. For general questions, join the Discord: https://discord.gg/rX7NxHY8vx.
Bug reports and feature requests: open an issue on this repo.

## License

MIT — see [LICENSE](LICENSE).

## Links

- [Lightning Enable](https://lightningenable.com) — Payment enablement middleware
- [Documentation](https://docs.lightningenable.com) — Full docs
- [Store](https://store.lightningenable.com) — Live L402 demo
- [NuGet](https://www.nuget.org/packages/LightningEnable.Mcp) — .NET package
- [PyPI](https://pypi.org/project/lightning-enable-mcp) — Python package
- [Docker Hub](https://hub.docker.com/r/refinedelement/lightning-enable-mcp) — Docker image
