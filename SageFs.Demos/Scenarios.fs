/// The Phase-1 smoke scenario (demo-gif-plan.md §10 Phase 1): dashboard only
/// — open a REAL sample project (not a bare Quick Start temp session, which
/// has nothing to auto-open and nothing to eval against), watch it warm up
/// to Ready with the project's own source files/assemblies visibly scanned,
/// then evaluate an expression that reads the project's OWN mutable state
/// and watch the result land. Built the same way the worked hero example in
/// §6.1 is: a plain F# value, no strings for closed sets, each step a
/// caption/action/expectation/dwell.
///
/// This is deliberately a real "watch SageFs open and evaluate a real
/// project live" demo, not just "a session card appears" (the session-card
/// testid fires while the session is still `WarmingUp`, so a scenario that
/// stopped there recorded nothing interesting — §2's fix) and not just
/// "evaluate arithmetic on a session with nothing loaded" (§10's fix: Quick
/// Start creates a bare temp session with zero source files, so warmup
/// visibly complains "there was nothing to auto open" and an eval like
/// `[1..10] |> List.sum` proves nothing about the product's actual
/// capability — loading a real project's code and evaluating against it).
module SageFs.Demos.Scenarios

open SageFs.Demos.Domain

/// The sample this scenario opens as a REAL project — resolved to an
/// absolute path by `Runtime.fs` (the one place with a `repoRoot`) via
/// `Text.RepoRootToken`, never a literal path baked in here (§10).
let private sample = Sample.WebappDatastar

/// The "Open Directory" picker card's plain `<input>` (no `data-testid`,
/// bound via Datastar's `data-bind:newSessionDir` — `DashboardFragments.fs`)
/// — the ONLY text input on the "Start a Session" picker page, so this
/// unqualified class selector has no ambiguity to resolve.
let private newSessionDirSelector = ".picker-form input"

/// The "Open Directory" card's own "Create" button (`Ds.post
/// "/dashboard/session/create"`) — no `data-testid`, so `:has-text` (a
/// Playwright CSS extension, not a new testid added to the daemon's own
/// dashboard) picks it out from the "Discover" button next to it.
let private createSessionButtonSelector = ".picker-form button:has-text(\"Create\")"

/// The eval textarea has no `data-testid` (a plain `id="eval-textarea"`) —
/// `Target.DashboardCssSelector` is the documented, deliberate escape hatch
/// for exactly this case (`Domain.fs`), used instead of adding a testid to
/// the daemon's own dashboard for one demo-only target.
let private evalTextareaSelector = "#eval-textarea"

/// The daemon's own session-status label (`SessionState.label`) — "Ready"
/// once warmup finishes. A narrower `#session-status .status` selector was
/// tried first and measured directly against a real recording: the still
/// frame at the exact moment `WaitForSelectorAsync` timed out ALREADY showed
/// "[Ready]" plainly on screen, so that selector's assumed DOM shape doesn't
/// match reality closely enough to resolve reliably. `body:has-text("Ready")`
/// is deliberately unscoped — Playwright's `:has-text` normalizes whitespace
/// and is case-insensitive, so it matches the visible text directly rather
/// than depending on exact container structure — and nothing else on this
/// page ever renders the substring "Ready".
let private sessionStatusSelector = "body"

let private outputPanelSelector = "[data-testid=session-output]"

/// The "Evaluate" panel is a native HTML `<details id="evaluate-section">`
/// COLLAPSED by default — confirmed directly against a real recording: a
/// still frame taken right where step 3's eval click should have landed
/// showed "▸ Evaluate" still closed and "0 evals", meaning the click/type
/// never reached a visible, interactable box. A native `<details>` toggles
/// only when its `<summary>` child is clicked (clicking elsewhere in the
/// element does nothing), so the selector targets that summary specifically
/// — `#evaluate-section summary`, a real (read-only-confirmed) id already
/// in the dashboard's markup, not a new testid added for this task, per its
/// own constraint to leave the daemon's dashboard files alone. (A first
/// attempt used Playwright's `text=Evaluate` engine; it failed because the
/// same substring also appears in an unrelated "Evaluate code" keyboard-
/// shortcut hint elsewhere on the page, triggering a strict-mode multiple-
/// match failure — the precise CSS selector below has no such ambiguity.)
let private evaluateAccordionSelector = "#evaluate-section summary"

/// The live-testing panel's own container (`DashboardTypes.fs`'s
/// `DomIds.LiveTestingPanel = "live-testing-panel"`, a real `id` already in
/// the dashboard's markup) — scoped so "ON"/"✓" checks read THIS panel, not
/// some other part of the page.
let private liveTestingPanelSelector = "#live-testing-panel"

