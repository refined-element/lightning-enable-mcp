# Changelog

All notable changes to the Lightning Enable MCP server are documented here.
Versions apply to both ports (NuGet: `LightningEnable.Mcp`, PyPI: `lightning-enable-mcp`).

## [1.21.0]

### Added

- **In-product trial hint on gated tools.** Tools that require `LIGHTNING_ENABLE_API_KEY`
  (`create_l402_challenge`, `verify_l402_payment`, capability publish/unpublish,
  `request_agent_service`, attestations) now append a one-line signup pointer to their
  not-configured error: a 30-day free-trial checkout link plus a mention of the in-MCP
  `create_lightning_enable_account` tool. Identical string in both ports; behavior for
  configured users is unchanged; free-by-design tools (settle/discovery/reputation) untouched.
- **README funnel.** Root, PyPI, and NuGet READMEs lead with a "Monetize your own API —
  30-day free trial" section (UTM-tagged links) and download badges.

## [1.20.1]

### Fixed

- **Pin the `mcp` SDK to `<2.0.0`.** The dependency was unbounded (`mcp>=1.0.0`),
  so the just-released `mcp` 2.0.0 (a breaking major) was pulled on fresh installs
  and broke the server's `Server` API usage (and the test suite). The code targets
  and is verified against `mcp` 1.x; migration to 2.x is tracked separately.

## [1.20.0]

### Changed

- **`publish_agent_capability` now works on the hosted API.** The
  `POST /api/agents/capabilities` endpoint is live (ungated `AsaMarketplace`
  feature) and creates a publicly-listed L402 proxy that publishes a real,
  platform-signed kind 38400 listing via the manifest/Nostr pipeline. Provide
  `target_url`. Removed the "backend not yet enabled" caveat.
- **`get_agent_reputation` now works on the hosted API.** It reads kind 38403
  attestations off the relay via `GET /api/agents/attestations` and returns the
  average rating plus individual reviews. Note added that ratings are un-weighted
  on-relay data — apply proof/Web-of-Trust weighting before trusting them.
- **`request_agent_service` caveat removed.** The `/api/agents/requests` endpoint
  is live (ungated) and persists the service request.

### Not yet available

- **`publish_agent_attestation`** still returns an error and is intentionally
  disabled: the platform holds a single signing key, so a platform-signed review
  would share one pubkey across all reviewers (worthless for reputation). It
  stays disabled until per-agent, client-side signing exists. Reading reputation
  works today.

## [1.19.0]

### Changed

- **`unpublish_agent_capability` now targets the ungated L402 proxy pipeline**
  (`POST /api/proxy/{proxyId}/unpublish`) instead of the agent-capabilities
  backend, so it actually works for the marketplace listings created via the
  proxy/dashboard path. Params simplified to `service_id` (the listing's d-tag /
  proxy id) + optional `reason` (dropped `pubkey` and `mode`).
- **Honest availability notes** added to the agent-to-agent coordination tools
  (`publish_agent_capability`, `request_agent_service`, `publish_agent_attestation`,
  `get_agent_reputation`) and the README: those use the agent capability backend,
  which is not yet enabled on the hosted API, so calls there currently error.
  L402/producer, `discover_agent_services`, `settle_agent_service`, and
  `unpublish_agent_capability` work against the hosted API today.

## [1.18.0]

### Added

- **`unpublish_agent_capability`** (both ports) — take a published capability down
  (NIP-A5 listing lifecycle). In `remove` mode the backend soft-retires the L402
  proxy and publishes a NIP-09 `kind:5` deletion plus a `status=removed` 38400
  replacement, so other agents stop seeing a dead listing. Requires
  `LIGHTNING_ENABLE_API_KEY`. The advertised tool surface grows **25 → 26** (9
  API-key tools: 2 producer + 7 ASA).

## [1.17.0]

Tool-surface consolidation. The advertised tool surface drops from **26 to 25**
(**18 → 17 free**, 8 gated unchanged). No payment or L402 logic changed — this is a
tool-surface-only change. The three renamed/merged tools keep their **old names as
accepted-but-unadvertised forwarding aliases** for one minor cycle (removed in
**v2.0.0**); an alias still dispatches, forwards to the new tool, and its result carries
a `deprecated: { replaced_by, removal: "v2.0.0" }` marker.

### Changed

- **`confirm_payment` → `verify_confirmation_code`** (both ports). The tool only ever
  *verified* a confirmation code — it never moved money — so it is renamed to say so.
  Old name still works as a hidden alias.
- **`check_wallet_balance` + `get_all_balances` → `get_balance`** (both ports). A single
  tool returns the **superset** of what both returned — the scalar sats balance (plus
  `balanceMsat` on the .NET NWC path), the NWC `wallet_info` block (Python), a `balances[]`
  array (multi-currency for Strike, a single BTC entry otherwise), and the session spend
  summary — dropping nothing either old tool returned. Both old names still work as hidden
  aliases.

