module SageFs.Tests.WorkerLogSinkWiringTests

open System.Collections.Concurrent
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Utils

let private noopHandler (_: WorkerProtocol.WorkerMessage) : Async<WorkerProtocol.WorkerResponse> =
  async { return WorkerProtocol.WorkerResponse.WorkerShuttingDown }

let private startServer () =
  WorkerHttpTransport.startServer
    noopHandler (ref HotReloadState.empty) SageFs.Features.KeptState.Access.none []
    (fun () -> WarmupContext.empty)
    (fun () -> fun _ -> async { return Features.LiveTesting.TestResult.NotRun })
    (fun () -> SageFs.HostAgent.AgentAnswered SageFs.HostAgent.NoCoverage)
    0

[<Tests>]
let workerLogSinkWiringTests =
  testList "WorkerHttpTransport leaves the Log sinks alone" [

    testTask "WHY — startServer — the worker's HTTP host must not replace the Log sinks, because it has no logging providers and swapping them in dropped every Log.* line" {
      let saved = WorkerLogFile.currentSinks ()
      let seenInfo = ConcurrentQueue<string>()
      let seenDebug = ConcurrentQueue<string>()
      WorkerLogFile.restore ({ saved with Info = seenInfo.Enqueue; Debug = seenDebug.Enqueue } : LogSinks)
      try
        let! (server: WorkerHttpTransport.HttpWorkerServer) = startServer ()
        try
          Log.info "sink-wiring-marker %d" 7
          Log.debug "sink-wiring-debug %d" 8
        finally
          (server :> System.IDisposable).Dispose()
      finally
        WorkerLogFile.restore saved
      seenInfo |> Seq.toList |> Expect.contains "the installed Info sink still receives Log.info" "sink-wiring-marker 7"
      seenDebug |> Seq.toList |> Expect.contains "the installed Debug sink still receives Log.debug" "sink-wiring-debug 8"
    }
  ]
