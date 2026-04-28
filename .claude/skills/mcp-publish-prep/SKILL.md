---
name: mcp-publish-prep
description: Prepare MCP package for publishing (version bump, changelog, validation)
---

Prepare the MCP package for a new release. Use $ARGUMENTS for the new version number (e.g., `1.5.0`).

## Steps

1. **Bump version in BOTH files** (CRITICAL — must be simultaneous):
   - `dotnet/src/LightningEnable.Mcp/LightningEnable.Mcp.csproj` — update `<Version>`
   - `python/lightning-enable-mcp/pyproject.toml` — update `version`
   - These MUST match. CI/CD publishes both from the same version.

2. **Validate `server.json`:**
   - Check `description` field is ≤100 characters (enforced by MCP Registry and CI)
   - Update `version` field if present (CI auto-updates via jq, but good to keep in sync)

3. **Update changelog** if one exists

4. **Build verification:**
   - Run `dotnet build` in `dotnet/src/LightningEnable.Mcp/`
   - Verify no build errors

5. **Review CI/CD:**
   - Check `.github/workflows/publish-mcp.yml` is up to date
   - Verify secrets are configured: `NUGET_API_KEY`, `PYPI_API_TOKEN`, `DOCKERHUB_USERNAME`, `DOCKERHUB_TOKEN`

## Publishing targets (all automated on push to main):
- NuGet: `https://www.nuget.org/packages/LightningEnable.Mcp`
- PyPI: `https://pypi.org/project/lightning-enable-mcp`
- Docker Hub: `https://hub.docker.com/r/refinedelement/lightning-enable-mcp`
- MCP Registry: `https://registry.modelcontextprotocol.io`

## Output
Report the version bump, validation results, and build status. Remind the user to push to main to trigger CI/CD.
