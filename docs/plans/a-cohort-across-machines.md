# A cohort across machines

Roadmap item: `a-cohort-across-machines` (Exploring, Agents). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | L |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: a duplicated `AcquireClaim` envelope returns the first outcome. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

An agent on a second machine joins a cohort hosted on the first, claims files, and lands commits, with the same refusals and the same ledger a local member gets. It reaches the host over an authenticated connection that can call cohort verbs and cannot evaluate F#. The host never trusts the remote machine's clock, paths or process ids. A reply lost on the wire never leaves the remote agent unsure whether its claim exists.

Two outcome gates. The first is a simulation gate: a cohort with N members whose commands travel through a seeded lossy channel (drop, duplicate, reorder, delay, lost replies) ends in a state a fault-free run could also have produced, and every client eventually knows what happened to every command it sent. The second is a real gate: two OS processes, one acting as the remote, over a typed second listener on loopback, run join, conflicting claims, a refused role, a real landing from a pushed ref, and a reconnect after a dropped connection. Matrix: {local member, remote member} x {Implementer, Verifier, Observer token} x {clean network, dropped reply, duplicated command, dropped connection mid-landing, host restart}.

## What exists today

What already crosses a network cleanly:

- The cohort core is pure. `Cohort.decide clock entropy state command` takes time and randomness as parameters (`SageFs.Core/Cohort.fs`). `CohortOwner.handle` stamps the clock and entropy once, in the owner (`SageFs.Core/Features/CohortOwner.fs`, injected `clock` and `entropy`), so a remote machine's clock never enters the ledger.
- Commands, events and ledger entries serialise whole: `CohortLedgerExport.toJsonl` writes them with entropy, and `sagefs cohort-export`/`play` round-trip them.
- The owner is a single mailbox: `Handle.Commit` returns a `Task` of the result, `Handle.Post` takes a callback, `ReadFrame` is wait-free.
- A member token is a handle that does not depend on the socket it arrives on. `MemberId.Capability <fingerprint>` is independent of the connection (`SageFs.Core/MemberTable.fs`), tokens travel in `X-SageFs-Member-Token` or `_meta["sagefs/memberToken"]` (`Capability.CapabilityTransport`), and a grant carries role, scope prefix, route and expiry.
- The frame is versioned by ledger sequence number, and the SSE side has a sequenced replay buffer with gap detection (`SageFs.Core/SseReplayBuffer.fs`, `ReplayResult.Replayed | GapDetected`).

What does not, with the file that shows it:

| Not yet transparent | Where | What a remote member needs |
|:--|:--|:--|
| The daemon only binds loopback. `LoopbackHost` has no other case, because eval is remote code execution. `HttpOriginGuard.decide` rejects a non-loopback `Host`. | `SageFs.Core/SageFsConfig.fs` near line 139, `HttpOriginGuard.fs` line 231 | A second listener, typed, serving only cohort verbs and refusing eval at the type level. Roadmap item `watch-your-agents-from-your-phone` plans the Tailscale-bound read-only version of this. |
| A command carries no id. `RequestLanding` mints a new `LandingId` from fresh entropy on every arrival, `AcquireClaim` that already succeeded answers `ClaimConflict(scope, holder = self)` with no claim id or fence, `ReleaseClaim` twice answers `NotClaimHolder`. | `Cohort.fs` `decide` arms | An idempotency key per command and a recorded outcome, so a retry returns the first answer. |
| The cohort scope is an absolute path on the host: `Repository of rootPath`, derived by walking the host's disk (`Scope.repositoryRootOf`). A remote path that does not exist there becomes a different `Named` cohort. | `SageFs.Core/CohortScope.fs` | Scope chosen by the token's route, never by a caller-supplied directory. The docs already say a token call must pass `working_directory` inside its route; a remote cannot know that path. |
| Commits are read from a shared object store: `rebaseCommitsOnto` runs `git checkout --force --detach <memberTip>` in the host repo. | `SageFs/CohortGit.fs` near line 384 | The remote pushes to a ref on the host before `request_landing`, and the daemon checks the commits exist before it queues. |
| The test matrix reads local state: `frameOf` calls `getSessionTestOutcomes sid` for each member with `Session = Some sid`. | `CohortOwner.frameOf` | Nothing for a first version. A member with `Session = None` is a full member absent from the matrix (documented on `MemberRecord.Session`). Later, the remote daemon posts snapshots. |
| The integration worktree, the trunk checkout and its follower are host-local, and the binding is one process-wide ref. | `SageFs/McpCohortIntegration.fs` (`cohortIntegrationRef`, `trunkFollowRef`) | A remote member fetches the integration branch. Trunk follow does not reach a remote machine's running app, and the docs should say so. |
| Event delivery is in-process: `Handle.Events : IEvent<CohortEvent list>`; `Notify` is never constructed by `decide`. | `CohortOwner.Handle` | A resume cursor (`fromSeq`) over the ledger, which the dense `Seq` already provides. |
| The token table lives in daemon memory and is lost on restart, which also drops every remote member. | `Capability.fs`, roadmap `tokens-survive-a-restart` | Either that item, or remote clients re-mint through the conductor. |
| Save observation: `ObserveSave` is posted from the host's file watcher (`DaemonMode.fs` near line 3852). | `DaemonMode.fs` | The remote client posts its own saves. Advisory only, so a missed one costs a warning. |