let helloDashboard: Scenario =
  { Id = ScenarioId.ofRaw "hello-dashboard"
    Capability = Capability.Sessions
    Client = Client.Dashboard
    App = AppKind.NoApp
    Sample = sample
    Layout = LayoutTemplate.DashboardOnly
    Steps =
      // §10: type the sample project's real, absolute directory into the
      // "Open Directory" picker card, then click "Create" — the daemon
      // auto-discovers the lone .fsproj in that directory with no
      // ManualProjects signal needed (`resolveSessionProjects`,
      // `DashboardTypes.fs`). Not Quick Start: a Quick Start session has NO
      // project, so warmup has nothing to open (visibly logging "Auto-open
      // was enabled but no source files were found") and nothing real to
      // eval against.
      [ { Caption = Caption.mk "1/3 · Open a real F# project"
          Action =
            Action.TypeThenClick(
              Target.DashboardCssSelector newSessionDirSelector,
              Text.mk (sprintf "%s/%s" Text.RepoRootToken (Sample.relativePath sample)),
              CadenceSeed.ofId "hello-dashboard-open-project",
              Target.DashboardCssSelector createSessionButtonSelector
            )
          // A real, non-empty project's warmup actually scans its source
          // files — "Scanned 1 source files" (`AppState.fs`'s own literal
          // wording) is a genuine, structural proof the project loaded
          // (never fires for a bare Quick Start session, which scans 0),
          // not just "a session card appeared" (§2's already-fixed
          // nothingburger: that testid fires during `WarmingUp` too).
          Expect = Expectation.PageTextContains(outputPanelSelector, "Scanned 1 source files")
          Dwell = Dwell.short }
        // Creating a session does NOT switch the dashboard's main panel to
        // that session's own view — it stays on the "Start a Session"
        // picker until a session card is actually CLICKED (confirmed
        // directly against a real recording: a still frame taken well after
        // the tabline read "[Ready]" still showed the picker, with no eval
        // box on screen at all). Clicking the card here navigates into the
        // session's own view, which step 3 needs.
        { Caption = Caption.mk "2/3 · It warms up and goes green"
          Action = Action.Click(Target.DashboardElement DashboardId.SessionCard)
          Expect = Expectation.PageTextContains(sessionStatusSelector, "Ready")
          Dwell = Dwell.long }
        // The "Evaluate" accordion's open/closed state does NOT survive the
        // dashboard's own server-driven full-panel re-render (confirmed
        // directly: expanding it as its own step, well before typing, still
        // left it re-collapsed by the time the typing step ran) — a real,
        // separate dashboard defect another agent is fixing concurrently
        // upstream, out of scope here (and this task's own constraint keeps
        // this demo out of the daemon's dashboard files). The workaround:
        // `ClickThenTypeThenClick` folds "expand the accordion", "type the
        // expression", and "click Eval" into ONE step with NO step boundary
        // between the expand and the type — the only gap a re-render could
        // land in and undo the expand. The expression itself reads the
        // sample project's OWN mutable state
        // (`SageFs.Samples.WebappDatastar.Program.todos`) — proof the
        // assemblies genuinely loaded, not arithmetic that would work
        // identically on an empty session. Fully qualified, not bare
        // `todos`: confirmed directly against a real recording that
        // warmup's auto-open only opens REFERENCED PACKAGE namespaces
        // (Falco/Falco.Routing/Falco.Markup/Falco.Datastar/
        // Microsoft.AspNetCore.Builder, all real, all genuinely opened) —
        // never the current PROJECT's OWN compiled module, so a bare
        // `todos` faulted the eval ("not defined").
        { Caption = Caption.mk "3/3 · Eval your own code — instant result"
          Action =
            Action.ClickThenTypeThenClick(
              Target.DashboardCssSelector evaluateAccordionSelector,
              Target.DashboardCssSelector evalTextareaSelector,
              Text.mk "SageFs.Samples.WebappDatastar.Program.todos.Length",
              CadenceSeed.ofId "hello-dashboard-eval",
              Target.DashboardElement DashboardId.Eval
            )
          // "int = 3" (not a bare "3") — deliberately specific: the output
          // panel already prints "[3/4] Scanned assemblies..." during
          // warmup, so a bare "3" would trivially, falsely pass before the
          // eval ever ran. "int = 3" only ever appears in FSI's own
          // "val it: int = 3" result line.
          Expect = Expectation.PageTextContains(outputPanelSelector, "int = 3")
          Dwell = Dwell.long } ]
    Cost = CostClass.web
    Masks = [] }

/// The dashboard's own RESET button (`DashboardFragments.fs`'s `testid
/// "reset"`) — re-warms the SAME session from scratch, reusing the shared
/// steps below.
let private resetButtonSelector = "[data-testid=reset]"

