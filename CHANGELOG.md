# Changelog

All notable changes to the Lightning Enable MCP server are documented here.
Versions apply to both ports (NuGet: `LightningEnable.Mcp`, PyPI: `lightning-enable-mcp`).

## [Unreleased]

### Security

- .NET `send_onchain` no longer treats a lost response as "no funds moved". Previously a
  timeout, cancellation, or transport error after the Strike quote was executed released the
  budget, wrote no receipt, and let a retry (with a fresh confirmation code) create and execute
  a second quote — a double send of irreversible funds. Now:
  - The wallet result carries `Submitted` (the money-moving call was issued) and keeps the
    payment and quote ids. A lost response after execute is `Submitted`, `state: UNKNOWN`. A
    poll that runs out of time after a successful execute is `state: PENDING`, not a failure.
    Only an error before execute (or a provider-reported `FAILED`) proves no funds moved. LND
    on-chain sends follow the same rule.
  - `send_onchain` is keyed in the durable operation ledger by
    `SHA256("onchain:" + address + ":" + amountSats [+ ":" + intentId])`. While an earlier send
    for the same key is submitted, pending, unknown, or settled, the wallet's send is never
    called again, even after a restart or with a new confirmation code. The ledger is checked
    before the budget check and the confirmation gate, so a blocked retry mints and consumes no
    code and reserves no budget. It refreshes the provider status once (Strike
    `GET /v1/payments/{id}`) and returns `errorCode: "ALREADY_SUBMITTED"` with the recorded
    payment id and `statusLookup`. The wallet layer repeats the check atomically after
    reservation to close races. A new optional `intentId` parameter lets an agent
    intentionally pay the same address the same amount again; a blank `intentId` is the same
    as omitting it. The duplicate error code `DUPLICATE_SUBMISSION` is now `ALREADY_SUBMITTED`
    (Lightning invoice duplicates too), and an ambiguous outcome is `OUTCOME_UNKNOWN`.
  - Pending and ambiguous sends retain the budget (principal plus known fee, else fee headroom)
    and write a pending receipt. The response includes `state`, `paymentId`, `receipt_written`,
    and a `warning` that the send may have executed and must not be retried blindly. Plain
    failures now carry the same check-before-retrying warning as the Python port.
- Python: `send_onchain` is now idempotent and pending-aware after submission. Previously,
  any failure or exception, including a timeout or cancellation after the Strike quote was
  executed, was treated as "no funds moved": the budget reservation was released, no receipt
  was written, and a retry with a fresh confirmation code created a new quote and executed
  again, which could double-send irreversible funds.
  - On-chain wallet results carry `submitted` (true once the execute call was issued) and
    keep `payment_id` / `quote_id` whenever known. A timeout, transport error, or 5xx after
    execute returns `state="UNKNOWN"`. A poll that times out after a successful execute
    returns `state="PENDING"` as a success, not a failure. LND reports a read timeout after
    the send was issued as `UNKNOWN`; a connection failure is still a pre-submit failure.
  - The operation ledger now covers on-chain sends, keyed by
    `sha256("onchain:" + normalized_address + ":" + amount_sats)`, and adds an `unknown`
    state. A second send for the same address and amount while the prior one is
    `submitted`, `pending`, `unknown`, or `settled` never calls the wallet. It refreshes the
    status through the new `StrikeWallet.get_onchain_payment_status` (`GET /v1/payments/{id}`)
    or refuses and names the recorded payment ID. Only a recorded `failed` state allows a
    fresh send. The ledger survives restarts.
  - For submitted outcomes, including a cancellation after submission, the tool now commits
    the budget reservation (principal plus the known fee, or plus the fee headroom when the
    fee is unknown) and writes a pending receipt. It releases the reservation only when
    failure before submission is proven.

## [2.0.3] — 2026-09-25

### Security

- Generic paid HTTP (`access_l402_resource`, `settle_agent_service`) is now GET/HEAD only in
  both ports. The 402 → pay → retry flow replays the request against a caller-chosen URL, so
  state-changing methods (POST/PUT/PATCH/DELETE) are refused before any request, budget
  reservation, or wallet call. The rule is enforced inside the shared L402 client as well as
  at the tool layer; method casing and whitespace cannot bypass it. The first-party account
  bootstrap POST uses an explicit internal path pinned to the configured API origin.
