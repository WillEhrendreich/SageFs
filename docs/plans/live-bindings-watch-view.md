# A live bindings view you can switch and keep

Roadmap item: `live-bindings-watch-view` (Next, Dashboard). Plan written 2026-11-04 against master `7065203b`.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | L (five phases; phase 1 is M, phase 4 is L) |
| Branch and worktree | none yet |
| Last green commit | `7065203b` (no code has changed for this item; the plan is the only change) |
| Next action | Phase 1, step 1: write the failing property tests for a pure `SageFs.Core/ViewSettings.fs` (precedence, only-overrides-stored, `fitColumns`), then the module. Start "Measure first" (the Live testing spike) beside it, because it blocks only phase 5. |
| Decisions made | 2026-11-04, Will: a view is personal, and a change saves to "This app" by default. Expression rows and per-row views are wanted. "Use the current value as the initial value" is wanted. Following a call into its initializer is worth building. The kind of a value is a small glyph with the word on hover. The app-output strip collapses when no app is running. Earlier the same day, Will: colour follows the theme's semantic tokens and red means failure only. One configurable view with presets, a recommended preset for each mode, kept per app and mode. |
| Surprises | Writing the plan found five things the pane's history did not say. The repo already resolves settings through four layers, so persistence needs no new store. `PersistenceState.DiffersFromFile` stands for three different situations. `PathStep` has no step into a function body. The live walk carries no mutable-storage flag. And every built-in theme set `--fg-dim` below 4.5:1, which hid behind a test that read the stylesheet's fallback (fixed in `cfaebb0e`). |

## What done looks like

You open the Live Bindings pane in any workflow and it is a compact watch tree: one line per node, a small glyph for the kind with the word on hover, a pin that appears on hover, and no sentence repeated on every row. At the 252 px dock, ten collapsed bindings show at least eight rows, where the pane today shows two. The recommended view for each workflow is already on screen. You can switch to another preset, turn single settings on and off, and the choice is kept for that app and that workflow until you reset it. Colour follows the theme: the title is a panel heading, and red appears only for a failure.

For tweaking, every row says in one word how its live value relates to the source: `source`, `tweaked`, `formula`, `evolved` or `live only`. Save says what it writes ("Save 9.5 to Tuning.fs:5"). A value the program has changed since it was created never offers a plain Save, and offers two explicit actions: reset it, or use the current value as the new initial value, after a confirmation that names the consequence. A value built by a call (`let mutable player = initPlayer ()`) points at the literal inside the function that builds it, and says how many callers that literal also feeds.

Watch tabs hold expression rows and per-row views, behind the same containment as the held rows. While the test loop runs, the pane shows what the loop knows: whether the values are current with the last eval, what moved since the last green run, and the value of the expressions you pinned, re-evaluated each time the loop settles.

The outcome gate for each phase is a Playwright journey in the `--integration-browser` tier plus the property tests and simulation named under "Tests". None is skipped in any cell.

## What exists today

Everything below was checked against the tree on 2026-11-04.

