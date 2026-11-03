# Walk the rest of the symbol ladder

Roadmap item: `walk-the-rest-of-the-symbol-ladder` (Next, Agents). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | In progress, 2026-11-03. Slice 4 (steps 1 to 3) and the reply-size table of slice 5 are built and committed on master, not pushed. Not started: the shared `LadderContract` (step 5), the Readme cell and its test, and the 10-cell outcome gate (step 6). "Measure first" was not run. |
| Size | S, M with the shared contract |
| Branch and worktree | none, work on master |
| Last green commit | `7c520eac` (ratchets 498/498 Trusted; the new lists pass filtered; no unfiltered default run yet, which is the ship gate's job) |
| Next action | Step 5: `SageFs.Core/LadderContract.fs` and the Fable rewire (the decisions under "Decisions for Will" have recommendations: leave the freshness receipt out, link the file like `HatchUse.fs`). Then step 6. Run "Measure first" against a live session before the outcome gate fixes the fixtures: the per-segment latency of `GetCompletions` with `EveryMember`, and whether a list at the 2,000 wire ceiling hides the segment being looked for (the `StopsAtCutList` case exists for it but has only been exercised with a fake lister). |
| Decisions made | `list_references` answers `StopsAt` and `NothingResolves` at once and never asks the compiler. `StopsAtCutList` and `Unknown` still go to the compiler, because neither shows the name is absent. The plan's `ReferenceUnavailable.NameDoesNotResolve` became two cases on `McpReferencesPage.Page` (`NameStopsAt`, `NameNotListed`), because `ReferenceUnavailable` is in Core and `NamePathResolution` is in the SageFs project. `ParentProbe` and `probeParent` are gone: `classifyEmpty` takes the walk's result, and rendering moved to `McpNoMembersText.fs` when the `McpNoMembers.fs` size ratchet asked. Nearest names: at most `SageFsConfig.NearestNamesMax` (5), ratio at least `NearestNameFloor` (50), so a typo that resembles nothing names nothing. The reply-size table measures 6439 characters for `list_members` and 4225 for `list_references` on the worst-case fixtures. |
| Surprises | `McpNamePath.resolve` is a Task fold, and the blocking-call ratchet (24 `GetAwaiter().GetResult()` in test bodies) counts every blocking wait, so the tests are `testTask` cases and the plan's property test is a seeded loop (300 seeds, the seed in the failure message) and not FsCheck. A test whose name calls itself a ratchet must sit in a registered list, or the registry test fails. A `--filter-test-list` for a name that is not a list name runs zero tests and exits 0 (hit once, caught by reading `ran=`). |

## What done looks like

An agent holding a qualified name that no longer resolves (renamed, moved, or typed from memory) calls `list_references` or `list_members` and gets back where the name path stops resolving and the nearest names the compiler does list there, in the same call, without waiting out the 60 second references budget to be told "none found". Every ladder reply stays under a ceiling that only falls, one row per tool. The editor's Type Explorer and the daemon read one shared definition of which kinds are drillable and what the size words are, so the regex scrape in `McpMemberPageTests` goes away. The outcome gate runs the real formatters over a worst-case fixture for the matrix: tool (`list_members`, `list_references`) by name state (resolves, resolves but empty, parent resolves and name does not, no ancestor resolves, session unavailable). No cell is skipped.

## What exists today

Verified by opening the files.

- `SageFs/McpMembersRoute.fs`: `listMembers` asks `membersOf` (`SageFs/McpCompletions.fs:107`, a `GetCompletions` worker message with `CompletionLimit.EveryMember`). When the answer is empty it asks the parent once (`probeParent`) and `McpNoMembers.classifyEmpty` names why: `IsLeaf`, `EmptyContainer`, `NotListedByParent`, `NoParentToAsk`, `ParentUnavailable`. Rendering is `McpListMembers.formatListMembers` over `McpMemberPage.pageMembers`. The cap is `SageFsConfig.ListMembersMax` (100, `SAGEFS_LIST_MEMBERS_MAX`).
- `SageFs/McpReferencesRoute.fs`: `listReferences` sends `WorkerMessage.GetReferences(name, replyId)`. The reply type is `ReferenceReply` (`Counted` with a `CountedScope`, or `NotCounted` with a `ReferenceUnavailable`) in `SageFs.Core/Features/SymbolReferences.fs`, built in one place by `ReferenceReply.ofAnswer`. The compiler call is `SageFs.Core/Features/SymbolReferenceCheck.fs` (`sitesOf` keeps `GetAllUsesOfAllSymbols` hits whose `fullNameOf` equals the name). One budget, `Timeouts.referencesCheck`, covers all projects through `ReferenceGather.run`.
- A name that resolves nowhere is today a `Counted` reply with zero sites. `McpReferencesPage.body` renders "none found in the checked projects ... call list_members on its parent". That sentence is written without asking whether the name exists, and it is only reached after every project was checked, so a typo costs the whole budget. This is the stale-reference gap.
- `SageFs/McpMemberKinds.fs` holds `Rung` (`Drillable | Leaf`), `rungOf` (exhaustive over `CompletionKind`), `kindNoun`. Its comment says the VS Code Type Explorer makes the same split with `drillableKinds` in `sagefs-vscode/src/TypeExplorerProvider.fs:39` (a `Set [ "Namespace"; ... ]` of strings).
- `SageFs.Tests/McpMemberPageTests.fs:41` reads that set by regex (`let private drillableKinds =\s*Set \[...\]`) and line 145 reads `ExactWord` and `AtLeastWord` out of `sagefs-vscode/src/TypeExplorerPure.fs` the same way. Both are text scrapes of Fable source.
- `CompletionKind` and `CompletionTotal` live in `SageFs.Core/Features/AutoCompletion.fs`, a module that opens FCS and FuzzySharp, so the Fable project cannot compile it.
- Precedent for sharing a pure file: `SageFs.Core/HatchUse.fs` has no dependency past FSharp.Core and `tools/agent-hooks/SageFs.AgentHooks.fsproj` compiles it with `<Compile Include="../../SageFs.Core/HatchUse.fs" Link="HatchUse.fs" />`. `sagefs-vscode/src/SageFs.Vscode.fsproj` links nothing from Core today.
- Ratchet machinery: `Ratchet.table { Name; SourceFile; Budgets; Actual }` in `SageFs.Tests/TestInfrastructure.fs:641`, a table of `(string * int)` in `SageFs.Tests/RatchetBudgets.fs`, lowered by `--ratchets --tighten`. No table measures a tool reply. The only reply ceiling tests are `EvalResultSummaryTests` (4,000 characters) and one inline check in `McpMemberPageTests` (`text.Length < 4000`).
- Neither reply carries a freshness receipt (snapshot or generation). The research doc lists it under section 6 ("Every reply carries the snapshot/version it came from"). I grepped `McpMemberPage.fs` and `McpReferencesPage.fs` for snapshot, generation, version and found nothing.
- The Readme capability table (`Readme.md` near line 402) says Type explorer is `HTTP` in the MCP column. `list_members` is now a tool. I could not find a test that holds that table to the registry; `ToolSurfaceHonestyTests` holds tool counts and the docs table, not this one. Roadmap item `fix-the-readme-capability-mcp-marks` (Next) already reworded the cells, so this plan only changes the Type Explorer cell and adds the test.

## Design

The remaining rungs from `docs/progressive-map-research-2026-10-24.md` section 9 are slice 4 (name-path fallback for a stale reference) and slice 5 (result-size ratchet rows for the family, and the Readme capability row). Section 9 "How this stays true" item 2 (one shared `LadderContract` compiled into .NET and Fable) is not built and is what the scrape should become. The freshness receipt is section 6 and stays optional; it needs a decision (below).

### Slice 4: where the name stops resolving

A pure walk, in a new file `SageFs/McpNamePath.fs` (vertical slice, used by both routes):

```fsharp
[<RequireQualifiedAccess>]
type NamePathResolution =
  /// Every segment resolved; `kind` is what the last segment's parent calls it.
  | Resolved of kind: CompletionKind
  /// The path resolves as far as `resolvedPrefix`; `missing` is the first segment its members do not list.
  | StopsAt of resolvedPrefix: string * missing: string * nearest: string list
  /// No segment resolves, not even the first.
  | NothingResolves of first: string
  /// A member list could not be fetched, so how far the path resolves is unknown.
  | Unknown of at: string * reason: CompletionUnavailable
```

`resolve` takes `membersOf` as a function parameter (so a simulation and a unit test run with no session), walks segments left to right, stops at the first segment missing from its parent's list, and ranks `nearest` with `Fuzz.Ratio` from FuzzySharp (already a Core dependency used at `AutoCompletion.fs:241`, so no new package), capped at a named count in `SageFsConfig`. A prefix that resolves to a leaf kind is `Resolved` for the prefix and the leaf rule from `McpNoMembers` applies.

`list_references` runs `resolve` first, in the daemon, before `GetReferences`. `StopsAt`, `NothingResolves` and `Unknown` return at once with the existing `Page.Uncounted` shape extended by a new `ReferenceUnavailable.NameDoesNotResolve of NamePathResolution` case, so the scope line still comes first ("Scope: unavailable, the name does not resolve past X") and no count is printed. `Resolved` proceeds to the compiler as today. The zero-site sentence in `McpReferencesPage.body` then only appears for a name that resolves and has no uses, and says so.

`list_members` replaces `probeParent` with the same `resolve`, so `NotListedByParent` gains the nearest names and the stopping prefix. `McpNoMembers.NoMembersBecause` keeps its cases; `NotListedByParent` takes the extra fields.

Cost: `resolve` makes one `GetCompletions` call per segment with a cache hit on the worker side for the hot prefixes. For a ten-segment path that is ten cheap calls. Measure first (below).

### Slice 5: reply ceilings and the Readme row

New `Ratchet.table` named `ladder reply size`, source file `SageFs.Tests/RatchetBudgets.fs`, a list `ladderReplyChars : (string * int) list` with one row per tool (`list_members`, `list_references`). `Actual` runs the production formatters (`McpListMembers.formatListMembers`, `McpReferencesPage.render`) over a worst-case fixture (500 members of long names; 10,000 sites across 200 files with deep paths) at the configured caps and returns the character length. A tool with no row has a budget of zero. Starting numbers are the measured lengths, rounded up, then only down.

### The shared contract

New file `SageFs.Core/LadderContract.fs`, namespace `SageFs`, no dependency past FSharp.Core, compiled first in `SageFs.Core.fsproj` (before `Features\AutoCompletion.fs`, line 265) and linked into `sagefs-vscode/src/SageFs.Vscode.fsproj` before `TypeExplorerPure.fs`:

```fsharp
[<RequireQualifiedAccess>]
type LadderKind = Namespace | Module | Class | Struct | Interface | Enum | Union | Type | Constant | Delegate
                | EnumMember | Event | Exception | Field | Method | OverriddenMethod | Property | Typedef
                | Variable | ExtensionMethod | TypeParameter | Keyword | Folder | File
module LadderKind =
  let label (k: LadderKind) : string = ...        // one exhaustive match
  let tryParse (s: string) : Result<LadderKind, string> = ...  // the wire boundary; the Error carries the unknown word
  let rung (k: LadderKind) : LadderRung = ...      // exhaustive; the one definition of Drillable versus Leaf
module LadderSize =
  [<Literal>] let ExactWord = "exact"
  [<Literal>] let AtLeastWord = "atLeast"
```

`LadderRung` (`Drillable | Leaf`) moves into the contract and `Rung` in `McpMemberKinds.fs` becomes an alias of it. `rungOf` becomes a match over `CompletionKind` that maps to `LadderKind` (exhaustive, so a new `CompletionKind` breaks the build) and then `LadderKind.rung`. `CompletionKind.label` is defined as `LadderKind.label (LadderKind.ofCompletionKind k)` so the wire word has one home. `TypeExplorerProvider.fs` drops its string set and matches on `LadderKind.rung`. `TypeExplorerPure.fs` drops its two literals and uses `LadderSize`. `CompletionTotal.label` uses the same literals.

The tests that scrape source (`editorDrillableLabels`, `literalOf`) are deleted and replaced by one test that both projects compile the same file (see Tests), since the equality they asserted is now structural.

Wire and client impact: no wire shape changes in slices 4 and 5 beyond new refusal text in tool replies. The `kind` strings on `/api/completions` are unchanged. `apiVersion` does not move. The extension rebuild is required for the contract step, and it is a refactor with no behaviour change; it still needs the VS Code golden harness green. `sagefs.nvim` reads `kind` strings from `/api/completions`; it needs nothing, trailer `Plugin: n/a, the kind and size words on the wire are unchanged`. Commits touching `SageFs/McpTools.fs` or `docs/mcp-tools.md` carry a `Plugin:` trailer.

Token cost: no new tool. `list_references` and `list_members` descriptions gain one sentence each about unresolved names, roughly 40 tokens together. Check the description-length ratchet in `ToolDescriptionTests` before editing.

## Tests

RED first, in this order, all default tier, no session.

1. `SageFs.Tests/McpNamePathTests.fs` (new): `WHY - a name whose parent lists everything but the last segment stops at the parent and names the nearest candidates`. Fixture: `membersOf` fake listing `["Path"; "File"; "Directory"]` under `System.IO`; resolve `System.IO.Pth`; assert `StopsAt("System.IO", "Pth", "Path" :: _)`. Fails to compile today (module missing), which is the proof of broken.
2. `WHY - list_references on a name that stops resolving answers Uncounted and never runs the compiler`: the references call is a function parameter that throws if called; assert the page is `Uncounted` with `NameDoesNotResolve`.
3. `WHY - a name that resolves and has no uses still says none found, and says the name resolves`.
4. Property (FsCheck): for any generated tree of names and any path drawn from it with one segment mutated, `resolve` never answers `Resolved`, and `StopsAt.resolvedPrefix` is the longest true prefix. Random trees and paths come from seeds, so a failure replays.
5. Ratchet: the `ladder reply size` table exists, has one row per ladder tool, and a row above its measured length fails. Twin: a formatter that drops the page cap (prints every member) must exceed the budget, proving the ratchet has teeth.
6. Contract: `LadderContractTests` asserts `LadderKind` cases equal `CompletionKind` cases by name (reflection over both unions, one assertion per direction), `LadderKind.rung` equals `rungOf` for all, and that `sagefs-vscode/src/SageFs.Vscode.fsproj` and `SageFs.Core.fsproj` each contain the `LadderContract.fs` compile entry. The last check reads two project files, which is a structural check on wiring and not a scrape of source logic.
7. Readme: the MCP column for the Type explorer row is a tick if and only if `list_members` is registered; parse the table by header, not by line number.

Simulation: the reference check already has `ReferenceScopeSimTests` and `ReferenceCacheSimTests`. Slice 4 adds no timing of its own because `resolve` is a pure fold over an injected `membersOf`; the DST value is in the property above. If the measurement below shows per-segment calls are slow enough that a budget is needed, add a clock parameter and a sim like `ReferenceGather` with a twin that ignores the budget.

Outcome gate (the matrix above): `SageFs.Tests/LadderOutcomeTests.fs` renders each of the 10 cells through the real formatters and asserts the first line (scope line for references; total line for members), that the text names what to do next, and that length is under the ratchet row. A cell with no fixture fails the test; there is no skip.

No wall-clock waits: everything runs against injected functions.

## Steps

1. RED tests 1 to 3 and the `McpNamePath.fs` skeleton that turns test 1 green. File: new `SageFs/McpNamePath.fs`, listed in `SageFs/SageFs.fsproj` before `McpNoMembers.fs`.
2. Wire `resolve` into `McpReferencesRoute.listReferences`; add `ReferenceUnavailable.NameDoesNotResolve` (edits `SageFs.Core/Features/SymbolReferences.fs`, `SageFs/McpReferencesPage.fs`). Tests 2 and 3 green. Update `docs/mcp-tools.md` and `skills/sagefs/loop.md` text for the new refusal.
3. Replace `probeParent` in `McpMembersRoute.fs` with `resolve`; extend `NotListedByParent`. Property test 4.
4. Ratchet table and test 5 (parallel with 3; owns `SageFs.Tests/RatchetBudgets.fs` and a new `LadderReplySizeTests.fs`).
5. `LadderContract.fs` with test 6, then the rewire of `McpMemberKinds.fs`, `AutoCompletion.fs` label, `TypeExplorerProvider.fs`, `TypeExplorerPure.fs` (parallel with 3 and 4 only if the owner of 3 does not touch `McpMemberKinds.fs`; steps 3 and 5 both touch `McpNoMembers`/`McpMemberKinds`, so run 5 after 3).
6. Readme cell and test 7; the outcome gate file. Roadmap landmark update by the orchestrator.

Steps 1 to 3 are sequential. 4 and 6 are file-disjoint from them. 5 follows 3.

## Measure first

- Time `resolve` against a live session for a depth-8 path through a big namespace (`Microsoft.FSharp.Collections.List.map`): per-segment latency of `GetCompletions` with `EveryMember`. If a segment costs over a few hundred milliseconds on a cold worker, resolve only asks for the last two segments and falls back to the full walk when those disagree.
- The worst-case reply lengths for the ratchet rows, taken from the fixtures, before choosing the numbers.
- Whether `EveryMember` on a very large namespace (the 2,000 wire ceiling) still resolves a missing segment correctly, because an "at least N" list may omit the segment being looked for. `StopsAt` must then carry `CompletionTotal.AtLeast` as a caveat and not claim the name does not exist. If this happens on a real namespace, add a `PossiblyMissing` case.

## Risks and dependencies

- A wrong "does not resolve" is worse than the current vague line. The `AtLeast` caveat above covers the known hole. A name that exists only through an `open`ed module alias may not list under its parent path; the reply says "the compiler does not list it under X", not "it does not exist".
- The contract step changes the Fable build. Run the extension golden tests and the fsx contract tests before pushing.
- Depends on nothing outside the group. `fix-the-readme-capability-mcp-marks` is done and only the Type Explorer cell moves. A freshness receipt would touch the same two pages and should be sequenced after this plan to avoid conflicts.

## Size

S for slices 4 and 5 (days), M with the shared contract, because it spans the .NET and Fable builds.

## Decisions for Will

- Add the freshness receipt (section 6) to both replies in this item or leave it out? Recommendation: leave it out. The worker has no snapshot id to hand over today, and inventing one is its own design.
- Put `LadderContract.fs` in `SageFs.Core` (linked into the extension, as `HatchUse.fs` is linked into the hook) or in a new tiny project both reference? Recommendation: link, following the precedent, because a project adds a package and restore surface for one file.