- .NET payment history no longer retains the BOLT11 invoice, preimage, or L402 token, and
  stores URLs with userinfo, query and fragment stripped. A truncated payment-hash reference
  replaces raw proofs. The immediate payment result still returns the preimage where the
  protocol requires it.
- `settle_agent_service` in both ports no longer accepts plain `http://` to localhost "for
  development". HTTPS is required for every settlement endpoint, and the tool runs the same
  SSRF preflight as `access_l402_resource` (loopback, RFC 1918, link-local and cloud
  metadata, CGNAT, IPv4-mapped IPv6, `*.internal` / `*.localhost`, trailing-dot hosts, and
  inet_aton-style numeric hosts such as `2130706433` or `0x7f000001` are refused before any
  request, budget check, or wallet call). The Python connect-time pin in `ssrf_transport`
  remains the authoritative gate against DNS rebinding. There is no allowlist or environment
  escape hatch.

## [2.0.2] — 2026-09-23

### Security

- .NET BOLT11 amount decoder replaced with a checksum-verifying bech32 decoder. The previous
  parser read whole-BTC invoices (e.g. 1 BTC) and amountless invoices as 1 sat, so budget and
  approval checks could pass while the wallet paid the encoded amount. Python was not affected.

## [2.0.1] — 2026-09-11

### Fixed

- **`LND_TLS_CERT_PATH` was ignored by the Python server.** The variable is documented for
  both ports and honored by .NET, but the Python LND client only read `LND_SKIP_TLS_VERIFY`,
  so pointing `LND_TLS_CERT_PATH` at the node's own self-signed `tls.cert` still failed
  every request with `[SSL: CERTIFICATE_VERIFY_FAILED] self-signed certificate`. The 2.0.0
  notes claimed this was wired; it was not. The Python client now builds its httpx client
  from `lnd_wallet.build_tls_verify`, mirroring the .NET `BuildServerCertificateValidator`:
  `LND_TLS_CERT_PATH` pins exactly that certificate (PEM or DER, no system CAs, hostname
  check off because the pin already fixes the identity); `LND_SKIP_TLS_VERIFY=true` still
  turns verification off with a stderr warning; pinning wins when both are set; a missing
  or unreadable path fails at connect time with an error naming `LND_TLS_CERT_PATH` and the
  path instead of an opaque TLS failure on the first request. Verified against a mainnet
  LND v0.21.3-beta over stdio: `get_balance` succeeds with the cert pinned and returns the
  descriptive error with a wrong path. No .NET code change; the version bump is shared.

## [2.0.0] — 2026-09-10

**Breaking-by-policy.** Nothing here removes a capability, but the tool-surface
consolidation is significant enough to ship as a major: the advertised surface is now
**16 tools in `standard`** (six single-purpose tools folded into five `action` verbs),
**6 in `lite`**, **32 in `full`**. Every pre-consolidation tool name, plus the three v1
aliases (`confirm_payment`, `check_wallet_balance`, `get_all_balances`), still works as a
deprecated alias — the owner has deferred alias removal to **v3.0.0** (originally
targeted for this release; see Deprecated below). Also new: sats-denominated budget
limits, a configurable out-of-band confirmation channel (`stderr` / `refuse` / `webhook`
/ `file`), several new env/config keys (`LIGHTNING_ENABLE_TOOL_PROFILE`,
`LIGHTNING_ENABLE_CONFIRMATION_CHANNEL` and friends, the `limits.*Sats` budget keys),
`setup_wallet`, six new `l402_producer` seller-setup actions, and the durable receipt log
exposed as MCP resources (`lightning-enable://receipts`).

### Changed

