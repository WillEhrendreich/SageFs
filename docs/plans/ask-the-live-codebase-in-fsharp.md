# Ask the live codebase in F#

Roadmap item: `ask-the-live-codebase-in-fsharp` (Exploring, Agents). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M to L, then S |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: every role's verdict equals `admitTool` for the matching tool. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

This item holds three ideas that share one precondition (a place inside the session where typed things about the repo and the cohort can be reached) and differ a lot in risk:

- A. Typed query functions in the session, so the MCP read tools can leave the always-loaded list (the "code execution with MCP" pattern).
- B. Active patterns over the cohort frame, so you filter the cohort in code.
- C. A type provider over the repo and the cohort, so a misspelled symbol or test name is a compile error at the agent's prompt.

Build order is A, then B on top of A, then C only if its experiments pass. This plan leads with what to measure.

## What done looks like

For A: in a session, an agent evaluates `Sage.Cohort.members () |> Async.RunSynchronously |> Result.map (List.filter ...)` or `Sage.References.of' "SageFs.Features.CallerCheck.callersAsync"` and gets typed values back. The seven read tools it replaces can be dropped from the default `tools/list` for a profile that opted in, with no drop in task pass rate and a measured token saving. The caller's role follows the call: an Observer-class token cannot reach anything through the session that it could not call as a tool, and the test proves that for every pair of role and function.

For B: `match frame with | Orphaned claims -> ... | BlockedOn landing -> ...` filters the cohort without a new `list_*` tool.

For C: `Repo.Tests.``SageFs.Tests.Foo.bar``.CoveredSymbols` typechecks, and a misspelling fails `check_fsharp_code` with the compiler's error before any eval.

The outcome gate for A is the experiment harness the tool-list research asks for (task set, full list vs shortened list vs no SageFs, at least five runs per task, spread shown, tokens reported), plus a role-parity test over the whole matrix: every `RolePreset` x every function, same verdict as `Capability.admitTool` gives for the matching tool. No cell may be skipped.

## What exists today

- The MCP list is 71 tools, about 28,000 tokens (`mcp-tool-list-by-harness-2026-10-26.md`, untracked in `~/Work/SageFs`; section 7 "One larger idea" is the reasoning for A and names the enforcement problem). That document also sets the guard for any list change: additive first, every tool stays callable by name, the experiment is the gate.
- The read tools already have typed results before they are formatted. `McpCompletions.membersOf ctx sid name` returns `CompletionAnswer<CompletionEntry>` and `McpMembersRoute.listMembers` only formats it (`SageFs/McpMembersRoute.fs`, `McpListMembers.formatListMembers`). `McpReferencesRoute.referencesOf` returns `ReferenceReply` and `McpReferencesPage.build` renders it. The cohort frame is `CohortFrame<MemberId>` from `Handle.ReadFrame()`, projected by `Cohort.project`.
- Role gating is per tool: `Capability.ToolClass.toolsOf` lists each tool's class, `Capability.admitTool grant toolName` decides, and `visibleTools` filters `tools/list`. `list_members` and `list_references` are `SessionRead`; `get_cohort_status` is `CohortRead`; analysis tools are `CodeAnalysis`. `Eval` (`send_fsharp_code`, `cancel_eval`, `manage_scratch_pad`) is a separate class held by the Implementer preset only. `Capability.fs`'s header says any grant that includes `Eval` "can run arbitrary code as the daemon's OS user", so the narrow presets are the ones that mean something against a hostile agent.
- The FSI host is deliberately small. `docs/how-isolation-works.md` and the host tests (`FsiHostBuildTests.fs`, `FsiSessionContractTests.fs`) say the host closure contains nothing SageFs-owned and loads no SageFs assembly. `AGENTS.md` says never `#load` a file from a project the session already loaded, and `#load` only a pure file. So a prelude is generated source or a separately named assembly a user session references, not something added to the host's closure.
- There is no type provider in the tree. A repo-wide grep for `TypeProvider` finds only `ISignatureTypeProvider` in `Features/MetadataDelta/PeImage.fs`, which is unrelated.
- Checking a cell: `check_fsharp_code` uses `ParseAndCheckInteraction` (`SageFs.Core/Features/Diagnostics.fs` near line 109). FSI supports referencing a provider with `#r`; I have not tried it in a SageFs session.
- Worker requests are routed by session with a reply id (`routeToSession ctx sid (fun replyId -> WorkerMessage.GetReferences(name, SessionId.value replyId))` in `McpReferencesRoute.fs`). I did not read the eval path's request shape; experiment 2 does.
- Test count to design for: the default suite is about 6,900 tests (a lazy provider has to cope with that many names).

