/// Property and scenario tests for workflow switching.
///
/// These tests document the SAFETY GUARANTEES of switching workflows:
/// - TransitionCost is always accurately computed
/// - Same-workflow switches are no-ops (never destroy a session pointlessly)
/// - Dry-run previews have zero side effects
/// - Zero-cost detection controls the confirmation UX
///
/// WHY these tests matter: A user who switches workflows loses their REPL
/// state. Every guarantee here protects them from unexpected data loss.
module SageFs.Tests.WorkflowSwitchTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.WorkflowTypes
open SageFs.Tests.SharedGenerators

// ── Generators ──────────────────────────────────────────────

let private genBrowserRefreshConfig =
  Gen.elements [
    BrowserRefreshConfig.defaults
    { WatchPatterns = [ "*.fs" ] }
    { WatchPatterns = [ "*.fsx"; "*.fs"; "*.html" ] }
    { WatchPatterns = [] }
  ]

let private genSessionWorkflow =
  Gen.oneof [
    Gen.constant SessionWorkflow.Interactive
    Gen.constant SessionWorkflow.LiveTesting
    genBrowserRefreshConfig |> Gen.map SessionWorkflow.HotReload
  ]

let private genTransitionCost =
  gen {
    let! defs = Gen.choose (0, 100)
    let! cells = Gen.choose (0, 50)
    return TransitionCost.compute defs cells
  }

type SwitchGenerators =
  static member SessionWorkflow () =
    Arb.fromGen genSessionWorkflow
  static member BrowserRefreshConfig () =
    Arb.fromGen genBrowserRefreshConfig
  static member TransitionCost () =
    Arb.fromGen genTransitionCost

let private switchConfig = {
  propConfig with
    arbitrary = [
      typeof<SwitchGenerators>
    ]
}

// ── TransitionCost property tests ───────────────────────────

[<Tests>]
let transitionCostPropertyTests =
  testList "TransitionCost properties" [

    testList "computation is pure and deterministic" [

      testPropertyWithConfig switchConfig
        "same inputs always produce same cost" <|
        fun (evalCount: int) (cellCount: int) ->
          let evalCount = abs evalCount % 1000
          let cellCount = abs cellCount % 500
          let cost1 = TransitionCost.compute evalCount cellCount
          let cost2 = TransitionCost.compute evalCount cellCount
          cost1 = cost2
    ]

    testList "isZeroCost predicate" [

      testPropertyWithConfig switchConfig
        "isZeroCost ↔ DefinitionsLost = 0 AND CellsLost = 0" <|
        fun (cost: TransitionCost) ->
          // WHY: Zero-cost switches skip confirmation.
          // This predicate MUST be correct or we'll either nag users
          // unnecessarily or skip confirmation when there's data to lose.
          let expected =
            cost.DefinitionsLost = 0 && cost.CellsLost = 0
          TransitionCost.isZeroCost cost = expected

      testCase
        "zero cost is zero cost" <| fun _ ->
        // GIVEN a fresh session with nothing to lose
        let cost = TransitionCost.zero

        // WHEN checking if it's zero cost
        let result = TransitionCost.isZeroCost cost

        // THEN it should be — no confirmation needed
        result
        |> Expect.isTrue
          "TransitionCost.zero should be zero cost"

      testCase
        "any definitions means non-zero cost" <| fun _ ->
        // GIVEN a session with 5 evaluated definitions
        let cost = { TransitionCost.zero with DefinitionsLost = 5 }

        // WHEN checking cost
        let result = TransitionCost.isZeroCost cost

        // THEN it's NOT zero — user must be warned about data loss
        result
        |> Expect.isFalse
          "losing 5 definitions should not be zero cost"

      testCase
        "any cells means non-zero cost" <| fun _ ->
        // GIVEN a session with 3 evaluated cells
        let cost = { TransitionCost.zero with CellsLost = 3 }

        // WHEN checking cost
        let result = TransitionCost.isZeroCost cost

        // THEN it's NOT zero — user must be warned
        result
        |> Expect.isFalse
          "losing 3 cells should not be zero cost"
    ]

    testList "estimated restart time" [

      testCase
        "switches always estimate the cold-start restart" <| fun _ ->
        // GIVEN a switch — every switch spawns a fresh session,
        // there is no standby pool to give a near-instant switch
        let cost = TransitionCost.compute 5 3

        // WHEN checking estimated restart
        // THEN it reflects the full cold start
        cost.EstimatedRestart
        |> Expect.equal
          "restart should be the fixed cold-start estimate"
          TransitionCost.coldStartEstimate
    ]
  ]

// ── WorkflowSwitchOutcome scenario tests ────────────────────