- **Tool surface consolidated: 26 advertised tools → 15, in both ports** (16 once
  `setup_wallet` is counted — see Added). Every advertised
  tool's JSON schema is loaded into the agent's context at the start of each session, so the
  tool surface was a token cost on every turn. Sixteen single-purpose tools are now five
  `action` verbs:

  | New call | Replaces |
  |----------|----------|
  | `budget(action="status"\|"tighten")` | `get_budget_status`, `configure_budget` |
  | `receipts(source="durable"\|"session")` | `get_receipts`, `get_payment_history` |
  | `wallet_ops(action="price"\|"exchange"\|"send_onchain")` | `get_btc_price`, `exchange_currency`, `send_onchain` |
  | `l402_producer(action="create"\|"verify")` | `create_l402_challenge`, `verify_l402_payment` |
  | `agent_services(action="discover"\|"request"\|"settle"\|"publish"\|"unpublish"\|"attest"\|"reputation")` | the seven ASA tools |

  Together with tightened descriptions this cuts the advertised schema payload by ~40%
  (Python 17,926 → 10,626 bytes; .NET 17,864 → 10,864 bytes) — measured on the 16-tool
  surface as consolidated, i.e. after `setup_wallet` was added below and before the six
  seller-setup actions were added to `l402_producer`. With those, the shipped `standard`
  surface is 12,475 bytes (Python) / 12,910 bytes (.NET) — still ~30% under the old
  26-tool payload, and still 16 tools.

  **No behaviour changed.** Each action dispatches into the same handler the old tool called,
  so budget checks, out-of-band confirmation (including `send_onchain` always requiring a
  code), the receipt seam and the SSRF guards are the same code reached by a different name.
  `check_invoice_status` was deliberately NOT folded into `create_invoice`: reading a status
  and minting an invoice are different side-effect classes.

- **Descriptions tightened across the retained tools.** Same meaning, fewer tokens; the
  out-of-band confirmation rule (the code is printed to the server console, never returned in
  a tool result — ask the human) is kept verbatim everywhere it applies.

### Added

- **The seller side is now fully tool-driven: six new `l402_producer` actions, both ports.**
  `create` and `verify` handle one challenge each, but everything that has to happen *before*
  either is worth calling was raw REST an agent could not reach. An agent with an API key can
  now go from nothing to a monetized endpoint receiving payments on the merchant's own
  wallet without leaving the tool surface:

  | Action | Arguments | What it does |
  |--------|-----------|--------------|
  | `configure_receive` | `nwc_connection_string` (optional) | Stores the receiving wallet (`PUT /api/merchant/nwc-connection`), then switches the account to it (`PUT /api/merchant/payment-provider`) |
  | `status` | `limit` | Plan, receiving wallet, onboarding checklist and the most recent mints (read-only) |
  | `create_proxy` | `name`, `target_base_url`, `description`, `default_price_sats` | Puts an upstream API behind L402; returns the public base URL |
  | `add_endpoint` | `proxy_id`, `endpoint_id`, `path`, `http_method`, `summary`, `price_sats` | Prices one route and adds it to the manifest |
  | `publish` | `proxy_id`, `service_name`, `service_description`, `categories` | Enables the manifest and lists it publicly; returns the OpenAPI and manifest URLs |
  | `list_challenges` | `challenge_status`, `limit`, `offset` | Reads back what was minted (read-only) |

  Argument names follow each port's convention, as they already did: `price_sats` in Python,
  `priceSats` in .NET. The filter on `list_challenges` is `challenge_status`, not `status`,
  because `status` is an action name.

  **Same tool, new actions — the advertised inventory is unchanged at 16.** `create` and
  `verify` are untouched, and both deprecated aliases (`create_l402_challenge`,
  `verify_l402_payment`) still forward exactly as before.

  **API version.** `create_proxy`, `add_endpoint` and `publish` work against every Lightning
  Enable API build. `configure_receive` needs the NWC receiving lane
  (`PUT /api/merchant/nwc-connection`, provider `nwc`) and `list_challenges` needs
  `GET /api/l402/challenges`, both of which ship with the API release this version targets.
  `status` degrades gracefully — it reports what the deployment it reached could answer.

  `configure_receive` with no argument reuses the wallet this MCP server itself pays with,
  but **only when that wallet is an NWC wallet** — an LND / Strike / OpenNode server is
  refused with a message naming its wallet rather than sent a credential that cannot serve
  as a receiving connection. The connection string is validated locally before it goes on
  the wire, and **never comes back**: every result reports `nwcConnectionString: <set>`, and
  the result JSON is scrubbed of the string (in both its raw and JSON-escaped spellings) in
  case an upstream error body quoted it.

  Errors surface the Lightning Enable API's own members. It answers in RFC 9457
  `application/problem+json` with `type`/`title`/`detail` alongside the legacy
  `error`/`message`, so a failure carries the prose to act on (`"Plan 'free' caps proxy
  configs at 1"`) *and* the stable slug to branch on (`errorCode: plan_proxy_limit`), plus
  per-field `validationErrors` when the API rejects a body. The API key never appears in a
  result. `list_challenges` and `status` copy an allowlist of challenge fields, so a
  macaroon or preimage cannot ride along — the payment hash is the correlation handle.

  This is ~1.9KB (Python) / ~1.3KB (.NET) of extra schema, so both ports' schema-size guards
  were raised (0.60 → 0.75 and 0.65 → 0.78 of the pre-consolidation payload). That is the
  trade the guard exists to make visible: six capabilities folded into an existing verb, with
  ~1KB of headroom left before it fails again.