## Design

Idea A: an in-session client over the daemon, not an assembly in the host.

```fsharp
// Generated source, #load'ed into a session; BCL only (System.Net.Http, System.Text.Json).
module Sage =
  type QueryRefusal =
    | NotInRole of tool: string * RolePreset
    | HandleExpired
    | DaemonUnreachable of reason: string
    | SessionUnavailable of CompletionUnavailable
  module Cohort =
    val members : unit -> Async<Result<MemberView list, QueryRefusal>>
    val status  : unit -> Async<Result<CohortView, QueryRefusal>>
  module Symbols =
    val membersOf    : qualifiedName: string -> Async<Result<MemberPage, QueryRefusal>>
    val referencesOf : qualifiedName: string -> Async<Result<ReferenceView, QueryRefusal>>
```

The daemon exposes one authenticated loopback route, `POST /api/query/<name>`, that calls the same typed functions the tools call (`membersOf`, `referencesOf`, `ReadFrame`) and returns JSON through `SageFs.Json`. It resolves the caller from a handle and runs `Capability.admitTool` for the tool the query corresponds to. One table maps query name to tool name, so the gate cannot drift: a test iterates `ToolClass.toolsOf` and fails if a read tool that has a query is missing from the table.

The enforcement problem, answered: the function must carry the caller's role into the session without placing a long-lived token where a transcript can print it. Candidate handle designs, to be chosen by the second experiment:

| Handle | Reaches the code by | Risk |
|:--|:--|:--|
| Per-eval handle minted by the daemon from the caller's resolved grant, valid until that eval completes | An environment variable set by the host per eval (evals in a session are serial in the eval actor), read by the prelude | Racy if two evals overlap; confirm the actor serialises them |
| Constant in generated prelude | Source text | Printing it leaks it into the transcript; short lifetime limits harm |
| The member token itself | Prelude reads `SAGEFS_MEMBER_TOKEN` | Long-lived secret inside code the agent can print |

Recommendation: per-eval handle. What the gate cannot do: the agent holding `Eval` can run any code as the OS user, so the handle protects the role table against mistakes, and roles without `Eval` cannot reach a session at all. A hostile Implementer can get around it. Say so in the docs, matching the stance `Capability.fs` already takes.

A profile flag on `sagefs mcp` (additive) drops the migrated read tools from `tools/list`; they stay callable by name, which the guard requires, and `discover_features` lists them. The migrated set is chosen by the first experiment, not by this plan.

Idea B: after A, a small module of active patterns in the same prelude, `(|Orphaned|_|)`, `(|BlockedOn|_|)`, `(|Coupled|_|)`, over `CohortView`. They are plain F#. `Coupled` needs coverage coupling and waits on the `plan-a-cohort-as-code` measurement.

Idea C: an erasing provider needs a design-time DLL (`FSharp.TypeProviders.SDK` is the usual route, a new dependency that `AGENTS.md` says needs discussion) referenced from the user session with `#r`, reading a file the daemon writes under the data dir (`DataDirChoice`, `SAGEFS_DATA_DIR`). It cannot call the daemon over HTTP at design time because the provider runs inside the worker the daemon owns; the file avoids the cycle. Members must be generated lazily or the checker stalls on thousands of names. I do not plan C beyond its experiments.

Wire and client impact: A adds one internal route and one profile flag; the tools stay, so no client breaks. `EndpointContracts` gains the route's contract, which is a wire commit with a `Plugin:` trailer; neither editor needs to use it. `apiVersion` does not move for an added route. Token cost: the route is not a tool. The saving is the point: see the first experiment for the number.

## Tests

RED first, `SageFs.Tests/SageQueryTests.fs` (default suite):

1. The query-name to tool-name table is total over the migrated set, and every mapped tool exists in `ToolClass.toolsOf`.
2. Role parity: for every `RolePreset` and every query, `QueryGate.admit preset query` equals `Capability.admitTool (grant preset) (toolOf query)`. Property over the closed DU, no skipped cell.
3. A handle for one eval is refused for the next eval (`HandleExpired`), and a handle minted for token X cannot be exercised as token Y.
4. The generated prelude compiles in a bare session (check via `check_fsharp_code` in the outcome tier) and carries no secret literal that outlives the eval.
5. The prelude's types equal the daemon's JSON contract (round trip through `SageFs.Json`), so a field added on one side fails a test on the other.