/// `sessions-dashboard` (§6's matrix: `Sessions.dashboard`) — a proper
/// matrix-derived scenario distinct from the throwaway `hello-dashboard`
/// smoke test (§6.1's own note: "never one of the matrix values"). Reuses
/// the exact proven open-project/reach-Ready mechanics, then demonstrates
/// the Sessions capability itself — resetting a live session and watching it
/// warm back up to Ready — rather than repeating hello-dashboard's eval beat.
let sessionsDashboard: Scenario =
  { Id = ScenarioId.derive Capability.Sessions Client.Dashboard AppKind.NoApp
    Capability = Capability.Sessions
    Client = Client.Dashboard
    App = AppKind.NoApp
    Sample = sample
    Layout = LayoutTemplate.DashboardOnly
    Steps =
      [ { Caption = Caption.mk "1/3 · Open a real F# project"
          Action =
            Action.TypeThenClick(
              Target.DashboardCssSelector newSessionDirSelector,
              Text.mk (sprintf "%s/%s" Text.RepoRootToken (Sample.relativePath sample)),
              CadenceSeed.ofId "sessions-dashboard-open-project",
              Target.DashboardCssSelector createSessionButtonSelector
            )
          Expect = Expectation.PageTextContains(outputPanelSelector, "Scanned 1 source files")
          Dwell = Dwell.short }
        { Caption = Caption.mk "2/3 · The session warms up and goes green"
          Action = Action.Click(Target.DashboardElement DashboardId.SessionCard)
          Expect = Expectation.PageTextContains(sessionStatusSelector, "Ready")
          Dwell = Dwell.medium }
        // §9's own Sessions story: a live session can be reset from scratch
        // right from the dashboard. Confirmed directly against a real
        // recording: RESET is a soft reset (clears eval history/state on the
        // SAME warm FSI process, not a fresh cold warm-up), and the
        // dashboard's own literal confirmation text is "Reset: Session reset
        // successfully" — not a repeat of the "[4/4] Warm-up complete" line,
        // which never reappears for a soft reset.
        // Confirmed directly against a real recording: the confirmation line
        // does NOT land inside `[data-testid=session-output]` (reset replaces
        // that panel's whole subtree, clearing scrollback) — it shows up in
        // the statusline instead. `body` (already proven for the "Ready"
        // check above) catches it wherever it actually renders.
        { Caption = Caption.mk "3/3 · Reset the session"
          Action = Action.Click(Target.DashboardCssSelector resetButtonSelector)
          Expect = Expectation.PageTextContains(sessionStatusSelector, "Session reset successfully")
          Dwell = Dwell.long } ]
    Cost = CostClass.web
    Masks = [] }

/// `repl-dashboard` (§6's matrix: `Repl.scenario Client.Dashboard`) — shows
/// the REPL capability as genuine back-and-forth: two separate evaluations
/// against the SAME live session, one reading the project's own state (proof
/// assemblies are really loaded, exactly hello-dashboard's own reasoning)
/// and one general expression — an agent/human iterating at a live prompt,
/// not a single one-shot eval.
let replDashboard: Scenario =
  { Id = ScenarioId.derive Capability.Repl Client.Dashboard AppKind.NoApp
    Capability = Capability.Repl
    Client = Client.Dashboard
    App = AppKind.NoApp
    Sample = sample
    Layout = LayoutTemplate.DashboardOnly
    Steps =
      [ { Caption = Caption.mk "1/4 · Open a real F# project"
          Action =
            Action.TypeThenClick(
              Target.DashboardCssSelector newSessionDirSelector,
              Text.mk (sprintf "%s/%s" Text.RepoRootToken (Sample.relativePath sample)),
              CadenceSeed.ofId "repl-dashboard-open-project",
              Target.DashboardCssSelector createSessionButtonSelector
            )
          Expect = Expectation.PageTextContains(outputPanelSelector, "Scanned 1 source files")
          Dwell = Dwell.short }
        { Caption = Caption.mk "2/4 · It warms up and goes green"
          Action = Action.Click(Target.DashboardElement DashboardId.SessionCard)
          Expect = Expectation.PageTextContains(sessionStatusSelector, "Ready")
          Dwell = Dwell.medium }
        { Caption = Caption.mk "3/4 · Evaluate a plain expression"
          Action =
            Action.ClickThenTypeThenClick(
              Target.DashboardCssSelector evaluateAccordionSelector,
              Target.DashboardCssSelector evalTextareaSelector,
              Text.mk "List.sum [ 1 .. 10 ]",
              CadenceSeed.ofId "repl-dashboard-eval-1",
              Target.DashboardElement DashboardId.Eval
            )
          Expect = Expectation.PageTextContains(outputPanelSelector, "int = 55")
          Dwell = Dwell.medium }
        // Same live session, second eval — the REPL keeps state (a real
        // `it`-style prompt), so this reads the PROJECT's own mutable state
        // right after a totally unrelated expression, proving the session
        // never restarted between the two evals.
        { Caption = Caption.mk "4/4 · Evaluate the project's own state"
          Action =
            Action.ClickThenTypeThenClick(
              Target.DashboardCssSelector evaluateAccordionSelector,
              Target.DashboardCssSelector evalTextareaSelector,
              Text.mk "SageFs.Samples.WebappDatastar.Program.todos.Length",
              CadenceSeed.ofId "repl-dashboard-eval-2",
              Target.DashboardElement DashboardId.Eval
            )
          Expect = Expectation.PageTextContains(outputPanelSelector, "int = 3")
          Dwell = Dwell.long } ]
    Cost = CostClass.web
    Masks = [] }