- **`setup_wallet` — NWC-first wallet onboarding, both ports.** Nothing else in the tool
  surface works without a wallet, and an agent had no way to discover that or fix it: the
  only signal was a stderr warning at startup and a "wallet not configured" string on
  whatever tool it happened to call. Advertised in the `standard` AND `lite` profiles,
  because an agent needs it before anything else.

  With no arguments it reports which wallet is configured and where the credential came
  from — the provider and the source, **never the credential** — or, when there is none,
  the guided path: paste an NWC connection string, or set the LND/Strike env vars, with the
  exact `config.json` shape to write by hand.

  With `nwc_connection_string` it parses the string, refuses if an environment variable
  already selects a wallet (env beats config, so writing the file would be a silent no-op),
  probes the live wallet through the existing NWC client under a 10-second budget, and only
  then writes `wallets.nwcConnectionString` — merged into the existing document so the
  operator's limits and any unknown keys survive, with the same 0600 / `icacls` hardening as
  first-run config creation. It reports the wallet's declared methods and balance; a
  saved-but-dead credential would otherwise fail later, at a payment, where it is far more
  expensive to diagnose.

  The advertised inventory is now **16 tools = 14 free + 2 API-key-gated** (`lite` 6,
  `full` 32).

- **Sats-denominated budget limits: `limits.maxPerPaymentSats` /
  `limits.maxPerSessionSats` / `limits.autoApproveSats`, both ports** (env:
  `LIGHTNING_ENABLE_MAX_PER_PAYMENT_SATS` / `LIGHTNING_ENABLE_MAX_PER_SESSION_SATS` /
  `LIGHTNING_ENABLE_AUTO_APPROVE_SATS`). Spending limits were USD-only, so every budget
  check needed a BTC price; three price sources being down is rare but real, and when it
  happens the check cannot be evaluated and the payment is refused — correct, but it stops
  the agent dead on a fault that has nothing to do with its budget.

  A sats limit is enforced directly, with **no conversion and no price-feed dependency**. A
  USD-only budget still fails closed exactly as before. With both denominations set, the
  **stricter** cap wins on every check — USD is converted only when the feed is available;
  when it is not, the sats caps carry the budget alone. Every gate (approval check, atomic
  reservation, tighten) resolves the same three-way most-restrictive-wins across
  USD-converted, sats, and the runtime tighten cap; tighten-only semantics are unchanged and
  now also apply against a sats config cap.

  `budget(action="status")` reports the effective cap in sats, which configured limit
  produced it, the binding denomination (`usd` / `sats` / `runtime` / `none`), whether a
  price was available, `autoApproveSats`, whether outage mode is active, and one sentence on
  what is and is not in force.

  **Approval during an outage fails closed.** The sats ceilings still bound the spend, but a
  ceiling says "never more than this" — not "this much is fine unattended", which is what the
  unevaluable USD tier ladder normally says. So `limits.autoApproveSats` states that second
  thing explicitly, in satoshis: at or below it a payment is auto-approved; above it, or when
  it is unset, the payment takes the normal confirmation flow. `LOG_AND_APPROVE` is never
  returned on this path. `autoApproveSats` is a tier, not a ceiling — it is checked after the
  sats ceilings, the runtime tighten caps, the first-payment setting and the cooldown, so it
  can never widen any of them — and it is ignored entirely while a price is available. The
  auto-pay paths (L402 auto-payment, `send_onchain`, `agent_services action=settle`) refuse
  anything needing confirmation rather than prompting, so during an outage they proceed only
  under `autoApproveSats`.

