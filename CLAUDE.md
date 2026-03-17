# CLAUDE.md — Lightning Enable MCP

## Project Overview

Part of **Lightning Enable — infrastructure for agent commerce over Lightning.**

Open-source (MIT) MCP server for AI agent Lightning payments. See the main repo at `F:\lightning-enable` for full context.

**Publishing:** Bump version in both `LightningEnable.Mcp.csproj` AND `pyproject.toml`, push to main. `server.json` description must be ≤100 chars (enforced by MCP Registry and CI validation).

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
