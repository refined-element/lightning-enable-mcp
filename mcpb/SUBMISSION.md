# Anthropic MCP Directory Submission

## STOP — read this before submitting

Anthropic's published pre-submission checklist lists, under **Unsupported use cases**, that
connectors which **"transfer money, cryptocurrency, or other financial assets"** are **not
accepted** into the Connectors Directory:
<https://claude.com/docs/connectors/building/review-criteria#unsupported-use-cases>

Lightning Enable MCP's core tools (`pay_invoice`, `pay_l402_challenge`, `access_l402_resource`,
`send_onchain`, `settle_agent_service`, `create_lightning_enable_account`) do exactly that — they
move sats over Lightning on the agent's behalf. This is the server's entire reason to exist, so
there is no way to submit a version that both satisfies this rule and remains useful.

**Recommendation:** do not submit this bundle to the Anthropic MCP Directory submission form as
currently scoped. Two options if directory listing still matters:

1. **Email `mcp-review@anthropic.com` before submitting** and ask directly whether a
   Lightning/L402 payment connector is out of scope under this clause, or whether it's aimed at a
   narrower category (e.g., fiat wallet transfers, gift cards) that doesn't cover Bitcoin Lightning
   micropayments. Get a written answer before spending more review-prep effort.
2. **Skip the Directory, keep every other distribution channel** — NuGet, PyPI, Docker Hub, and
   the MCP Registry (`registry.modelcontextprotocol.io`) are all already live and unaffected by
   this clause (see the main repo's `CLAUDE.md` → MCP Distribution & Publishing). The MCPB bundle
   built here still has standalone value even without Directory inclusion: it's a valid one-click
   install artifact for Claude Desktop (drag-and-drop / double-click, see the README's "Claude
   Desktop manual install" section) and for Smithery's "Local" publish path, neither of which
   requires Anthropic review.

Everything below assumes the owner decides to submit anyway (e.g., after (1) comes back
favorable). It documents the bundle's current state against the published requirements so that
decision can be made quickly, not as an endorsement that submission will be accepted.

## Where this comes from

- Submission overview & form links: <https://claude.com/docs/connectors/building/submission>
- Pre-submission checklist (reviewer criteria): <https://claude.com/docs/connectors/building/review-criteria>
- Desktop extension (MCPB) submission form: <https://clau.de/desktop-extention-submission>
- Software Directory Terms: <https://support.claude.com/en/articles/13145338-anthropic-software-directory-terms>
- Software Directory Policy: <https://support.claude.com/en/articles/13145358-anthropic-software-directory-policy>

Desktop extensions (MCPB) use the separate Google-Form-style submission link above — **not** the
Claude.ai org-settings portal that remote MCP servers use. No Team/Enterprise org is required for
this path.

## Bundle state as of this pass (2026-09-07)

Verified locally on branch `feat/mcpb-bundle`:

| Check | Result |
|---|---|
| `mcpb validate mcpb/manifest.json` | **Pass** — "Manifest schema validation passes!" Icon warning is informational only ("Recommended size is 512×512" — already 512×512). |
| `mcpb pack mcpb <out>.mcpb` | **Pass** — 30.5 kB packed / 36.0 kB unpacked, 5 files (`manifest.json`, `pyproject.toml`, `README.md`, `icon.png`, `src/run.py`); `.mcpbignore` correctly excludes caches/lockfiles from the archive. |
| Icon | Present, `mcpb/icon.png`, confirmed 512×512 PNG RGBA. |
| Privacy policy | `privacy_policies: ["https://docs.lightningenable.com/legal/privacy-policy"]` in `manifest.json`; `manifest_version` is `0.4` (≥ required 0.2); URL is HTTPS. README has no dedicated "Privacy Policy" heading yet — the top-level repo README has a `## Privacy` section (see [Owner follow-ups](#owner-follow-ups-before-submitting) below on the exact-heading requirement). |
| Support contact | `manifest.json` → `support: "https://github.com/refined-element/lightning-enable-mcp/issues"`; `author.email: support@lightningenable.com`. Have both ready for the form's "support contact" field. |
| Description length | `manifest.json` → `description` shortened to 98 chars (was 117) for the ≤100-char convention used elsewhere in this ecosystem (`server.json` for the MCP Registry has the same cap). Not a hard `mcpb validate` error either way, but kept consistent. |
| Version pin | `mcpb/pyproject.toml` pins `lightning-enable-mcp==1.24.1`, matching the current PyPI latest (`https://pypi.org/pypi/lightning-enable-mcp/json` → `info.version: "1.24.1"`) and the `dotnet/.../LightningEnable.Mcp.csproj` / `server.json` versions in this repo. |
| Tool count in manifest description | Qualitative only ("pay invoices, unlock paid APIs, and manage budgets") — no hardcoded tool count, matching the README's canonical-inventory-table approach. Safe against the tool-surface changes landing on another branch. |
| Smoke test | **Pass** — see below. |

### Smoke test transcript

Ran the bundle entry point the way `uv` (and therefore Claude Desktop's `uv` server type) would
invoke it, with stdin closed (so the server sees immediate EOF on its stdio transport and exits
cleanly instead of hanging):

```
$ uv run --with lightning-enable-mcp==1.24.1 python mcpb/src/run.py < /dev/null
warning: Ignoring dangling temporary directory: `C:\Python312\Lib\site-packages\~ightning_enable_mcp-1.8.0.dist-info`
2026-09-07 17:23:09,238 - lightning-enable-mcp - INFO - Starting Lightning Enable MCP server...
$ echo $?
0
```

The "dangling temporary directory" line is a pre-existing `uv`/pip artifact from an older
`lightning_enable_mcp` install left on this machine — an environment quirk, not a bundle bug.

Also ran the *exact* command the manifest's `mcp_config` issues
(`uv run --directory mcpb src/run.py`) to confirm the packaged form works too — same clean
start banner, same graceful exit 0 on stdin EOF.

## Pack command (for a local rebuild)

```bash
# from the repo root, mcpb CLI via npx (npm install can hang on this checkout's
# filesystem — npx works fine)
npx -y @anthropic-ai/mcpb validate mcpb/manifest.json
npx -y @anthropic-ai/mcpb pack mcpb mcpb/lightning-enable-mcp.mcpb
```

CI (`.github/workflows/mcpb.yml`) runs the same two commands (after stamping the version from
the csproj) on every PR touching `mcpb/`, on push to `main`, and on manual dispatch, then uploads
the `.mcpb` as a workflow artifact. It never tags, releases, or publishes anywhere.

## Required assets checklist

- [x] Icon, 512×512 PNG — `mcpb/icon.png`
- [x] Privacy policy URL (HTTPS) in `manifest.json` `privacy_policies`
- [x] `manifest_version` ≥ 0.2 (bundle uses 0.4)
- [x] Support contact available (GitHub issues URL + support email)
- [x] Short description ≤ 100 chars
- [x] Documentation URL (`manifest.json` → `documentation`, also linked from README)
- [ ] **"Privacy Policy" section heading in README.md** — the checklist literally asks for that
      heading; add one (can be a short pointer to the doc site's privacy policy) if submitting.
      Not added in this pass because it's a content decision for the owner, not a bundle-mechanics
      fix, and the repo README already covers the substance under `## Privacy`.
- [ ] **Tool `title` + `readOnlyHint`/`destructiveHint` annotations** — the checklist requires
      every tool to declare these. Grepped both ports (`python/.../tools/`,
      `dotnet/src/LightningEnable.Mcp`) and found no `readOnlyHint`/`destructiveHint`/
      `ToolAnnotations` usage anywhere. This is a source-code change in the tool definitions, out
      of scope here (another branch is actively reshaping the tool surface) — **owner follow-up**,
      needed regardless of the money-transfer blocker above.
- [ ] **Test account / credentials for reviewers** — the form requires a fully populated test
      account a reviewer can use to exercise every tool end to end. That means handing Anthropic
      real Lightning wallet credentials (or a sandboxed one) — a decision only the owner can make,
      and one worth revisiting once/if the blocker above is resolved.
- [ ] **Allowed link URIs** (optional but recommended) — list `https://lightningenable.com`,
      `https://docs.lightningenable.com`, `https://dashboard.strike.me` if any tool ever surfaces
      a clickable link via `ui/open-link`. Not currently verified either way in the tool code.

## Fields the owner must fill in personally (submission form)

The desktop-extension form (<https://clau.de/desktop-extention-submission>) and, if a remote
listing is ever pursued instead, the portal's "Listing" step both ask for fields no agent should
fill in unattended:

- Company name, website, and a named primary contact (pre-filled from the submitter's Anthropic
  account — must be an actual person at Refined Element / Lightning Enable)
- Server/listing name (≤100 chars), tagline (≤55 chars), description (≤2000 chars, this is the
  *directory* description, separate from `manifest.json`'s short `description`)
- Category selection (1–5 categories)
- Use-case narrative: what a user needs before connecting (a wallet + optionally an Agentic
  Commerce subscription), whether the connector reads data, writes data, or both (it does both:
  reads balances/prices, writes payments)
- Seven compliance acknowledgments (directory guidelines, first-party API usage, **financial
  transactions** — this one is exactly the clause flagged above, so answering it honestly means
  disclosing the money-movement tools — AI media generation, prompt injection, conversation data
  collection, public documentation)
- Test account credentials for reviewers (see checklist above)

None of this can be completed from this worktree — it requires an Anthropic org login and human
judgment calls about disclosure, so it's left entirely to the owner.