- **The durable receipt log as MCP resources, both ports.** `lightning-enable://receipts`
  (the most recent 200 receipts as JSONL, `application/x-ndjson`) and
  `lightning-enable://receipts/{paymentHash}`. A tool call is the agent deciding to look; a
  resource is something a client can attach, watch, or show a human without the model
  spending a turn on it — and the spend log is exactly that kind of artifact. The `receipts`
  tool is unchanged, and resources are unaffected by the tool profile: they cost no schema
  bytes in the model's context.

  Redaction now happens at the **read boundary**, so the tool and both resources are covered
  by one pass. Receipts never carry a preimage by construction, but the log is a plain file
  on the operator's disk — a hand-edit or a future writer could put one there, and by then
  it is one read away from a model's context. Credential-shaped fields (preimage, secret,
  macaroon, connection string, api key, …) are matched by property name, case-insensitively,
  and their value is replaced with `[REDACTED]` so a reader can see the field was withheld.
  The payment hash is deliberately not in that set: it is the safe reference the log is
  keyed on.

- **`LIGHTNING_ENABLE_TOOL_PROFILE` (`lite` | `standard` | `full`), both ports.** Chooses how
  much of the surface `tools/list` advertises: `lite` = 6 tools (`setup_wallet`,
  `pay_invoice`, `access_l402_resource`, `get_balance`, `budget`, `receipts`); `standard`
  (the default) = the 16 tools above; `full` = those plus every pre-consolidation name, for
  prompts and scripts written against the old surface. An unset or unrecognized value resolves to
  `standard` (unrecognized also warns) — never to an empty surface.

  **Profiles are listing-only.** A tool the profile does not advertise is still callable by
  name in every profile; narrowing the profile trims what the model has to read, never what
  the agent can do.

- **A configurable approval channel for over-threshold payments, both ports.** The
  confirmation code was always printed to the server's stderr — right for a local server with
  a human at the terminal, wrong for a hosted one (a claude.ai connector, Docker, a fleet),
  where nobody reads stderr and, on a shared host, the agent might. `confirmation.channel` in
  `~/.lightning-enable/config.json` (or `LIGHTNING_ENABLE_CONFIRMATION_CHANNEL`) now selects
  where the code goes:

  | Channel | Behaviour |
  |---------|-----------|
  | `stderr` | Print to the server console. The default; unchanged. |
  | `refuse` | Refuse over-threshold payments outright. **No code is minted at all.** |
  | `webhook` | POST the pending confirmation to `confirmation.webhookUrl`, signed `X-LightningEnable-Signature: t=…,v1=…` (HMAC-SHA256 over `{t}.{body}`) with `confirmation.webhookSecret`. |
  | `file` | Append the same JSON line to `confirmation.filePath` (default `~/.lightning-enable/confirmations.jsonl`), 0600 on POSIX. |

  The webhook goes through the same connect-time SSRF guard as agent-supplied URLs (so the
  URL must be public) and **never** follows redirects — a `3xx` is a delivery failure, so a
  signed approval can only reach the URL you configured.

  **Two invariants hold on every channel.** The code is never returned in a tool result, and
  a payment is never approved because its notification could not be delivered — a delivery
  failure REFUSES the payment and withdraws the minted code. The `refuse` channel creates no
  pending confirmation at all, and its tool result says so rather than telling the agent to
  go ask a human for a code that does not exist.

