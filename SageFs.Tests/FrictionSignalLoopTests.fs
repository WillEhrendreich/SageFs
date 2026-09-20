module SageFs.Tests.FrictionSignalLoopTests

// Closes the friction-detection -> action-queue loop (sagefs-roast.md
// §1 "the friction subsystem... reports to a store no product surface acts
// on" + §3 "PushEvent.ActionQueueReady/ImpactAlert... constructed ONLY in
// SageFs.Tests/PushEventTests.fs"):
//
//   A1 — a detected friction signal produces at least one ranked action.
//   A2 — an unhealthy action queue report pushes ActionQueueReady through
//        the SAME accumulate-event path DiagnosisReady already uses, with
//        no-change suppression under repeated ModelChanged-style ticks.
//   A3 — only cells transitively downstream of a changed cell are reported
//        stale (previously: every cell in the graph, on every tick).

open Expecto
open Expecto.Flip
open SageFs.Features.ActionPrioritizer
open SageFs.Features.CellDependencyGraph
open SageFs.McpPushNotifications
open SageFs.Server.McpServer

let private emptyGraph : CellGraph = { Cells = Map.empty; Edges = [] }

// ── A1 — a detected friction signal produces at least one ranked action ────

[<Tests>]
let frictionSignalProducesActionTests = testList "ActionPrioritizer friction signals" [

  testCase "a detected friction signal produces at least one ranked action naming its evidence" <| fun _ ->
    let signal : FrictionSignalReport =
      { SignalId = "observed.reset-thrash"; EvidenceCount = 4; WindowDescription = "14:02 .. 14:03" }

    let report = ActionPrioritizer.compose emptyGraph [] [] Set.empty [ signal ]

    report.Actions
    |> List.exists (fun a ->
      match a.Kind with
      | ActionKind.AddressFriction signalId -> signalId = "observed.reset-thrash"
      | _ -> false)
    |> Expect.isTrue "friction signal produced an AddressFriction action"

    report.Actions
    |> List.exists (fun a -> a.Reason.Contains "4" && a.Reason.Contains "observed.reset-thrash")
    |> Expect.isTrue "rationale names the signal id and its evidence count"

  testCase "no friction signals produces no friction actions" <| fun _ ->
    let report = ActionPrioritizer.compose emptyGraph [] [] Set.empty []

    report.Actions
    |> List.exists (fun a -> match a.Kind with ActionKind.AddressFriction _ -> true | _ -> false)
    |> Expect.isFalse "nothing detected, nothing to report"
]

// ── A3 — only cells downstream of a real change are stale ──────────────────

[<Tests>]
let staleCellPropagationTests = testList "ActionPrioritizer stale cell propagation" [

  testCase "only the downstream cell is reported stale when its upstream cell is edited" <| fun _ ->
    let cellA : CellInfo = { Id = 1; Source = "let a = 1"; Produces = [ "a" ]; Consumes = [] }
    let cellB : CellInfo = { Id = 2; Source = "let b = a + 1"; Produces = [ "b" ]; Consumes = [ "a" ] }
    let graph = buildGraph [ cellA; cellB ]

    let report = ActionPrioritizer.compose graph [] [] (Set.ofList [ 1 ]) []

    let staleCellIds =
      report.Actions
      |> List.choose (fun a -> match a.Kind with ActionKind.ReEvaluateCell cid -> Some cid | _ -> None)

    staleCellIds |> Expect.equal "exactly cell 2 is stale — cell 1 was just edited, not stale" [ 2 ]

  testCase "an untouched cell graph reports no stale cells" <| fun _ ->
    let cellA : CellInfo = { Id = 1; Source = "let a = 1"; Produces = [ "a" ]; Consumes = [] }
    let cellB : CellInfo = { Id = 2; Source = "let b = a + 1"; Produces = [ "b" ]; Consumes = [ "a" ] }
    let graph = buildGraph [ cellA; cellB ]

    let report = ActionPrioritizer.compose graph [] [] Set.empty []

    report.Actions
    |> List.exists (fun a -> match a.Kind with ActionKind.ReEvaluateCell _ -> true | _ -> false)
    |> Expect.isFalse "nothing changed, nothing is stale"
]

// ── A2 — an unhealthy action queue pushes ActionQueueReady ──────────────────

[<Tests>]
let actionQueuePushTests = testList "ActionQueuePush" [

  testCase "a Healthy report never pushes" <| fun _ ->
    let tracker = McpServerTracker()
    let lastPushed = ref (None: ActionQueueReport option)

    ActionQueuePush.pushIfActionable tracker (Some "s1") lastPushed ActionQueueReport.empty

    tracker.PendingEvents |> Expect.equal "a healthy queue is a no-op" 0

  testCase "an unhealthy report pushes ActionQueueReady through the SAME accumulate-event path DiagnosisReady uses" <| fun _ ->
    let tracker = McpServerTracker()
    let lastPushed = ref (None: ActionQueueReport option)
    let unhealthy = { ActionQueueReport.empty with HealthGrade = SessionHealthGrade.Critical 1; TotalFailures = 1 }

    ActionQueuePush.pushIfActionable tracker (Some "s1") lastPushed unhealthy

    tracker.PendingEvents |> Expect.equal "one event queued" 1
    tracker.DrainEvents(Some "s1")
    |> Array.exists (fun formatted -> formatted.Contains "action queue")
    |> Expect.isTrue "the drained, LLM-formatted event names the action queue (PushEvent.formatForLlm's ActionQueueReady branch)"

  testCase "the SAME unhealthy report is never pushed twice in a row" <| fun _ ->
    let tracker = McpServerTracker()
    let lastPushed = ref (None: ActionQueueReport option)
    let unhealthy = { ActionQueueReport.empty with HealthGrade = SessionHealthGrade.Critical 1; TotalFailures = 1 }

    ActionQueuePush.pushIfActionable tracker (Some "s1") lastPushed unhealthy
    tracker.DrainEvents(Some "s1") |> ignore // drain the first push
    ActionQueuePush.pushIfActionable tracker (Some "s1") lastPushed unhealthy

    tracker.PendingEvents |> Expect.equal "a repeat tick with the SAME report pushes nothing new" 0

  testCase "a genuinely changed unhealthy report pushes again after a repeat" <| fun _ ->
    let tracker = McpServerTracker()
    let lastPushed = ref (None: ActionQueueReport option)
    let first = { ActionQueueReport.empty with HealthGrade = SessionHealthGrade.Critical 1; TotalFailures = 1 }
    let second = { ActionQueueReport.empty with HealthGrade = SessionHealthGrade.Critical 2; TotalFailures = 2 }

    ActionQueuePush.pushIfActionable tracker (Some "s1") lastPushed first
    tracker.DrainEvents(Some "s1") |> ignore
    ActionQueuePush.pushIfActionable tracker (Some "s1") lastPushed second

    tracker.PendingEvents |> Expect.equal "a real transition pushes again" 1
]