- **The dock.** `SageFs/LiveBindingsDock.fs` (495 lines) draws every field on every row: a kind badge, the type, a 28 px pin, and the sentence "Bound in the REPL. No file of this session holds a literal, a record field or a formula by this name, so there is nothing to save it to." On daemon 0.6.937 at 1440 by 900 the dock is 252 px, the tree 159 px, each collapsed binding 65 px, and 2 of 10 bindings are in view. The sentence is on 10 of 10 rows. One opened record of 13 nodes has 13 kind badges and 13 type cells and is 302 px tall. The dock has no workflow logic; its only mention of hot reload is a comment about the tooling's own binding names.
- **Colour.** `.live-pane-title` is `--fg-red` (`SageFs/dashboard.css:1320`), the title text carries a red circle emoji (`LiveBindingsDock.fs:202` and `:401`), and a pinned row is tinted yellow (`.live-pinned`, `.session-btn-on`), which is the warning colour. Panel headings elsewhere are `--fg-blue`. Every preset in `SageFs/ThemePresets.fs` has a syntax colour equal to a semantic one: strings equal pass in six themes and warn in two, keywords equal fail in Gruvbox and Monokai. State therefore needs a word beside its dot.
- **What a row knows.** `SageFs.Core/Features/Tweak/BindingTweak.fs` has a closed `PersistenceState` (`InSource`, `NotInAFile`, `Held`, `Derived`, `Ambiguous`, `DiffersFromFile`, `StaleAddress`, `Refused`, `Writing`). `DiffersFromFile` is documented as "a file declares it, and the REPL holds another value. Nothing here knows which came last", so it covers a tweak, a change by the program and a REPL rebind alike. The file's own note on stage 2 names the missing states: a tweak the file does not hold yet, a saved tweak, and a value the app copied at startup. A row is tied to source by NAME only (`SourceIndex`), and `SourceFacts.PartNotSpelled` is the answer for a record field filled by a call.
- **The address model.** `SageFs.Core/Features/Tweak/TweakAddress.fs` has `PathStep` cases `RecordField`, `TupleItem`, `ListItem`, `AppArg`, `IfCond`, `IfThen`, `IfElse`, `BinOpLeft` and `BinOpRight`. There is no step into a function body, so a call cannot be followed today.
- **The walk.** `SageFs.Core/Features/LiveValueTree.fs` has `NodeKind` cases for leaves, records, lists, maps, options, unions, tuples, arrays, classes, closures, cycles, truncation and not-evaluated. It carries no flag for mutable storage.
- **Kept values.** `SageFs.Core/Features/ReloadOutcome.fs` has `KeptValue { Binding; KeptValue; NewInitializer }`. The Hot Reload sidebar panel lists them, apart from the binding's row.
- **Settings.** `SageFs.Core/Settings.fs` has `ConfigLayer` (`LDefault`, `LGlobal`, `LRepo`, `LSession`), a per-setting `SettingScope` (`Global`, `RepoOverridable`, `SessionOnly`), `Provenance` (the effective value, its layer, and the value at each layer) and `SettingDescriptor`. `SettingValue` is a closed set: `VToggle`, `VPort`, `VTimeout`, `VBindHost`, `VEnum`, `VDir`. `SageFs.Core/SettingsStore.fs` reads a global `settings.json` and `<repo>/.SageFs/settings.json`. `.SageFs/` is git-ignored here (`.gitignore:55`). `SageFs/SettingsPanel.fs` renders the resolved rows. The dashboard keeps only the dock height and the sidebar width in the browser (`localStorage`).
- **Modes.** `SageFs.Core/WorkflowTypes.fs` has `SessionWorkflow` (`Interactive`, `LiveTesting`, `HotReload`). `docs/workflow-modes.md` says Live testing adds nothing to the REPL itself, and the dashboard handles `WorkflowSwitching` and `WorkflowSwitched` events, so a session can change workflow while someone looks at it.
- **Live testing.** `SageFs.Core/Features/LiveTestActivity.fs` has `LiveTestActivity`: `Off`, `Discovering`, `DiscoveryFailed`, `NoTestsFound`, `BlockedByCompileErrors` (file, error count, last results), `BlockedByFailedRebuild`, `Rebuilding`, `Running` and `Settled`. `POST /api/sessions/{sid}/buffer-changed` dispatches `BufferContentChanged (session, file, content)` to the daemon's update loop. `explain_test_failure` and `coverage_intel` exist as MCP tools; `coverage_intel` needs the instrumented runs. I did not read whether the update loop records "the buffer is ahead of the last eval", which is why it is a measurement.
- **App output.** `SageFs/AppOutputView.fs` draws a strip that is 35 px of the dock when no app is running.
- **Contrast.** Fixed on master in `cfaebb0e`: all eight presets' `--fg-dim` reaches 4.5:1 on the page, the panel and the selection, held by `DashboardAccessibilityTests`.
- **RAD Debugger** (the 0.9.16-alpha release notes, read on 2026-11-04). Rows are expressions with three default columns (expression, value, type). There are several Watch tabs with their own expressions and per-tab settings. Views are written like function calls, such as `dynamic_array.slice().hex()`. A `range1` view is a slider for a bounded scalar. Type views, such as `raddbg_type_view(DynamicArray<?>, slice($))`, are declared in source or in config. Config is text at user and project scope. The notes say nothing about splitting panels or layout files, so this plan claims neither.

