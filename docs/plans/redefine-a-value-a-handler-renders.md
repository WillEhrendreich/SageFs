# Redefine a value a handler renders

Roadmap item: `redefine-a-value-a-handler-renders` (Exploring, hot reload). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M to L |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: a concatenated read returned ends in the response. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

In an app the reload agent runs (started from FSI or an `.SageFs/init.fsx`), a page handler reads `greeting` on every request and renders it. The user loads the page, edits `let greeting = "hello"` to `"howdy"`, saves, reloads the page, and sees `howdy`, with the app's state intact and the save reported `Patched` only after a response carried the new value. A handler that keeps the value (a static cache, a `Lazy`, a closure built at startup) still restarts and names who kept it. When SageFs cannot tell, it restarts and says why, as it does now. The outcome gate is `HotReloadStateOutcomeTests` rows on .NET 10 and .NET 11 across the matrix: value kind (literal string, computed string, int) by handler shape (direct concat, builder-style render, cached in a static, cached in a `Lazy`, captured at startup, a callee with no summary) by what is served afterwards (new text, old text). Every cell has a stated verdict. None is skipped.

## What exists today

- The classifier is `SageFs.Core/Middleware/ValueReads.fs`, which is also compiled into the FSI host (`SageFs.FsiHost/FsiHost.fsproj` includes it, so it may use only the BCL and FSharp.Core). `fateAt` simulates the IL stack forward from a read and returns `ReadFate = Discarded | Escaped of Escape`. `Escape` is `StoredInField | CapturedBy | PassedTo of methodName | Returned | AddressTaken | Untraced`. The handler case is `PassedTo "System.String.Concat"` or `Returned`, and `fateAt` stops there. `ValueReadsTests.fs` pins this ("passing the value as an argument lets it escape into code SageFs doesn't follow", "returning the value hands it to the caller, who might keep it (a lazy does)").
- `ValueEvidence`/`ValueRead` (`ThrownAway | NotRunYet | Kept of site * ReadSeen`) and `verdictOf` give `ValueVerdict = SafeToPatch | HeldBy | CannotTell`. `checkSave` refuses a save unless every verdict is `SafeToPatch`. A read that escaped and ran after startup is `Kept`, which is why a handler that rendered `greeting` forces a restart (`docs/hot-reload.md`, values table, fourth row).
- The runtime half is `ValueReadTracking.Tracker` (`Track`, `ScanEval`, `Evidence`, `Checkpoint`), with one-shot probes on readers whose read escapes. `WorkerMain.redefinitionRefusals` (`SageFs.Host/WorkerMain.fs` around line 1442) asks the agent for evidence, runs `verdictOf` and `checkSave`, and turns refusals into `ReloadChange.ValueCopied` or `ValueUntraceable`. It is asked before the patch and again after (`recheck`).
- Confirmation of a patch is "the new body ran": `EntryProbes.fs`, `PatchConfirmation.fs`, with `PatchPending -> Patched | NeverEntered`. The simulation is `SageFs.Simulation/ValueReadSim.fs` (real `fateAt`, real ledger, real `checkSave`, ground truth kept apart) with its tests `ValueReadSimTests.fs`; twins put the naive rules back.
- `SageFs.Host/DevReloadMiddleware.fs` already buffers an HTML response into a `MemoryStream`, reads it back with the response's encoding and rewrites it (around lines 176 to 215), limited by `DevReload.DevReloadConfig.defaults.MaxBodyBufferSizeBytes`. It runs in the app's process when SageFs injects it (`DevReloadInjector`). It sees response text; it does not look for any value in it today.
- Fixture: `SageFs.Tests/fixtures/HotReloadStateFixture/State.fs` has `greeting`, `greet ()`, `banner` (captured at startup into a closure), `motto`/`lazyMotto` (forced lazy), and a `handlers` list captured by value. There is no page handler that renders a value into a response, and no handler that caches one.

