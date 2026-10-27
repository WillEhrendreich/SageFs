# Markers on individual Expecto cases

Roadmap item: `markers-on-expecto-cases` (Later, Live testing). Plan written 2026-10-26 against master `59a8692c`.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | In progress (step 7 of 9) |
| Size | M |
| Branch and worktree | plan/markers-on-expecto-cases in /home/will/Work/SageFs (main checkout, no separate worktree) |
| Last green commit | Step 7's wire is green in this commit: `TestLocationsApplied.apply`'s four rows went from red `cde75622` to green, the `matchedCase` extraction left every `place` and `discover` test green, and both TRUST lines (the ratchet lane and the full default suite) are quoted in the commit message. |
| Next action | The rest of step 7: the simulation `SageFs.Simulation/ExpectoCasesSim.fs` (new) with plan test 8's invariants and its line-offset twin, then the outcome journey (plan test 9) on fixture `ExpectoMarks.fs` under `--integration-lt`. RED first per the plan. |
| Decisions made | 2026-10-26, Will's session: "Measure first" question 1 answered before any code. Tree-sitter reaches the `testCase` application in all six builder shapes: each parses as an `application_expression` whose second child is the string literal, under `application_expression`, `infix_expression` (for `<\|`) or `ce_expression` (for the CE builders). No hand scanner fallback needed. Pinned in `SageFs.Tests/fixtures/TreeSitter/expecto-builder-shapes.txt`. 2026-10-26, step 2: label matching splits each label on non-letter/non-digit runs and compares whole tokens, keeping `arch` and `ui` working as whole labels while `building` and `searching` no longer trip them. Net zero lines in LiveTestingTypes.fs (3708, at its budget). 2026-10-26, step 3: `SourceCase` carries `FilePath` and `Kind` (Binding/List/Case), neither of which is in the plan's sketch: `Exact` names a file and `place` takes no file parameter, and without `Kind` a list and a case with the same path are indistinguishable while reflected paths are leaves. `unmatched` carries the reflected segments at Computed positions (the names the text cannot check). `NearestAncestor` lands on the deepest ancestor whose path is all literal and whose span holds the case, with the binding (`Path = []`) as the last prefix tried and the case's own line as the last resort when the source has no ancestor entry at all; spans also keep two bindings' equal paths apart. Equal structured paths across two bindings resolve to the first in source order, because the segments carry no binding name; watch for that at step 7's wiring. Compile order: `TestTreeSitter.fs` moved above `LiveTestingExecutors.fs` (comment in `SageFs.Core.fsproj`) so `ExpectoCases` can serve both step 5 (discover needs the parse surface) and step 6 (discoverLeafTests needs `categoryOf`). Plan test 7's `categoryOf` mutants belong to step 6, where that function exists; its two `place` mutants are killed here. 2026-10-27, step 4: `TestCase` carries `NamePath: string list` instead of a side map. The worker hook DTO already carries `TestCase` (additive JSON on an internal channel), the step 7 edge then reads the path straight off `state.DiscoveredTests` for its one line, and one record cannot drift from a parallel map. Attribute discovery fills `[]`, because a `MethodInfo` has no list chain; only Expecto discovery produces structure. The god-file budget was paid by moving the tail cluster (debounce, test-cycle requests, `TestCycleEffect`, 165 lines) into `Features/LiveTestingRequests.fs`, compiled after `LiveTestingTypes.fs`, and the `fileSize` row is dropped by `--tighten`, never by hand. 2026-10-27, step 5: `TestTreeSitter.tryResources` exposes the one native Language and compiled queries so `discover` never builds a second Language against the same library. Binding entries are found by row containment, because `TestTreeSitter.discover` reports the binding name's line (the attribute and the `let` may share a line or not, and only containment handles both); the REPL prototype's start-row equality passed purely because its fixture had them on one line. `nameArgument` calls only a quoted string const Literal: interpolation and any other expression become Computed. 2026-10-27, step 6: `categoryOf` takes a `CategoryEvidence` record (ListLabels, FilePath, Body) rather than the SourceCase itself, reversing an earlier lean: reflection discovery's signature is (cache, asm) with no source case to hand, and inventing one would fabricate Line and EndLine values; the record keeps each input honest and a source-side caller can fill all three from a real SourceCase later. The measure-first diff was run over all 17551 discovered cases: 581 change category, zero flip to Integration (reflection supplies no body, so the I/O rule cannot fire there), the losses are whole-name coincidences (CohortIntegrationScopeTests and title-marked [Integration] latency tests fall to Unit, the direction that keeps them running) and the gains are real list labels (transitions: Unit to Property 253, Unit to Architecture 80, Integration to Browser 74, Unit to Benchmark 2, against Integration to Unit 111, Browser to Unit 51, Browser to Architecture 6, Integration to Architecture 4); the full list is saved as category-diff.txt in the session scratchpad. `bodyIoPatterns` checks the longer names before the shorter because SqlConnection is a substring of NpgsqlConnection. 2026-10-27, step 6 (host mirror): the rules live in `Features/LiveTestingCategories.fs` rather than in `ExpectoCases`, because `FsiHost.fsproj` compiles the live-testing executors in the isolated host and takes no packages on purpose, so the file carrying the TreeSitter parse surface could never join that list. The ConfigHost suite test failed on exactly that: the host's copy of LiveTestingExecutors could not see ExpectoCases after the switch. The call site now names `LiveTestingCategories` directly so the host never needs `ExpectoCases`, and `ExpectoCases` re-exports `categoryOf`, `categoryWords` and `categoryEvidenceTokens`, so the plan's `ExpectoCases.categoryOf` contract and the mutation tests keep a single implementation. All three mirrors (`FsiHost.fsproj`, Core's `FsiHost` embeds, `FsiHostBuild.hostSourceNames`) carry the new file in matching position. 2026-10-27, step 7 (wire): the edge re-categorises, settled on this step: plan test 9 requires a SqliteConnection fixture case to stay off the keystroke, and only a body-supplied categoryOf can make that distinction, because only the edge has bodies. Every placed test's category is recomputed from CategoryEvidence (reflection's labels, the source's file, the source's body); an unplaced test keeps discovery's answer. `matchedCase` is the one selection shared by `place` and the edge, so a mark and its category can never name two different cases. `apply` runs the old merge first and `place` after it, so placement wins where the method-name heuristic also spoke, and the locations pass through untouched. Both producers parse each file twice, attributes then cases; the second parse sits outside the TreeSitterParse trace, so the histogram still measures only tree-sitter, and sharing one parse is a possible follow-up if the keystroke path ever shows cost. SageFsApp shrank under its ceiling and the fileSize row was tightened with --tighten, 2467 to 2465. |
| Surprises | `categorize` had two existing rows pinning `["arch"]` as Architecture, so the fix had to keep whole-token `arch` working rather than dropping the short form. Also: the FSI session resolves `SageFs.Tests` oddly because `PaneId` has a union case `Tests` (documented in ProjectLoading.fs); qualifying with `global.SageFs.Tests...` avoids it. A generated binding is one root shape, because a `[<Tests>]` let is one expression. Step 4's file list in this plan missed two mirrors of the FSI host's sources: `FsiHost.fsproj` and `FsiHostBuild.hostSourceNames` name the same files as Core's `FsiHost` embeds, and moving only the fsproj put an FSC error inside a cache directory, far from the edit. All three move together now, and a registered invariant test pins the agreement. A text sweep over this item also needs more than a `with` keyword window: one of this file's own test names contains the word, and the window read it as an update expression. The matrix test's first expectation (lines 2 to 7) was written from the REPL fixture where the attribute shared a line with the `let`; `fixtureSource` gives the attribute its own line, so the real lines are 3 to 8. The production output was probed directly and was right, so the check was corrected against the fixture rather than the code bent to match it. The FSI rebuild hint blaming `FeatureDiscoveryTests.fs` for the missing `discover` was noise; the missing member was the real cause, quoted in the red commit. The plan's measure-first fear (a Unit to Integration flip driven by the I/O list) could not occur at this site over 17551 cases: reflection supplies neither body nor file, so those two rules stay dormant until a source-side caller exists, and that handoff is step 7's decision. The switch broke the FSI host a second time, same miss class as step 4's `LiveTestingRequests`: the host's file list is its own world, so a reference added on the daemon side stays invisible there until a test actually builds a host. The registered mirror guard pins agreement between the three lists but cannot see a file none of them has, so the ConfigHost test in the default suite is what catches a missing file; the two layers do different real work. The previous handoff named the sim file `TestScenario.fs` and a `GetSessionRuns` expectation; the plan says `ExpectoCasesSim.fs` (plan test 8) and the outcome journey on fixture `ExpectoMarks.fs` (plan test 9), and the Next action row now says so. |