## Design

### The view is a handful of settings

The four layouts the roast produced differ along independent axes, so they are presets of one component.

| Axis | Values |
|:--|:--|
| Type column | on, off |
| State column | on, off |
| Source column | on, off |
| Group by | none, origin |
| Pinned strip | on, off |
| Inspector | on, off |
| Values | clipped, full |
| Row height | 20 px, 26 px |

Presets set the first six and leave the last two alone, because value printing and row height are personal. The presets are Watch window (Type on, the rest off), Tree and inspector (Inspector on, the rest off), Pinned strip, Grouped by origin, and Tuner (Type, State, Source, strip and inspector on). The recommended preset for REPL and Live testing is the Watch window, and for Hot reload it is the Tuner.

An axis is a setting. A `ViewAxis` DU and the `SessionWorkflow` DU generate 8 x 3 = 24 `SettingDescriptor`s from two closed sets, with one exhaustive key function (`bindings.view.<workflow>.<axis>`), as the house rule for closed sets of strings asks. Every axis is an `Enum` or a `Toggle`, so no new `SettingValue` kind is needed in phase 1. The scope is `RepoOverridable`. A change saves to "This app" (the repo layer) by default, with "All apps" (global) and "This session only" one click away. The existing resolver does the layering, so only overrides are stored, and an axis nobody touched follows the recommendation when it improves. "Reset this setting" removes the layer that supplies it, and "Reset this app and mode" removes the repo and session layers for that workflow.

The pure core is a function from the resolved axes and a pane width to the columns shown. Columns give way before the value does: when the pane is too narrow, Type goes first, then Source, then State, and the view says which it hid. The Value column has a minimum of 140 px, or 320 px in Full mode, which makes columns give way sooner. The mockup found this by testing, because five columns and the inspector squeezed the value to nothing.

### One line per row, and what it carries

- The kind is a glyph of about 11 px, with the word in the hover text and in the accessible name. The shape says what a node is, so the glyph takes no colour of its own.
- The pin is a 12 px icon that shows on hover and when pinned. A pinned row does not move. With the strip on, pins live in the strip.
- The REPL-only reason is one chip in the title bar, or a group header in the grouped preset. It is not repeated on rows.
- The title is a panel heading (`--fg-blue`) with no emoji. The pane's one state is a dot from the semantic tokens plus a word: current (`--color-pass`), stale, paused or kept (`--color-warn`), changed since the last eval (`--color-info`), a refused or failed write (`--color-fail`) with its reason and next step, idle (`--fg-dim`). Selected and pinned use `--bg-selection` and `--border-focus`, never yellow.
- The app-output strip collapses to nothing when no app is running, and returns when an app starts. The one exception this plan keeps, flagged under "Decisions for Will", is a run that exited non-zero: its last output stays until dismissed.

### Tweaking: five relations and what Save writes

Add `Tweaked` and `Evolved` to `PersistenceState`, so the one exhaustive render stops compiling until it draws them. A row is exactly one of:

| Relation | Meaning | What Save writes |
|:--|:--|:--|
| `source` | Live equals the literal in the file. | Nothing to save. |
| `tweaked` | Live equals the value last pushed, and the file still holds the old literal. | That value, into exactly that literal. |
| `formula` | The file spells it as an expression such as `baseCoyote * 1.5`. | Either the literal inside the formula (the link stays) or a plain number that replaces it (the link breaks, and the button says so). |
| `evolved` | The program changed it after it was created, so live is neither the literal nor anything pushed. | No plain Save. Reset to the initial value, or use the current value as the initial value after a confirmation. |
| `live only` | No file expression stands for it. | Nothing. Save is off and says why. |