### Fixed

- **`verify_confirmation_code` (was `confirm_payment`) no longer implies money moved.**
  On a valid code the .NET port returned `"Payment of $X confirmed"`, which reads as a
  completed payment. Both ports now return `"Code verified — NOTHING HAS BEEN PAID. To
  execute, call <tool> again with confirmation_nonce=<code>."`, plus `valid: true`,
  `amount_sats`, and `tool`.
- **Stale confirmation parameter descriptions.** The `confirmationNonce` parameters on
  `access_l402_resource`, `pay_invoice`, and `pay_l402_challenge` (.NET) pointed at the
  old `confirm_payment` tool; they now describe the out-of-band, human-relayed console
  code.

### Migration

- Replace `confirm_payment` with `verify_confirmation_code`, and `check_wallet_balance` /
  `get_all_balances` with `get_balance`. The old names keep working (with a `deprecated`
  marker in the response) until they are removed in **v2.0.0**. `get_balance` is a strict
  superset, so existing fields your code read still appear.

## [1.16.0] — 2026-07-17

Payment-correctness fixes. **Upgrade recommended** if you pay invoices through
OpenNode, Strike, or Coinos.

### Fixed

- **OpenNode payments reported a fabricated preimage (an internal withdrawal ID) as proof
  of payment — upgrade recommended.** (Python) The OpenNode wallet returned the withdrawal
  ID whenever no preimage was available, and `pay_invoice` published it to the agent as
  `preimage`, the field L402 treats as proof of payment. The resulting
  `L402 <macaroon>:<withdrawal-id>` header is always rejected by the server: money spent,
  no access, and a payment record that falsely claimed a valid preimage. A settled payment
  with no preimage is now reported as a success **with `preimage: null`**, a `trackingId`,
  and an explicit warning that it cannot authenticate. The same fabrication in the Strike
  wallet (which returned the Strike payment ID) is fixed the same way.

- **In-flight payments were reported as completed successfully.** (Both ports) A `pending`
  or `processing` OpenNode payment — normal for a slow Lightning route — was reported as
  `success: true, "Payment successful"` for a payment that could still **fail**, so agents
  proceeded believing they had paid. Pending is now its own outcome: `success: false`,
  `status: "pending"`, with a tracking ID to poll, and an explicit instruction not to retry
  (retrying risks paying twice). It still counts against the session budget, because the
  funds are committed.

- **Values that are not preimages are no longer accepted as proof of payment.** (Both ports)
  Preimages are now validated (64-character hex) at every wallet boundary — OpenNode, Strike,
  NWC, and LND, in both ports. This closes a case in the .NET NWC wallet, which detected a
  UUID instead of a preimage (the known Coinos internal-transfer bug), logged a warning, and
  then returned it to the agent anyway. It also closes two boundaries that checked less than
  they appeared to: the Python NWC wallet validated that every character was a hex *digit*
  but never the *length*, so a short value like `deadbeef` passed; and both LND wallets
  (Python and .NET) decoded whatever arrived and published it unchecked, on the assumption
  that LND always returns a real preimage. A settled payment whose "preimage" fails this
  check is reported as a success **without** a preimage (the funds are gone — calling it a
  failure would invite a retry that pays twice), never as proof.

- **A zero or malformed price in an API manifest read as `affordable_calls: "unlimited"`.**
  (Both ports) Manifests are third-party documents, so any API could claim unlimited
  affordability by publishing a `base_price_sats` of `0`, a string, or nothing at all. An
  unknown price now reads `"unknown"`, never "unlimited" or free. Malformed manifests can no
  longer throw and take down the whole `discover_api` call either.

- **`discover_api` and `get_all_balances` always reported a remaining budget of 0.**
  (Python) Both read `remainingSats`/`limitSats` from the budget service, which never
  emitted those keys, so every value silently defaulted to 0 and every `affordable_calls`
  computed as 0. A `get_btc_price_usd()` call to a method that does not exist also meant the
  USD annotation never rendered. Remaining budget is now derived correctly; when it cannot be
  determined it reads `null`/"unknown" rather than 0.

- **`check_budget` invented a 100,000-sat per-request limit** when no cap was configured and
  reported it to callers as if it were real. It now reports `null` — no cap configured.

### Documentation

- `PriceService` (both ports) documented a stale-price fallback that does not exist. The code
  is correct and fails closed — it raises when all three price sources fail and never serves a
  stale or hardcoded price. The docs now say so. No behavior change.

### Notes

No hardcoded BTC rate was introduced anywhere; the three-source raced, fail-closed price
design is unchanged.