[<Tests>]
let workflowSwitchOutcomeTests =
  testList "WorkflowSwitchOutcome scenarios" [

    testList "same-workflow is always a no-op" [

      testPropertyWithConfig switchConfig
        "switching to same workflow returns AlreadyActive" <|
        fun (workflow: SessionWorkflow) ->
          // WHY: Destroying a session to recreate it identically
          // wastes time and loses REPL state for nothing.
          let cost = TransitionCost.zero
          let outcome = WorkflowSwitchOutcome.alreadyInWorkflow workflow cost
          outcome |> WorkflowSwitchOutcome.wasExecuted |> not

      testPropertyWithConfig switchConfig
        "same-workflow outcome message contains 'already'" <|
        fun (workflow: SessionWorkflow) ->
          let cost = TransitionCost.zero
          let outcome = WorkflowSwitchOutcome.alreadyInWorkflow workflow cost
          let msg = WorkflowSwitchOutcome.message outcome
          msg.Contains "Already" || msg.Contains "already"
    ]

    testList "dry-run preview" [

      testCase
        "preview returns cost without switching" <| fun _ ->
        // GIVEN a session in Interactive mode with some state
        let current = SessionWorkflow.Interactive
        let target = SessionWorkflow.HotReload BrowserRefreshConfig.defaults
        let cost = TransitionCost.compute 5 3

        // WHEN previewing the switch
        let outcome = WorkflowSwitchOutcome.preview current target cost

        // THEN the outcome is DryRunPreview with correct cost
        match outcome with
        | WorkflowSwitchOutcome.DryRunPreview (c, _) ->
          c.DefinitionsLost
          |> Expect.equal
            "should report 5 definitions at risk" 5
          c.CellsLost
          |> Expect.equal
            "should report 3 cells at risk" 3
        | other ->
          failwithf "Expected DryRunPreview, got %A" other
        outcome
        |> WorkflowSwitchOutcome.sessionId
        |> Expect.isNone
          "DryRunPreview structurally has no session ID"

      testPropertyWithConfig switchConfig
        "preview message contains both workflow labels" <|
        fun (current: SessionWorkflow) (target: SessionWorkflow) ->
          let cost = TransitionCost.zero
          let outcome = WorkflowSwitchOutcome.preview current target cost
          let msg = WorkflowSwitchOutcome.message outcome
          msg.Contains (SessionWorkflow.label current)
          && msg.Contains (SessionWorkflow.label target)
    ]

    testList "successful switch" [

      testCase
        "switch creates Executed outcome with correct metadata" <| fun _ ->
        // GIVEN switching from Interactive to HotReload
        let previous = SessionWorkflow.Interactive
        let target = SessionWorkflow.HotReload BrowserRefreshConfig.defaults
        let cost = TransitionCost.compute 2 1
        let newSid = "abc-new-session"

        // WHEN executing the switch
        let outcome =
          WorkflowSwitchOutcome.switched previous target cost newSid

        // THEN it's an Executed outcome with new session details
        match outcome with
        | WorkflowSwitchOutcome.Executed (prev, tgt, c, sid, _) ->
          sid
          |> Expect.equal
            "should carry new session ID" newSid
          SessionWorkflow.label prev
          |> Expect.equal
            "should record previous workflow" "REPL"
          SessionWorkflow.label tgt
          |> Expect.equal
            "should record target workflow" "Hot Reload"
          c
          |> Expect.equal
            "should carry transition cost" cost
        | other ->
          failwithf "Expected Executed, got %A" other
    ]
  ]

// ── SSE event serialization tests ───────────────────────────

[<Tests>]
let workflowSseEventTests =
  testList "Workflow SSE event serialization" [

    testList "WorkflowSwitching event" [

      testCase
        "serializes with correct type discriminator" <| fun _ ->
        // WHY: Editors parse these events by type field.
        // Wrong discriminator = 4 editor plugins break silently.
        let evt =
          SageFs.Server.SseEvent.WorkflowSwitching("sid-1", "REPL", "Live")
        let json =
          SageFs.Server.SseEvent.toJson evt

        json
        |> Expect.stringContains
          "should have correct type" "workflow_switching"
        json
        |> Expect.stringContains
          "should include session ID" "sid-1"
        json
        |> Expect.stringContains
          "should include from workflow" "REPL"
        json
        |> Expect.stringContains
          "should include to workflow" "Live"
    ]

    testList "WorkflowSwitched event" [

      testCase
        "serializes with all derived fields" <| fun _ ->
        // WHY: Editors render capability info from these events.
        // Missing fields = broken status bar.
        let evt =
          SageFs.Server.SseEvent.WorkflowSwitched(
            "sid-2", "Live", "ExpressionOnly", true)
        let json =
          SageFs.Server.SseEvent.toJson evt

        json
        |> Expect.stringContains
          "should have correct type" "workflow_switched"
        json
        |> Expect.stringContains
          "should include label" "Live"
        json
        |> Expect.stringContains
          "should include replCapability" "ExpressionOnly"
        json
        |> Expect.stringContains
          "should include hotReloadActive" "true"

      testPropertyWithConfig switchConfig
        "round-trip preserves session ID" <|
        fun () ->
          let sid = "test-session-42"
          let evt =
            SageFs.Server.SseEvent.WorkflowSwitched(
              sid, "REPL", "Full", false)
          let json =
            SageFs.Server.SseEvent.toJson evt
          json.Contains sid
    ]
  ]