This needs three things the daemon does not hold today: the value last pushed into each row (a field on `Memory` in `BindingTweakRows.fs`), a flag for mutable storage on the walk, and, for a value built by a call, the literals inside the callee (phase 3). With the pushed value, `tweaked` is exact, and a row whose live value later moves away from it becomes `evolved` with the note that the program overtook the tweak.

"Use the current value as the initial value" writes the literal where the value was created. Its confirmation reads, for example, "Write State.fs:14 in initPlayer so the program starts with Health = 62? The code that changed it still runs, so it can move again.", and when the literal is inside a function it adds the caller count ("2 callers use this literal"). The write goes through the nudge door (`Nudge.fs`), so it has the door's address check, its transaction and its undo trail.

A strip on top shows the number of unsaved tweaks and the number of values the program changed, with the tweaks listed as before and after per file. Save all and Revert all act on tweaks only. A tweaked row has a warn-coloured bar and tint on its left edge, and an evolved row has an info-coloured bar with no tint.

### Following a call into its initializer

Add one address step, `CallResult`: from an application in a binding's expression, follow the call to the callee's body and continue through the result expression. The link is the NAME, matched in the same `SourceIndex` the rows use, and it never guesses.

- Follow only when the callee is a top-level function declared exactly once in the session's own files. Zero declarations, two declarations and a file that cannot be read are three different answers, as `SourceFacts` already has.
- Follow only to a result expression that is a literal, a record, a tuple or a list of tweakable literals. A literal that depends on a parameter is refused and the refusal names the parameter. Until the follow works, `PartNotSpelled` stays the honest answer.
- Every caller of the callee feeds from the same literal, so a write changes all of them. The row counts the callers by scanning the parsed files for applications of that name, and the consequence line says so.
- The write is one literal in the callee, through the same door.

### Watch tabs, expression rows and per-row views

A watch tab is a list of rows and its own view settings, as in RAD Debugger. Built-in tabs are queries over the bindings: All, Pinned and Changes (the tweaks and the evolved values). A tab of your own holds expression rows.

- A new `SettingValue` kind carries a tab's expressions, built through a smart constructor that refuses an expression that does not parse, so an illegal expression cannot be stored. Tabs are personal, like views, and live in the same layers.
- An expression is evaluated by the worker under the containment the held rows use (`MemberEvaluation`, `GuardOutcome`). In Safe mode a row made only of names and field access reads values without running your code, and a row that calls a function is held until you click it, as a held getter is today. In Everything mode a row may re-evaluate by itself.
- Per-row views are a closed `RowView` DU with one exhaustive formatting function: Hex, Binary, Colour and `Slider of lo * hi` first, the slider being the tweak knob for a bounded value. The first version is a picker on the row. A text form like RAD's chained calls waits for a decision.

### While the test loop runs

The loop already knows more than the pane shows. The plan has three parts, in the order they should be built, and a spike under "Measure first" that decides the fourth.

1. **Freshness follows the loop.** The pane's state chip mirrors `LiveTestActivity`: `Running` and `Rebuilding` read as in progress, `Settled` as current, and `BlockedByCompileErrors` as "values from the last good eval, `File.fs` has 2 errors" in the warn colour. If the update loop can tell that the live buffer is ahead of the last eval, the chip says "as of eval 14, buffer ahead".
2. **Since green.** The daemon keeps a snapshot of the walk at each `Settled` with no failures. A tab, "Since green", lists the bindings whose value differs from that snapshot, with the old value beside the new. This answers "what moved when my test went red?", which is the question the loop raises.
3. **Spies.** An expression row, pinned, re-evaluates each time the loop settles, not on each keystroke, and shows the previous value beside the new one. This is the REPL inside the loop: `normalize "  Foo "` stays on screen while you edit `normalize`. It needs phase 4 and a known answer to which environment the expression runs in.
4. **Failure context** (decided by the spike). A group of the failing tests with their failure text from `explain_test_failure`, and the bindings in the files those tests cover, if `coverage_intel` data is available at a useful grain.

