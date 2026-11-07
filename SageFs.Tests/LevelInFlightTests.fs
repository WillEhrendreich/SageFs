/// A level asked while a test run is in flight, with the real pieces a worker is made of: a real FSI host behind the real eval
/// actor, the worker's own HTTP transport (the streamed run live testing uses, and the level route), and the real `LevelKeeper`
/// gate between them. Only the test runner is a stand-in, because a test that blocks until the case lets it go is the thing the
/// case needs. The order it proves: the run in flight finishes on the host it started on, the level acts only after it, and a
/// run that arrives meanwhile waits for the level and runs on the new host.
module SageFs.Tests.LevelInFlightTests

open System
open System.IO
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.ActorCreation
open SageFs.AppState
open SageFs.Server
open SageFs.WorkerProtocol
open SageFs.Tests.TestInfrastructure

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private within (bound: TimeSpan) (pending: Task<'a>) : Task<'a> =
  task {
    let! first = Task.WhenAny(pending :> Task, Task.Delay bound)
    match obj.ReferenceEquals(first, pending) with
    | true -> return! pending
    | false -> return failtestf "did not happen within %A" bound
  }

/// The same bound for a task that has no result.
let private withinSignal (bound: TimeSpan) (pending: Task) : Task =
  task {
    let! first = Task.WhenAny(pending, Task.Delay bound)
    match obj.ReferenceEquals(first, pending) with
    | true -> do! pending
    | false -> failtestf "did not happen within %A" bound
  } :> Task

let private fsharpCoreFixtureProject =
  Path.Combine(RepoPaths.repoPathFull [||], "SageFs.Tests", "fixtures", "FSharpCoreIdentityFixture", "FSharpCoreIdentityFixture.fsproj")

/// POST the streamed run for one test, and keep reading its answer in the background: the task ends when the stream does.
let private streamRun (baseUrl: string) (name: string) : Task =
  task {
    use client = new HttpClient(BaseAddress = Uri baseUrl, Timeout = TestTimeouts.sessionReadyColdBuild)
    let body = Serialization.serialize {| tests = [| WorkerWireRoundTripTests.sampleTest name |]; maxParallelism = 1 |}
    use content = new StringContent(body, Encoding.UTF8, "application/json")
    let! response = client.PostAsync("/run-tests-stream", content)
    let! _ = response.Content.ReadAsStringAsync()
    ()
  }

[<Tests>]
let tests =
  Integration.hostList "A level and a test run in flight, over the real actor, gate and transport" [
    testTask "WHY - the run in flight finishes on the host it started on, the level acts after it, and a run that arrives meanwhile waits and runs on the new host" {
      let loadConfig =
        { SageFs.Args.ProjectLoadConfig.empty with
            Targets = [ SessionProjectTarget.Project fsharpCoreFixtureProject ]
            WorkingDir = Path.GetDirectoryName fsharpCoreFixtureProject }
      let args = { mkCommonActorArgs quietLogger false ignore loadConfig with FsiKind = SessionKinds.Isolated }
      let! session = within TestTimeouts.sessionReadyColdBuild (createActor args |> Async.AwaitTask |> Started.asTask)
      let keeper = LevelKeeper(session.Actor, fun () -> DynamicTests.Absent)
      // What each run saw of the host (the maps array is replaced with the host) when it started and when it ended.
      let hostOfRun1AtStart = ref (box null)
      let hostOfRun1AtEnd = ref (box null)
      let hostOfRun2AtStart = ref (box null)
      let runs = ref 0
      let run1Started = new SemaphoreSlim(0)
      let run2Started = new SemaphoreSlim(0)
      let releaseRun1 = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
      let runTest (tc: Features.LiveTesting.TestCase) : Async<Features.LiveTesting.TestResult> =
        async {
          match Interlocked.Increment runs with
          | 1 ->
            hostOfRun1AtStart.Value <- box session.InstrumentationMapsRef.Value
            run1Started.Release() |> ignore
            do! releaseRun1.Task |> Async.AwaitTask
            hostOfRun1AtEnd.Value <- box session.InstrumentationMapsRef.Value
          | _ ->
            hostOfRun2AtStart.Value <- box session.InstrumentationMapsRef.Value
            run2Started.Release() |> ignore
          return Features.LiveTesting.TestResult.NotRun
        }
      let handler (message: WorkerMessage) : Async<WorkerResponse> =
        async {
          match message with
          | WorkerMessage.LevelRepl rid ->
            let! answer = keeper.Level()
            return WorkerResponse.LevelResult(rid, answer)
          | _ -> return WorkerResponse.WorkerError (SageFsError.EvalFailed "unexpected")
        }
      let! (server: WorkerHttpTransport.HttpWorkerServer) =
        WorkerHttpTransport.startServerGated
          keeper.Hold
          ignore
          ignore
          handler
          (ref HotReloadState.empty)
          Features.KeptState.Access.none
          []
          (fun () -> WarmupContext.empty)
          (fun () -> runTest)
          (fun () -> HostAgent.AgentAnswered HostAgent.NoCoverage)
          0
      use _server = server
      let first = streamRun server.BaseUrl "first"
      do! withinSignal TestTimeouts.patienceBrief (run1Started.WaitAsync())
      // The level is asked while the run is in flight, over the worker's own route.
      let proxy = HttpWorkerClient.httpProxy server.BaseUrl
      let level = proxy (WorkerMessage.LevelRepl "lv") |> Started.asTask
      let! pending = awaitCondition (TestTimeouts.asMs TestTimeouts.patienceBrief) (fun () -> keeper.Standing = GateStanding.LevelPending 1)
      pending |> Expect.isTrue "the level is pending behind the run in flight"
      level.IsCompleted |> Expect.isFalse "and has not acted"
      // A second run arrives while the level is pending, and is held.
      let second = streamRun server.BaseUrl "second"
      do! Task.Delay TestTimeouts.repeatActionEvery
      Volatile.Read(&runs.contents) |> Expect.equal "the second run has not reached the host" 1
      // The first run ends, so the level may act, and when it has, the second run is served.
      releaseRun1.SetResult()
      do! withinSignal TestTimeouts.sessionReadyColdBuild first
      let! answer = within TestTimeouts.sessionReadyColdBuild level
      match answer with
      | WorkerResponse.LevelResult(_, LevelAnswer.Leveled) -> ()
      | other -> failtestf "the level should have replaced the host, got %A" other
      do! withinSignal TestTimeouts.sessionReadyColdBuild (run2Started.WaitAsync())
      do! withinSignal TestTimeouts.sessionReadyColdBuild second
      Object.ReferenceEquals(hostOfRun1AtStart.Value, hostOfRun1AtEnd.Value)
      |> Expect.isTrue "the run in flight ended on the host it started on: the level did not replace it under the run"
      Object.ReferenceEquals(hostOfRun1AtStart.Value, hostOfRun2AtStart.Value)
      |> Expect.isFalse "the run that arrived during the level ran on the new host"
    }
  ]

let private largestProject =
  Path.Combine(RepoPaths.repoPathFull [||], "SageFs.Tests", "SageFs.Tests.fsproj")

/// What the machine was doing while a number was taken: the one-, five- and fifteen-minute load averages.
let private loadAverages () : string =
  try File.ReadAllText("/proc/loadavg").Split(' ') |> Array.truncate 3 |> String.concat " " with _ -> "unreadable"

[<Tests>]
let costTests =
  Integration.hostList "A level on the largest project here" [
    testTask "WHY - the level's own cost on SageFs.Tests, the largest project in this repository, is measured and reported, so the background budget is a number and not a guess" {
      let loadConfig =
        { SageFs.Args.ProjectLoadConfig.empty with
            Targets = [ SessionProjectTarget.Project largestProject ]
            WorkingDir = Path.GetDirectoryName largestProject }
      let args = { mkCommonActorArgs quietLogger false ignore loadConfig with FsiKind = SessionKinds.Isolated }
      let! session = within TestTimeouts.sessionReadyColdBuild (createActor args |> Async.AwaitTask |> Started.asTask)
      let loadBefore = loadAverages ()
      let times = ResizeArray<float>()
      let outside : OutsideFacts = { DynamicTests = DynamicTests.Absent; TestRuns = TestRuns.Idle }
      // The first level runs and records its cost. If that is over the budget, the actor refuses the next ones by itself,
      // so the measurement takes the later resets as a person's hard reset would (no rebuild), which costs the same.
      let! first =
        within TestTimeouts.sessionReadyColdBuild (session.Actor.PostAndAsyncReply(fun reply -> LevelSession(outside, reply)) |> Started.asTask)
      first |> Expect.equal "an untouched REPL levels the first time" LevelAnswer.Leveled
      for _ in 1 .. 5 do
        let watch = System.Diagnostics.Stopwatch.StartNew()
        let! reset =
          within TestTimeouts.sessionReadyColdBuild (session.Actor.PostAndAsyncReply(fun reply -> HardResetSession(false, reply)) |> Started.asTask)
        reset |> Result.isOk |> Expect.isTrue "the reset resolved"
        times.Add watch.Elapsed.TotalMilliseconds
      // Whatever the first level cost, a second one is judged by it.
      let! second =
        within TestTimeouts.sessionReadyColdBuild (session.Actor.PostAndAsyncReply(fun reply -> LevelSession(outside, reply)) |> Started.asTask)
      let lastReset = System.TimeSpan.FromMilliseconds times[times.Count - 1]
      match second, lastReset > Timeouts.replLevelCostBudget with
      | LevelAnswer.LevelDeferred [ LevelBlocker.LevelWouldTakeTooLong _ ], true -> ()
      | LevelAnswer.Leveled, false -> ()
      | other, _ -> failtestf "the second level should follow what a reset costs on this project, got %A" other
      let sorted = times |> Seq.sort |> Array.ofSeq
      eprintfn "REPL-LEVELING large project: SageFs.Tests, five levels (ms): %s; p50 %.0f, max %.0f; load average before %s, after %s; %d logical cores"
        (times |> Seq.map (sprintf "%.0f") |> String.concat ", ") sorted[2] sorted[4] loadBefore (loadAverages ()) Environment.ProcessorCount
    }
  ]

[<Tests>]
let evalWaitsTests =
  Integration.hostList "An eval sent while a level replaces the host" [
    testTask "WHY - an eval that arrives while a level is replacing the host waits for it and runs on the new host, and is never refused" {
      let loadConfig =
        { SageFs.Args.ProjectLoadConfig.empty with
            Targets = [ SessionProjectTarget.Project fsharpCoreFixtureProject ]
            WorkingDir = Path.GetDirectoryName fsharpCoreFixtureProject }
      let args = { mkCommonActorArgs quietLogger false ignore loadConfig with FsiKind = SessionKinds.Isolated }
      let! session = within TestTimeouts.sessionReadyColdBuild (createActor args |> Async.AwaitTask |> Started.asTask)
      let finished = System.Diagnostics.Stopwatch.StartNew()
      let levelDoneAt = ref -1L
      let evalDoneAt = ref -1L
      let outside : OutsideFacts = { DynamicTests = DynamicTests.Absent; TestRuns = TestRuns.Idle }
      let level =
        session.Actor.PostAndAsyncReply(fun reply -> LevelSession(outside, reply))
        |> Started.asTask
      let levelDone = level.ContinueWith(fun (t: Task<LevelAnswer>) -> Volatile.Write(&levelDoneAt.contents, finished.ElapsedTicks); t.Result)
      // The actor is replacing the host: the session is no longer Ready.
      let! replacing = awaitCondition (TestTimeouts.asMs TestTimeouts.patienceBrief) (fun () -> session.GetSessionState() <> SessionState.Ready)
      replacing |> Expect.isTrue "the level is under way"
      let cts = new CancellationTokenSource()
      let eval =
        session.Actor.PostAndAsyncReply(fun reply -> Eval({ Code = "1 + 1"; Args = Map.empty }, cts.Token, reply))
        |> Started.asTask
      let evalDone = eval.ContinueWith(fun (t: Task<EvalResponse>) -> Volatile.Write(&evalDoneAt.contents, finished.ElapsedTicks); t.Result)
      let! answer = within TestTimeouts.sessionReadyColdBuild levelDone
      answer |> Expect.equal "the level replaced the host" LevelAnswer.Leveled
      let! response = within TestTimeouts.sessionReadyColdBuild evalDone
      match response.EvaluationResult with
      | Ok text -> text |> Expect.stringContains "the eval ran" "2"
      | Error error -> failtestf "an eval sent during a level must wait and run, got %s" error.Message
      (Volatile.Read(&levelDoneAt.contents) <= Volatile.Read(&evalDoneAt.contents))
      |> Expect.isTrue "and it ran after the level had finished"
    }
  ]
