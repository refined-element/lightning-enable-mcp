# CLAUDE.md — Lightning Enable MCP

## Project Overview

Part of **Lightning Enable — infrastructure for agent commerce over Lightning.**

Open-source (MIT) MCP server for AI agent Lightning payments. See the main repo at `F:\lightning-enable` for full context.

**Publishing:** Bump the version in ALL THREE files in the SAME commit, then push to main:
1. `dotnet/src/LightningEnable.Mcp/LightningEnable.Mcp.csproj` — `<Version>`
2. `python/lightning-enable-mcp/pyproject.toml` — `version`
3. `server.json` — `.version`, **both** `.packages[].version` (nuget + pypi), **and** the Docker image pin in `.packages[2].identifier` (`docker.io/refinedelement/lightning-enable-mcp:<version>`)

`server.json` is committed source-of-truth for the MCP Registry entry — it is NOT rewritten at publish time. `publish-mcp.yml` **verifies** it matches the release version and **fails the registry publish on any drift**, so a partial bump (e.g. csproj/pyproject bumped but `server.json` left stale) is caught before it strands registry.modelcontextprotocol.io on an old version. `server.json`'s `description` must be ≤100 chars (also enforced by MCP Registry and CI validation).

## Approval channel (out-of-band confirmation)

Over-threshold payments require a human-relayed confirmation code. The code is the
**operator's**, never the model's — it must never appear in a tool result on any channel, and
a payment is never approved because its notification could not be delivered (a delivery
failure REFUSES the payment and withdraws the minted code).

Where the code goes is configurable, because stderr is only out-of-band when a human is
watching the console:

| `confirmation.channel` | Behaviour |
|------------------------|-----------|
| `stderr` (default) | Print the code to the server console. The historical local behaviour, unchanged. |
| `refuse` | Refuse over-threshold payments. **No pending confirmation is created at all.** |
| `webhook` | POST the pending confirmation to `confirmation.webhookUrl`, signed `X-LightningEnable-Signature: t=…,v1=…` (HMAC-SHA256 over `{t}.{body}`) with `confirmation.webhookSecret`. Goes through the SSRF-guarded client; never follows redirects; a 3xx or non-2xx is a delivery failure. |
| `file` | Append the same JSON line to `confirmation.filePath` (default `~/.lightning-enable/confirmations.jsonl`), 0600 on POSIX. |

Precedence: `LIGHTNING_ENABLE_CONFIRMATION_CHANNEL` > `confirmation.channel` > auto. Auto
keeps `stderr` unless stdin is not a TTY **and** `LIGHTNING_ENABLE_HOSTED=1`, which selects
`refuse`; a non-TTY stdin alone only earns a one-line startup warning (stdio MCP servers
always have a piped stdin). An unparseable channel name fails closed to `refuse`.

Key files — the two ports mirror each other and must stay in sync:
- .NET: `Models/ConfirmationSettings.cs`, `Models/ConfirmationRequest.cs`,
  `Services/ConfirmationChannelResolver.cs`, `Services/ConfirmationChannels.cs`,
  `Services/ConfirmationChannelFactory.cs`, `BudgetService.RequestConfirmationAsync`.
- Python: `confirmation_channel.py`, `config.ConfirmationSettings`,
  `BudgetService.request_confirmation`.

Payment tools MUST call `RequestConfirmationAsync` / `request_confirmation` rather than
minting a code themselves — that single entry point is what enforces the refuse path and the
fail-closed cancel.

## Bug Fix Workflow

When a bug is reported, do NOT immediately start trying to fix it. Follow this process:
1. **Write a failing test first** — Reproduce the bug with a test that proves it fails
2. **Fix the bug** — Use subagents to implement the fix
3. **Prove the fix** — Show the test now passes

## Engineering Standards

Inherited from Lightning Enable — these apply to all ecosystem properties.

1. **User-facing error messages:** Never return blank/empty responses on failure. Always provide descriptive error context.
2. **Graceful error handling:** Handle all errors gracefully. Never let unhandled exceptions crash the process or leak stack traces.
3. **Never log sensitive data:** Never log API keys, wallet credentials, macaroons, preimages, or NWC connection strings. Use safe identifiers only.
4. **Secret key exposure:** If any credentials are potentially leaked (in logs, responses, git history), flag to the user IMMEDIATELY.
5. **Enterprise scale:** Flag any patterns that won't scale to hundreds of thousands of concurrent users.
