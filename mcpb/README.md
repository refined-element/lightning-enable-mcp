# MCPB Bundle

This directory builds the [MCPB](https://github.com/anthropics/mcpb) (MCP Bundle) for the
Lightning Enable MCP server — a one-click install for Claude Desktop and the format accepted by
the Smithery "Local" publish path and the Anthropic MCP Directory.

It uses the **uv runtime** (`manifest_version` 0.4, `server.type: "uv"`): the bundle ships only a
manifest, a thin `pyproject.toml`, and an entry shim. At install time uv fetches the published
[`lightning-enable-mcp`](https://pypi.org/project/lightning-enable-mcp) package from PyPI along with
its native dependencies (`coincurve`, `cryptography`) as prebuilt wheels — so the bundle stays tiny
(~30 kB) and cross-platform, with no vendored binaries to keep in sync.

## Contents

| File | Purpose |
|------|---------|
| `manifest.json` | Bundle metadata, the uv server config, `user_config` (wallet creds + optional API key), and `privacy_policies`. |
| `pyproject.toml` | Pins `lightning-enable-mcp==<version>` so uv installs the right release + deps. |
| `src/run.py` | Entry shim — hands off to `lightning_enable_mcp.server:main`. |
| `icon.png` | 512×512 icon. |
| `.mcpbignore` | Excludes caches / lockfiles from the archive. |

## Build

```bash
# from the repo root
npx -y @anthropic-ai/mcpb validate mcpb/manifest.json
npx -y @anthropic-ai/mcpb pack mcpb mcpb/lightning-enable-mcp.mcpb
```

The resulting `.mcpb` is a build artifact (gitignored). Install it in Claude Desktop by
double-clicking, or drag it into Settings → Extensions.

## Release sync

CI (`.github/workflows/mcpb.yml`) stamps the `version` in `manifest.json` and the pinned
`lightning-enable-mcp==<version>` in `pyproject.toml` from the package version in
`LightningEnable.Mcp.csproj` (the single source of truth) at build time, then validates, packs,
and uploads the `.mcpb` as a workflow artifact. So the committed values here are a template — the
released bundle always tracks the package version automatically. For a local build, either match
them by hand or run the workflow's stamp step first.

The workflow intentionally does **not** create a tag or GitHub release: `publish-mcp.yml` triggers
on `mcp-v*` tags, so creating one here would double-publish the packages.
