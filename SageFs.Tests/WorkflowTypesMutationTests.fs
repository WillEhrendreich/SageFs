/// ## WorkflowTypes Mutation Tests
///
/// Proves the test suite catches mutations in the SessionWorkflow model —
/// the DU that makes "hot reload + full REPL" structurally unrepresentable.
/// A swapped case or dropped `--multiemit-` flag here would silently let a
/// WebLive session run with the wrong FSI capability. Each case asserts
/// EXACT equality against the correct value so a mutant that returns any
/// other wrong value is killed too.
module WorkflowTypesMutationTests

open Expecto
open Expecto.Flip
open SageFs.WorkflowTypes

let webLive = SessionWorkflow.WebLive BrowserRefreshConfig.defaults

let workflowTypesMutationTests = testList "WorkflowTypes mutations" [

  // ── feedbackStrategy / replCapability / fsiArgs / label / isHotReloadActive ──

  testCase "WHY — interactive_is_ReplDriven_Full_no_args_REPL_label" <| fun () ->
    (SessionWorkflow.feedbackStrategy SessionWorkflow.Interactive,
     SessionWorkflow.replCapability SessionWorkflow.Interactive,
     SessionWorkflow.fsiArgs SessionWorkflow.Interactive,
     SessionWorkflow.label SessionWorkflow.Interactive,
     SessionWorkflow.isHotReloadActive SessionWorkflow.Interactive)
    |> Expect.equal "Interactive must be ReplDriven, Full capability, no extra args, label \"REPL\", hot reload OFF"
      (FeedbackStrategy.ReplDriven, ReplCapability.Full, [], "REPL", false)

  testCase "WHY — webLive_is_SaveDriven_ExpressionOnly_multiemit_Live_label" <| fun () ->
    (SessionWorkflow.feedbackStrategy webLive,
     SessionWorkflow.replCapability webLive,
     SessionWorkflow.fsiArgs webLive,
     SessionWorkflow.label webLive,
     SessionWorkflow.isHotReloadActive webLive)
    |> Expect.equal "WebLive must be SaveDriven, ExpressionOnly capability, [--multiemit-], label \"Live\", hot reload ON"
      (FeedbackStrategy.SaveDriven BrowserRefreshConfig.defaults, ReplCapability.ExpressionOnly, [ "--multiemit-" ], "Live", true)

  testCase "WHY — fromHotReloadBool_true_is_webLive_false_is_interactive — the boolean-to-DU boundary mapping must not be swapped" <| fun () ->
    (SessionWorkflow.fromHotReloadBool true, SessionWorkflow.fromHotReloadBool false)
    |> Expect.equal "true must map to WebLive (defaults), false must map to Interactive"
      (SessionWorkflow.WebLive BrowserRefreshConfig.defaults, SessionWorkflow.Interactive)

  testCase "WHY — replCapability_label_full_and_expressionOnly_are_distinct" <| fun () ->
    (ReplCapability.label ReplCapability.Full, ReplCapability.label ReplCapability.ExpressionOnly)
    |> Expect.equal "labels must be exactly \"Full\" and \"ExpressionOnly\"" ("Full", "ExpressionOnly")

  // ── TransitionCost ────────────────────────────────────────────────────────

  testCase "WHY — transitionCost_zero_isZeroCost_true" <| fun () ->
    TransitionCost.isZeroCost TransitionCost.zero |> Expect.isTrue "the zero cost record must be zero-cost"

  testCase "WHY — transitionCost_definitionsLost_nonzero_is_not_zeroCost" <| fun () ->
    TransitionCost.isZeroCost { TransitionCost.zero with DefinitionsLost = 1 }
    |> Expect.isFalse "any DefinitionsLost > 0 must NOT be zero-cost, even with CellsLost = 0"

  testCase "WHY — transitionCost_cellsLost_nonzero_is_not_zeroCost — both fields must be checked, not just DefinitionsLost" <| fun () ->
    TransitionCost.isZeroCost { TransitionCost.zero with CellsLost = 1 }
    |> Expect.isFalse "any CellsLost > 0 must NOT be zero-cost, even with DefinitionsLost = 0"

  testCase "WHY — transitionCost_compute_maps_fields_without_swapping — evalCount and cellCount must land in their OWN fields, not swapped" <| fun () ->
    TransitionCost.compute 3 7
    |> Expect.equal "compute 3 7 must produce DefinitionsLost=3, CellsLost=7 (not swapped), 15s restart estimate"
      { DefinitionsLost = 3; CellsLost = 7; EstimatedRestart = System.TimeSpan.FromSeconds 15.0 }

  // ── WorkflowSwitchOutcome ─────────────────────────────────────────────────

  testCase "WHY — alreadyInWorkflow_carries_cost_and_readable_message" <| fun () ->
    let cost = { DefinitionsLost = 2; CellsLost = 4; EstimatedRestart = System.TimeSpan.Zero }
    WorkflowSwitchOutcome.alreadyInWorkflow SessionWorkflow.Interactive cost
    |> Expect.equal "AlreadyActive must carry the exact cost and the REPL-labeled message"
      (WorkflowSwitchOutcome.AlreadyActive (cost, "Already in REPL workflow — no switch needed"))

  testCase "WHY — preview_message_names_current_and_target_and_exact_counts" <| fun () ->
    let cost = { DefinitionsLost = 5; CellsLost = 9; EstimatedRestart = System.TimeSpan.Zero }
    WorkflowSwitchOutcome.preview SessionWorkflow.Interactive webLive cost
    |> Expect.equal "preview must name current (REPL), target (Live), and the EXACT lost counts (5 definitions, 9 cells)"
      (WorkflowSwitchOutcome.DryRunPreview (cost, "Preview: switching from REPL to Live would lose 5 definitions and 9 cells"))

  testCase "WHY — switched_message_names_new_session_id" <| fun () ->
    let cost = TransitionCost.zero
    WorkflowSwitchOutcome.switched SessionWorkflow.Interactive webLive cost "abc12345"
    |> Expect.equal "Executed must carry previous, target, cost, the exact new session id, and a message naming it"
      (WorkflowSwitchOutcome.Executed (SessionWorkflow.Interactive, webLive, cost, "abc12345", "Switched from REPL to Live (new session: abc12345)"))

  testCase "WHY — cost_extracts_from_every_outcome_case" <| fun () ->
    let cost = { DefinitionsLost = 1; CellsLost = 2; EstimatedRestart = System.TimeSpan.Zero }
    (WorkflowSwitchOutcome.cost (WorkflowSwitchOutcome.AlreadyActive (cost, "m")),
     WorkflowSwitchOutcome.cost (WorkflowSwitchOutcome.DryRunPreview (cost, "m")),
     WorkflowSwitchOutcome.cost (WorkflowSwitchOutcome.Executed (SessionWorkflow.Interactive, webLive, cost, "id", "m")))
    |> Expect.equal "cost must extract the SAME cost value from all three cases" (cost, cost, cost)

  testCase "WHY — sessionId_only_present_for_Executed — AlreadyActive and DryRunPreview must never carry a session id" <| fun () ->
    let cost = TransitionCost.zero
    (WorkflowSwitchOutcome.sessionId (WorkflowSwitchOutcome.AlreadyActive (cost, "m")),
     WorkflowSwitchOutcome.sessionId (WorkflowSwitchOutcome.DryRunPreview (cost, "m")),
     WorkflowSwitchOutcome.sessionId (WorkflowSwitchOutcome.Executed (SessionWorkflow.Interactive, webLive, cost, "sid", "m")))
    |> Expect.equal "only Executed yields Some sessionId; the other two must be None" (None, None, Some "sid")

  testCase "WHY — wasExecuted_true_only_for_Executed" <| fun () ->
    let cost = TransitionCost.zero
    (WorkflowSwitchOutcome.wasExecuted (WorkflowSwitchOutcome.AlreadyActive (cost, "m")),
     WorkflowSwitchOutcome.wasExecuted (WorkflowSwitchOutcome.DryRunPreview (cost, "m")),
     WorkflowSwitchOutcome.wasExecuted (WorkflowSwitchOutcome.Executed (SessionWorkflow.Interactive, webLive, cost, "sid", "m")))
    |> Expect.equal "wasExecuted must be false, false, true" (false, false, true)

  // ── WorkflowDetection.suggest ────────────────────────────────────────────

  testCase "WHY — suggest_datastar_takes_priority_over_generic_web — a project with BOTH kinds of hits must cite the Datastar reason" <| fun () ->
    WorkflowDetection.suggest [ "Falco.Datastar"; "Falco" ]
    |> Expect.equal "Datastar hits must win the priority order and be the ones reported as detected"
      (Some { SuggestedWorkflow = SessionWorkflow.WebLive BrowserRefreshConfig.defaults
              Reason = "Datastar project detected — Live mode enables SSE-driven DOM morphing"
              DetectedPackages = [ "Falco.Datastar" ] })

  testCase "WHY — suggest_web_only_no_datastar" <| fun () ->
    WorkflowDetection.suggest [ "Falco"; "Newtonsoft.Json" ]
    |> Expect.equal "a web-only project must cite the web reason and the exact matched package"
      (Some { SuggestedWorkflow = SessionWorkflow.WebLive BrowserRefreshConfig.defaults
              Reason = "Web project detected — Live mode enables browser hot reload"
              DetectedPackages = [ "Falco" ] })

  testCase "WHY — suggest_no_hits_is_none — a non-web project must suggest nothing, not default to WebLive" <| fun () ->
    WorkflowDetection.suggest [ "Newtonsoft.Json"; "FSharp.Core" ]
    |> Expect.equal "no matching packages must be None" None

  testCase "WHY — suggest_empty_packages_is_none" <| fun () ->
    WorkflowDetection.suggest [] |> Expect.equal "no packages at all must be None" None

  // ── WorkflowDetection.isTestPackageSet / extractPackageNames ────────────

  testCase "WHY — isTestPackageSet_true_for_expecto_prefix_match" <| fun () ->
    WorkflowDetection.isTestPackageSet [ "Expecto.FsCheck" ]
    |> Expect.isTrue "a package name starting with a known test-package prefix must be recognized"

  testCase "WHY — isTestPackageSet_is_case_insensitive" <| fun () ->
    WorkflowDetection.isTestPackageSet [ "xunit.core" ]
    |> Expect.isTrue "lowercase \"xunit.core\" must match the \"xunit\" prefix (OrdinalIgnoreCase)"

  testCase "WHY — isTestPackageSet_false_for_non_test_packages" <| fun () ->
    WorkflowDetection.isTestPackageSet [ "Falco"; "Newtonsoft.Json" ]
    |> Expect.isFalse "a package list with no test framework must be false"

  testCase "WHY — extractPackageNames_filters_out_test_projects_entirely — a whole project's packages are dropped if it looks like a test project" <| fun () ->
    WorkflowDetection.extractPackageNames [ [ "Falco" ]; [ "Expecto"; "FsCheck" ] ]
    |> Expect.equal "the Expecto-tagged group must be filtered out wholesale, keeping only the non-test group's names" [ "Falco" ]

  testCase "WHY — extractPackageNames_distinct_across_groups — duplicate names across non-test groups must be deduplicated" <| fun () ->
    WorkflowDetection.extractPackageNames [ [ "Falco"; "Serilog" ]; [ "Falco"; "Newtonsoft.Json" ] ]
    |> Expect.equal "Falco appearing in two groups must appear only ONCE in the result" [ "Falco"; "Serilog"; "Newtonsoft.Json" ]
]
