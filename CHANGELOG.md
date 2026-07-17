# Changelog

All notable changes to the Lightning Enable MCP server are documented here.
Versions apply to both ports (NuGet: `LightningEnable.Mcp`, PyPI: `lightning-enable-mcp`).

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