// ── The switch events are really emitted ────────────────────
//
// `WorkflowSwitching` and `WorkflowSwitched` were defined, serialized and documented, and nothing in the daemon
// constructed either: a switch through `POST /api/sessions/{sid}/workflow` said nothing on `/events`. The daemon's one
// switch command restarts the same session spawn-first, so the two points are read off the session list.

module SwitchWatch = SageFs.Server.WorkflowSwitchWatch

let private watchHandle : SageFs.WorkerProtocol.WorkerHandle = { Pid = 1; Port = Some 5000 }

let private watchSid = "0a0b0c0d"

let private watchSessionId : SageFs.WorkerProtocol.SessionId =
  match SageFs.WorkerProtocol.SessionId.validate watchSid with
  | Ok id -> id
  | Error e -> failwith e

let private sessionAt
  (workflow: SessionWorkflow)
  (status: SageFs.WorkerProtocol.SessionLifecycleStatus)
  : SageFs.WorkerProtocol.SessionInfo =
  let at = System.DateTime(2026, 10, 4, 0, 0, 0, System.DateTimeKind.Utc)
  { Id = watchSessionId
    Name = None
    Projects = []
    WorkingDirectory = "/repo/app"
    SolutionRoot = None
    Status = status
    Workflow = workflow
    CreatedAt = at
    LastActivity = at
    ActiveProject = None
    ProjectRoles = []
    App = SageFs.AppRun.AppRunState.NotRunning
    Rebuild = SageFs.LastRebuild.NeverRebuilt
    Reload = SageFs.SessionReload.NoReloadYet
    Freshness = SageFs.ReplFreshness.InSync }

let private serving = SageFs.WorkerProtocol.SessionLifecycleStatus.Ready watchHandle

let private restarting =
  SageFs.WorkerProtocol.SessionLifecycleStatus.Restarting (SageFs.WorkerProtocol.PreviousWorker.Was watchHandle.Pid)

let private faulted =
  SageFs.WorkerProtocol.SessionLifecycleStatus.Faulted (SageFs.WorkerProtocol.FaultReason.report "the replacement worker died")

/// Look at one session after another, carrying the watch, and return what each look said.
let private looks (sessions: SageFs.WorkerProtocol.SessionInfo list list) : SageFs.Server.WorkflowObservation list =
  sessions
  |> List.scan
    (fun (_, watch) one ->
      let seen = SwitchWatch.observe watch one
      Some seen, seen.Watch)
    (None, Map.empty)
  |> List.choose fst

let private typeOf (evt: SageFs.Server.SseEvent) : string =
  use doc = System.Text.Json.JsonDocument.Parse(SageFs.Server.SseEvent.toJson evt)
  doc.RootElement.GetProperty("type").GetString()