## What done looks like

Open an Expecto test file. Every `testCase`, `testCaseTask`, `testTask`, `testAsync`, `testProperty` and `test "name" { }` that has a literal name carries its own mark on its own line, with its own verdict, in VS Code, Neovim and the dashboard. A case whose name is computed gets the mark of its nearest named ancestor and says it was placed by fallback. A test's category comes from where it sits (the list it is in, the file it is in), and a slow test with no telltale word in its name stops running on every change. The outcome gate is a journey on a fixture Expecto file that edits one case's body and asserts that exactly that line's mark changes, across the matrix below.

| Axis | Cells |
|---|---|
| Name shape | literal, interpolated (`$"..."`), `sprintf`, built in a `for`/`List.map` |
| Nesting | case in a binding, case in one `testList`, case in three nested lists, case in a list that has a computed name |
| Builder | `testCase "n" <\| fun`, `testCase "n" (fun () ->`, `testTask`, `testProperty`, `test "n" { }` |
| Buffer | saved text, unsaved edit that moves lines |
| State | pending (`ptestCase`), focused (`ftestCase`) |

## What exists today

- Discovery of an Expecto leaf is reflection. `ExpectoExecutor.discoverLeafTests` (`SageFs.Core/Features/LiveTestingExecutors.fs`, around line 786) flattens each `[<Tests>]` binding with `Expecto.TestModule.toTestCodeList` and builds one `TestCase` per `FlatTest`. The full name is `Namespace.Module.binding/list/.../case`. The list path is available as `string list` at that point (`name`) and is thrown away after joining with `/`. `Origin = TestOrigin.ReflectionOnly`, `Labels = []`, and `Category = CategoryDetection.categorize [] fullName TestFramework.Expecto [||] None`.
- Source positions come from tree-sitter, and only for attributed bindings. `SageFs.Core/tests.scm` matches `[<Tests>] let name = ...` (and the xUnit, NUnit, MSTest attributes); `TestTreeSitter.discover` returns `SourceTestLocation { AttributeName; FunctionName; FilePath; Line; Column }`. Nothing queries the `testCase` application inside the binding.
- `SourceMapping.mergeSourceLocations` (`LiveTestingTypes.fs`, around line 3021) matches by `extractMethodName test.FullName`, which for an Expecto name is the binding name before the first `/`. So every leaf under one binding gets the binding's line.
- The gutter projects by line. `TestAnnotations.project` groups `SourceMapped(file, line)` entries by line and keeps the worst status, so one binding with 40 cases shows one mark with the worst verdict. `LiveTestingTypes.annotationsForFile` does the same.
- Categories: `CategoryDetection.categorize` takes labels, the full name, referenced assemblies and optional source text. It checks `fullName.ToLowerInvariant().Contains "integration"` (and `"browser"`, `"e2e"`) over the whole name, which includes the case's own title. For Expecto the source text is always `None`, so the I/O heuristic (`containsIoPattern`: `System.IO`, `File.`, `HttpClient`, `SqliteConnection`, `Process.Start` and others) never runs on an Expecto test. Run policy defaults: unit and property on every change, integration and browser on demand, architecture on save (`RunPolicyDefaults`).
- A latent bug in the label branch of `categorize`: it tests `l.Contains "ui"` for Browser and `l.Contains "arch"` for Architecture, as substring matches. `Labels` is empty for Expecto so it is inert today. Filling `Labels` with list names, which this plan wants, would turn "building" into Browser and "search" into Architecture.
- How much this matters in this repo, counted with grep over `SageFs.Tests` on 2026-10-26: about 7,850 `testCase "`, 1,110 `testTask "`, 120 `testAsync "`, 520 `testProperty "`, 630 `testPropertyWithConfig`, 3,980 `test "` (a count that includes `ptest "`/`ftest "`), 3,020 `testList "`, 1,684 `[<Tests>]`. Names that are not a literal right after the builder: about 37 `testCase (`, 6 `testCase <ident>`, 12 `testList <ident or paren>`. So literal names are the overwhelming case; the fallback is for the rest. The grep is rough and the first step recounts it properly.

