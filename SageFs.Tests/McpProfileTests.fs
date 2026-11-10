module SageFs.Tests.McpProfileTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.McpRouteError
open SageFs.McpProfile

/// The decisions in the `profile` tool that need no daemon: which process to record, which assemblies are the user's, what the
/// reply says when the trace is read, refused or the host crashed.

let private hostReply (host: WorkerProtocol.HostProcess) : Result<WorkerProtocol.WorkerResponse, RouteError> =
  Result.Ok(WorkerProtocol.WorkerResponse.HostProcessResult("r1", host))

/// A trace with 30 ms of managed CPU in one eval-defined method and 500 ms in native code.
let private trace = RecordedTrace.Speedscope SageFs.Simulation.ProfileRecorderSim.goodTrace

/// A nettrace with two sampled allocations of strings (one at a function typed into the session, one at a project's) and one of
/// another type.
let private allocTrace =
  RecordedTrace.NetTrace(SageFs.Tests.ProfileNetTraceTests.Fixture.traceWith [ "System.String", 100000, 1; "System.String", 100000, 2; "Foo.Bar", 50000, 3 ])

[<Tests>]
let tests =
  testList "profile tool decisions" [

    testList "the process to record" [
      testCase "WHY - an isolated host is recorded by its process id" <| fun _ ->
        hostPidOf (hostReply (WorkerProtocol.HostProcess.Isolated 4242)) |> Expect.equal "pid" (Result.Ok 4242)

      testCase "WHY - a session that runs inside the worker is refused with the reason" <| fun _ ->
        match hostPidOf (hostReply WorkerProtocol.HostProcess.InWorker) with
        | Result.Error(ProfileFailure.NotRecordable reason) -> reason |> Expect.stringContains "says where the code runs" "worker process"
        | other -> failtestf "got %A" other

      testCase "WHY - a session with no running FSI is refused, not recorded" <| fun _ ->
        match hostPidOf (hostReply WorkerProtocol.HostProcess.NotRunning) with
        | Result.Error(ProfileFailure.NotRecordable _) -> ()
        | other -> failtestf "got %A" other

      testCase "WHY - a worker that cannot be reached is CouldNotAttach with what the route said" <| fun _ ->
        match hostPidOf (Result.Error(Message "Session 'x' not found")) with
        | Result.Error(ProfileFailure.CouldNotAttach detail) -> detail |> Expect.stringContains "carries the route's words" "not found"
        | other -> failtestf "got %A" other

      testCase "WHY - another reply to the question is refused and named" <| fun _ ->
        match hostPidOf (Result.Ok(WorkerProtocol.WorkerResponse.EvalCancelled true)) with
        | Result.Error(ProfileFailure.CouldNotAttach _) -> ()
        | other -> failtestf "got %A" other
    ]

    testList "which assemblies are the user's" [
      testCase "WHY - a project path gives its assembly name and an empty one gives nothing" <| fun _ ->
        projectModulesOf [ "/work/MyProject/MyProject.fsproj"; "/work/Other/Other.Tests.fsproj"; "" ]
        |> Expect.equal "names" (Set.ofList [ "MyProject"; "Other.Tests" ])
    ]

    testList "the reply" [
      testCase "WHY - the eval's own text comes first and the table follows it" <| fun _ ->
        let text = answerOf Set.empty ProfileMode.Cpu "val it: int = 7" (Result.Ok trace)
        text |> Expect.stringStarts "eval first" "val it: int = 7"
        text |> Expect.stringContains "the table is named by its mode" "--- profile (cpu) ---"
        text |> Expect.stringContains "the hot method is in it" "FSI_0008.hotFib"

      testCase "WHY - a refusal replaces the table and keeps the eval's text" <| fun _ ->
        let text = answerOf Set.empty ProfileMode.Cpu "val it: int = 7" (Result.Error(ProfileFailure.HostGone "exit 3"))
        text |> Expect.stringStarts "eval first" "val it: int = 7"
        text |> Expect.stringContains "the reason" "exit 3"

      testCase "WHY - a trace that holds no managed CPU is NoSamples in the reply, not an empty table" <| fun _ ->
        let idle =
          """{ "shared": { "frames": [ { "name": "Thread (1)" }, { "name": "UNMANAGED_CODE_TIME" } ] },
               "profiles": [ { "type": "evented", "events": [
                 { "type": "O", "frame": 0, "at": 0 }, { "type": "O", "frame": 1, "at": 0 },
                 { "type": "C", "frame": 1, "at": 90 }, { "type": "C", "frame": 0, "at": 90 } ] } ] }"""
        answerOf Set.empty ProfileMode.Cpu "ok" (Result.Ok(RecordedTrace.Speedscope idle)) |> Expect.stringContains "named" "no sample to report"

      testCase "WHY - an allocation reply names the types, and the function that allocated them" <| fun _ ->
        let text = answerOf (Set.ofList [ "MyProject" ]) ProfileMode.Alloc "val it: int = 7" (Result.Ok allocTrace)
        text |> Expect.stringStarts "eval first" "val it: int = 7"
        text |> Expect.stringContains "the table is named by its mode" "--- profile (alloc) ---"
        text |> Expect.stringContains "the type" "System.String"
        text |> Expect.stringContains "the function typed into the session" "FSI_0001.makeThings"
        text |> Expect.stringContains "the function of the project" "MyProject.Lib.build"

      testCase "WHY - a recorder that gave the other kind of trace than the mode reads is refused by name" <| fun _ ->
        answerOf Set.empty ProfileMode.Cpu "ok" (Result.Ok allocTrace) |> Expect.stringContains "named" "other kind of trace"
        answerOf Set.empty ProfileMode.Alloc "ok" (Result.Ok trace) |> Expect.stringContains "named" "other kind of trace"

      testCase "WHY - the host crash the eval reported wins over a trace read from a process that died" <| fun _ ->
        let crash : HostCrash = { Exit = ExitedWith 3; Output = "boom" }
        match traceAfterEval (Some(SageFsError.FsiHostCrashed crash)) (Result.Ok trace) with
        | Result.Error(ProfileFailure.HostGone detail) -> detail |> Expect.stringContains "carries the crash text" "boom"
        | other -> failtestf "got %A" other

      testCase "WHY - any other eval outcome leaves the trace as it was" <| fun _ ->
        traceAfterEval None (Result.Ok trace) |> Expect.equal "unchanged" (Result.Ok trace)
        traceAfterEval (Some(SageFsError.EvalFailed "type error")) (Result.Ok trace) |> Expect.equal "a failed eval is still profiled" (Result.Ok trace)
    ]
  ]