[<Tests>]
let workflowSwitchEmissionTests =
  testList "Workflow switch events are emitted" [

    testCase "a session seen for the first time is not announced as switched" <| fun _ ->
      let seen = looks [ [ sessionAt SessionWorkflow.Interactive serving ] ]
      seen |> List.collect (fun o -> o.Events) |> Expect.isEmpty "creating a session into a workflow is not a switch"

    testCase "a changed workflow with its replacement worker still coming says switching, naming both workflows" <| fun _ ->
      let seen = looks [ [ sessionAt SessionWorkflow.Interactive serving ]; [ sessionAt SessionWorkflow.LiveTesting restarting ] ]
      seen.[1].Events
      |> Expect.equal "one switching event, from what it ran to what it will run"
           [ SageFs.Server.SseEvent.WorkflowSwitching (watchSid, SessionWorkflow.label SessionWorkflow.Interactive, SessionWorkflow.label SessionWorkflow.LiveTesting) ]
      seen.[1].Awaiting |> Expect.equal "the switch is waited on until its worker is ready" [ watchSessionId ]

    testCase "switched follows once the new worker is serving, with the capability and hot reload state the workflow derives" <| fun _ ->
      let hotReload = SessionWorkflow.HotReload BrowserRefreshConfig.defaults
      let seen =
        looks
          [ [ sessionAt SessionWorkflow.Interactive serving ]
            [ sessionAt hotReload restarting ]
            [ sessionAt hotReload serving ]
            [ sessionAt hotReload serving ] ]
      seen.[2].Events
      |> Expect.equal "one switched event with the labels the workflow derives"
           [ SageFs.Server.SseEvent.WorkflowSwitched
               (watchSid,
                SessionWorkflow.label hotReload,
                ReplCapability.label (SessionWorkflow.replCapability hotReload),
                SessionWorkflow.isHotReloadActive hotReload) ]
      seen.[3].Events |> Expect.isEmpty "once told, a later look says nothing more"

    testCase "a switch whose worker faults says switching and never switched" <| fun _ ->
      let seen =
        looks
          [ [ sessionAt SessionWorkflow.Interactive serving ]
            [ sessionAt SessionWorkflow.LiveTesting restarting ]
            [ sessionAt SessionWorkflow.LiveTesting faulted ] ]
      seen |> List.collect (fun o -> o.Events) |> List.map typeOf
      |> Expect.equal "the failure is the session's health event, not a switched event" [ "workflow_switching" ]

    testCase "a second switch while one is in flight starts from the first one's target" <| fun _ ->
      let hotReload = SessionWorkflow.HotReload BrowserRefreshConfig.defaults
      let seen =
        looks
          [ [ sessionAt SessionWorkflow.Interactive serving ]
            [ sessionAt SessionWorkflow.LiveTesting restarting ]
            [ sessionAt hotReload restarting ] ]
      seen.[2].Events
      |> Expect.equal "the superseded target is where the new switch begins"
           [ SageFs.Server.SseEvent.WorkflowSwitching (watchSid, SessionWorkflow.label SessionWorkflow.LiveTesting, SessionWorkflow.label hotReload) ]

    testCase "a session that is gone is forgotten, so one that returns is a first sight" <| fun _ ->
      let seen =
        looks
          [ [ sessionAt SessionWorkflow.Interactive serving ]
            []
            [ sessionAt SessionWorkflow.LiveTesting serving ] ]
      seen |> List.collect (fun o -> o.Events) |> Expect.isEmpty "no switch is claimed across a gap"

    testCase "both events are scoped to the session that switched, so /events?sessionId= stays honest" <| fun _ ->
      let seen = looks [ [ sessionAt SessionWorkflow.Interactive serving ]; [ sessionAt SessionWorkflow.LiveTesting serving ] ]
      let frames = seen.[1].Events |> List.map SageFs.Server.SseEvent.frame
      frames |> List.length |> Expect.equal "switching and switched" 2
      frames
      |> List.map (fun f -> f.Scope)
      |> List.distinct
      |> Expect.equal "every frame is that one session's" [ SageFs.FrameScope.Session watchSid ]

    testTask "wired to the daemon's state changes, a switch pushes both frames, and the ready one needs no further state change" {
      let stateChanged = Event<SageFs.Server.SseEvent>()
      let current = ref [ sessionAt SessionWorkflow.Interactive serving ]
      let pushed = System.Collections.Concurrent.ConcurrentQueue<SageFs.SseFrame>()
      let switchedSeen = System.Threading.Tasks.TaskCompletionSource()
      let publish (frame: SageFs.SseFrame) =
        pushed.Enqueue frame
        match frame.Wire.Contains "workflow_switched" with
        | true -> switchedSeen.TrySetResult() |> ignore
        | false -> ()
      let ops : SageFs.SessionManagementOps =
        { SageFs.SessionManagementOps.stub with
            GetAllSessions = fun () -> System.Threading.Tasks.Task.FromResult current.Value
            // The session is ready by the time the wait for it ends; no state change announces it.
            AwaitReady = fun _ _ ->
              current.Value <- [ sessionAt SessionWorkflow.LiveTesting serving ]
              System.Threading.Tasks.Task.FromResult(Result.Ok ()) }
      use _watch = SwitchWatch.wire stateChanged.Publish ops publish
      stateChanged.Trigger SageFs.Server.SseEvent.SessionProgress
      current.Value <- [ sessionAt SessionWorkflow.LiveTesting restarting ]
      stateChanged.Trigger SageFs.Server.SseEvent.SessionProgress
      let! finished =
        System.Threading.Tasks.Task.WhenAny(switchedSeen.Task, System.Threading.Tasks.Task.Delay TestTimeouts.patienceBrief)
      finished |> Expect.equal "switched arrived" (switchedSeen.Task :> System.Threading.Tasks.Task)
      let wires = pushed.ToArray() |> Array.map (fun f -> f.Wire)
      wires |> Array.length |> Expect.equal "two frames" 2
      wires.[0] |> Expect.stringContains "switching comes first" "workflow_switching"
      wires.[1] |> Expect.stringContains "switched comes second" "workflow_switched"
    }
  ]