Transport over TLS or a tailnet is not in the tree. Nothing in the repository today carries cohort traffic off the machine.

## Design

Keep the core as it is and put the network at an edge that speaks one message type.

```fsharp
type CommandId = CommandId of string            // client-minted, unique per member

type Envelope<'m> = { Id: CommandId; Member: 'm; Command: CohortCommand<'m> }

type Outcome<'m> =
  | Applied of CohortEvent<'m> list * seq: int64<ledgerSeq>
  | Refused of CohortError<'m> * seq: int64<ledgerSeq>

type Dedupe = Map<CommandId, Outcome<MemberId>>          // owner-side, bounded
```

The owner records the outcome of each `CommandId` it has seen, bounded by the cohort's retention window, and answers a repeat with the recorded outcome and applies nothing. Two ways to keep this out of `Cohort.decide`: hold `Dedupe` in `OwnerState` beside `NextSeq` (not in the ledger, so a restart forgets it and a retry after a restart sees the ordinary refusal), or add a ledgered `CommandId` field. The first costs nothing in the ledger and covers lost replies; recommend it, and let the second wait for a measured need.

Edge pieces, each a vertical slice:

- `SageFs/CohortRemoteListener.fs`: a second Kestrel binding on one configured interface address (never `0.0.0.0`), serving `POST /cohort/command`, `GET /cohort/frame`, `GET /cohort/events?fromSeq=N`. It authenticates by token only, resolves scope from the token's route, and has no eval or session route. `Capability.admitTool` and `admitClaim` already do the role and prefix checks.
- `SageFs.Core/Features/CohortRemote.fs`: the pure half, `Envelope`, `Outcome`, dedupe, and the mapping from a request to `CohortCommand`. Testable without a socket.
- A client library for the remote side is the existing stdio bridge or the SDK's `McpClient` pointed at the listener. Whether to expose cohort verbs as MCP on the second listener or as the small HTTP surface above is a decision for Will; MCP keeps one tool vocabulary.
- Git transport stays git: the remote pushes to `refs/sagefs/incoming/<member>/<n>` on the host over SSH, then `request_landing` names the shas. The daemon checks `git cat-file -e` for each before it queues, and refuses with the missing sha otherwise.

Wire and client impact: new routes and a new listener setting, so `EndpointContracts` changes and `apiVersion` moves for any breaking field; `sagefs-vscode` and `sagefs.nvim` are unaffected until they gain a remote mode. New config is an `envOrDefault` in `Timeouts.fs` only for tunable durations (none expected). No change to ledger rows or to the SQLite schema for the recommended dedupe. Token cost: no new MCP tool on the loopback server.

## Tests

RED first, `SageFs.Tests/CohortRemoteTests.fs`:

1. A duplicated `AcquireClaim` envelope returns the first outcome, including the claim id and fence, and the ledger has one `ClaimAcquired`.
2. A duplicated `RequestLanding` envelope queues one landing.
3. Envelope with a `Member` different from the token's member is refused before `decide` sees it.
4. A request with the right token but a non-cohort route (eval) is refused by type: the listener has no such route, so the test checks the route table.
5. `request_landing` naming a sha the host does not have is refused with that sha.

