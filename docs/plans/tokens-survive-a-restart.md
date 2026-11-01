# Member tokens survive a daemon restart

Roadmap item: `tokens-survive-a-restart` (Later, Agents). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: a revoked token is still revoked after restore. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

The conductor mints a checkout-bound token, the daemon restarts (an `install-local --force`, a crash, a reboot), and the agent holding the token makes its next call with the same token and the same member id (`cap:<id>`), with no re-mint. A token that was revoked before the restart is still refused after it. A token past its expiry, or idle past the lease window across the downtime, is refused with the reason the live path already gives (`Expired`, `LeaseLapsed`). A token bound to a single session is not carried across, and the refusal says why and what to do. The outcome gate is an integration test that restarts a real daemon over the matrix: token state before the restart (active checkout-bound, active session-bound, revoked, expired, lapsed) by restart kind (clean shutdown, `kill -9`) and asserts the call result and the reason after.

This item is gated on a need. The roadmap says it waits until someone needs it, and the first step below measures whether anyone does.

## What exists today

- `SageFs/CapabilityStore.fs` holds `CapabilityState` (`Records: Map<TokenHash, CapabilityRecord>`) in memory behind one lock. Its comment states the design: "In memory by design. Tokens do not survive a daemon restart, so a stolen ledger or data directory holds nothing to steal." `SageFs.Core/Capability.fs` (line 728) says the same and that the table is "Not the cohort ledger, so a token's existence is never in a ledger row."
- `CapabilityRecord` (Capability.fs:715): `Id`, `MintedBy`, `MintedAt`, `LastSeen`, `Grant` (preset, `ScopePrefix`, `RouteBinding`, `NotAfter`), `Status` (`Active | Revoked of at`). `TokenHash` is a private SHA-256 hex string, `TokenHash.tryOfHex` re-reads one.
- `Capability.resolve` already names the refusal: `Unknown`, `Expired`, `LeaseLapsed` (idle for `Cohort.leaseWindow`, `Timeouts.cohortLeaseWindow`), `Revoked`. A revoked hash can never be minted again (`MintOverwrites` is a simulation twin in `SageFs.Simulation/CapabilitySim.fs`).
- The cohort ledger (`~/.SageFs/cohort.ledger.db`) replays the seat of a member across a restart. After a restart a `cap:<id>` seat can exist with no token behind it. `docs/mcp-tools.md` says "Because no token outlives a restart, there are no tokens from before the route binding existed and nothing to migrate." That sentence stops being true here.
- Single-writer persistence precedent: `SageFs.Core/Features/ManifestOwner.fs` owns `daemon.sagefm`: a mutation is applied in order, written atomically, and the reply means it is durable; a corrupt file is renamed aside by its owner (`QuarantineCorrupt`, used in `DaemonMode.resumePreviousSessions`). Format primitives: `SageFs.Core/BinaryFormat.fs` (`Crc32`, `BinaryPrimitives.writeLpString` and the bounded readers), spec in `docs/binary-format-spec.md`. The data dir is made owner-only by `DaemonState.ensureDataDir`.
- Session ids do not survive a restart. `resumePreviousSessions` (`SageFs/DaemonMode.fs:1694`) calls `sessionOps.CreateSession` for each previous entry and logs "Resumed session ... (retired old id ...)" (line 1852). A `RouteBinding.BoundToSession id` token therefore points at an id that no longer exists. `RouteBinding.BoundToCheckout` survives, because a checkout is a path.
- `McpTools.sessionOwners` (who created a session) is also in memory and is forgotten on restart; `docs/mcp-tools.md` says so ("a daemon restart forgets who made what").

## Design

A new store, separate from the cohort ledger and from `daemon.sagefm`: `<data dir>/capabilities.sagefm`, written by a single owner `CapabilityOwner` that follows `ManifestOwner`'s shape (in-memory state, ordered commits, atomic write via temp file and rename, CRC-32 over the payload using `BinaryFormat.Crc32`, quarantine on corruption). Owner-only file mode set the way `ensureDataDir` sets the directory.

What is stored: for each record, the `TokenHash` hex, `CapabilityId`, `MintedBy`, `MintedAt`, `LastSeen`, the grant, and the status. Never the raw token (it is not available after the mint reply), and the existing `describeMintedForLog` redaction is unchanged.

Which records are stored, as a closed type so the rule has one home:

```fsharp
[<RequireQualifiedAccess>]
type Carry =
  | Persist                        // checkout-bound, or revoked (a revocation must survive)
  | MemoryOnly of why: MemoryOnlyReason

and MemoryOnlyReason =
  | BoundToASessionThatEndsWithTheDaemon   // BoundToSession: resume retires the old id
```