## Tests

RED first, each failing before its code exists, in the default suite unless it says otherwise.

1. `ViewSettingsTests.fs`: for any stack of layers, the highest layer that sets an axis wins, and a setting equal to the layer below is not stored. Every (workflow, axis) pair has exactly one descriptor and one distinct key. A property over widths and column sets: `fitColumns` never hides Value, hides Type, then Source, then State, and never shows a set whose minimums exceed the width.
2. `LiveBindingsViewBrowserTests.fs` (`--integration-browser`): ten bindings in a 252 px dock show at least eight rows in view. The title is not red in any of the eight presets (read from the computed colour against `--fg-blue`). A state dot always has a word beside it. A setting changed under "This app" survives a reload for that app and workflow and does not appear for another app. With no app running the output strip is absent.
3. `BindingRelationTests.fs`: for the five relations, a table of (live, file literal, last pushed, storage mutable) to the relation, with the property that `evolved` never offers a plain Save. A twin that derives `evolved` from `DiffersFromFile` alone must fail it.
4. `TweakerVersusProgramSim.fs` in `SageFs.Simulation`: a seeded run of a program changing mutable fields, a tweaker pushing, saving and reverting, and file edits, where a saved tweak always equals what the file holds and an evolved value is never written without a confirmation. It replays by seed, and a twin that lets the program's change count as a tweak must fail it.
5. `CallResultTests.fs`: golden F# files for a callee found, two callees of one name, a callee in an unreadable file, a literal that depends on a parameter, and a callee with three callers. The write changes exactly one literal and the caller count is right.
6. `WatchExpressionTests.fs`: in Safe mode an expression that calls a function is held and never runs; a property that an expression that does not parse cannot be constructed; `RowView` formatting round-trips for Hex and Binary over any integer.
7. `LoopFreshnessTests.fs`: each `LiveTestActivity` case maps to one chip text and one semantic colour, exhaustively. The "Since green" snapshot is replaced only by a settle with no failures.
8. Mutation: `fitColumns` with the order of the hidden columns swapped, the precedence function with two layers swapped, and `evolved` derived from the wrong comparison.

The simulation is item 4 and, for the settings stack, item 1 as a property, because the resolver is pure. The expression evaluation reuses the worker's own containment tests. No wall-clock waits: the browser journeys wait on frames and state changes, as the existing journeys do.

## Steps

**Phase 1: the view (M).** Commit each test red with its step number, then the change that turns it green.
1. `SageFs.Core/ViewSettings.fs` (pure) with test 1, and the 24 descriptors in the settings catalog. Prove the resolver and `fitColumns` in a SageFs session before persisting.
2. Rewrite `LiveBindingsDock.fs` to draw from the resolved view: one line per row, the glyph, the hover pin, the chip, the tokens, the collapsed app-output strip. Test 2. The two dock journeys (`DashboardLiveBindingsDockBrowserTests.fs`, `DashboardLiveBindingsTweakBrowserTests.fs`) change in the same commit.
3. The view panel in the pane bar (a gear), writing through the existing settings handlers, with the scope menu defaulting to "This app", per-axis reset and "Reset this app and mode". Wire surface: none new if the settings handlers carry it; if a route or payload changes, the commit carries `Plugin:`.

**Phase 2: the tweak states (M to L).**
4. `Tweaked` and `Evolved` in `PersistenceState`, the pushed-value record in `Memory`, the mutable flag on the walk. Tests 3 and 4. The walk's payload changes, so the commit carries a `Plugin:` trailer, and sagefs.nvim and the VS Code extension are checked for readers of the live-values payload first.
5. The Changes strip, the State and Source columns, the Save labels, and "use the current value as the initial value" with its confirmation.

