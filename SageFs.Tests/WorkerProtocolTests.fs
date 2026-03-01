module SageFs.Tests.WorkerProtocolTests // trigger discovery

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.Features.LiveTesting

let roundTrip<'T> (value: 'T) =
  let json = Serialization.serialize value
  let result = Serialization.deserialize<'T> json
  json, result

[<Tests>]
let workerProtocolTests =
  testList "WorkerProtocol" [

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
            Framework = "expecto"
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
          Severity = SageFs.Features.Diagnostics.DiagnosticSeverity.Error
          Message = "undefined value"
          StartLine = 3
          StartColumn = 4
          EndLine = 3
          EndColumn = 10
        }
        let resp = WorkerResponse.CheckResult("r4", [diag])
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

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
        let resp = WorkerResponse.TypeCheckWithSymbolsResult("r-tc2", false, [], [])
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "TypeCheckWithSymbolsResult with data round-trips"
      <| fun _ ->
        let diag = {
          Severity = SageFs.Features.Diagnostics.DiagnosticSeverity.Warning
          Message = "unused variable"
          StartLine = 1; StartColumn = 4
          EndLine = 1; EndColumn = 5
        }
        let sym1 = { WorkerSymbolRef.SymbolFullName = "MyModule.add"; IsFromDefinition = false; FilePath = "MyModule.fs"; Line = 10 }
        let sym2 = { WorkerSymbolRef.SymbolFullName = "MyModule.validate"; IsFromDefinition = true; FilePath = "MyModule.fs"; Line = 20 }
        let resp = WorkerResponse.TypeCheckWithSymbolsResult("r-tc3", true, [diag], [sym1; sym2])
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

    testList "SessionInfo" [

      testCase "displayName uses solution root directory name"
      <| fun _ ->
        let info = {
          Id = "session-abc"
          Name = None
          Projects = ["Tests.fsproj"]
          WorkingDirectory = @"C:\Code\Repos\SageFs\SageFs.Tests"
          SolutionRoot = Some @"C:\Code\Repos\SageFs"
          CreatedAt = DateTime(2026, 1, 1)
          LastActivity = DateTime(2026, 1, 1)
          Status = SessionStatus.Ready
          WorkerPid = Some 1234
        }
        SessionInfo.displayName info
        |> Expect.equal "should use solution root dir name" "SageFs"

      testCase "displayName falls back to working directory name"
      <| fun _ ->
        let info = {
          Id = "session-def"
          Name = None
          Projects = ["App.fsproj"]
          WorkingDirectory = @"C:\Code\MyApp"
          SolutionRoot = None
          CreatedAt = DateTime(2026, 1, 1)
          LastActivity = DateTime(2026, 1, 1)
          Status = SessionStatus.Ready
          WorkerPid = None
        }
        SessionInfo.displayName info
        |> Expect.equal "should use working dir name" "MyApp"

      ptestCase "findSolutionRoot finds slnx in ancestor"
      <| fun _ ->
        let result = SessionInfo.findSolutionRoot @"C:\Code\Repos\SageFs\SageFs.Tests"
        result |> Expect.isSome "should find solution root"
        result |> Expect.equal "should be repo root" (Some @"C:\Code\Repos\SageFs")

      ptestCase "findGitRoot finds .git in ancestor"
      <| fun _ ->
        let result = SessionInfo.findGitRoot @"C:\Code\Repos\SageFs\SageFs.Core"
        result |> Expect.isSome "should find git root"
        result |> Expect.equal "should be repo root" (Some @"C:\Code\Repos\SageFs")

      ptestCase "findGitRoot returns None at filesystem root"
      <| fun _ ->
        let result = SessionInfo.findGitRoot @"C:\"
        result |> Expect.isNone "should not find git root at C:\\"

      testCase "SessionInfo round-trips through JSON"
      <| fun _ ->
        let info = {
          Id = "session-xyz"
          Name = None
          Projects = ["A.fsproj"; "B.fsproj"]
          WorkingDirectory = @"C:\Code\Repos\Test"
          SolutionRoot = Some @"C:\Code\Repos\Test"
          CreatedAt = DateTime(2026, 2, 13, 12, 0, 0)
          LastActivity = DateTime(2026, 2, 13, 12, 30, 0)
          Status = SessionStatus.Evaluating
          WorkerPid = Some 5678
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
          UsedInTestId = Some (SageFs.Features.LiveTesting.TestId.create "parseTests.should_parse" "expecto")
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
  ]