- **Hosted auto-detection.** With no channel configured and stdin not a TTY, the server logs a
  one-line startup warning naming the risk, and defaults to `refuse` only when
  `LIGHTNING_ENABLE_HOSTED=1` — an explicit opt-in, so nothing flips behaviour on its own.
  Otherwise it keeps `stderr`. A channel name the server can't parse fails closed to `refuse`
  and says so at startup. Webhook and file settings also read from
  `LIGHTNING_ENABLE_CONFIRMATION_WEBHOOK_URL` / `_SECRET` and `LIGHTNING_ENABLE_CONFIRMATION_FILE`,
  for deployments with no config file. See "Deploying hosted" in the README.

- **MCP tool annotations on every advertised tool, both ports.** A human-readable `title` and
  an explicit `readOnlyHint` on all of them, `destructiveHint` on everything that can spend the
  wallet (`pay_invoice`, `access_l402_resource`, `pay_l402_challenge`, `test_l402_payment`,
  `create_lightning_enable_account`, `wallet_ops`, `agent_services`), and `idempotentHint` on
  `budget`. Action tools are annotated for their **widest** action: `budget` is not read-only
  because `tighten` writes, and `wallet_ops` is destructive because `send_onchain` is.

### Fixed

- **A dropped connection mid-payment was reported as a retryable failure (LND, both
  runtimes).** Once the node had answered 2xx on `POST /v2/router/send` it may already
  have accepted the payment, but a transport error while reading the streamed frames
  (connection reset, torn chunk, read timeout) was wrapped as "Failed to connect to LND"
  / `HTTP_ERROR` / `EXCEPTION` — a *retryable* failure that invited a second payment of
  the same invoice. After a 2xx, any read error now surfaces as **pending** with the
  invoice payment hash as the tracking id (`PaymentPendingError` in Python,
  `NwcPaymentResult.Pending` in .NET), never as failed or succeeded. Errors before any
  response still map to the plain connection failure, because nothing was submitted.
  Also added the missing routing-fee tests (5% ceil, 2-sat floor, `LND_FEE_LIMIT_SATS`
  override honored, `0`/negative/non-numeric override ignored) in both runtimes, and an
  unreadable `LND_TLS_CERT_PATH` now fails with a clear configuration error naming the
  path instead of an unhandled exception at handler creation (.NET).
- **Every LND payment failed with a 404 (Python).** The client paid through
  `POST /v1/channels/transactions` — the `lnrpc.SendPaymentSync` route, which LND has
  REMOVED. A current node answers it with `404 {"code":5,"message":"Not Found"}` and never
  creates a payment, so an agent with a working LND wallet could read its balance but
  could not pay anything, and the node showed no attempt to explain why. Verified against
  LND v0.21.3-beta. Payments now go through `POST /v2/router/send`
  (`routerrpc.SendPaymentV2`) and read its streamed payment frames; the old route is kept
  as a fallback for pre-`routerrpc` nodes and tried only on a 404, which proves nothing
  was submitted and so cannot double-pay. Three things came with the new route: a
  routing-fee ceiling is always sent (`SendPaymentV2` reads the default `0` as
  "zero-fee routes only", which silently fails most payments — default is 5% of the
  invoice, overridable with `LND_FEE_LIMIT_SATS`); the read is bounded node-side *and*
  client-side, so a stalled payment stream surfaces as a non-retryable "pending" instead
  of hanging the agent (`LND_PAYMENT_TIMEOUT_SECONDS`, default 25s); and the all-zero
  preimage LND returns when no proof exists is rejected by name, since it is 64 valid hex
  characters and the format check alone accepted it as L402 proof of payment.
  **Fixed in .NET the same way (next entry).**