I could not find: the parse-tree shape tree-sitter gives for `testCase "n" <| fun _ -> ...`, `testCase "n" (fun () -> ...)` and `test "n" { ... }`. `tests.scm` documents only the attribute shapes. Step 1 dumps them.

## Design

Read the cases from source, match them to the reflected leaves by list path, put each leaf on its case's line, and decide the category from where the case sits.

### Types (all in a new `SageFs.Core/Features/ExpectoCases.fs`, a vertical slice; `LiveTestingTypes.fs` is at its size budget of 3708 lines and may not grow)

```fsharp
/// One case or list found in the text, with the literal names on the way down to it.
type SourceCase =
  { Path: SourceName list          // outermost list first, the case last
    Line: int
    Column: int
    EndLine: int                   // the span of the call, for the I/O scan
    Body: string }

[<RequireQualifiedAccess>]
type SourceName =
  | Literal of string
  | Computed                        // interpolated, sprintf, an identifier, a loop variable

/// Where a reflected leaf landed.
[<RequireQualifiedAccess>]
type CasePlacement =
  | Exact of file: string * line: int            // every segment of the reflected path matched a literal
  | NearestAncestor of file: string * line: int * unmatched: string list   // placed on the closest matched list or the binding
  | NotInSource                                    // no binding found: stays ReflectionOnly
```

