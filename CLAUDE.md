# CLAUDE.md — Lightning Enable MCP

## Project Overview

Open-source (MIT) MCP server for AI agent Lightning payments. See the main repo at `F:\lightning-enable` for full context.

## Bug Fix Workflow

When a bug is reported, do NOT immediately start trying to fix it. Follow this process:
1. **Write a failing test first** — Reproduce the bug with a test that proves it fails
2. **Fix the bug** — Use subagents to implement the fix
3. **Prove the fix** — Show the test now passes
