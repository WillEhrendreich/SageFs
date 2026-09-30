module SageFs.Tests.WorkerLogSinkWiringTests

open System
open System.Collections.Concurrent
open System.IO
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

/// `Log`'s sinks are process-global, so every test that swaps them lives in this
/// one sequenced list: two of them interleaving would each restore the other's
/// sinks and lose the entries they are asserting on.
[<Tests>]
let workerLogSinkWiringTests =
  testSequenced
  <| testList "Worker Log sinks (process-global, sequenced)" [

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
          (server :> IDisposable).Dispose()
      finally
        WorkerLogFile.restore saved
      seenInfo |> Seq.toList |> Expect.contains "the installed Info sink still receives Log.info" "sink-wiring-marker 7"
      seenDebug |> Seq.toList |> Expect.contains "the installed Debug sink still receives Log.debug" "sink-wiring-debug 8"
    }

    testCase "WHY — tryInstall — Log.warn lands in <data dir>/workers/<sessionId>.log, which is what the worker's ClearProviders() used to swallow" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "sagefs-workerlog-install-" + Guid.NewGuid().ToString("N"))
      let saved = WorkerLogFile.currentSinks ()
      try
        match WorkerLogFile.tryInstall dir "cafe0001" with
        | Error err -> failtestf "install should succeed, got %A" err
        | Ok writer ->
          try
            Log.warn "Loader returned %d projects, attempting manual fsproj parse" 0
          finally
            WorkerLogFile.restore saved
            (writer :> IDisposable).Dispose()
        File.ReadAllText(Path.Combine(dir, "workers", "cafe0001.log"))
        |> Expect.stringContains "the Log.warn line landed" "[WRN] Loader returned 0 projects, attempting manual fsproj parse"
      finally
        WorkerLogFile.restore saved
        try Directory.Delete(dir, true) with _ -> ()

    testCase "WHY — tryInstall — a hostile session id is an error and leaves the Log sinks exactly as they were" <| fun _ ->
      let saved = WorkerLogFile.currentSinks ()
      match WorkerLogFile.tryInstall (Path.GetTempPath()) "../escape" with
      | Ok writer ->
        (writer :> IDisposable).Dispose()
        failtest "a path-escaping id must not install"
      | Error _ ->
        let after = WorkerLogFile.currentSinks ()
        Object.ReferenceEquals(saved.Info, after.Info)
        |> Expect.isTrue "sinks unchanged"
  ]