**Phase 3: follow the call (M).**
6. `PathStep.CallResult` and its resolver, with test 5. The caller count. The consequence line.

**Phase 4: watch tabs, expressions and row views (L).**
7. The built-in tabs (All, Pinned, Changes), each with its own view settings.
8. The new setting kind for a tab's expressions, the evaluation under containment, and test 6.
9. `RowView` and the picker. Then, after the decision below, a text form.

**Phase 5: the test loop (M).** Run the spike first.
10. Freshness follows the loop, with test 7.
11. "Since green".
12. Spies on top of phase 4, and failure context if the spike supports it.

Phases 1 and 2 can overlap after step 1, because the view and the relation states touch different files. Phase 3 follows phase 2. Phase 5 steps 10 and 11 need only phase 1.

## Measure first

The spike answers four questions about the test loop. It takes a day, it changes no product code, and the answers go in this plan. It blocks phase 5 only.

1. Does the update loop keep "the buffer is ahead of the last eval"? Read the handler of `BufferContentChanged` and what it stores. If it does not, step 10 says "as of eval N" and the buffer-ahead chip waits.
2. Which environment does a test run against while the loop is live, and can an expression be evaluated there after a settle? If the loop runs the buffer's compiled form and the REPL holds the last eval, a spy would show values from the wrong one, and step 12 has to say which.
3. What does `explain_test_failure` return for a test that went from passing to failing, and does it carry the expected and the actual value?
4. At what grain does `coverage_intel` map a failing test to source, and can that be joined to the bindings of a file?

After the spike, use the "Since green" tab and the freshness chip ourselves for two weeks on real work, note which tab stays open, and let that decide whether spies and failure context are built.

## Risks and dependencies

- The live-values payload gains fields. Editors that read it need a reader first, and the plugin lands first.
- Following a call is only as good as the parse. A callee written with a pipeline or a computation expression is refused with a reason, which is the right default and may refuse more than people expect. The first real use will say.
- Expression rows run code. They stay behind the held-row containment, and the first version does not skip it.
- A view saved for "This app" is personal and git-ignored. A layout a whole team should share needs a committed file of its own, which is a later decision.
- Phase 2 touches the same files as `docs/plans/more-settings-editable.md` (the Settings panel). The view keys appear in that panel for free, and the two plans should not both rewrite `SettingsPanel.fs` at once.
- `docs/plans/move-and-dock-dashboard-panes.md` is about where panes sit. This plan is about what is in one pane. They meet at the pane bar's gear and nowhere else.

## Size

L overall. Phase 1 is about a week, phase 2 a week and a half, phase 3 a week, phase 4 two to three weeks, phase 5 a week after the spike. These are estimates, and only phase 1 has been looked at in enough detail to trust.

## Decisions for Will

- Answered 2026-11-04: a view is personal. A change saves to "This app" by default. Expression rows and per-row views are wanted. "Use the current value as the initial value" is wanted. Following a call into its initializer is worth building. The kind is a small glyph with the word on hover. The app-output strip collapses when no app is running.
- Open: a run that exits non-zero is the one time the app's output is useful after the app stops. The plan keeps that output until dismissed. If you want it gone anyway, say so.
- Open: the text form of a per-row view. A picker on the row ships first. RAD Debugger writes views as chained calls (`value.hex()`), and F# has its own pipeline, so the text form is a design question for after the picker has been used.
- Open: in Safe mode, an expression that calls a function is held until you click it, as a held getter is. The alternative is to refuse calls in Safe mode altogether. The plan takes the first.
- Open, for the Live testing loop: what you look at while the loop runs. The plan builds freshness and "Since green" because the loop's own data supports them, and decides on spies and failure context after two weeks of use.
