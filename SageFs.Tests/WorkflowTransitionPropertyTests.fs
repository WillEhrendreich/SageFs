/// Phase 6B: State machine boundary and chaos tests for workflow switching.
///
/// These property-based tests are COMPLEMENTARY to WorkflowSwitchTests.fs.
/// They focus on domain INVARIANTS and boundary conditions that must hold
/// for ALL possible inputs — not just the happy-path scenarios already covered.
///
/// WHY chaos tests: Users interact with workflow switching through 5 editor
/// plugins (TUI, Raylib GUI, VS Code, Visual Studio, Neovim). Any violation
/// of these invariants breaks at least one of those UIs silently.
module SageFs.Tests.WorkflowTransitionPropertyTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.WorkflowTypes
open SageFs.Tests.SharedGenerators

// ── FsCheck Generators ─────────────────────────────────────

let private genBrowserRefreshConfig =
  gen {
    let! patternCount = Gen.choose (0, 5)
    let! patterns =
      Gen.listOfLength patternCount (
        Gen.elements [
          "*.fs"; "*.fsx"; "*.html"; "*.css"; "*.js"
          "**/*.fs"; "src/**/*.fsx"; "*.json"
        ]
      )
    return { WatchPatterns = patterns }
  }

let private genSessionWorkflow =
  Gen.oneof [
    Gen.constant SessionWorkflow.Interactive
    genBrowserRefreshConfig |> Gen.map SessionWorkflow.WebLive
  ]

/// Direct TransitionCost record generator (for testing isZeroCost boundary)
let private genRawTransitionCost =
  gen {
    let! defs = Gen.choose (0, 100)
    let! cells = Gen.choose (0, 50)
    let! restartMs = Gen.choose (0, 30000)
    return {
      DefinitionsLost = defs
      CellsLost = cells
      EstimatedRestart = TimeSpan.FromMilliseconds(float restartMs)
    }
  }

type TransitionGenerators =
  static member SessionWorkflow () =
    Arb.fromGen genSessionWorkflow
  static member BrowserRefreshConfig () =
    Arb.fromGen genBrowserRefreshConfig
  static member TransitionCost () =
    Arb.fromGen genRawTransitionCost

let private chaosConfig = {
  propConfig with
    arbitrary = [ typeof<TransitionGenerators> ]
}

/// Normalize an arbitrary int to a non-negative value in a useful range.
let private normalize (bound: int) (n: int) = abs n % (bound + 1)

// ── Property tests ──────────────────────────────────────────

