/// BDD scenario tests for the SessionWorkflow system.
///
/// These tests document the USER EXPERIENCE — what happens when a user
/// creates sessions, switches workflows, and hits mode limitations.
/// A product manager should be able to read these and understand the behavior.
///
/// Each test answers: "When the user does X, what should happen?"
module SageFs.Tests.WorkflowScenarioTests

open Expecto
open Expecto.Flip
open SageFs.WorkflowTypes

// ── Scenario: Session creation ──────────────────────────────

let private sessionCreationScenarios =
  testList "session creation with workflows" [

    testCase
      "creating a session with Interactive workflow gets full REPL" <| fun _ ->
      // GIVEN a user who wants to explore F# interactively
      let workflow = SessionWorkflow.Interactive

      // WHEN they create a session with the Interactive workflow
      let capability = SessionWorkflow.replCapability workflow

      // THEN they get full REPL capability (type redefinition, etc.)
      capability
      |> Expect.equal
        "Interactive workflow should give full REPL"
        ReplCapability.Full

    testCase
      "creating a session with WebLive gets expression-only REPL" <| fun _ ->
      // GIVEN a user building a Falco web app who wants hot reload
      let workflow =
        SessionWorkflow.WebLive BrowserRefreshConfig.defaults

      // WHEN they create a session with the WebLive workflow
      let capability = SessionWorkflow.replCapability workflow

      // THEN they get expression-only REPL (no type redefinition)
      capability
      |> Expect.equal
        "WebLive workflow should restrict REPL to expressions only"
        ReplCapability.ExpressionOnly

    testCase
      "Interactive session never activates Harmony patching" <| fun _ ->
      // GIVEN a user in Interactive mode
      let workflow = SessionWorkflow.Interactive

      // WHEN checking whether Harmony should be installed
      let hotReload = SessionWorkflow.isHotReloadActive workflow

      // THEN it's not — no Harmony means no --multiemit- constraint
      hotReload
      |> Expect.isFalse
        "Interactive mode should not activate hot reload"

    testCase
      "WebLive session activates Harmony patching" <| fun _ ->
      // GIVEN a user who chose Live mode for browser refresh
      let workflow =
        SessionWorkflow.WebLive { WatchPatterns = [ "*.fs" ] }

      // WHEN checking whether Harmony should be installed
      let hotReload = SessionWorkflow.isHotReloadActive workflow

      // THEN it IS — Harmony enables save-driven browser refresh
      hotReload
      |> Expect.isTrue
        "WebLive mode should activate hot reload"
  ]

// ── Scenario: Status bar display ────────────────────────────

let private statusBarScenarios =
  testList "status bar reflects workflow" [

    testCase "Interactive shows REPL in status bar" <| fun _ ->
      // GIVEN a user in Interactive mode
      let workflow = SessionWorkflow.Interactive

      // WHEN the status bar renders
      let displayLabel = SessionWorkflow.label workflow

      // THEN it shows "REPL" — clear, searchable, universal
      displayLabel
      |> Expect.equal "should display REPL" "REPL"

    testCase "WebLive shows Live in status bar" <| fun _ ->
      // GIVEN a user in WebLive mode
      let workflow =
        SessionWorkflow.WebLive BrowserRefreshConfig.defaults

      // WHEN the status bar renders
      let displayLabel = SessionWorkflow.label workflow

      // THEN it shows "Live" — indicates active hot reload
      displayLabel
      |> Expect.equal "should display Live" "Live"
  ]

// ── Scenario: FSI flag generation ───────────────────────────

let private fsiFlagScenarios =
  testList "FSI flags match workflow requirements" [

    testCase
      "Interactive generates no special flags" <| fun _ ->
      // GIVEN a user who wants full REPL
      let workflow = SessionWorkflow.Interactive

      // WHEN FSI starts
      let flags = SessionWorkflow.fsiArgs workflow

      // THEN no special flags — FSI runs in default multi-emit mode
      flags
      |> Expect.isEmpty
        "Interactive should not need special FSI flags"

    testCase
      "WebLive generates --multiemit- flag" <| fun _ ->
      // GIVEN a user who wants browser hot reload
      let workflow =
        SessionWorkflow.WebLive BrowserRefreshConfig.defaults

      // WHEN FSI starts
      let flags = SessionWorkflow.fsiArgs workflow

      // THEN --multiemit- is present — Harmony requires single-assembly mode
      flags
      |> Expect.equal
        "WebLive should emit --multiemit- for Harmony compatibility"
        [ "--multiemit-" ]
  ]

// ── Scenario: Transition cost estimation ────────────────────