`TestOrigin.SourceMapped of file * line` stays as it is; `CasePlacement` is the decision, and a pure function turns it into an `Origin`. `NearestAncestor` carries why, and the row's tooltip says "placed by the list it is in; its name is computed".

### The pure core

- `ExpectoCases.discover : filePath:string -> code:string -> SourceCase array`, a new function in `TestTreeSitter` territory (it needs the native library, so the pure matcher below takes its output as data).
- `ExpectoCases.place : SourceCase array -> reflectedPaths:(string * string list) list -> (string * CasePlacement) list`. Input is each leaf's full name and its structured list path. Matching walks the path against the source tree: a `Literal` segment matches an equal literal; a `Computed` source segment matches any one reflected segment and everything below it falls back to `NearestAncestor`. A reflected path with segments that contain `/` is why the structured list is kept; splitting the joined full name on `/` is wrong for a case title like "a/b".
- `ExpectoCases.categoryOf : CategoryEvidence -> TestCategory * CategoryWhy` where `CategoryWhy` is a closed DU (`FromListLabel of string | FromFilePath of string | FromBodyIo of string | FromDefault`). Order: the nearest enclosing list whose label is a whole word equal to a category name (`unit`, `integration`, `browser`, `e2e`, `property`, `architecture`, `benchmark`), then the file path (a directory or a file name with `Integration`, `Browser`, `E2E` as whole tokens), then the I/O scan of the case's own `Body` limited to patterns that mean a network, a database or a child process (`HttpClient`, `Socket`, `SqlConnection`, `NpgsqlConnection`, `SqliteConnection`, `Process.Start`), then Unit. The existing whole-name substring check is dropped for Expecto. `File.`/`Path.`/`System.IO` are excluded from the Expecto I/O scan on purpose: a unit test that calls `Path.Combine` would otherwise leave the on-every-change set and stop giving feedback. The rule is to fail toward running.
- `categorize` itself gets whole-token matching for labels (split on non-letters, compare whole tokens), which fixes the `"ui"`/`"arch"` substring bug for every framework.

### The edge

