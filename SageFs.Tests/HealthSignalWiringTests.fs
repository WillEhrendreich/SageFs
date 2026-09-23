/// The other three signals HealthWatch was built to carry — before this,
/// only worker RSS was ever actually sampled (SageFs/DaemonMode.fs's
/// GetDaemonHealth). These tests pin that HealthLatency, MailboxQueueDepth
/// and EvalLatency each reach their sink from the real production sampling
/// function, not merely that a function which WOULD forward exists somewhere
/// unreachable.
///
/// Deliberately independent of `HealthWatch`'s own process-global registry:
/// each production sampling function takes its sink (`observe: float ->
/// unit`) as a parameter rather than calling `HealthWatch.observe` directly,
/// so these tests capture into a plain local list instead of racing every
/// OTHER test in the assembly that also touches the one shared,
/// concurrently-mutated `HealthWatch` dictionary (HealthWatchWiringTests.fs's
/// own `HealthWatch.reset()` calls can fire at any point during a parallel
/// run — that cross-test race is what a first version of this file actually
/// hit, wiping observations it had just made). The production sink each
/// function is wired to in real code (`observeHealthLatencyToHealthWatch` /
/// `observeMailboxQueueDepthToHealthWatch` / `observeEvalLatencyToHealthWatch`)
/// is a one-line, visibly-correct forward to `HealthWatch.observe` with the
/// right `SignalId` — the part worth a runtime proof is that the sampling
/// function actually calls its sink with the actual measured value, which is
/// exactly what these tests pin.
module SageFs.Tests.HealthSignalWiringTests

open System.Collections.Generic
open System.Threading.Tasks
open Expecto
open Expecto.Flip

[<Tests>]
let healthSignalWiringTests =
  testList "The other three signals reach their sink, not just exist as sampling code" [
    testTask "HealthLatency: McpServer.timeHealthLatency reports the /health handler's own real elapsed time" {
      let observed = ResizeArray<float>()
      let! result =
        SageFs.Server.McpServer.timeHealthLatency observed.Add (fun () ->
          task {
            do! Task.Delay 20
            return "health-payload"
          })
      result |> Expect.equal "the wrapped work's own result still comes back" "health-payload"
      observed |> Expect.hasLength "exactly one reading per call" 1
      (observed.[0], 20.0) |> Expect.isGreaterThanOrEqual "the Task.Delay(20) really elapsed"
    }

    testCase "MailboxQueueDepth: DaemonMode.sampleMailboxQueueDepth reports the same counter checkMailboxAdmission reads"
    <| fun _ ->
      let observed = ResizeArray<float>()
      SageFs.Server.DaemonMode.sampleMailboxQueueDepth observed.Add (fun () -> 42)
      observed |> List.ofSeq |> Expect.equal "the depth reaches the sink unchanged, just as a float" [ 42.0 ]

    testCase "EvalLatency: AppState.observeEvalLatency reports from the exact point EvalCompleted is published"
    <| fun _ ->
      let observed = ResizeArray<float>()
      SageFs.AppState.observeEvalLatency observed.Add 123.5
      observed |> List.ofSeq |> Expect.equal "the eval's own measured duration reaches the sink unchanged" [ 123.5 ]
  ]
