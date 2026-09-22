module SageFs.Tests.WorkerProtocolTests // trigger discovery

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.WorkerProtocol
open SageFs.Features.LiveTesting
open SageFs.Tests.SharedGenerators

let roundTrip<'T> (value: 'T) =
  let json = Serialization.serialize value
  let result = Serialization.deserialize<'T> json
  json, result

// ── Wire generators ──
// Every value the daemon and worker can put on the wire. The overrides keep the
// generators inside what the protocol actually carries: strings are non-null
// and valid UTF-16 (JSON cannot carry a lone surrogate), floats are finite
// (JSON has no NaN/Infinity), and DateTimes are UTC or unspecified (the
// processes share one clock, never a local-time conversion).

let private genWireString =
  ArbMap.defaults
  |> ArbMap.generate<string>
  |> Gen.map (fun s ->
    match s with
    | null -> ""
    | s -> s |> String.filter (fun c -> not (Char.IsSurrogate c)))

let private genFiniteFloat =
  ArbMap.defaults
  |> ArbMap.generate<float>
  |> Gen.filter (fun f -> not (Double.IsNaN f || Double.IsInfinity f))

/// Ticks spread over years 0001-6700 (Int32.MaxValue * 1e9 stays below DateTime.MaxValue).
let private genTicks = Gen.choose (0, Int32.MaxValue) |> Gen.map (fun t -> int64 t * 1_000_000_000L)

let private genWireDateTime =
  gen {
    let! ticks = genTicks
    let! kind = Gen.elements [ DateTimeKind.Utc; DateTimeKind.Unspecified ]
    return DateTime(ticks, kind)
  }

let private genWireDateTimeOffset =
  gen {
    let! ticks = genTicks
    let! offsetMinutes = Gen.choose (-14 * 60, 14 * 60)
    let dt = DateTime(ticks + int64 (abs offsetMinutes) * TimeSpan.TicksPerMinute + TimeSpan.TicksPerDay)
    return DateTimeOffset(dt, TimeSpan.FromMinutes (float offsetMinutes))
  }

let private scalarArbs =
  ArbMap.defaults
  |> ArbMap.mergeArb (Arb.fromGen genWireString)
  |> ArbMap.mergeArb (Arb.fromGen genFiniteFloat)
  |> ArbMap.mergeArb (Arb.fromGen genWireDateTime)
  |> ArbMap.mergeArb (Arb.fromGen genWireDateTimeOffset)
  // Only reachable through SageFsError.Unexpected, which is filtered out below.
  |> ArbMap.mergeArb (Arb.fromGen (Gen.constant (exn "unreachable")))

/// SageFsError.Unexpected carries a live .NET exception, which has no JSON
/// round-trip; the worker never sends it (WorkerMain maps failures to the
/// string-carrying cases), so it is outside the wire contract under test.
let private genWireError =
  scalarArbs
  |> ArbMap.generate<SageFsError>
  |> Gen.filter (fun e ->
    match e with
    | SageFsError.Unexpected _ -> false
    | _ -> true)

let private wireArbs = scalarArbs |> ArbMap.mergeArb (Arb.fromGen genWireError)

let genWorkerMessage = wireArbs |> ArbMap.generate<WorkerMessage>
let genWorkerResponse = wireArbs |> ArbMap.generate<WorkerResponse>