- `discoverLeafTests` keeps the structured path (new field on the internal result, then a new `TestCase` field or a side map; step 4 decides, see Measure first) and stops computing the category inline.
- `TuiEvent.TestLocationsDetected` (handled in `SageFs/SageFsApp.fs` around line 1479) already merges tree-sitter output into `DiscoveredTests`. It gains a second input, the cases, and applies `ExpectoCases.place`. `SageFsApp.fs` is at its size budget (2467); the call is one line into the new file.
- Tree-sitter runs on the buffer on every keystroke (50 ms debounce), so an unsaved edit that moves lines moves the marks with it. Reflected names come from the last compile, so a case renamed in an unsaved buffer is `NotInSource` for the reflected name and a new, undiscovered case until the next eval. That is the existing behaviour for renamed tests, and the plan keeps it.

### Wire and clients

`file_annotations` already carries a line per entry, so marks, lenses and coverage hovers move to case lines with no new fields. Neovim and VS Code get N marks per file instead of one per binding; a 500-case file has 500 lens/extmark entries. Both clients need a check that rendering stays cheap (Measure first). Categories already travel in the entries. `apiVersion` does not move. Commit trailer: `Plugin: n/a, file_annotations keeps its shape; the plugin renders more lines`.

### Persistence

`.sagetc` stores results and coverage by test id (`TestCachePersistence.fs`). Test ids come from `TestId.create fullName framework` and do not change. Nothing to migrate.

## Tests

RED first.

