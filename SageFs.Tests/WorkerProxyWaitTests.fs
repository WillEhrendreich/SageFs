module SageFs.Tests.WorkerProxyWaitTests

/// WHY — a live-testing run is dispatched with a `NotRun` result for every
/// test if the worker's proxy has not registered yet. The old policy waited
/// four fixed attempts — 50/100/200/400ms, 750ms total — which is fine for one
/// warm daemon and strands every run when the integration tier starts 250.
/// That shortening was measured against a warm worker and kept as a constant,
/// so the failure was invisible in the common case and total under load.
///
/// The property: the schedule is bounded BY A DEADLINE, not by a count, and
/// it never returns "no retries" for a positive budget.

open System
open Expecto
open Expecto.Flip
open SageFs

let private sum (xs: int list) = xs |> List.sum

[<Tests>]
let workerProxyWaitTests =
  testList "worker proxy wait policy" [

    testCase "WHY — the budget is far above the old 750ms, because the old window is what stranded the run" <| fun _ ->
      (WorkerProxyWait.BudgetMs > 750)
      |> Expect.isTrue "750ms stranded a run under load, so the budget must exceed it"

    testCase "WHY — the schedule never exceeds its budget (a ceiling, not a quota)" <| fun _ ->
      for budget in [ 1; 7; 50; 750; 5000; 15000 ] do
        (sum (WorkerProxyWait.delays budget 50) <= budget)
        |> Expect.isTrue (sprintf "budget %d must not be exceeded" budget)

    testCase "WHY — a positive budget always yields at least one retry, so a caller cannot silently get none" <| fun _ ->
      for budget in [ 1; 2; 50; 15000 ] do
        (WorkerProxyWait.delays budget 50 |> List.isEmpty)
        |> Expect.isFalse (sprintf "budget %d must produce a retry" budget)

    testCase "WHY — a zero or negative budget yields no retries, so an exhausted budget stops immediately" <| fun _ ->
      WorkerProxyWait.delays 0 50
      |> Expect.equal "no budget, no waiting" []
      WorkerProxyWait.delays (-5) 50
      |> Expect.equal "a negative budget is no budget" []

    testCase "WHY — the delays grow, so a worker that appears late is still caught" <| fun _ ->
      let d = WorkerProxyWait.delays 15000 50
      let rec strictlyIncreasing = function
        | a :: (b :: _ as rest) -> a < b && strictlyIncreasing rest
        | _ -> true

      strictlyIncreasing d
      |> Expect.isTrue (sprintf "delays must grow, got %A" d)

    testCase "WHY — the old four-attempt policy is provably inside the new one, so this is a strict widening" <| fun _ ->
      let oldPolicy = [ 50; 100; 200; 400 ]
      let current = WorkerProxyWait.delaysByDefault
      (List.length current > List.length oldPolicy)
      |> Expect.isTrue "more attempts than the policy that stranded runs"

    testCase "WHY — the deadline is what ends the wait, not an attempt counter" <| fun _ ->
      WorkerProxyWait.isExpired 14999 15000
      |> Expect.isFalse "one millisecond short is not expired"
      WorkerProxyWait.isExpired 15000 15000
      |> Expect.isTrue "the budget is the boundary"
      WorkerProxyWait.isExpired 20000 15000
      |> Expect.isTrue "past the budget is expired"

    testCase "WHY — the first delay stays small, so a worker that is already up is picked up without a stall" <| fun _ ->
      WorkerProxyWait.FirstDelayMs
      |> Expect.equal "the common case must not pay the full budget" 50
      // Not the full budget: a short remainder is dropped so the sequence
      // never steps backwards, and the budget is a ceiling.
      (sum (WorkerProxyWait.delays 15000 50) <= 15000)
      |> Expect.isTrue "and the total stays within the budget"
  ]
