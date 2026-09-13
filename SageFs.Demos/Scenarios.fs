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