1. `ExpectoCasesTests.fs` (pure, default suite): `place` of a source with `testList "a" [ testCase "x" ...; testCase "y" ... ]` against reflected paths `["a";"x"]` and `["a";"y"]` returns `Exact` with two different lines. Fails today because nothing exists.
2. `CategoryDetectionTests`: `categorize ["Building blocks"] ...` is `Unit` (today's code returns Browser once labels are used, because "building" contains "ui"); `"Searching"` is not `Architecture`; a case titled "integration of the parser" inside `testList "unit"` is `Unit`, with `CategoryWhy.FromListLabel "unit"`.
3. `categoryOf` with a body containing `new SqliteConnection(` and no word in any name is `Integration`, `FromBodyIo "SqliteConnection"`; a body containing only `Path.Combine` is `Unit`.
4. Property (FsCheck): for any generated tree of lists and cases with literal names, `place` is `Exact` for every leaf and the lines are strictly increasing in source order; replacing any one name with `Computed` changes only that subtree to `NearestAncestor`; no leaf is ever placed outside its own binding.
5. Property: names containing `/` still place correctly, because placement uses the structured path.
6. Tree-sitter shape tests (`TestTreeSitterCasesTests.fs`, needs the native lib, in the tier that already runs `TestTreeSitter` tests): the six builders in the matrix each produce a `SourceCase` with the right line. A missing native library fails the test with `NativeAvailability.describe`, it does not skip.
7. Mutation (`MutationTestingFramework`): `place` with the nearest-ancestor fallback replaced by "drop the leaf"; with `Computed` treated as `Literal ""`; `categoryOf` with the list-label check and the file-path check swapped. Each killed.
8. Simulation (`SageFs.Simulation/ExpectoCasesSim.fs`, new): pool is a generated source tree and a seeded edit schedule (insert a line above, rename a case, delete a case, reorder lists, add a computed name); the sim folds the REAL `ExpectoCases.place` with the REAL `TestAnnotations.project`. Invariants: every `Exact` mark's line holds the case's literal name in the text; no two leaves share a line unless both are `NearestAncestor` of the same parent; a mark never lands in another file. Twin: place by line offset from the binding without re-reading the text (breaks the first invariant after an insert above); same seed same trace.
9. Outcome gate: `LiveTestingJourneyTests`-style journey under `--integration-lt` on a new fixture `ExpectoMarks.fs` copied beside the FromCSharp sample: flip one case's assertion, wait on the `/events` frame, assert via `GET /api/live-testing/entries` and `file_annotations` that exactly that case's line goes red and its neighbours stay green; cover each cell of the matrix. For categories, a fixture case that opens a `SqliteConnection` with a neutral name must not run on a keystroke and must run on an explicit `run_tests`.

No wall-clock waits: the journey waits on frames through `LtStream.expectFrame`.

## Steps

1. Dump the tree-sitter parse of the six builder shapes into a golden file under `SageFs.Tests` fixtures (a test that fails when the grammar changes shape). Smallest first commit; no behaviour change.
2. Fix `categorize` label matching to whole tokens (RED test 2 first). Files: `LiveTestingTypes.fs` (net zero lines: replace, do not add). Independent.
3. `ExpectoCases.fs` with `SourceCase`, `place`, and tests 1, 4, 5, 7. Pure. Parallel with 2 and 4.
4. Keep the structured path out of `discoverLeafTests` (decide field vs side map after reading `TestCase` consumers), tests for it. Files: `LiveTestingExecutors.fs`, possibly `LiveTestingTypes.fs`. Parallel with 3.
5. `tests.scm` additions and `ExpectoCases.discover` (test 6). Files: `SageFs.Core/tests.scm`, `TestTreeSitter.fs`. Needs 1.
6. `categoryOf` and its tests (3, 7), then switch `discoverLeafTests` to it.
7. Wire into `TestLocationsDetected` (one line in `SageFsApp.fs`), the sim (8), the journey (9).
8. Clients: a rendering check in VS Code (`TestCodeLensProvider.fs`, `TestDecorations.fs`) and sagefs.nvim (`annotations.lua`, `coverage.lua`) on a 500-case file; collapse a run of adjacent unchanged marks only if it is slow.
9. Docs: `how-live-testing-works.md` ("What's still rough", the Expecto paragraph), `LIVE_TESTING_GUIDE.md`, the roadmap landmark (`ExpectoCases.fs`).

First week: steps 1 to 4 in parallel (four file-disjoint owners), step 5 and 6 at the end.

## Measure first

- Does tree-sitter reach the case applications in this grammar (ionide/tree-sitter-fsharp via TreeSitter.DotNet)? Step 1 answers it. If `testCase "n" <| fun` parses as an operator expression with an unreliable shape, the fallback is a small hand scanner over the buffer for `testCase`-family identifiers followed by a string literal, with the same `SourceCase` output; the pure core does not change.
- What fraction of `SageFs.Tests` leaves are `Exact`, `NearestAncestor` and `NotInSource` once built. Recount the grep above with the real matcher. If `NearestAncestor` is above 5 percent, look at what shapes it and extend the query before shipping.
- How many cases change category under `categoryOf` versus today, and whether any flip from Unit to Integration that a person would want on every change. Run it over the whole suite and read the diff list by hand; this is the check on the I/O pattern list.
- Render cost of a 500-mark file in both editors.

## Risks and dependencies

- A mark on every case makes a long file loud. Collapsing runs of marks is a client setting.
- A false Integration category silently removes feedback. That is why the I/O list is narrow, `CategoryWhy` is shown on the row, and the default is Unit.
- Depends on nothing else in the roadmap. `run-tests-reuses-proven-results` benefits from `Body`: a test whose body reads files must not have its pass reused, and this plan produces the body per case. `test-selection-that-learns` can use the same per-case line and category as features.
- The sibling items `confirm-builds-without-restarting-the-worker` and `live-testing-numbers-on-bigger-projects` are unaffected.

## Size

M. The pure core and the category rules are days; the tree-sitter shapes are the unknown that decides whether it is one week or two.

## Decisions for Will

1. "Take categories from where you put them": I read that as the enclosing list's label and the file's path. Do you also want an explicit marker (for example a `// category: integration` comment above the case, or a naming convention in `.SageFs/config`)? Recommendation: not now. A comment is invisible to reflection and drifts; lists and files are what people already do.
2. Drop the whole-name `Contains "integration"` check for Expecto? Recommendation: yes. It misfires on case titles, and the list label check covers the real use.
3. Should a case that is `NearestAncestor` show a mark at all, or only the parent's? Recommendation: show it, dimmed, with the reason in the tooltip; hiding a case hides a verdict.