- **Every LND payment failed with a 404 (.NET).** `LndWalletService.PayInvoiceAsync` posted to
  the same removed `/v1/channels/transactions` route. It now pays through
  `POST /v2/router/send` and reads the newline-delimited `{"result": <lnrpc.Payment>}` stream
  until a terminal status, keeping the legacy route only as a 404 fallback (never on any
  other error: the route then exists and may already have taken the payment). Same
  funds-safety set as Python: `fee_limit_sat` is always sent (5% of the invoice, floor 2 sats,
  `LND_FEE_LIMIT_SATS` override); the read is bounded node-side (`timeout_seconds`) and
  client-side (`LND_PAYMENT_TIMEOUT_SECONDS`, default 25s), and a stall reports as
  non-retryable pending rather than a failure that invites a double-pay; the all-zero
  preimage is rejected by name; a non-`SUCCEEDED` terminal frame reports pending. Also wired
  the documented-but-unimplemented TLS options on the LND client: `LND_TLS_CERT_PATH` pins the
  node's own `tls.cert` (anything else is rejected) and `LND_SKIP_TLS_VERIFY=true` turns
  verification off with a stderr warning (dev only). Verified end to end against a mainnet
  LND v0.21.3-beta with the cert pinned: a 3-sat L402 invoice settles with a preimage in 2.0s
  and the replayed `Authorization: L402` clears the challenge.

- **An all-digit NWC wallet pubkey could never connect (.NET).** The 64-hex wallet pubkey in
  a `nostr+walletconnect://` string is not a hostname, but it was read through `System.Uri`,
  which applies host rules to it: an all-digit pubkey (a legal x-only key — rare, but a
  wallet can mint one) parses as a malformed IPv4 literal and is rejected outright. The same
  check was lossy in the other direction, accepting any host-shaped 64-character value, so a
  non-hex "pubkey" only failed later at key derivation. Parsing now splits scheme, authority
  and query by hand and validates the pubkey directly, as the Lightning Enable API does. (The
  Python port was unaffected.)

- **Agent-facing hints named the pre-consolidation tools.** Result messages still told agents
  to call `settle_agent_service(...)`, `check get_budget_status`, `use verify_l402_payment`,
  `set via configure_budget`. Those names still dispatch, so nothing was broken — the agent
  was just steered onto the deprecated path, came back with a deprecation marker, and paid a
  round trip for it. Every such hint now names the current call (`agent_services
  action=settle`, `budget action=status`, `l402_producer action=verify`, `budget
  action=tighten`), in both ports, with a drift guard in each so they cannot regress. The
  deprecation aliases themselves are untouched.

### Deprecated

- **The 16 pre-consolidation tool names.** They remain accepted and dispatch to their
  replacement, and every result carries `deprecated: { replaced_by, use, removal }` naming the
  new call (for example `budget(action="status")`). They are unadvertised unless
  `LIGHTNING_ENABLE_TOOL_PROFILE=full`. **Removed in v3.0.0.** The three v1 aliases
  (`confirm_payment`, `check_wallet_balance`, `get_all_balances`) are unchanged and stay hidden
  in every profile.

## [1.24.0]

### Added

- **MPP draft-00 client support (draft-httpauth-payment-00 + draft-lightning-charge-00), both ports.**
  Modern `Payment` challenges — `id`/`realm`/`method`/`intent`/`request`/`expires` plus optional
  `digest`/`description`/`opaque`, with the invoice inside the base64url `request` param — are now
  parsed (superset headers carrying legacy `invoice=`/`amount=`/`currency=` params included), and
  answered on the retry with the modern single-use `Authorization: Payment <base64url(JSON)>`
  credential: a byte-exact echo of every received challenge param plus the lowercase-hex preimage.
  Within the `Payment` scheme the modern profile is preferred over the legacy profile; the existing
  L402-vs-Payment preference is unchanged, and legacy `Payment` + L402/LSAT behavior is untouched.
  Client-side safety checks run before any payment: expired challenges are refused, `intent` must be
  `charge`, `currency` (when present) must be `sat`, and the declared amount must agree with the
  invoice. The `Payment-Receipt` response header is parsed tolerantly and surfaced in
  `access_l402_resource` results (`paymentReceipt` — payment hash only, never the preimage), and
  `pay_l402_challenge` accepts the raw challenge via a new optional `challengeHeader` (.NET) /
  `challenge_header` (Python) argument, returning the single-use credential. Modern credentials are
  never cached or replayed.

### Fixed