What I could not find: any summary of what a library call does with its argument (`Falco.Markup`, `String.Concat`, `HttpResponse.WriteAsync`), anywhere in the tree. `Escape.PassedTo` carries only the callee's name.

## Design

Two layers. The first rules out retention by reading code. The second watches the response. `Patched` needs both. The first alone yields `Applied`, never confirmed, in the same way a function patch with no caller yet ends as "not confirmed".

Layer 1, follow the value past the reader. New pure file `SageFs.Core/Middleware/ValueFollow.fs` (BCL and FSharp.Core only, compiled before `ValueReads.fs` is used by the host). It computes, per method and parameter, a summary by running the same forward stack simulation `fateAt` uses, starting at `ldarg i` instead of a getter call:

```fsharp
[<RequireQualifiedAccess>]
type ParamFate =
  /// Flows nowhere: popped, or into a local nothing reads.
  | Drops
  /// The result is derived from it; the caller follows the call's result.
  | FlowsToResult
  /// Stored somewhere that outlives the call.
  | Retains of Escape
  /// The summary could not be computed, and says why. Counts as Retains.
  | NotFollowed of why: string

[<RequireQualifiedAccess>]
type Handoff =
  /// Every path from the read ends dropped or in the value returned to the framework, with no store on the way.
  | ReachesResponseOnly of path: string list
  | StoredAt of Escape
  | LostAt of why: string
```

`followFate : Tokens -> SummaryOf -> Instr[] -> int -> Handoff` replaces the leaf of `fateAt`'s `PassedTo` and `Returned` cases. `SummaryOf` is an injected function from a resolved callee to `ParamFate`, like `Tokens` is injected today, so the sim and the tests run it over hand-built IL. Rules: depth bound (the `maxLocalHops` pattern, a new literal in `ValueFollow.fs`, 6 call levels), recursion through a visited set returning `NotFollowed "recursion"`, a branch before use is `LostAt` as `fateAt` already treats it.

Where summaries come from. For callees in the user's project assemblies, computed on demand from IL, memoised per session. For library callees, a curated closed table, `LibrarySummaries`, keyed by a resolved method identity (declaring type, name, parameter types) with one entry per method and a reason line. A test reflects over the real assembly for each entry to confirm the method exists and has that signature (contract test against the external source of truth). Not in the table means `NotFollowed`, so the save restarts as today. The table starts from what Experiment 1 finds.

The new verdict: `ValueVerdict` gains `PatchIfServed of sites: ReadSite list`. `checkSave` treats it as acceptable for the patch and returns the list so the worker can arm layer 2. `describeHolder` is unchanged for `HeldBy`. This is a wire-adjacent type (it travels over `FsiProtocol`), so the protocol golden tests change with it. Both ends of that protocol ship in one package, so no compatibility shim.

Layer 2, confirm by the response. After a patch whose verdicts were `PatchIfServed`, the worker arms a `ServedWatch` for each value: the old and new rendered text of the value (strings and primitive numbers only in v1; the new text must differ from the old). `DevReloadMiddleware` checks each buffered response body it already holds:

```fsharp
[<RequireQualifiedAccess>]
type ServedVerdict =
  | NewSeen of route: string
  | OldSeen of route: string        // a copy exists: restart, naming the route
  | NeitherSeen                     // keep waiting
```

`NewSeen` makes the save `Patched` (mechanism `detour`, the existing wire value). `OldSeen` becomes a restart with a new `RestartReason.ValueServedStale of binding * route` (a wire `case`, additive; the `type` stays `restart`, so clients that read `type` are unaffected). When the old text is a substring of the new one, `OldSeen` cannot be told from `NewSeen`; the watch then reports `NewSeen` only and the report says the old text could not be excluded. The wait ends on a response event, never a deadline of its own; the existing `Timeouts` bound (`SAGEFS_PATCH_CONFIRM_SECONDS`) is what ends it as `NeverEntered`-style "not confirmed", with the message "no response carried the new value yet; load a page that renders `greeting`". Only HTML and text responses that DevReload buffers are watched; a streamed response (Datastar SSE) is not, and the value stays `Applied`. Apps with no response (console apps) have no layer 2, so layer 1 alone never produces `Patched` there and they restart as now.