`Carry.of` is an exhaustive function over `RouteBinding` and `CapabilityStatus`. A revoked session-bound record is `Persist` so a revoked hash can never be minted again after a restart; the revocation outlives the thing it was bound to.

Restore is a pure function `restore : now -> persisted list -> CapabilityState * RestoreReport`. It rebuilds `Records` and applies no new rule: every record goes back as stored, and the existing `Capability.resolve` decides at the next `Present` whether it is expired or lapsed. The `RestoreReport` is what the daemon logs and what `get_daemon_status` could show: counts restored, counts already expired, counts revoked, counts dropped with a reason each. A file that fails its CRC restores nothing and says so; it never restores part of a table.

Wiring: `CapabilityStore` takes the owner at construction and writes through it on `Mint` and `Revoke`. `Touch` updates `LastSeen` in memory and is written on a timer and at shutdown only; writing on every call would put disk IO on the hot path of every token call. The cost of that choice: after a `kill -9` a token can look idle for up to one flush interval longer than it was. That errs toward refusing, which fails closed. The flush interval is a named constant in `Timeouts`, not a literal.

The seat: on restore the cohort ledger already replays the seat; nothing is committed to the cohort at restore. The first call the token makes renews the seat through the call filter's `recordMemberActivity`, as today.

Honest limit, to be written into `docs/mcp-tools.md` and `docs/decisions.md`: a persisted hash file means write access to the data dir is enough to forge a token (insert a hash you know with a wide grant). Today the same access can already do worse (it can edit the ledger), but the previous design had the sentence "holds nothing to steal", and this change removes it. The mitigation is the owner-only mode and the fact that a forged record also needs a seat to do anything in the cohort. Signing the file with a key kept beside it adds nothing against the same user. If `a-token-for-every-daemon-call` ships, the operator credential could key an HMAC over the file, which would at least stop a process that can write the file but cannot read `operator.token`. That is an option, not part of this plan.

Wire and client impact: none on the wire, `apiVersion` stays, no editor change. `mint_member`'s reply drops "Tokens do not survive a daemon restart" for a sentence that names the two exceptions (session-bound tokens, and a file that failed its CRC). `get_daemon_status` gains a restored-tokens line only if the status payload has room; that is a wire addition and carries `Plugin: n/a`. No new tool, so no catalog cost.

## Tests

RED first.

1. Default tier, `SageFs.Tests/CapabilityPersistenceTests.fs`: `WHY - a revoked token is still revoked after the store is restored`. Round-trips a `CapabilityState` through encode, decode, `restore`. Fails to compile today (no encoder).
2. `WHY - the persisted bytes contain no raw token`: mint through the real path with a known token string, encode, assert the bytes do not contain the token or its base64. This pins the central privacy property.
3. `WHY - a session-bound token is not carried and the report says why`.
4. Property: for any `CapabilityState` built by random mints, touches and revokes, `restore (encode s)` equals `s` for every record with `Carry.Persist`, and omits exactly the `MemoryOnly` ones.
5. `WHY - a file with one flipped byte restores nothing`: flip each byte of a small file in turn (exhaustive, not sampled); every flip is detected by the CRC or a bounded reader and the state is empty with `Corrupt` named.
6. Simulation: extend `SageFs.Simulation/CapabilitySim.fs` with a `Restart of kind: Clean | Crash` event. A `Crash` drops the in-memory `Touch` updates since the last flush; the fold then runs `encode`, `decode`, `restore`. New invariants in `CapabilitySimInvariants.fs`: a token revoked before a restart is never admitted after; a token never gains lifetime across a restart (`NotAfter` is unchanged); a restart never turns a lapsed token active. Twins, one bug each: `RestoreRevivesRevokedTwin` (restore treats every record as `Active`), `RestoreRenewsExpiryTwin` (restore resets `NotAfter` to now plus the default lifetime), `PersistsRawTokenTwin` (stores the token), `RestoreIgnoresCarryTwin` (restores session-bound records). Each twin has to trip its invariant.
7. Integration tier, `--integration-host`, extending `CapabilityHttpIntegrationTests.fs` (it already spawns a daemon with its own port and data dir): the outcome matrix above. The restart waits on the daemon's readiness event (the harness's existing ready signal), not on a sleep. `kill -9` uses the harness's process handle.
8. Ratchet: `ToolSurfaceHonestyTests` and the docs slop test already hold prose; add an assertion that no doc still says "Tokens do not survive a daemon restart" without the exceptions.

## Steps

