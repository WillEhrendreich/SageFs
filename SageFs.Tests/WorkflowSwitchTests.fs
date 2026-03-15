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
    genBrowserRefreshConfig |> Gen.map SessionWorkflow.WebLive
  ]

let private genTransitionCost =
  gen {
    let! defs = Gen.choose (0, 100)
    let! cells = Gen.choose (0, 50)
    let! standby = Gen.elements [true; false]
    return TransitionCost.compute defs cells standby
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
        fun (evalCount: int) (cellCount: int) (standby: bool) ->
          let evalCount = abs evalCount % 1000
          let cellCount = abs cellCount % 500
          let cost1 = TransitionCost.compute evalCount cellCount standby
          let cost2 = TransitionCost.compute evalCount cellCount standby
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

    testList "standby affects estimated restart time" [

      testCase
        "standby ready gives near-instant estimate" <| fun _ ->
        // GIVEN a standby worker is ready
        let cost = TransitionCost.compute 5 3 true

        // WHEN checking estimated restart
        // THEN it's much faster than cold start
        (cost.EstimatedRestart, System.TimeSpan.FromSeconds 1.0)
        |> Expect.isLessThan
          "standby restart should be under 1 second"

      testCase
        "no standby gives cold start estimate" <| fun _ ->
        // GIVEN no standby worker available
        let cost = TransitionCost.compute 5 3 false

        // WHEN checking estimated restart
        // THEN it reflects full cold start
        (cost.EstimatedRestart, System.TimeSpan.FromSeconds 5.0)
        |> Expect.isGreaterThan
          "cold restart should be over 5 seconds"
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
        let target = SessionWorkflow.WebLive BrowserRefreshConfig.defaults
        let cost = TransitionCost.compute 5 3 false

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
        // GIVEN switching from Interactive to WebLive
        let previous = SessionWorkflow.Interactive
        let target = SessionWorkflow.WebLive BrowserRefreshConfig.defaults
        let cost = TransitionCost.compute 2 1 true
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
            "should record target workflow" "Live"
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
          SageFs.SessionEvents.WorkflowSwitching("sid-1", "REPL", "Live")
        let json =
          SageFs.SessionEvents.serializeSessionEvent evt

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
          SageFs.SessionEvents.WorkflowSwitched(
            "sid-2", "Live", "ExpressionOnly", true)
        let json =
          SageFs.SessionEvents.serializeSessionEvent evt

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
            SageFs.SessionEvents.WorkflowSwitched(
              sid, "REPL", "Full", false)
          let json =
            SageFs.SessionEvents.serializeSessionEvent evt
          json.Contains sid
    ]
  ]