What this does not prove, and the report says: a route nobody has requested since the patch may still hold a copy that layer 1 could not see. Layer 1 rules out stores in code it followed, which is the guard against that.

Files: new `ValueFollow.fs`; `ValueReads.fs` (verdict case, `checkSave`); `ValueReadTracking.fs` (the tracker builds the `SummaryOf` function from loaded assemblies in `Track`); `SageFs.Host/WorkerMain.fs` (`redefinitionRefusals`, arming the watch); `SageFs.Host/DevReloadMiddleware.fs` (the check); `SageFs.Core/Features/ReloadOutcome.fs` (`ValueServedStale`); `PatchConfirmation.fs` (a served-watch input beside entry probes).

Wire and clients: `lastReload` gains a restart `case` and a `suggestedAction`; no new MCP tool, no new SSE `type`, no `apiVersion` move. Commit trailer `Plugin: n/a, a new restart case name in an existing field; clients print it`. The dashboard's Hot Reload panel text for the case comes from one describe function. Persistence: none.

## Tests

RED first, all in the default suite unless marked:

1. `ValueFollowTests`: "WHY - a read concatenated and returned ends in the response, because String.Concat keeps nothing" (hand-built IL with the `emit` helper from `ValueReadsTests.fs`, a `SummaryOf` that says `Concat: FlowsToResult`). RED: `followFate` does not exist.
2. "WHY - a read passed to a callee that stores it in a static two calls down is StoredAt, naming the field".
3. "WHY - a callee with no summary is LostAt, so the save restarts".
4. "WHY - a recursive callee is NotFollowed, not an infinite loop".
5. `ServedWatchTests`: "WHY - a response with the old text after the patch is a restart naming the route", "WHY - the new text seen is Patched", "WHY - old text inside new text is not claimed as excluded".
6. `LibrarySummariesTests`: every table entry resolves by reflection and has a reason; the table is sorted and has no duplicate identity.
7. Host tier (`--integration-host`, by reference in the `Integration` registry), `HotReloadStateOutcomeTests` new rows from the matrix above, fixtures added to `HotReloadStateFixture`: `handlerRender` (renders `greeting`, expect `Patched` after a request shows new text), `handlerCache` (stores the page in a static: expect restart naming the field), `handlerLazy` (expect restart), `handlerWrongSummary` (calls a fixture-only callee whose table entry deliberately says `Drops` but which keeps the value: the response watch must catch the stale text and restart; this proves layer 2 is a net under layer 1).

Property tests (FsCheck): generate small call graphs of methods with random param flows; `followFate` agrees with a ground-truth interpreter on every graph, and never answers `ReachesResponseOnly` when the interpreter finds a store (the sound direction).

Simulation: extend `ValueReadSim.Shape` with `ToResponse of callee` and `ToResponseAndCache`, and add a `Serve` intent that renders a route with the current or a stale copy. Real `fateAt`/`followFate`, real ledger, real `checkSave`; modelled server. Invariants: `Patched` is reported only after a served response carried the new value; a route that served old text after the patch ends in a restart verdict; no save is `Patched` over a copy `canHold` says exists unless a served response showed it (the old invariant, kept). Twins: report `Patched` on `PatchIfServed` without waiting for a response; layer 1 treats every `NotFollowed` as `Drops`. Both must be caught. Extend `ReflectionReadSim` only if Experiment 3 says the lapse interacts.

Waiting: layer 2 waits on a response event from the middleware, nothing else. The host rows drive requests themselves and read the report; they do not sleep.

## Steps