[<Tests>]
let workerProtocolTests =
  testList "WorkerProtocol" [

    testList "codec properties" [
      testPropertyWithConfig propConfig
        "WHY — Serialization — every WorkerMessage deserializes to exactly what was serialized because the worker must run the request the daemon sent"
      <| Prop.forAll (Arb.fromGen genWorkerMessage) (fun msg ->
        snd (roundTrip<WorkerMessage> msg) = msg)

      testPropertyWithConfig lightConfig
        "WHY — Serialization — every WorkerResponse deserializes to exactly what was serialized because the daemon must see the worker's real outcome"
      <| Prop.forAll (Arb.fromGen genWorkerResponse) (fun resp ->
        snd (roundTrip<WorkerResponse> resp) = resp)

      testPropertyWithConfig lightConfig
        "WHY — Serialization — re-serializing a decoded WorkerResponse reproduces the same JSON because no field may be dropped or rewritten in transit"
      <| Prop.forAll (Arb.fromGen genWorkerResponse) (fun resp ->
        let json, decoded = roundTrip<WorkerResponse> resp
        Serialization.serialize<WorkerResponse> decoded = json)

      testPropertyWithConfig lightConfig
        "WHY — Serialization — every SessionInfo round-trips because the dashboard and editors render sessions from this JSON"
      <| Prop.forAll
           (Arb.fromGen (wireArbs |> ArbMap.mergeArb (Arb.fromGen genSessionId) |> ArbMap.generate<SessionInfo>))
           (fun info -> snd (roundTrip<SessionInfo> info) = info)
    ]

    testList "WorkerMessage round-trip" [

      testCase "EvalCode round-trips"
      <| fun _ ->
        let msg = WorkerMessage.EvalCode("let x = 42", "r1")
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "should round-trip" msg

      testCase "CheckCode round-trips"
      <| fun _ ->
        let msg = WorkerMessage.CheckCode("let x = 42", "r2")
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "should round-trip" msg

      testCase "GetCompletions round-trips"
      <| fun _ ->
        let msg = WorkerMessage.GetCompletions("System.", 7, "r3")
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "should round-trip" msg

      testCase "CancelEval round-trips"
      <| fun _ ->
        let _, result = roundTrip<WorkerMessage> WorkerMessage.CancelEval
        result |> Expect.equal "should round-trip" WorkerMessage.CancelEval

      testCase "LoadScript round-trips"
      <| fun _ ->
        let msg = WorkerMessage.LoadScript(@"C:\test.fsx", "r4")
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "should round-trip" msg

      testCase "ResetSession round-trips"
      <| fun _ ->
        let msg = WorkerMessage.ResetSession "r5"
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "should round-trip" msg

      testCase "HardResetSession round-trips"
      <| fun _ ->
        let msg = WorkerMessage.HardResetSession(true, "r6")
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "should round-trip" msg

      testCase "GetStatus round-trips"
      <| fun _ ->
        let msg = WorkerMessage.GetStatus "r7"
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "should round-trip" msg

      testCase "Shutdown round-trips"
      <| fun _ ->
        let _, result = roundTrip<WorkerMessage> WorkerMessage.Shutdown
        result |> Expect.equal "should round-trip" WorkerMessage.Shutdown

      testCase "TypeCheckWithSymbols round-trips"
      <| fun _ ->
        let msg = WorkerMessage.TypeCheckWithSymbols("let x = 42", "test.fsx", "r-tc1")
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "should round-trip" msg

      testCase "RunTests round-trips"
      <| fun _ ->
        let tc: SageFs.Features.LiveTesting.TestCase =
          { Id = TestId.TestId "abc123"
            FullName = "MyModule.myTest"
            DisplayName = "myTest"
            Origin = TestOrigin.ReflectionOnly
            Labels = []
            Framework = TestFramework.Expecto
            Category = TestCategory.Unit }
        let msg = WorkerMessage.RunTests([| tc |], 4, "r-run1")
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "should round-trip" msg
    ]

    testList "WorkerResponse round-trip" [

      testCase "EvalResult success round-trips"
      <| fun _ ->
        let resp = WorkerResponse.EvalResult("r1", Ok "val x: int = 42", [], Map.empty)
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "EvalResult with diagnostics round-trips"
      <| fun _ ->
        let diag = {
          Severity = SageFs.Features.Diagnostics.DiagnosticSeverity.Warning
          Message = "unused variable"
          StartLine = 1
          StartColumn = 0
          EndLine = 1
          EndColumn = 5
          ErrorNumber = 0
        }
        let resp = WorkerResponse.EvalResult("r2", Ok "val x = 42", [diag], Map.empty)
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "EvalResult error round-trips"
      <| fun _ ->
        let resp = WorkerResponse.EvalResult("r3", Error (SageFsError.EvalFailed "type mismatch"), [], Map.empty)
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "CheckResult round-trips"
      <| fun _ ->
        let diag = {
          Severity = SageFs.Features.Diagnostics.DiagnosticSeverity.Blocking
          Message = "undefined value"
          StartLine = 3
          StartColumn = 4
          EndLine = 3
          EndColumn = 10
          ErrorNumber = 39
        }
        let resp = WorkerResponse.CheckResult("r4", [diag])
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "CheckResult diagnostic ErrorNumber round-trips"
      <| fun _ ->
        let diag = {
          Severity = SageFs.Features.Diagnostics.DiagnosticSeverity.Blocking
          Message = "undefined value"
          StartLine = 3
          StartColumn = 4
          EndLine = 3
          EndColumn = 10
          ErrorNumber = 39
        }
        let resp = WorkerResponse.CheckResult("r4b", [diag])
        let _, result = roundTrip<WorkerResponse> resp
        match result with
        | WorkerResponse.CheckResult(_, [d]) ->
          d.ErrorNumber |> Expect.equal "ErrorNumber should round-trip" 39
        | other -> failwithf "unexpected: %A" other

      testCase "CompletionResult round-trips"
      <| fun _ ->
        let resp = WorkerResponse.CompletionResult("r5", ["Console"; "Convert"; "Char"])
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "StatusResult round-trips"
      <| fun _ ->
        let status = {
          Status = SessionStatus.Ready
          EvalCount = 10
          AvgDurationMs = 150L
          MinDurationMs = 5L
          MaxDurationMs = 1000L
          StatusMessage = None
          Projects =
            [ { Path = "/src/App/App.fsproj"
                Role = SageFs.ProjectLoading.ProjectRole.Executable
                PackageRefs = [ "Falco" ] } ]
        }
        let resp = WorkerResponse.StatusResult("r6", status)
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "EvalCancelled round-trips"
      <| fun _ ->
        let resp = WorkerResponse.EvalCancelled true
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "ResetResult round-trips"
      <| fun _ ->
        let resp = WorkerResponse.ResetResult("r7", Ok ())
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "HardResetResult round-trips"
      <| fun _ ->
        let resp = WorkerResponse.HardResetResult("r8", Ok "rebuilt in 3.2s")
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "ScriptLoaded round-trips"
      <| fun _ ->
        let resp = WorkerResponse.ScriptLoaded("r9", Ok "loaded successfully")
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "WorkerReady round-trips"
      <| fun _ ->
        let _, result = roundTrip<WorkerResponse> WorkerResponse.WorkerReady
        result |> Expect.equal "should round-trip" WorkerResponse.WorkerReady

      testCase "WorkerShuttingDown round-trips"
      <| fun _ ->
        let _, result = roundTrip<WorkerResponse> WorkerResponse.WorkerShuttingDown
        result |> Expect.equal "should round-trip" WorkerResponse.WorkerShuttingDown

      testCase "WorkerError round-trips"
      <| fun _ ->
        let resp = WorkerResponse.WorkerError (SageFsError.EvalFailed "something went wrong")
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "TypeCheckWithSymbolsResult empty round-trips"
      <| fun _ ->
        let resp = WorkerResponse.TypeCheckWithSymbolsResult("r-tc2", [], [])
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "TypeCheckWithSymbolsResult with data round-trips"
      <| fun _ ->
        let diag = {
          Severity = SageFs.Features.Diagnostics.DiagnosticSeverity.Warning
          Message = "unused variable"
          StartLine = 1; StartColumn = 4
          EndLine = 1; EndColumn = 5
          ErrorNumber = 0
        }
        let sym1 = { WorkerSymbolRef.SymbolFullName = "MyModule.add"; IsFromDefinition = false; FilePath = "MyModule.fs"; Line = 10 }
        let sym2 = { WorkerSymbolRef.SymbolFullName = "MyModule.validate"; IsFromDefinition = true; FilePath = "MyModule.fs"; Line = 20 }
        let resp = WorkerResponse.TypeCheckWithSymbolsResult("r-tc3", [diag], [sym1; sym2])
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "TestRunResults round-trips"
      <| fun _ ->
        let r1: SageFs.Features.LiveTesting.TestRunResult =
          { TestId = TestId.TestId "abc123"
            TestName = "should add"
            Result = TestResult.Passed (TimeSpan.FromMilliseconds 42.0)
            Timestamp = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            Output = None }
        let r2: SageFs.Features.LiveTesting.TestRunResult =
          { TestId = TestId.TestId "def456"
            TestName = "should fail"
            Result = TestResult.Failed(
              TestFailure.AssertionFailed "Expected 42",
              TimeSpan.FromMilliseconds 15.0)
            Timestamp = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            Output = None }
        let resp = WorkerResponse.TestRunResults("r-run1", [| r1; r2 |])
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp
    ]

    testList "SessionStatus.Building" [
      testCase "label renders reason" <| fun () ->
        SessionStatus.label (SessionStatus.Building "dotnet build")
        |> Flip.Expect.equal "label should include reason" "Building (dotnet build)"

      testCase "toSessionState maps to Evaluating" <| fun () ->
        SessionStatus.toSessionState (SessionStatus.Building "compiling")
        |> Flip.Expect.equal "Building maps to Evaluating" SessionState.Evaluating

      testCase "isAlive returns true" <| fun () ->
        SessionStatus.isAlive (SessionStatus.Building "compiling")
        |> Flip.Expect.isTrue "Building session is alive"

      testCase "isOperational returns false" <| fun () ->
        SessionStatus.isOperational (SessionStatus.Building "compiling")
        |> Flip.Expect.isFalse "Building session cannot accept new work"

      testCase "parse round-trips Building reason" <| fun () ->
        let label = SessionStatus.label (SessionStatus.Building "recompiling")
        SessionStatus.parse label
        |> Flip.Expect.equal "should round-trip" (Ok (SessionStatus.Building "recompiling"))

      testCase "parse returns Error for unknown status" <| fun () ->
        match SessionStatus.parse "Teleporting" with
        | Error _ -> ()
        | Ok v -> failwithf "Expected Error but got Ok %A" v
    ]

    testList "SessionInfo" [

      testCase "displayName uses solution root directory name"
      <| fun _ ->
        let info = {
          Id = testSessionId "abc0abc1"
          Name = None
          Projects = ["Tests.fsproj"]
          WorkingDirectory = @"C:\Code\Repos\SageFs\SageFs.Tests"
          SolutionRoot = Some @"C:\Code\Repos\SageFs"
          CreatedAt = DateTime(2026, 1, 1)
          LastActivity = DateTime(2026, 1, 1)
          Status = SessionLifecycleStatus.Ready { Pid = 1234; Port = None }
          Workflow = WorkflowTypes.SessionWorkflow.Interactive
          ActiveProject = None

          ProjectRoles = []

          App = SageFs.AppRun.AppRunState.NotRunning

        }
        SessionInfo.displayName info
        |> Expect.equal "should use solution root dir name" "SageFs"

      testCase "displayName falls back to working directory name"
      <| fun _ ->
        let info = {
          Id = testSessionId "def0def1"
          Name = None
          Projects = ["App.fsproj"]
          WorkingDirectory = @"C:\Code\MyApp"
          SolutionRoot = None
          CreatedAt = DateTime(2026, 1, 1)
          LastActivity = DateTime(2026, 1, 1)
          Status = SessionLifecycleStatus.Ready { Pid = 1; Port = None }
          Workflow = WorkflowTypes.SessionWorkflow.Interactive
          ActiveProject = None

          ProjectRoles = []

          App = SageFs.AppRun.AppRunState.NotRunning

        }
        SessionInfo.displayName info
        |> Expect.equal "should use working dir name" "MyApp"

      testCase "findSolutionRoot finds slnx in ancestor"
      <| fun _ ->
        let tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
        let subDir = Path.Combine(tmp, "SubProject")
        Directory.CreateDirectory(subDir) |> ignore
        File.WriteAllText(Path.Combine(tmp, "MySolution.slnx"), "")
        try
          let result = SessionInfo.findSolutionRoot subDir
          result |> Expect.isSome "should find solution root"
          result |> Expect.equal "should be parent dir containing slnx" (Some tmp)
        finally
          Directory.Delete(tmp, true)

      testCase "findGitRoot finds .git in ancestor"
      <| fun _ ->
        let tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
        let subDir = Path.Combine(tmp, "SubDir")
        Directory.CreateDirectory(subDir) |> ignore
        Directory.CreateDirectory(Path.Combine(tmp, ".git")) |> ignore
        try
          let result = SessionInfo.findGitRoot subDir
          result |> Expect.isSome "should find git root"
          result |> Expect.equal "should be parent dir containing .git" (Some tmp)
        finally
          Directory.Delete(tmp, true)

      testCase "findGitRoot returns None at filesystem root"
      <| fun _ ->
        let root = Path.GetPathRoot(Path.GetTempPath())
        match Directory.Exists(Path.Combine(root, ".git")) with
        | true -> () // skip: filesystem root has .git (extremely unusual environment)
        | false ->
          let result = SessionInfo.findGitRoot root
          result |> Expect.isNone "should not find git root at filesystem root"

      testCase "SessionInfo round-trips through JSON"
      <| fun _ ->
        let info = {
          Id = testSessionId "ab0cde0f"
          Name = None
          Projects = ["A.fsproj"; "B.fsproj"]
          WorkingDirectory = @"C:\Code\Repos\Test"
          SolutionRoot = Some @"C:\Code\Repos\Test"
          CreatedAt = DateTime(2026, 2, 13, 12, 0, 0)
          LastActivity = DateTime(2026, 2, 13, 12, 30, 0)
          Status = SessionLifecycleStatus.Evaluating { Pid = 5678; Port = None }
          Workflow = WorkflowTypes.SessionWorkflow.Interactive
          ActiveProject = None

          ProjectRoles = []

          App = SageFs.AppRun.AppRunState.NotRunning

        }
        let _, result = roundTrip<SessionInfo> info
        result |> Expect.equal "should round-trip" info
    ]

    testList "WorkerSymbolRef conversions" [

      testCase "fromDomain preserves fields"
      <| fun _ ->
        let domainRef: SageFs.Features.LiveTesting.SymbolReference = {
          SymbolFullName = "Helpers.parseInput"
          UseKind = SageFs.Features.LiveTesting.SymbolUseKind.Reference
          UsedInTestId = Some (SageFs.Features.LiveTesting.TestId.create "parseTests.should_parse" TestFramework.Expecto)
          FilePath = "Helpers.fs"
          Line = 42
        }
        let wireRef = WorkerSymbolRef.fromDomain domainRef
        wireRef.SymbolFullName |> Expect.equal "full name preserved" "Helpers.parseInput"
        wireRef.IsFromDefinition |> Expect.isFalse "IsFromDefinition preserved"
        wireRef.FilePath |> Expect.equal "file path preserved" "Helpers.fs"
        wireRef.Line |> Expect.equal "line preserved" 42

      testCase "toDomain sets UsedInTestId to None"
      <| fun _ ->
        let wireRef = { WorkerSymbolRef.SymbolFullName = "M.f"; IsFromDefinition = true; FilePath = "M.fs"; Line = 10 }
        let backRef = WorkerSymbolRef.toDomain wireRef
        backRef.SymbolFullName |> Expect.equal "full name back" "M.f"
        backRef.UseKind |> Expect.equal "UseKind preserved" SageFs.Features.LiveTesting.SymbolUseKind.Definition
        backRef.UsedInTestId |> Expect.isNone "UsedInTestId should be None"
        backRef.FilePath |> Expect.equal "file path back" "M.fs"
        backRef.Line |> Expect.equal "line back" 10
    ]

    testList "HTTP route mapping" [

      testCase "TypeCheckWithSymbols maps to POST /typecheck-symbols"
      <| fun _ ->
        let method, path, body =
          HttpWorkerClient.toRoute (WorkerMessage.TypeCheckWithSymbols("let x = 1", "file.fsx", "r1"))
        method |> Expect.equal "method should be POST" "POST"
        path |> Expect.equal "path" "/typecheck-symbols"
        body.IsSome |> Expect.isTrue "should have body"
        body.Value |> Expect.stringContains "body contains code" "let x = 1"
        body.Value |> Expect.stringContains "body contains filePath" "file.fsx"
        body.Value |> Expect.stringContains "body contains replyId" "r1"
    ]

    testList "App run protocol" [
      let project = "/src/Web/Web.fsproj"
      let at = DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc)
      let running : AppRun.RunningApp =
        { RunId = "run1"
          Project = project
          EntryPoint = "Web.Program.main"
          Endpoint = AppRun.AppEndpoint.Http ("http://127.0.0.1:5123", [ "https://localhost:7001" ])
          StartedAt = at }

      testCase "WHY — WorkerMessage.RunApp — round-trips because daemon and worker must agree on the project to run" <| fun _ ->
        let msg = WorkerMessage.RunApp(project, SageFs.AppRun.PreviousAddress.ReuseAddress "http://127.0.0.1:5123", "r1")
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "round-trip" msg

      testCase "WHY — WorkerMessage.StopApp — round-trips because stop must reach the worker that owns the app" <| fun _ ->
        let msg = WorkerMessage.StopApp(SageFs.AppRun.StopScope.OnlyRun "run1", "r2")
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "round-trip" msg

      testCase "WHY — WorkerMessage.AwaitAppChange — round-trips because the daemon long-polls a specific run" <| fun _ ->
        let msg = WorkerMessage.AwaitAppChange("run1", "r3")
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "round-trip" msg

      testCase "WHY — WorkerResponse.AppRunResult — every app state round-trips because the dashboard renders each one" <| fun _ ->
        [ AppRun.AppRunState.NotRunning
          AppRun.AppRunState.Starting (project, AppRun.StartPhase.RestartingIntoWebLive, at)
          AppRun.AppRunState.Starting (project, AppRun.StartPhase.LaunchingEntryPoint, at)
          AppRun.AppRunState.Running running
          AppRun.AppRunState.Running { running with Endpoint = AppRun.AppEndpoint.NoServer }
          AppRun.AppRunState.Exited (project, 3, at)
          AppRun.AppRunState.Crashed (project, "Missing connection string 'Db'", at) ]
        |> List.iter (fun state ->
          let resp = WorkerResponse.AppRunResult("r1", Ok state)
          let _, result = roundTrip<WorkerResponse> resp
          result |> Expect.equal (sprintf "%A round-trips" state) resp)

      testCase "WHY — WorkerResponse.AppRunResult — an error round-trips because run failures must reach the user" <| fun _ ->
        let resp = WorkerResponse.AppRunResult("r1", Error (SageFsError.AppRunFailed (project, "no entry point")))
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "round-trip" resp

      testCase "WHY — HttpWorkerClient.toRoute — RunApp posts the project to /run-app because the worker route table is shared" <| fun _ ->
        let method, path, body = HttpWorkerClient.toRoute (WorkerMessage.RunApp(project, SageFs.AppRun.PreviousAddress.ReuseAddress "http://127.0.0.1:5123", "r1"))
        method |> Expect.equal "method" "POST"
        path |> Expect.equal "path" "/run-app"
        body |> Expect.isSome "has a body"
        body.Value |> Expect.stringContains "body carries the project" "Web.fsproj"
        body.Value |> Expect.stringContains "body carries the replyId" "r1"

      testCase "WHY — HttpWorkerClient.toRoute — StopApp posts to /stop-app because stop must not be a cacheable GET" <| fun _ ->
        let method, path, _ = HttpWorkerClient.toRoute (WorkerMessage.StopApp(SageFs.AppRun.StopScope.OnlyRun "run1", "r2"))
        method |> Expect.equal "method" "POST"
        path |> Expect.equal "path" "/stop-app"

      testCase "WHY — HttpWorkerClient.toRoute — AwaitAppChange posts the run id to /await-app-change because the long poll is per run" <| fun _ ->
        let method, path, body = HttpWorkerClient.toRoute (WorkerMessage.AwaitAppChange("run1", "r3"))
        method |> Expect.equal "method" "POST"
        path |> Expect.equal "path" "/await-app-change"
        body.Value |> Expect.stringContains "body carries the run id" "run1"
    ]

    // The choke point for a whole bug class: `SessionInfo.LastActivity` (and
    // therefore the dashboard's idle/busy display) is only ever as honest as
    // this classification. A `GetStatus` poll must read as NOT activity, or a
    // genuinely idle session could never show idle; every message a real user
    // action sends must read as activity, or that action silently never
    // refreshes idleness (this exact bug — TouchSession existed and had a
    // handler, but nothing ever posted it).
    testList "WorkerMessage.isActivity" [
      let sampleTc: SageFs.Features.LiveTesting.TestCase =
        { Id = TestId.TestId "abc123"
          FullName = "M.t"; DisplayName = "t"
          Origin = TestOrigin.ReflectionOnly; Labels = []
          Framework = TestFramework.Expecto; Category = TestCategory.Unit }

      testCase "WHY — every activity-bearing message classifies true" <| fun _ ->
        [ WorkerMessage.EvalCode("1+1", "r1")
          WorkerMessage.CheckCode("1+1", "r2")
          WorkerMessage.TypeCheckWithSymbols("1+1", "f.fsx", "r3")
          WorkerMessage.GetCompletions("System.", 7, "r4")
          WorkerMessage.LoadScript(@"C:\a.fsx", "r5")
          WorkerMessage.RunTests([| sampleTc |], 4, "r6")
          WorkerMessage.EvalLiveTestFile("f.fs", "let x = 1", "r7")
          WorkerMessage.RunApp("proj.fsproj", SageFs.AppRun.PreviousAddress.NoPreviousAddress, "r8")
          WorkerMessage.StopApp(SageFs.AppRun.StopScope.OnlyRun "run1", "r9") ]
        |> List.map WorkerMessage.isActivity
        |> Expect.equal "all nine must classify as activity" (List.replicate 9 true)

      testCase "WHY — status/read-only/control messages classify false — a poll must never look like activity" <| fun _ ->
        [ WorkerMessage.CancelEval
          WorkerMessage.ResetSession "r1"
          WorkerMessage.HardResetSession(true, "r2")
          WorkerMessage.GetStatus "r3"
          WorkerMessage.GetLiveValues "r4"
          WorkerMessage.GetTestDiscovery "r5"
          WorkerMessage.GetInstrumentationMaps "r6"
          WorkerMessage.AwaitAppChange("run1", "r7")
          WorkerMessage.Shutdown ]
        |> List.map WorkerMessage.isActivity
        |> Expect.equal "all nine must classify as NOT activity" (List.replicate 9 false)
    ]

    testList "SessionProxy.touching" [
      let echoOn (respond: WorkerMessage -> WorkerResponse) : SessionProxy =
        fun msg -> async { return respond msg }

      testTask "WHY — touch fires exactly once per activity message, never for GetStatus" {
        let touches = ref 0
        let wrapped =
          SessionProxy.touching (fun () -> touches.Value <- touches.Value + 1)
            (echoOn (fun _ -> WorkerResponse.WorkerReady))
        let! _ = wrapped (WorkerMessage.EvalCode("1+1", "r1")) |> Async.StartAsTask
        let! _ = wrapped (WorkerMessage.GetStatus "r2") |> Async.StartAsTask
        let! _ = wrapped (WorkerMessage.CheckCode("1+1", "r3")) |> Async.StartAsTask
        touches.Value |> Expect.equal "two activity messages, one status poll — touch must fire exactly twice" 2
      }

      testTask "WHY — the wrapped proxy still delivers the message and returns the real response" {
        let wrapped =
          SessionProxy.touching ignore
            (echoOn (function
              | WorkerMessage.GetStatus replyId -> WorkerResponse.EvalResult(replyId, Ok "ok", [], Map.empty)
              | other -> failwithf "unexpected message reached the proxy: %A" other))
        let! response = wrapped (WorkerMessage.GetStatus "r1") |> Async.StartAsTask
        response
        |> Expect.equal "the underlying proxy's real response must pass through untouched" (WorkerResponse.EvalResult("r1", Ok "ok", [], Map.empty))
      }
    ]
  ]