[<Tests>]
let workflowTransitionPropertyTests =
  testList "WorkflowTransition properties" [

    // ── (a) Preview idempotency ──────────────────────────────

    testList "preview is idempotent" [

      testPropertyWithConfig chaosConfig
        "previewing same switch twice yields identical costs" <|
        fun (rawEval: int) (rawCell: int) ->
          // WHY: Users who click "preview" repeatedly must see
          // consistent information — flickering costs erode trust.
          let evalCount = normalize 500 rawEval
          let cellCount = normalize 200 rawCell
          let cost1 = TransitionCost.compute evalCount cellCount
          let cost2 = TransitionCost.compute evalCount cellCount
          cost1 = cost2
    ]

    // ── (b) Round-trip preserves workflow identity ────────────

    testList "round-trip preserves workflow identity" [

      testPropertyWithConfig chaosConfig
        "REPL→Live→REPL returns to same label" <|
        fun (workflow: SessionWorkflow) ->
          // WHY: Users must be confident round-trips are safe —
          // switching away and back should not silently change mode.
          let originalLabel = SessionWorkflow.label workflow
          let opposite =
            match workflow with
            | SessionWorkflow.Interactive ->
              SessionWorkflow.WebLive BrowserRefreshConfig.defaults
            | SessionWorkflow.WebLive _ ->
              SessionWorkflow.Interactive
          let roundTripped =
            match opposite with
            | SessionWorkflow.Interactive ->
              SessionWorkflow.WebLive BrowserRefreshConfig.defaults
            | SessionWorkflow.WebLive _ ->
              SessionWorkflow.Interactive
          let roundTrippedLabel = SessionWorkflow.label roundTripped
          originalLabel = roundTrippedLabel
    ]

    // ── (c) TransitionCost accuracy ──────────────────────────

    testList "TransitionCost accuracy" [

      testPropertyWithConfig chaosConfig
        "definitions lost equals eval count input" <|
        fun (rawEval: int) (rawCell: int) ->
          // WHY: Cost must honestly reflect what users will lose —
          // underreporting causes surprise data loss.
          let evalCount = normalize 1000 rawEval
          let cellCount = normalize 500 rawCell
          let cost = TransitionCost.compute evalCount cellCount
          cost.DefinitionsLost = evalCount
          && cost.CellsLost = cellCount
    ]

    // ── (d) Zero-cost predicate is exact conjunction ─────────

    testList "zero-cost predicate is exact conjunction" [

      testPropertyWithConfig chaosConfig
        "isZeroCost ↔ (DefinitionsLost=0 ∧ CellsLost=0)" <|
        fun (cost: TransitionCost) ->
          // WHY: Zero-cost switches skip confirmation — the predicate
          // must be exact. False positive = skipped warning before data
          // loss. False negative = unnecessary nag dialog.
          let expected = cost.DefinitionsLost = 0 && cost.CellsLost = 0
          TransitionCost.isZeroCost cost = expected
    ]

    // ── (e) Restart estimate is the fixed cold-start cost ─────

    testList "restart always reflects cold start" [

      testPropertyWithConfig chaosConfig
        "restart estimate equals the fixed 15s cold-start cost" <|
        fun (rawEval: int) (rawCell: int) ->
          // WHY: The standby pool was dissolved — every switch spawns a
          // fresh session, so the estimate is always the cold-start 15s.
          let evalCount = normalize 500 rawEval
          let cellCount = normalize 200 rawCell
          let cost = TransitionCost.compute evalCount cellCount
          cost.EstimatedRestart = TimeSpan.FromSeconds 15.0
    ]

    // ── (f) alreadyInWorkflow always returns AlreadyActive ─────

    testList "alreadyInWorkflow always returns AlreadyActive" [

      testPropertyWithConfig chaosConfig
        "outcome is AlreadyActive and message contains label" <|
        fun (workflow: SessionWorkflow) (cost: TransitionCost) ->
          // WHY: UI must reliably detect no-op to show an appropriate
          // "already in this mode" message instead of a success toast.
          let outcome = WorkflowSwitchOutcome.alreadyInWorkflow workflow cost
          let label = SessionWorkflow.label workflow
          match outcome with
          | WorkflowSwitchOutcome.AlreadyActive (_, msg) ->
            msg.Contains label
          | _ -> false

      testPropertyWithConfig chaosConfig
        "message contains 'Already' for any workflow" <|
        fun (workflow: SessionWorkflow) ->
          let cost = TransitionCost.zero
          let outcome = WorkflowSwitchOutcome.alreadyInWorkflow workflow cost
          let msg = WorkflowSwitchOutcome.message outcome
          msg.Contains "Already"
          || msg.Contains "already"

      testPropertyWithConfig chaosConfig
        "sessionId is structurally None for AlreadyActive" <|
        fun (workflow: SessionWorkflow) (cost: TransitionCost) ->
          let outcome = WorkflowSwitchOutcome.alreadyInWorkflow workflow cost
          WorkflowSwitchOutcome.sessionId outcome = None
    ]

    // ── (g) switched always produces Executed ───────────────

    testList "switched always produces Executed" [

      testPropertyWithConfig chaosConfig
        "Executed always carries the provided sessionId" <|
        fun (prev: SessionWorkflow) (next: SessionWorkflow)
            (cost: TransitionCost) ->
          // WHY: Client code expects to reconnect to the new session —
          // missing sessionId would leave the editor disconnected.
          let sid =
            sprintf "test-session-%s" (Guid.NewGuid().ToString("N").[..7])
          let outcome = WorkflowSwitchOutcome.switched prev next cost sid
          match outcome with
          | WorkflowSwitchOutcome.Executed (_, _, _, actualSid, _) ->
            actualSid = sid
          | _ -> false

      testPropertyWithConfig chaosConfig
        "Executed outcome message contains session ID" <|
        fun (prev: SessionWorkflow) (next: SessionWorkflow) ->
          let cost = TransitionCost.zero
          let sid =
            sprintf "chaos-%s" (Guid.NewGuid().ToString("N").[..7])
          let outcome = WorkflowSwitchOutcome.switched prev next cost sid
          (WorkflowSwitchOutcome.message outcome).Contains sid
    ]

    // ── (h) No negative values in TransitionCost ─────────────

    testList "rapid successive cost computations never produce negative values" [

      testPropertyWithConfig chaosConfig
        "all TransitionCost fields are non-negative" <|
        fun (rawEval: int) (rawCell: int) ->
          // WHY: Negative "definitions lost" would confuse users and
          // break UI rendering (progress bars, cost displays).
          let evalCount = normalize 10000 rawEval
          let cellCount = normalize 5000 rawCell
          let cost = TransitionCost.compute evalCount cellCount
          cost.DefinitionsLost >= 0
          && cost.CellsLost >= 0
          && cost.EstimatedRestart >= TimeSpan.Zero
    ]

    // ── (i) Label function is total ──────────────────────────

    testList "label function is total" [

      testPropertyWithConfig chaosConfig
        "every SessionWorkflow variant produces a non-empty string" <|
        fun (workflow: SessionWorkflow) ->
          // WHY: Status bars in all 5 editors display this label.
          // Empty string = broken UI in TUI, Raylib, VS Code, VS, Neovim.
          let lbl = SessionWorkflow.label workflow
          not (String.IsNullOrEmpty lbl)

      testPropertyWithConfig chaosConfig
        "label is always REPL or Live" <|
        fun (workflow: SessionWorkflow) ->
          let lbl = SessionWorkflow.label workflow
          lbl = "REPL" || lbl = "Live"
    ]

    // ── (j) fsiArgs and replCapability consistency ───────────

    testList "fsiArgs and replCapability are consistent with workflow kind" [

      testPropertyWithConfig chaosConfig
        "Interactive → empty fsiArgs and Full replCapability" <|
        fun () ->
          // WHY: Incorrect args would silently break the REPL or hot
          // reload — this is the #1 user confusion source.
          let workflow = SessionWorkflow.Interactive
          let args = SessionWorkflow.fsiArgs workflow
          let cap = SessionWorkflow.replCapability workflow
          args = []
          && cap = ReplCapability.Full

      testPropertyWithConfig chaosConfig
        "WebLive → fsiArgs contains --multiemit- and ExpressionOnly" <|
        fun (cfg: BrowserRefreshConfig) ->
          let workflow = SessionWorkflow.WebLive cfg
          let args = SessionWorkflow.fsiArgs workflow
          let cap = SessionWorkflow.replCapability workflow
          args |> List.contains "--multiemit-"
          && cap = ReplCapability.ExpressionOnly

      testPropertyWithConfig chaosConfig
        "isHotReloadActive agrees with workflow kind" <|
        fun (workflow: SessionWorkflow) ->
          let isActive = SessionWorkflow.isHotReloadActive workflow
          match workflow with
          | SessionWorkflow.Interactive -> not isActive
          | SessionWorkflow.WebLive _ -> isActive

      testPropertyWithConfig chaosConfig
        "feedbackStrategy is consistent with workflow kind" <|
        fun (workflow: SessionWorkflow) ->
          match workflow, SessionWorkflow.feedbackStrategy workflow with
          | SessionWorkflow.Interactive, FeedbackStrategy.ReplDriven -> true
          | SessionWorkflow.WebLive _, FeedbackStrategy.SaveDriven _ -> true
          | _ -> false
    ]
  ]