- **NWC multi-relay failover (parity with `L402Requests` 0.8.1).** 1.23.2 fixed the comma-joined-relay
  crash by selecting the first valid relay; this completes it: the client now retains ALL advertised relays,
  validates each `ws://`/`wss://` URI up front, and **fails over** across them at connect — a dead first
  relay disposes its half-open socket and the next relay is tried, surfacing an error only once every relay
  is exhausted. Wired into both connect sites (the pay/send path and the NIP-47 INFO-event fetch). Single-relay
  behavior is byte-for-byte unchanged. (`.NET` port only; Python/JS were unaffected.)

## [1.23.2]

### Fixed

- **Multi-relay NWC connection strings (Alby Hub etc.).** A `nostr+walletconnect://`
  string advertising more than one `relay=` param was mishandled: `HttpUtility.ParseQueryString`'s
  indexer comma-joins duplicate keys, producing an invalid `wss://a,wss://b` URI that threw
  when the wallet connected — so `pay_invoice` (and every L402 payment routed through an NWC
  wallet) failed. The .NET port now selects the first valid advertised relay. (The Python/JS
  ports were unaffected.)

## [1.23.1]

### Security

- **Connect-time SSRF pinning on the Python HTTP path (MCP-03).** Outbound requests in
  `l402_client` and `discover_api` now go through an SSRF-safe `httpcore` backend that
  re-validates every resolved IP at connect time and pins the connection to the
  validated address. This closes a DNS-rebinding (TOCTOU) window where a hostname could
  pass an up-front validation check and then resolve to a private/reserved address at
  connect. TLS/SNI is preserved (`verify=True`), and the resolver fails closed if it
  raises. (The .NET port was already connection-pinned via `SocketsHttpHandler`.)

## [1.23.0]

### Added

- **Atomic spend reservations across the payment path (both ports).** The
  check-then-pay-then-record spending-cap flow is replaced with a
  reserve → pay → commit/release lifecycle, so concurrent payments can no longer race
  past the per-request / per-session caps (a TOCTOU that let two in-flight payments each
  pass the check before either recorded). Backed by a durable operation ledger
  (`operations.jsonl`) with idempotency keys so a retried or interrupted payment settles
  at most once.

## [1.22.0]

### Added

- **A durable receipt on every payment method (both ports).** Previously only
  `access_l402_resource` wrote to `~/.lightning-enable/receipts.jsonl` — a payment made
  via `pay_invoice`, `pay_l402_challenge`, `settle_agent_service`, `send_onchain`, or
  `create_lightning_enable_account` left no durable record. Payments are now receipted
  at the wallet seam (a `ReceiptRecordingWallet` decorator on the resolved wallet), so
  every payment — including any future payment tool — leaves exactly one receipt line.
- **`receipt_written: true|false` on every value-moving tool result**, so a failed
  receipt write is visible instead of silent. `null` on results where nothing was paid.
- **Generalized receipt schema.** New lines are `type: "payment_receipt"` with
  `kind` (`invoice` | `l402` | `onchain`), `status` (`settled` | `pending` — pending
  funds are committed and counted by the budget, so they are receipted), a derived
  `paymentHash` (SHA256 of the preimage — never the preimage, BOLT11, or macaroon),
  optional `context`/`policy`, and `feeSats`/`txId` for on-chain sends. Old
  `l402_payment_receipt` lines in the same file remain readable; no migration.

### Changed

- On-chain receipts carry policy `confirm` (every on-chain send passes the
  human-confirmation gate), and `send_onchain` failure results now warn that a
  network/timeout failure may hide an executed send — check the provider before
  retrying an irreversible payment.

### Fixed

- **`create_lightning_enable_account` no longer double-counts the activation fee.**
  The tool recorded budget spend + payment history on top of the L402 client's own
  single-source-of-truth recording, so every Fast Lane activation debited the
  session budget twice and wrote two history entries.

## [1.21.1]

### Fixed

- **Never log preimage bytes.** `StrikePaymentProvider` (an internal `ILightningPaymentProvider`
  implementation) logged the first 8 characters of a settled payment preimage to stderr; it now
  logs only that a preimage was received, matching every other provider. No behavior change.

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
**v3.0.0**); an alias still dispatches, forwards to the new tool, and its result carries
a `deprecated: { replaced_by, removal: "v3.0.0" }` marker.

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
  marker in the response) until they are removed in **v3.0.0**. `get_balance` is a strict
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