1. RED test 1 and `ValueFollow.fs` with `followFate` for the straight-line concat case. Green on the hand-built IL. Owns `ValueFollow.fs`, its test file, fsproj lines.
2. RED tests 2 to 4: interprocedural summaries, depth bound, recursion. Same files.
3. RED test 6 and `LibrarySummaries` from Experiment 1's list. Owns `LibrarySummaries.fs` (parallel with 4 and 5, file-disjoint).
4. Verdict case and `checkSave` change; update `FsiProtocol` goldens. Owns `ValueReads.fs`, `FsiProtocol` goldens. After 1.
5. RED test 5, `ServedWatch` pure module and the `DevReloadMiddleware` check. Parallel with 4.
6. Tracker builds `SummaryOf`; worker arms the watch; `ReloadOutcome.ValueServedStale`; docs strings. Collides with 4 on `ValueReads.fs`, so after it.
7. Simulation extension and its twins. After 4 and 5.
8. Host-tier rows and fixture changes (test 7). After 6.
9. Docs: values table row and the "Handlers that render a value" paragraph in `docs/hot-reload.md`, and the matching lines in `docs/how-hot-reload-works.md`, from measured results.

## Measure first

1. The corpus. In a SageFs session (not a raw script), run `sitesIn` over every module-value read in the assemblies of `fixtures/WebAppFixture` and the Falco and Datastar demo sample, and print `fateAt`'s `Escape` per site, grouped by `PassedTo` callee. Question: how many distinct callees cover the page-handler reads. If 25 or fewer curated summaries resolve at least 80 percent of handler-shaped sites, build layer 1. If the list is long or full of Falco internals that call further into ASP.NET, layer 1 cannot reach the response and the item stays Exploring; layer 2 alone is not sound enough to ship (a cache would slip through).
2. Does the response watch see what a handler writes? Check `DevReloadMiddleware` for which content types and sizes it buffers and what it skips (compressed, streamed, over `MaxBodyBufferSizeBytes`). A handler whose response is never buffered gets no confirmation.
3. Cost of summaries. Compute summaries on demand for callees reachable from `Escaped` sites only, on SageFs.Core (46,982 methods). Budget: no measurable change to `Tracker.Track`. The existing IL walks report 23 ms (`GenericReload.reach` on the parity fixture); I have no figure for this one.
4. A deliberate adversary: a Falco handler that renders through `Response.ofHtml` while an `IMemoryCache` holds the page. Layer 1 must call it `StoredAt` or `LostAt`; if it calls it `ReachesResponseOnly`, the table has a wrong entry.

## Risks and dependencies

- Soundness lives in `LibrarySummaries`. A wrong entry weakens layer 1 and leaves only layer 2. Mitigation: the reflection test, the `handlerWrongSummary` row, and a rule that an entry cites the library method's source or a decompile line in its reason.
- `Patched` for a value now needs an observed response; an app nobody requests stays unconfirmed. That is honest and different from a function patch only in what the evidence is.
- Reflection reads and tiering: this item raises the number of patches that depend on the reflection watch being exact. `hot-reload-without-tiering-off` changes how that watch is kept reliable; do not land both in the same week.
- Shares `ValueReads.fs`, `ValueReadSim`, the state fixture and `WorkerMain.redefinitionRefusals` with that item.

## Size

M to L. Layer 1's core is a small extension of `fateAt`, the table and the response watch are each a few days, and the host rows on two runtimes carry the rest. Experiment 1 can shrink it to nothing.

## Decisions for Will

1. Is a `Patched` that rests on an observed response plus a code-level no-store proof acceptable, given the doc's line "never says Patched when it can't prove it"? Recommend yes, with the report naming the route that confirmed it and saying other routes were not checked.
2. Should v1 stop at strings and numbers? Recommend yes; a record rendered through a template has no text to look for.
3. Library summaries as a hand-kept table, or computed from library IL on demand? Recommend the table for v1: library IL reaches into ASP.NET and the analysis would not terminate usefully.
