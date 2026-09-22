module SageFs.Tests.EvalStatsWorkerReadTests

// RED-first regression tests for the dashboard's eval-performance readout
// (sagefs-ux-roast.md §3.1/§11 Island B items 1-2):
//
//   1. `DaemonMode.getEvalStatsFromWorker` used to hand-parse the worker's
//      raw `/status` JSON, reading `evalCount`/`avgDurationMs`/... off the
//      envelope ROOT. The worker's actual reply nests them one level down
//      inside a `StatusResult(replyId, snapshot)` union case
//      (`{"type":"StatusResult","value":["probe",{"evalCount":1,...}]}`),
//      so every read silently returned the default and the dashboard's
//      eval counter/avg/min/max was permanently zero. The fix routes
//      through the SAME typed `WorkerMessage.GetStatus` /
//      `WorkerResponse.StatusResult` proxy `/api/sessions` already uses —
//      these tests prove the real numbers now come through, including a
//      round-trip through the EXACT wire encoding (`WorkerProtocol.
//      Serialization`) the roast's own JSON example used, so a future
//      regression to a hand-rolled parse would be caught here.
//
//   2. "Worker unreachable" used to collapse to the exact same
//      `EvalStats.empty` value as "the worker answered zero evals" —
//      indistinguishable to every reader. `EvalStatsReading` makes the two
//      different values; `toEvalStats` is the explicit, named place that
//      collapses them back for callers that only want a number.

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.Server.DaemonMode

let private sid =
  match SessionId.validate "0a0b0c0d" with
  | Ok id -> id
  | Error e -> failwith e

let private statusSnapshot (evalCount: int) (avgMs: int64) (minMs: int64) (maxMs: int64) : WorkerStatusSnapshot =
  { Status = SessionStatus.Ready
    StatusMessage = None
    EvalCount = evalCount
    AvgDurationMs = avgMs
    MinDurationMs = minMs
    MaxDurationMs = maxMs
    Projects = []
    CoreVersion = "0.0.0-test" }

/// A `getProxy` that always answers with the given `WorkerResponse` for
/// `GetStatus`, exactly as `SessionOps.GetProxy`'s `SessionProxy` would.
let private proxyReturning (response: WorkerResponse) : WorkerProtocol.SessionId -> Task<WorkerProtocol.SessionProxy option> =
  fun _ -> Task.FromResult(Some (fun (_msg: WorkerMessage) -> async { return response }))

let private proxyThrowing (ex: exn) : WorkerProtocol.SessionId -> Task<WorkerProtocol.SessionProxy option> =
  fun _ -> Task.FromResult(Some (fun (_msg: WorkerMessage) -> async { return raise ex }))

let private noProxy : WorkerProtocol.SessionId -> Task<WorkerProtocol.SessionProxy option> =
  fun _ -> Task.FromResult None

[<Tests>]
let evalStatsWorkerReadTests =
  testList "Eval stats worker read" [

    testTask "a real 20.4s eval's StatusResult produces the real EvalStats, not zero (§3.1)" {
      let snap = statusSnapshot 1 20468L 20468L 20468L
      let! (stats: Affordances.EvalStats) =
        getEvalStatsFromWorker (proxyReturning (WorkerResponse.StatusResult("dash-stats", snap))) sid
      stats.EvalCount |> Expect.equal "evalCount should be 1, not the old hardcoded-zero" 1
      stats.TotalDuration.TotalMilliseconds |> Expect.floatClose "total duration should reflect the real 20468ms avg" Accuracy.medium 20468.0
      stats.MinDuration.TotalMilliseconds |> Expect.floatClose "min duration should be 20468ms" Accuracy.medium 20468.0
      stats.MaxDuration.TotalMilliseconds |> Expect.floatClose "max duration should be 20468ms" Accuracy.medium 20468.0
    }

    testTask "the fix survives a real JSON round-trip through the wire envelope the roast measured (§3.1)" {
      // Exactly the shape sagefs-ux-roast.md §3.1 quoted:
      // {"type":"StatusResult","value":["probe",{"evalCount":1,"avgDurationMs":20468,...}]}
      // A hand-rolled root-level parse of this JSON would read `evalCount`
      // off the OUTER object (which has none) and silently default to 0.
      let snap = statusSnapshot 1 20468L 20468L 20468L
      let wire = Serialization.serialize (WorkerResponse.StatusResult("probe", snap))
      wire |> Expect.stringContains "the wire shape nests the snapshot one level down, as the roast measured" "\"value\":[\"probe\""
      let roundTripped = Serialization.deserialize<WorkerResponse> wire
      let! (stats: Affordances.EvalStats) =
        getEvalStatsFromWorker (proxyReturning roundTripped) sid
      stats.EvalCount |> Expect.equal "evalCount should survive the real wire round-trip" 1
      stats.TotalDuration.TotalMilliseconds |> Expect.floatClose "avg*count should survive the round-trip" Accuracy.medium 20468.0
    }

    testTask "a worker that genuinely has zero evals reads as Live EvalStats.empty (§11 Island B item 2)" {
      let snap = statusSnapshot 0 0L 0L 0L
      let! (reading: EvalStatsReading) =
        getEvalStatsReadingFromWorker (proxyReturning (WorkerResponse.StatusResult("dash-stats", snap))) sid
      match reading with
      | EvalStatsReading.Live stats -> stats.EvalCount |> Expect.equal "zero evals is a legitimate Live answer" 0
      | EvalStatsReading.Unreachable reason -> failwithf "expected Live, got Unreachable %s" reason
    }

    testTask "no worker registered reads as Unreachable, not the same value as zero evals (§11 Island B item 2)" {
      let! (reading: EvalStatsReading) = getEvalStatsReadingFromWorker noProxy sid
      match reading with
      | EvalStatsReading.Unreachable _ -> ()
      | EvalStatsReading.Live stats -> failwithf "expected Unreachable, got Live %A" stats
    }

    testTask "an HTTP failure reaching the worker reads as Unreachable (§11 Island B item 2)" {
      let! (reading: EvalStatsReading) =
        getEvalStatsReadingFromWorker (proxyThrowing (Net.Http.HttpRequestException "connection refused")) sid
      match reading with
      | EvalStatsReading.Unreachable _ -> ()
      | EvalStatsReading.Live stats -> failwithf "expected Unreachable, got Live %A" stats
    }

    testTask "Live and Unreachable are different EvalStatsReading values even though toEvalStats collapses them the same way" {
      let liveZero = EvalStatsReading.Live Affordances.EvalStats.empty
      let unreachable = EvalStatsReading.Unreachable "no worker is registered for this session"
      (liveZero = unreachable) |> Expect.isFalse "Live EvalStats.empty and Unreachable must not be the same value"
      EvalStatsReading.toEvalStats liveZero
      |> Expect.equal "toEvalStats collapses Live .empty to .empty for number-only callers" Affordances.EvalStats.empty
      EvalStatsReading.toEvalStats unreachable
      |> Expect.equal "toEvalStats collapses Unreachable to .empty for number-only callers" Affordances.EvalStats.empty
    }

    testTask "getEvalStatsFromWorker (the back-compat entry point) still returns a plain EvalStats when unreachable" {
      let! (stats: Affordances.EvalStats) = getEvalStatsFromWorker noProxy sid
      stats |> Expect.equal "unreachable collapses to .empty for DashboardQueries.GetEvalStats" Affordances.EvalStats.empty
    }
  ]
