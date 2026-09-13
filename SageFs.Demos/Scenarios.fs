/// The Phase-1 smoke scenario (demo-gif-plan.md §10 Phase 1): dashboard only
/// — start a session, watch it warm up to Ready, then evaluate a real F#
/// expression and watch the result land. Built the same way the worked hero
/// example in §6.1 is: a plain F# value, no strings for closed sets, each
/// step a caption/action/expectation/dwell.
///
/// This is deliberately a real "watch SageFs evaluate F# live" demo, not
/// just "a session card appears": the session-card testid fires while the
/// session is still `WarmingUp`, so a scenario that stopped there recorded
/// nothing interesting — §2's fix threads the story through to an actual
/// eval result on screen.
module SageFs.Demos.Scenarios

open SageFs.Demos.Domain

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
    Sample = Sample.WebappDatastar
    Layout = LayoutTemplate.DashboardOnly
    Steps =
      [ { Caption = Caption.mk "1/3 · Start a session"
          Action = Action.Click(Target.DashboardElement DashboardId.QuickStart)
          Expect = Expectation.PageShows(DashboardId.SessionCard, Text.mk "")
          Dwell = Dwell.short }
        // Creating a session (Quick Start) does NOT switch the dashboard's
        // main panel to that session's own view — it stays on the "Start a
        // Session" picker until a session card is actually CLICKED
        // (confirmed directly against a real recording: a still frame taken
        // well after the tabline read "[Ready]" still showed the picker,
        // with no eval box on screen at all). Clicking the card here
        // navigates into the session's own view, which step 3 needs.
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
        // land in and undo the expand.
        { Caption = Caption.mk "3/3 · Evaluate F# — instant result"
          Action =
            Action.ClickThenTypeThenClick(
              Target.DashboardCssSelector evaluateAccordionSelector,
              Target.DashboardCssSelector evalTextareaSelector,
              Text.mk "[1..10] |> List.sum",
              CadenceSeed.ofId "hello-dashboard-eval",
              Target.DashboardElement DashboardId.Eval
            )
          Expect = Expectation.PageTextContains(outputPanelSelector, "55")
          Dwell = Dwell.long } ]
    Cost = CostClass.web
    Masks = [] }
