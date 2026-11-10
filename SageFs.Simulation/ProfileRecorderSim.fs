namespace SageFs.Simulation

open System
open System.Collections.Generic
open SageFs
open SageFs.Features

/// Deterministic Simulation Testing for the profile tool's recorder: what `ProfileRecorder.around` does when the machine under
/// it misbehaves. The collector is a simulated port that fails on a schedule (the tool is not installed, it cannot attach, the
/// host ends mid-recording, it does not stop in time, the disk is full, it writes a trace that cannot be read), over a simulated
/// disk that remembers every file the recorder wrote. The decision folded is the REAL one: `ProfileRecorder.around`, then
/// `SpeedscopeTrace.cpuReport` over what it returned. The truth is a table from fault to outcome that never reads the run.
///
/// Same design rules as `ReferenceScopeSim`:
///   * Chaos is DATA: a `Scenario` is a seed, a fault and whether the eval throws. Same scenario, same trace.
///   * Twins reintroduce the bugs the invariants exist to catch: a recorder that leaves its trace file behind when the host
///     dies, one that runs the user's code under a profile it already refused, and one that skips the finish when the eval
///     throws, so the recording would run on.
module ProfileRecorderSim =

  [<RequireQualifiedAccess>]
  type Fault =
    /// Nothing goes wrong.
    | NoFault
    /// `dotnet-trace` is not installed.
    | ToolNotInstalled
    /// The tool starts and cannot attach to the host.
    | AttachFails
    /// The host ends while it is recorded.
    | HostEndsMidRecording
    /// The tool does not stop and write within the allowed time.
    | StopTimesOut
    /// The disk is full when the trace is written.
    | DiskFull
    /// The tool writes a file that is not a trace.
    | CorruptTrace

  type Scenario =
    { Seed: int
      Fault: Fault
      /// The eval throws, which the recorder must survive without leaving the recording running.
      EvalThrows: bool }

  [<RequireQualifiedAccess>]
  type Outcome =
    | Refused of ProfileFailure
    | Reported of CpuReport
    | TraceFailed of ProfileFailure

  type Trace =
    { Scenario: Scenario
      Outcome: Outcome
      EvalRuns: int
      BeginCalls: int
      FinishCalls: int
      /// Files the simulated disk still holds when the call has returned.
      FilesLeft: string list
      /// What the eval's result was, when it ran.
      EvalSeen: string option
      Reducer: string }

  [<RequireQualifiedAccess>]
  type Policy =
    /// The real behaviour.
    | Real
    /// TWIN: the recorder leaves its trace file on the disk when the host ends.
    | LeavesFileWhenHostEnds
    /// TWIN: the user's code runs even when the recording was refused.
    | RunsEvalWhenRefused
    /// TWIN: an eval that throws leaves the recording unfinished.
    | SkipsFinishWhenEvalThrows

  /// The expected outcome class for a fault, from the fault alone.
  [<RequireQualifiedAccess>]
  type Expected =
    | Report
    | Refusal of string
    | TraceFailure of string

  let expectedOf (fault: Fault) : Expected =
    match fault with
    | Fault.NoFault -> Expected.Report
    | Fault.ToolNotInstalled -> Expected.Refusal "ToolMissing"
    | Fault.AttachFails -> Expected.Refusal "CouldNotAttach"
    | Fault.HostEndsMidRecording -> Expected.TraceFailure "HostGone"
    | Fault.StopTimesOut -> Expected.TraceFailure "StopTimedOut"
    | Fault.DiskFull -> Expected.TraceFailure "TraceUnreadable"
    | Fault.CorruptTrace -> Expected.TraceFailure "TraceUnreadable"

  let caseNameOf (failure: ProfileFailure) : string =
    match failure with
    | ProfileFailure.UnknownMode _ -> "UnknownMode"
    | ProfileFailure.NotRecordable _ -> "NotRecordable"
    | ProfileFailure.ToolMissing _ -> "ToolMissing"
    | ProfileFailure.CouldNotAttach _ -> "CouldNotAttach"
    | ProfileFailure.HostGone _ -> "HostGone"
    | ProfileFailure.StopTimedOut _ -> "StopTimedOut"
    | ProfileFailure.TraceUnreadable _ -> "TraceUnreadable"
    | ProfileFailure.NoSamples -> "NoSamples"
    | ProfileFailure.SocketPathTooLong _ -> "SocketPathTooLong"
    | ProfileFailure.TraceTooLarge _ -> "TraceTooLarge"

  /// A trace of the shape the real tool writes: 30 ms of managed CPU in one method, 500 ms in native code.
  let goodTrace : string =
    """{ "shared": { "frames": [ { "name": "Thread (1)" }, { "name": "FSI-ASSEMBLY!FSI_0008.hotFib(int32)" }, { "name": "CPU_TIME" }, { "name": "UNMANAGED_CODE_TIME" } ] },
         "profiles": [ { "type": "evented", "name": "Thread (1)", "unit": "milliseconds", "startValue": 0, "endValue": 530, "events": [
           { "type": "O", "frame": 0, "at": 0 }, { "type": "O", "frame": 1, "at": 0 }, { "type": "O", "frame": 2, "at": 0 },
           { "type": "C", "frame": 2, "at": 30 }, { "type": "O", "frame": 3, "at": 30 }, { "type": "C", "frame": 3, "at": 530 },
           { "type": "C", "frame": 1, "at": 530 }, { "type": "C", "frame": 0, "at": 530 } ] } ] }"""

  let request : RecordingRequest = { HostPid = 4242; Mode = ProfileMode.Cpu; TraceBase = "/sim/sagefs-profile-1" }

  let tracePath = request.TraceBase
  let speedscopePath = DotnetTrace.speedscopePathOf request.TraceBase

  /// The simulated disk, and the counters the invariants read.
  type World() =
    member val Files = HashSet<string>() with get
    member val BeginCalls = 0 with get, set
    member val FinishCalls = 0 with get, set

  let removeFiles (world: World) : unit =
    world.Files.Remove tracePath |> ignore
    world.Files.Remove speedscopePath |> ignore

  /// What the recorder does with its files when the host it records ends. The twin leaves them.
  let cleanUpWhenHostEnds (policy: Policy) (world: World) : unit =
    match policy with
    | Policy.LeavesFileWhenHostEnds -> ()
    | Policy.Real
    | Policy.RunsEvalWhenRefused
    | Policy.SkipsFinishWhenEvalThrows -> removeFiles world

  /// The simulated collector. A fault is decided by the scenario and nothing else.
  let collectorOf (policy: Policy) (scenario: Scenario) (world: World) : TraceCollector =
    let finish () : Async<Result<RecordedTrace, ProfileFailure>> =
      async {
        world.FinishCalls <- world.FinishCalls + 1
        match scenario.Fault with
        | Fault.HostEndsMidRecording ->
          cleanUpWhenHostEnds policy world
          return Result.Error(ProfileFailure.HostGone "simulated: the recorder ended on its own")
        | Fault.StopTimesOut ->
          removeFiles world
          return Result.Error(ProfileFailure.StopTimedOut Timeouts.profileRecorderWait)
        | Fault.DiskFull ->
          removeFiles world
          return Result.Error(ProfileFailure.TraceUnreadable "simulated: no space left on device")
        | Fault.CorruptTrace ->
          removeFiles world
          return Result.Ok(RecordedTrace.Speedscope "{ this is not a trace")
        | Fault.NoFault
        | Fault.ToolNotInstalled
        | Fault.AttachFails ->
          removeFiles world
          return Result.Ok(RecordedTrace.Speedscope goodTrace)
      }
    let begin' (_: RecordingRequest) : Async<Result<ActiveRecording, ProfileFailure>> =
      async {
        world.BeginCalls <- world.BeginCalls + 1
        match scenario.Fault with
        | Fault.ToolNotInstalled -> return Result.Error(ProfileFailure.ToolMissing(DotnetTrace.toolName, DotnetTrace.installCommand))
        | Fault.AttachFails -> return Result.Error(ProfileFailure.CouldNotAttach "simulated: the diagnostics socket refused")
        | _ ->
          world.Files.Add tracePath |> ignore
          return Result.Ok { Finish = finish }
      }
    { Begin = begin' }

  /// The twin orchestrators: the real `around` with one rule broken.
  let aroundTwin (policy: Policy) (collector: TraceCollector) (run: unit -> Async<string>) : Async<Result<Profiled<string>, ProfileFailure>> =
    async {
      match policy with
      | Policy.RunsEvalWhenRefused ->
        let! started = collector.Begin request
        match started with
        | Result.Error failure ->
          let! _ = Async.Catch(run ())
          return Result.Error failure
        | Result.Ok active ->
          let! ran = Async.Catch(run ())
          let! trace = active.Finish()
          return Result.Ok { Eval = (match ran with | Choice1Of2 v -> Guarded.Attempt.Completed v | Choice2Of2 e -> Guarded.Attempt.Threw e); Trace = trace }
      | Policy.SkipsFinishWhenEvalThrows ->
        let! started = collector.Begin request
        match started with
        | Result.Error failure -> return Result.Error failure
        | Result.Ok active ->
          let! ran = Async.Catch(run ())
          match ran with
          | Choice1Of2 value ->
            let! trace = active.Finish()
            return Result.Ok { Eval = Guarded.Attempt.Completed value; Trace = trace }
          | Choice2Of2 ex -> return Result.Ok { Eval = Guarded.Attempt.Threw ex; Trace = Result.Error(ProfileFailure.HostGone "left running") }
      | Policy.Real
      | Policy.LeavesFileWhenHostEnds -> return! ProfileRecorder.around collector request run
    }

  let run (policy: Policy) (scenario: Scenario) : Trace =
    let world = World()
    let evalRuns = ref 0
    let seen = ref None
    let evalBody () : Async<string> =
      async {
        evalRuns.Value <- evalRuns.Value + 1
        match scenario.EvalThrows with
        | true -> return failwith "simulated: the eval threw"
        | false ->
          seen.Value <- Some "eval-result"
          return "eval-result"
      }
    let collector = collectorOf policy scenario world
    let result = aroundTwin policy collector evalBody |> Async.RunSynchronously
    let outcome =
      match result with
      | Result.Error failure -> Outcome.Refused failure
      | Result.Ok profiled ->
        match profiled.Trace with
        | Result.Error failure -> Outcome.TraceFailed failure
        | Result.Ok(RecordedTrace.Speedscope text) ->
          match SpeedscopeTrace.cpuReport Set.empty 5 text with
          | Result.Ok report -> Outcome.Reported report
          | Result.Error failure -> Outcome.TraceFailed failure
        | Result.Ok(RecordedTrace.NetTrace _) ->
          Outcome.TraceFailed(ProfileFailure.TraceUnreadable "the simulated recorder gave a nettrace to a cpu request")
    { Scenario = scenario
      Outcome = outcome
      EvalRuns = evalRuns.Value
      BeginCalls = world.BeginCalls
      FinishCalls = world.FinishCalls
      FilesLeft = world.Files |> Seq.toList
      EvalSeen = seen.Value
      Reducer =
        (match policy with
         | Policy.Real -> "real (ProfileRecorder.around over SpeedscopeTrace.cpuReport)"
         | Policy.LeavesFileWhenHostEnds -> "twin-leaves-file-when-host-ends"
         | Policy.RunsEvalWhenRefused -> "twin-runs-eval-when-refused"
         | Policy.SkipsFinishWhenEvalThrows -> "twin-skips-finish-when-eval-throws") }

  /// Seeded scenarios: any fault, the eval throwing one time in five. Same seed, identical scenario.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random seed
    let faults =
      [| Fault.NoFault; Fault.NoFault; Fault.ToolNotInstalled; Fault.AttachFails; Fault.HostEndsMidRecording
         Fault.StopTimesOut; Fault.DiskFull; Fault.CorruptTrace |]
    { Seed = seed; Fault = faults.[rnd.Next faults.Length]; EvalThrows = rnd.Next 5 = 0 }

  // -- Invariants over a Trace --

  [<RequireQualifiedAccess>]
  type Check =
    | Holds
    | Violated of message: string

  type Invariant = { Id: string; Description: string; Check: Trace -> Check }

  let violated (t: Trace) (message: string) : Check =
    Check.Violated(sprintf "%s (seed=%d, fault=%A, evalThrows=%b)" message t.Scenario.Seed t.Scenario.Fault t.Scenario.EvalThrows)

  let begunOk (t: Trace) : bool =
    match t.Scenario.Fault with
    | Fault.ToolNotInstalled
    | Fault.AttachFails -> false
    | _ -> true

  /// The trace file never outlives the call, whatever happened to it.
  let noFileOutlivesTheCall : Invariant =
    { Id = "no-file-outlives-the-call"
      Description = "Whichever way a recording ends, the files it wrote are gone when the call returns."
      Check = fun t -> match t.FilesLeft with | [] -> Check.Holds | left -> violated t (sprintf "files left on the disk: %A" left) }

  /// The user's code runs exactly once under a recording that started, and never under one that was refused.
  let evalRunsOnlyUnderARecording : Invariant =
    { Id = "eval-runs-once-under-a-recording-and-never-under-a-refusal"
      Description = "A refused recording does not run the code; a started one runs it exactly once."
      Check = fun t ->
        match begunOk t, t.EvalRuns with
        | true, 1 -> Check.Holds
        | false, 0 -> Check.Holds
        | expected, runs -> violated t (sprintf "the recording %s and the eval ran %d times" (match expected with | true -> "started" | false -> "was refused") runs) }

  /// A recording that started is finished exactly once, even when the eval threw.
  let startedMeansFinishedOnce : Invariant =
    { Id = "a-started-recording-is-finished-once"
      Description = "Begin succeeded means Finish ran exactly once, so no recorder is left running."
      Check = fun t ->
        match begunOk t, t.FinishCalls with
        | true, 1 -> Check.Holds
        | false, 0 -> Check.Holds
        | _, finished -> violated t (sprintf "finish ran %d times" finished) }

  /// The outcome is the one the fault calls for, and a report exists only for a clean run.
  let outcomeMatchesTheFault : Invariant =
    { Id = "outcome-matches-the-fault"
      Description = "No fault gives a report; each fault gives the named refusal or failure it calls for; nothing is both a report and a failure."
      Check = fun t ->
        match expectedOf t.Scenario.Fault, t.Outcome with
        | Expected.Report, Outcome.Reported report when report.ManagedMs > 0.0 -> Check.Holds
        | Expected.Refusal name, Outcome.Refused failure when caseNameOf failure = name -> Check.Holds
        | Expected.TraceFailure name, Outcome.TraceFailed failure when caseNameOf failure = name -> Check.Holds
        | expected, got -> violated t (sprintf "expected %A and the call answered %A" expected got) }

  /// A report names what ran: the hot method of the trace leads the table.
  let reportNamesTheHotMethod : Invariant =
    { Id = "a-report-names-the-hot-method"
      Description = "When a report is given, the first row is the method the trace spent its managed time in."
      Check = fun t ->
        match t.Outcome with
        | Outcome.Reported report ->
          match report.SelfRows with
          | first :: _ when first.Label = "FSI-ASSEMBLY!FSI_0008.hotFib" -> Check.Holds
          | rows -> violated t (sprintf "the first row is not the hot method: %A" rows)
        | Outcome.Refused _
        | Outcome.TraceFailed _ -> Check.Holds }

  /// An eval that ran hands its result back even when the profile failed.
  let evalResultSurvivesAFailedProfile : Invariant =
    { Id = "the-eval-result-survives-a-failed-profile"
      Description = "A profile that failed after the eval ran never loses the eval's own result."
      Check = fun t ->
        match t.Outcome, t.Scenario.EvalThrows, t.EvalSeen with
        | Outcome.Refused _, _, _ -> Check.Holds
        | _, true, _ -> Check.Holds
        | _, false, Some _ -> Check.Holds
        | _, false, None -> violated t "the eval's result is missing" }

  let all : Invariant list =
    [ noFileOutlivesTheCall
      evalRunsOnlyUnderARecording
      startedMeansFinishedOnce
      outcomeMatchesTheFault
      reportNamesTheHotMethod
      evalResultSurvivesAFailedProfile ]

  /// The invariants a trace breaks, by id.
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Check.Holds -> None
      | Check.Violated message -> Some(inv.Id, message))
