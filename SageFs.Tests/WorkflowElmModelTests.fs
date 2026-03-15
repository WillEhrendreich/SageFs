module SageFs.Tests.WorkflowElmModelTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkflowTypes

// ─── Helpers ────────────────────────────────────────────────

let private defaultModel = SageFsModel.initial ()

let private modelWithSuggestion suggestion =
  { defaultModel with PendingSuggestion = Some suggestion }

let private sampleSuggestion = {
  SuggestedWorkflow = SessionWorkflow.WebLive BrowserRefreshConfig.defaults
  Reason = "Datastar project detected"
  DetectedPackages = [ "Falco.Datastar" ]
}

// ─── Scenario tests ─────────────────────────────────────────

/// WHY: The UI needs the suggestion to render the banner/notification.
/// When WorkflowDetection.suggest returns Some, the Elm model must store it
/// so the view layer can display "SageFs suggests Live mode for this project."
let suggestionReceivedPopulatesPending =
  testCase
    "SuggestionReceived populates PendingSuggestion — UI can render the suggestion banner" <| fun _ ->
    let model = defaultModel
    model.PendingSuggestion
    |> Expect.isNone "should start with no suggestion"

    let model', effects =
      SageFsUpdate.update (SageFsMsg.WorkflowSuggestionReceived sampleSuggestion) model

    model'.PendingSuggestion
    |> Expect.isSome "should have pending suggestion after receiving one"

    model'.PendingSuggestion
    |> Option.get
    |> fun s -> s.SuggestedWorkflow
    |> Expect.equal "should store the suggested workflow"
        (SessionWorkflow.WebLive BrowserRefreshConfig.defaults)

    effects
    |> Expect.isEmpty "receiving a suggestion is pure model update — no side effects"

/// WHY: User said "no thanks" — clear the banner, don't nag.
/// Dismissal is a conscious choice that must be respected.
/// The suggestion should not reappear until a new session is created.
let suggestionDismissedClearsPending =
  testCase
    "SuggestionDismissed clears PendingSuggestion — user said no, stop nagging" <| fun _ ->
    let model = modelWithSuggestion sampleSuggestion
    model.PendingSuggestion
    |> Expect.isSome "precondition: model has a pending suggestion"

    let model', effects =
      SageFsUpdate.update SageFsMsg.WorkflowSuggestionDismissed model

    model'.PendingSuggestion
    |> Expect.isNone "should clear suggestion after dismissal"

    effects
    |> Expect.isEmpty "dismissing a suggestion is pure model update — no side effects"

/// WHY: Accepting triggers session recreation — effect, not inline mutation.
/// The Elm architecture demands: update returns effects, the runtime executes them.
/// SwitchWorkflow effect will create a new session with the suggested workflow.
let suggestionAcceptedClearsAndEmitsEffect =
  testCase
    "SuggestionAccepted clears suggestion AND emits SwitchWorkflow effect — triggers session recreation" <| fun _ ->
    let model = modelWithSuggestion sampleSuggestion

    let model', effects =
      SageFsUpdate.update SageFsMsg.WorkflowSuggestionAccepted model

    model'.PendingSuggestion
    |> Expect.isNone "should clear suggestion after acceptance"

    effects
    |> List.length
    |> Expect.equal "should emit exactly one effect" 1

    let hasSwitchEffect =
      effects |> List.exists (function
        | SageFsEffect.SwitchWorkflow _ -> true
        | _ -> false)

    hasSwitchEffect
    |> Expect.isTrue "should contain SwitchWorkflow effect for the suggested workflow"

/// WHY: Before any session exists, the default workflow applies.
/// The projection function must handle the None case gracefully —
/// every UI component that reads the workflow must get a sensible default.
let currentWorkflowDefaultsToInteractive =
  testCase
    "currentWorkflow defaults to Interactive when no session context — safe fallback for UI" <| fun _ ->
    let model = { defaultModel with SessionContext = None }

    SageFsModel.currentWorkflow model
    |> Expect.equal "should default to Interactive when no session exists"
        SessionWorkflow.Interactive

/// WHY: When a session IS active, the projection reads from the actual session config.
/// This proves the projection doesn't always return the default — it responds to real state.
let currentWorkflowReflectsSessionContext =
  testCase
    "currentWorkflow reflects actual session workflow when session context exists" <| fun _ ->
    let ctx : SessionContext = {
      SessionId = "test-session"
      ProjectNames = ["TestProj"]
      WorkingDir = "/code"
      Status = "Ready"
      Warmup = WarmupContext.empty
      FileStatuses = []
      Workflow = SessionWorkflow.WebLive BrowserRefreshConfig.defaults
    }
    let model = { defaultModel with SessionContext = Some ctx }

    SageFsModel.currentWorkflow model
    |> Expect.equal "should reflect WebLive from session context"
        (SessionWorkflow.WebLive BrowserRefreshConfig.defaults)

// ─── Test list ──────────────────────────────────────────────

[<Tests>]
let tests = testList "Workflow Elm Model" [
  testList "Suggestion lifecycle — Elm update" [
    suggestionReceivedPopulatesPending
    suggestionDismissedClearsPending
    suggestionAcceptedClearsAndEmitsEffect
  ]
  testList "Projection — currentWorkflow" [
    currentWorkflowDefaultsToInteractive
    currentWorkflowReflectsSessionContext
  ]
]