let private transitionCostScenarios =
  testList "transition cost informs the user" [

    testCase "TransitionCost.zero represents a costless switch" <| fun _ ->
      // GIVEN a fresh session with no REPL state
      let cost = TransitionCost.zero

      // THEN the cost shows nothing will be lost
      cost.DefinitionsLost
      |> Expect.equal "no definitions to lose" 0
      cost.CellsLost
      |> Expect.equal "no cells to lose" 0

    testCase "TransitionCost captures real losses" <| fun _ ->
      // GIVEN a session where the user has accumulated REPL state
      let cost = {
        DefinitionsLost = 12
        CellsLost = 3
        EstimatedRestart = System.TimeSpan.FromSeconds 8.0
      }

      // THEN the cost accurately describes what switching will cost
      cost.DefinitionsLost
      |> Expect.equal "should report 12 definitions" 12
      cost.CellsLost
      |> Expect.equal "should report 3 cells" 3
  ]

// ── Scenario: Project detection suggestions ─────────────────

let private detectionScenarios =
  testList "project detection suggests the right workflow" [

    testCase
      "console app gets no suggestion — default is fine" <| fun _ ->
      // GIVEN a project that's not a web app
      let packages = [ "FSharp.Core"; "Expecto"; "Newtonsoft.Json" ]

      // WHEN SageFs analyzes the project
      let suggestion = WorkflowDetection.suggest packages

      // THEN no suggestion — Interactive is the right default
      suggestion
      |> Expect.isNone
        "console apps should not get a workflow suggestion"

    testCase
      "Datastar project gets specific suggestion with SSE reason" <| fun _ ->
      // GIVEN a Falco.Datastar project
      let packages =
        [ "Falco"; "Falco.Datastar"; "Falco.Markup" ]

      // WHEN SageFs analyzes the project
      let suggestion = WorkflowDetection.suggest packages

      // THEN it suggests Live mode with Datastar-specific reason
      suggestion
      |> Expect.isSome "should suggest for Datastar project"
      suggestion.Value.Reason
      |> Expect.stringContains
        "reason should mention SSE/DOM" "Datastar"

    testCase
      "Giraffe web project gets generic web suggestion" <| fun _ ->
      // GIVEN a Giraffe web project (no Datastar)
      let packages = [ "Giraffe"; "FSharp.Core" ]

      // WHEN SageFs analyzes the project
      let suggestion = WorkflowDetection.suggest packages

      // THEN it suggests Live mode with generic web reason
      suggestion
      |> Expect.isSome "should suggest for web project"
      suggestion.Value.Reason
      |> Expect.stringContains
        "reason should mention hot reload" "hot reload"

    testCase
      "suggestion never auto-applies — it's informational" <| fun _ ->
      // GIVEN any workflow suggestion
      let suggestion =
        WorkflowDetection.suggest [ "Falco" ]

      // THEN the suggestion includes enough info for the user to decide
      match suggestion with
      | Some s ->
        s.Reason
        |> System.String.IsNullOrWhiteSpace
        |> Expect.isFalse "reason must be non-empty"
        s.DetectedPackages
        |> Expect.isNonEmpty "must list which packages triggered it"
      | None -> failtest "Falco should trigger a suggestion"
  ]

// ── Scenario: Default workflow ──────────────────────────────

let private defaultWorkflowScenarios =
  testList "default workflow is safe and universal" [

    testCase
      "default workflow is Interactive — works for all projects" <| fun _ ->
      // GIVEN a user who doesn't specify a workflow
      let defaultWf = SessionWorkflow.defaultWorkflow

      // THEN they get Interactive — full REPL, no surprises
      match defaultWf with
      | SessionWorkflow.Interactive -> ()
      | SessionWorkflow.WebLive _ ->
        failtest "default should be Interactive, not WebLive"

    testCase
      "default workflow has full REPL capability" <| fun _ ->
      SessionWorkflow.defaultWorkflow
      |> SessionWorkflow.replCapability
      |> Expect.equal
        "default should have full REPL"
        ReplCapability.Full

    testCase
      "default workflow has no hot reload" <| fun _ ->
      SessionWorkflow.defaultWorkflow
      |> SessionWorkflow.isHotReloadActive
      |> Expect.isFalse
        "default should not activate hot reload"
  ]

// ── Root test list ──────────────────────────────────────────

[<Tests>]
let workflowScenarioTests =
  testList "Workflow scenarios" [
    sessionCreationScenarios
    statusBarScenarios
    fsiFlagScenarios
    transitionCostScenarios
    detectionScenarios
    defaultWorkflowScenarios
  ]