Simulation: the handle table is a small state machine with expiry and concurrency (`SageFs.Simulation/QueryHandleSim.fs`): mint, use, eval ends, eval starts, two overlapping evals injected as a fault, clock skew. Invariants: a handle is never valid after its eval ended; never valid for a different member; at most one live handle per session unless evals overlap, in which case both are distinct. Twin: a table that forgets expiry, caught by the first.

Outcome gate: `SageFs.Tests/SageQueryOutcomeTests.fs`, `--integration-host` tier: a real session, an Implementer-token eval calls each function, a Verifier token (no `Eval`) is refused at the tool before it can reach a session, and the answers equal the corresponding tools' answers on the same state. The deadlock check is part of it: an eval calling the daemon while the daemon awaits that eval must return, asserted by awaiting the call's completion, never by a timeout.

The experiment harness for the tool list is built under `tools/` or `SageFs.Tests` as its own project item (it is named in the research and not built); this plan consumes it.

## Steps

1. Experiments 1 and 2 below. No repo changes.
2. Test 1 and 2, then `QueryGate` and the table (new slice file `SageFs.Core/Features/QueryGate.fs`).
3. `POST /api/query/<name>` for `members`, `references`, `status`, in `SageFs/McpServer.fs` or a new route file, and the handle table in `SageFs.Core`. Test 3 and the simulation.
4. The generated prelude (`SageFs.Core/Features/SagePrelude.fs` producing source) and test 4 and 5.
5. Outcome gate, then the profile flag and the list experiment. Docs in `docs/mcp-tools.md` and `skills/sagefs`.
6. B, a small file of active patterns with tests, after step 5.
7. C only if experiment 3 passes, in a separate project under a spike branch first.

Steps 2 and 4 are file-disjoint and can run in parallel once the JSON contract type exists. Step 3 owns the server routes.

## Measure first

1. Tokens saved and tasks held. From a live `tools/list`, sum the characters of description and schema for the read tools that A would move (`list_members`, `list_references`, `get_cohort_status`, `list_sessions`, `get_session_status`, `get_daemon_status`, `list_tests`, `get_available_projects`, the `CodeAnalysis` family), divided by 4. Then run the task set on the full list and the reduced list with the queries available. If the saving is under about 3,000 tokens, A is not worth the new surface. If the reduced list loses tasks, A stays opt-in. The 3,000 is my threshold, to be replaced by what the numbers show.
2. How the handle reaches code, and the deadlock. Spike in a scratch directory: a session, a generated prelude, an HTTP call back to a stub on the daemon port while the eval is in flight. Confirm evals in one session are serial (read the eval actor, `EvalActorDecision`), that an environment variable set per eval is visible to the prelude, and that the call returns while the eval holds the session. If any of these fails, the handle design changes.
3. Is a provider viable in FSI at all? Build a throwaway erasing provider over a 7,000-name JSON file, `#r` it in a SageFs session, and measure: time to first check with eager members vs lazy members; whether the session's host loads it without breaking the closure tests; what happens to an already-evaluated cell when the file changes and the provider invalidates (my expectation is nothing, because FSI does not re-run earlier cells; unproven). If first check is over a few seconds, or invalidation cannot refresh, C is dropped.

## Risks and dependencies

- The session becomes a second API surface. Every function needs a tool-parity test or it becomes a bypass of the per-tool gate, which is the failure the research note warns about.
- A generated prelude is loaded as source into the user's session. It must not shadow user names; put everything under one module and test that a user binding named `Sage` still works or is refused with a reason.
- Depends on the tool-list experiment harness (not built). `a-token-per-subagent-in-claude-code` and `each-agent-its-own-member` decide which caller a handle names when sub-agents share a connection. `plan-a-cohort-as-code` supplies the coverage coupling `Coupled` needs.

## Size

A is M to L: a route, a handle table with a simulation, a generated prelude, and an experiment harness. B is S on top. C is a research spike of its own, with the size unknown until experiment 3.

## Decisions for Will

1. Is a new loopback query route acceptable? Recommendation: yes, if it accepts only per-eval handles and refuses a bare request, so it adds no unauthenticated door.
2. Take the `FSharp.TypeProviders.SDK` dependency for the spike? Recommendation: spike only in scratch, discuss the dependency if experiment 3 passes.
3. Ship A as an opt-in profile only, with the default list unchanged until a week of dogfood agrees? Recommendation: yes, which is the guard the research note sets.