Simulation, `SageFs.Simulation/CohortRemoteSim.fs`, composed from `Scenario.fs`, `FaultInjection.fs` and the cohort generators: N members (some local, some behind the channel), a seeded schedule of commands, and faults from `FaultInjection` (drop, duplicate, reorder, clock skew) plus a new reply-drop combinator. Clients retry with the same `CommandId` until they see an outcome. Invariants: the final state equals the fold of the ledger; each `CommandId` appears in the ledger at most once; no client holds a view that a later `ReadFrame` contradicts at the same sequence number; every sent command has a known outcome after the schedule drains. Twin: an owner without dedupe. The duplicate-landing invariant must catch it, which also confirms the retry hazards in the table.

Outcome gate: `SageFs.Tests/CohortRemoteOutcomeTests.fs`, `--integration-host` tier, the matrix above against a real daemon with a second process as the remote and a throwaway git repo for the push. Waits are on state: the remote waits for the sequence number its command returned, and reconnect waits for the `fromSeq` replay to catch up. No fixed sleeps.

## Steps

1. Test 1 and 2 plus `CohortRemote.fs` (pure) and the dedupe in `OwnerState`. Owns `CohortOwner.fs` and the new file.
2. Simulation and the reply-drop fault combinator (new files; `FaultInjection.fs` gets one addition).
3. Resume cursor: `ledger from seq` read on the owner handle. Owns `CohortOwner.fs` (after step 1).
4. Test 3 to 5 and `CohortRemoteListener.fs` with the route table. Owns the listener file, `DaemonMode.fs` wiring.
5. Commit existence check in the landing request path. Owns `McpCohortTools.fs`, `CohortGit.fs`.
6. Outcome gate. Then docs, and a security note beside `HttpOriginGuard` explaining why this listener exists and what it refuses.

Steps 2 and 4 can run in parallel; steps 1, 3 and 5 share `CohortOwner.fs` or the landing path and run in order.

## Measure first

1. The zero-code experiment: `ssh -L 37749:localhost:37749 <host>` from a second machine, run `claude mcp add sagefs -- sagefs mcp` there against the forwarded port (the bridge talks to `http://localhost:<port>/`, `McpStdioBridge.fs`), with `SAGEFS_IDENTITY_POLICY=TokenRequired` on the host and a minted token in `SAGEFS_MEMBER_TOKEN`. `HttpOriginGuard` accepts the forwarded `Host: localhost`, so this should connect today. Record what breaks: the scope the remote lands in, the refusal text, a landing from an unpushed commit, a dropped tunnel mid-landing. This settles which rows of the table matter in practice before any listener is written. Caveat: the tunnel gives the remote a path to every route on the daemon, eval included, so run it only with narrow tokens on a machine you own.
2. The simulation alone, with no dedupe, shows which command classes break under retry. If only `RequestLanding` and `AcquireClaim` do, dedupe can be narrower than a general table.
3. Latency against the lease: `cohortLeaseWindow` is 30 minutes (`Timeouts.fs`), so network latency does not threaten a seat. Check that a laptop that sleeps for an hour loses its seat and its claims are orphaned as designed, and that the remote client says so on wake.

## Risks and dependencies

- A second listener widens the attack surface of a process that evaluates code. The mitigation is in the type: the listener has no eval route, tokens are narrow, and the bind address is one configured interface. A review by someone other than the author belongs before it ships.
- Conductor-only verbs over the network (`mint_member`, `set_integration_ref`) should stay local at first. The remote surface is member verbs.
- Depends on `tokens-survive-a-restart` for restart behaviour, on `watch-your-agents-from-your-phone` for the typed Tailscale listener (share the listener type), and on the per-scope integration binding if two cohorts are hosted. `drive-real-agents-as-members` benefits from the same idempotency keys.

## Size

L. The pure core is ready, but the dedupe, the listener, the git transport and a security review are four pieces of work, and the simulation is the part that earns trust.

## Decisions for Will

1. Run the zero-code SSH experiment first? Recommendation: yes, it costs an afternoon and tells you whether anyone wants the rest.
2. Dedupe in `OwnerState` only (forgotten on restart) or ledgered? Recommendation: `OwnerState` only.
3. Cohort verbs on the second listener as MCP or as a small HTTP surface? Recommendation: MCP, to keep one vocabulary, with the tool list filtered to cohort classes by `Capability.visibleTools`.