/// `lt-dashboard` (§6's matrix: `LiveTesting.scenario Client.Dashboard
/// Sample.FromCSharp`) — opens the real `SageFs.Samples.FromCSharp` project
/// (a genuine Expecto test suite, `Hello.fs`'s ten real, currently-passing
/// tests), turns live testing ON via the dashboard's own
/// `data-testid=live-testing-toggle` (`DashboardFragments.fs`'s
/// `renderLiveTestingPanel`), and watches the panel's own header move from
/// "OFF" to "ON" and then show a real passed count — SageFs discovering and
/// running the project's actual tests, not a canned status string.
///
/// NOT YET PASSING (recorded but NOT published to the gallery — the job's own
/// "don't fake it" rule): steps 1-3 pass genuinely (real project opens, warms
/// up with Expecto/Expecto.Flip opened, live testing flips to "ON", and
/// `SageFsApp.fs`'s `EnableLiveTesting` handler does dispatch
/// `RequestInitialDiscovery` + `RegisterFileWatcher`), but step 4 — waiting
/// for a passed-test checkmark in `#live-testing-panel` — times out at 90s on
/// every attempt (reproduced twice). The cell's own `daemon.log` shows
/// "Registered file watcher" but NO discovery-related log line ever appears,
/// so discovery is requested but never observably completes for a project
/// opened directly through the dashboard's "Open Directory" picker (as
/// opposed to a project SageFs already knows as "runnable"). Root-causing
/// this further means reading `LiveTestingExecutors`/`SageFsApp`'s discovery
/// effect handlers in `SageFs.Core`/`SageFs`, which is real product logic
/// beyond this demo tool's own scope (`AGENTS.md`: don't touch product code
/// beyond what a scenario needs) — left here, still wired to `record
/// lt-dashboard` for whoever picks this up, with the concrete evidence above
/// instead of a workaround that would silently pass without a checkmark ever
/// appearing.
let ltDashboard: Scenario =
  { Id = ScenarioId.derive Capability.LiveTesting Client.Dashboard AppKind.NoApp
    Capability = Capability.LiveTesting
    Client = Client.Dashboard
    App = AppKind.NoApp
    Sample = Sample.FromCSharp
    Layout = LayoutTemplate.DashboardOnly
    Steps =
      [ { Caption = Caption.mk "1/4 · Open a real test project"
          Action =
            Action.TypeThenClick(
              Target.DashboardCssSelector newSessionDirSelector,
              Text.mk (sprintf "%s/%s" Text.RepoRootToken (Sample.relativePath Sample.FromCSharp)),
              CadenceSeed.ofId "lt-dashboard-open-project",
              Target.DashboardCssSelector createSessionButtonSelector
            )
          Expect = Expectation.PageTextContains(outputPanelSelector, "Scanned")
          Dwell = Dwell.short }
        { Caption = Caption.mk "2/4 · It warms up and goes green"
          Action = Action.Click(Target.DashboardElement DashboardId.SessionCard)
          Expect = Expectation.PageTextContains(sessionStatusSelector, "Ready")
          Dwell = Dwell.medium }
        { Caption = Caption.mk "3/4 · Turn live testing on"
          Action = Action.Click(Target.DashboardElement DashboardId.LiveTestingToggle)
          Expect = Expectation.PageTextContains(liveTestingPanelSelector, "Live Testing: ON")
          Dwell = Dwell.medium }
        // No click — just watch the same live session actually discover and
        // run the project's real Expecto tests (§9's "Await" pattern: a step
        // with no click/type action still waits out its own expectation).
        { Caption = Caption.mk "4/4 · SageFs runs the real tests — they pass"
          Action = Action.Await Signal.testRunCompleted
          Expect = Expectation.PageTextContains(liveTestingPanelSelector, "✓")
          Dwell = Dwell.long } ]
    Cost = CostClass.console
    Masks = [] }