1. Measure the need (below). Stop here if the answer is no.
2. RED tests 1 to 5 and `CapabilityCodec.fs` (pure encode, decode, `Carry`, `restore`) in `SageFs.Core`, listed in `SageFs.Core.fsproj` after `Capability.fs`. Owns those files and the unit test file.
3. Simulation extension (test 6). Owns `CapabilitySim.fs` and `CapabilitySimInvariants.fs`; parallel with step 2 once the codec types exist.
4. `CapabilityOwner` and wiring into `CapabilityStore`, flush timer, shutdown flush; owns `SageFs/CapabilityStore.fs` and a new `SageFs/CapabilityOwner.fs`, plus the `SageFs.fsproj` entries. After step 2.
5. Integration test 7. After 4.
6. Docs: `docs/mcp-tools.md` member-token section, `docs/decisions.md` (a new entry that reopens the "in memory by design" decision with its reason), `McpCapability.describe` text. Last.

## Measure first

- Whether anyone needs it. Count, over the last few weeks of the daemon logs and the cohort ledger exports, the number of `mint_member` calls that followed a daemon start within the lifetime of a token minted before it (a token minted, then a restart, then a mint for the same role and directory). Each is one avoided re-mint. If it is a handful in weeks, leave the item Later. The AGENTS.md rule that installing a build restarts the daemon and drops open sessions on purpose means most runs lose their sessions anyway, so a surviving token buys only the case where the agent makes its own session again.
- How often `LastSeen` needs to be flushed. Measure the write cost of the whole table (at most hundreds of records) with a stopwatch in the integration daemon; if it is under a millisecond, the flush interval can be short enough that the crash window is a minute.
- Whether a restored token whose seat lapsed in the ledger can rejoin the cohort without a conductor action. Check `McpCohortTools` on a token call after a restart in the spike; if the seat must be re-joined by hand the token is half useful and the plan needs a restore step that re-seats.

## Risks and dependencies

- The privacy claim changes. Written plainly above, and the decision belongs to Will.
- A bug in `Carry` that persists a session-bound token would bind a restored token to a dead id; the route gate would refuse every call with a confusing message. Tests 3, 4 and the `RestoreIgnoresCarryTwin` cover it.
- Depends on nothing in other items, but should be designed with `a-token-for-every-daemon-call` in view: if credentials gain the `sfv_` and `sfo_` kinds, the codec's record needs a kind tag so the format does not need a second version. Leave a version byte and a kind byte in the header from the first write.
- Feeds `drive-real-agents-as-members` (a long-running driver that mints per run and survives an upgrade).

## Size

M. The codec and the simulation are small. The owner, the flush timer and the integration restart matrix are most of the time.

## Decisions for Will

- Build it at all before someone needs it? Recommendation: no until the count in step 1 shows a real pattern; the roadmap already says so.
  - Added 2026-11-01: `a-token-per-subagent-in-claude-code` makes the need concrete, and the numbers are in. A token lives in daemon memory and a sub-agent holds it for the length of a run. This repo has 22 `release v` commits in the last 21 days, up to five a day from 10-16 to 10-25 and none since; each release installs the build and restarts the daemon, and the daemon now running has been up 5.5 days. So restarts come in bursts, not constantly. A run that spans a burst loses every token at the next install.
  - What the sub-agent sees is worse than the loss. Measured: after a restart the old token gets 0 tools from `tools/list`, so Claude Code shows the sub-agent no SageFs tool at all, and the daemon's clear refusal ("the daemon restarted… mint a new one") is never shown to the model. Surviving the restart fixes the loss. The empty list for a bad token is a separate fix that is worth making either way.
  - Measured on 2026-11-01 (`docs/agent-identity-research-2026-11-01.md`): Claude Code re-runs a `headersHelper` and retries the call once when the server answers `403`, so a helper that mints a fresh token restores access after a restart with no persisted state. It does not keep the member: a fresh token is a new `cap:` id and the old member's claims are orphaned. Persisting hashes (this item) is the only option that keeps identity, so count how many claims a restart orphans before building it.
  - Count in step 1 how many orchestrated runs overlap a release burst. If it is weekly, this item comes right after slice 0 of `a-token-for-every-daemon-call`.
- Accept that "holds nothing to steal" no longer holds? Recommendation: yes only with owner-only mode and, once the operator credential exists, an HMAC keyed by it.
- Should downtime count against the idle lapse? As planned it does, because `LastSeen` is stored as it was. The alternative (reset `LastSeen` to the restore time) is more forgiving and also lets a stolen token live through a restart. Recommendation: keep it strict.
