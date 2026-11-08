namespace SageFs.Features

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open SageFs
open SageFs.Utils
open SageFs.Features.ProjectCheckCore

/// The effects behind `check_project`: the MSBuild process that reads the compiler arguments, the build of a referenced
/// project that is behind, the overlay files, and the compiler call. Every decision is `ProjectCheckCore`'s; this module
/// reads the disk and the process, hands the facts to those functions, and does what they say.
///
/// The compiler call is the `ParseAndCheckProject` that `CallerCheck` and `SymbolReferenceCheck` already make, with the
/// options built from the design-time arguments and not from a loaded session, so a check needs no session and touches
/// none.
module CheckProject =

  let killTree (proc: Process) : unit =
    try
      proc.Kill(true)
    with
    | :? InvalidOperationException -> () // it has already exited
    | :? System.ComponentModel.Win32Exception -> () // it ended while the kill was in flight

  /// How a child process ended.
  [<RequireQualifiedAccess>]
  type ProcessEnd =
    | Exited of code: int * stdout: string * stderr: string
    | CancelledByCaller
    | TimedOut of after: TimeSpan
    | NotStarted of detail: string

  /// Runs `dotnet <args>` in `directory` and gives back what it printed. Stderr is drained on its own handler (an unread
  /// pipe fills and blocks the child). Cancellation and the bound both kill the whole process tree.
  let runDotnet (directory: string) (args: string list) (bound: TimeSpan) (cancel: CancellationToken) : Task<ProcessEnd> =
    task {
      let info = ProcessStartInfo("dotnet", WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true)
      for a in args do info.ArgumentList.Add a
      info.Environment["MSBUILDDISABLENODEREUSE"] <- "1"
      info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
      info.Environment["DOTNET_NOLOGO"] <- "1"
      let errors = StringBuilder()
      let lockErrors = obj ()
      try
        use proc = new Process(StartInfo = info)
        proc.ErrorDataReceived.Add(fun e ->
          match e.Data with
          | null -> ()
          | line -> lock lockErrors (fun () -> errors.AppendLine line |> ignore))
        proc.Start() |> ignore
        proc.BeginErrorReadLine()
        use limit = CancellationTokenSource.CreateLinkedTokenSource cancel
        limit.CancelAfter bound
        let readOut = proc.StandardOutput.ReadToEndAsync()
        try
          do! proc.WaitForExitAsync limit.Token
          let! out = readOut
          return ProcessEnd.Exited(proc.ExitCode, out, lock lockErrors (fun () -> errors.ToString()))
        with :? OperationCanceledException ->
          killTree proc
          return
            (match cancel.IsCancellationRequested with
             | true -> ProcessEnd.CancelledByCaller
             | false -> ProcessEnd.TimedOut bound)
      with
      | :? System.ComponentModel.Win32Exception as why -> return ProcessEnd.NotStarted why.Message
      | :? InvalidOperationException as why -> return ProcessEnd.NotStarted why.Message
    }

  /// The last few lines of a failed run that say what went wrong.
  let private errorLines (stdout: string) (stderr: string) : string =
    let lines = (stdout + "\n" + stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
    match lines |> Array.filter (fun l -> l.Contains "error") with
    | [||] -> lines |> Array.truncate 5 |> String.concat " | "
    | errs -> errs |> Array.truncate 5 |> String.concat " | "

  let currentStamp (path: string) : StampState =
    try
      let info = FileInfo path
      match info.Exists with
      | true -> StampState.Present(info.LastWriteTimeUtc.Ticks, info.Length)
      | false -> StampState.Absent
    with
    | :? IOException -> StampState.Absent
    | :? UnauthorizedAccessException -> StampState.Absent

  [<RequireQualifiedAccess>]
  type FactsFailure =
    | Refused of ArgumentsRefusal
    | Stopped of ProcessEnd

  /// What a process that did not exit cleanly means for the stage it was running.
  let outcomeOfStop (stopped: Stopped) (ended: ProcessEnd) : CheckOutcome =
    match ended with
    | ProcessEnd.CancelledByCaller -> CheckOutcome.Cancelled stopped
    | ProcessEnd.TimedOut after -> CheckOutcome.TimedOut(stopped, after)
    | ProcessEnd.NotStarted detail -> CheckOutcome.Refused(CheckRefusal.CompilerFailed detail)
    | ProcessEnd.Exited(code, stdout, stderr) ->
      CheckOutcome.Refused(CheckRefusal.CompilerFailed(sprintf "process exited %d: %s" code (errorLines stdout stderr)))

  /// What a failure to read a project's arguments means for the check.
  let outcomeOfFailure (failure: FactsFailure) : CheckOutcome =
    match failure with
    | FactsFailure.Refused refusal -> CheckOutcome.Refused(CheckRefusal.Arguments refusal)
    | FactsFailure.Stopped ended -> outcomeOfStop Stopped.ArgumentsRun ended

  /// One design-time MSBuild run for `projectFile`.
  let readFacts (projectFile: string) (configuration: BuildConfiguration) (cancel: CancellationToken) : Task<Result<MsBuildFacts, FactsFailure>> =
    task {
      let directory = Path.GetDirectoryName projectFile
      let args = "msbuild" :: Path.GetFileName projectFile :: MsBuildFacts.msbuildArgumentsFor (BuildConfiguration.value configuration)
      let! ended = runDotnet directory args Timeouts.projectArgumentsRun cancel
      match ended with
      | ProcessEnd.Exited(0, stdout, _) -> return MsBuildFacts.read projectFile stdout |> Result.mapError FactsFailure.Refused
      | ProcessEnd.Exited(code, stdout, stderr) ->
        return Error(FactsFailure.Refused(ArgumentsRefusal.TargetFailed(sprintf "msbuild exited %d: %s" code (errorLines stdout stderr))))
      | other -> return Error(FactsFailure.Stopped other)
    }

  // ---- what is kept between calls, and the calls that share a run ----

  /// A run that several calls wait on. It is cancelled only when every waiter has gone, so one caller's cancel does not
  /// take another caller's answer with it.
  type SharedRun<'a>(start: CancellationToken -> Task<'a>) =
    let source = new CancellationTokenSource()
    let gate = obj ()
    let mutable waiters = 0
    let task' = lazy (start source.Token)
    member _.Task : Task<'a> = task'.Value

    /// A waiter joins. The function it gets back is that waiter leaving (it got the answer or it was cancelled), and it
    /// counts once however often it is called. The last waiter to leave a run that is still going cancels it.
    member _.Join() : unit -> unit =
      lock gate (fun () -> waiters <- waiters + 1)
      let leave =
        lazy
          (lock gate (fun () ->
            waiters <- waiters - 1
            match waiters = 0 && not task'.Value.IsCompleted with
            | true -> source.Cancel()
            | false -> ()))
      fun () -> leave.Force()

  type Kept() =
    let gate = obj ()
    let mutable store : ArgumentStore = ArgumentStore.empty
    let mutable inflight : Inflight = Inflight.empty
    let runs = Dictionary<CacheKey, SharedRun<Result<MsBuildFacts, FactsFailure>>>()
    member _.Entry(key: CacheKey) : CacheEntry option = lock gate (fun () -> ArgumentStore.tryFind key store)

    /// Starts the run for `key` or joins the one in flight.
    member _.JoinRun
      (key: CacheKey, make: unit -> SharedRun<Result<MsBuildFacts, FactsFailure>>)
      : SharedRun<Result<MsBuildFacts, FactsFailure>> * (unit -> unit) =
      lock gate (fun () ->
        let joined, next = Inflight.join key inflight
        inflight <- next
        match joined with
        | Joined.Starts ->
          let run = make ()
          runs[key] <- run
          run, run.Join()
        | Joined.Follows ->
          let run = runs[key]
          run, run.Join())

    member _.Finish(key: CacheKey, entry: CacheEntry option, ending: RunEnd) : unit =
      lock gate (fun () ->
        match entry with
        | Some e -> store <- ArgumentStore.record e ending store
        | None -> ()
        inflight <- Inflight.finish key inflight
        runs.Remove key |> ignore)

  let kept : Kept = Kept()

  /// The arguments for a project: the kept ones when nothing that shaped them moved, and otherwise a design-time run that
  /// every simultaneous call for the same key shares.
  let argumentsFor (projectFile: string) (configuration: BuildConfiguration) (cancel: CancellationToken) : Task<Result<MsBuildFacts * ArgumentsSource, FactsFailure>> =
    task {
      let key = { Project = projectFile; Configuration = BuildConfiguration.value configuration }
      match ArgumentCache.decide (kept.Entry key) currentStamp with
      | CacheDecision.Reuse facts -> return Ok(facts, ArgumentsSource.Kept)
      | CacheDecision.Refresh reason ->
        let make () =
          SharedRun<Result<MsBuildFacts, FactsFailure>>(fun token ->
            task {
              let! result = readFacts projectFile configuration token
              match result with
              | Ok facts ->
                let entry = { Key = key; Facts = facts; Stamps = Fingerprint.stamp currentStamp facts }
                kept.Finish(key, Some entry, RunEnd.Completed)
              | Error _ -> kept.Finish(key, None, RunEnd.Cancelled)
              return result
            })
        let run, leave = kept.JoinRun(key, make)
        let gone = TaskCompletionSource<unit>()
        use registration =
          cancel.Register(fun () ->
            leave ()
            gone.TrySetResult() |> ignore)
        try
          // A caller that cancels returns at once, whether or not the run it was waiting on goes on for another caller.
          let! first = Task.WhenAny(run.Task :> Task, gone.Task :> Task)
          match obj.ReferenceEquals(first, run.Task) with
          | false -> return Error(FactsFailure.Stopped ProcessEnd.CancelledByCaller)
          | true ->
            let! result = run.Task
            return result |> Result.map (fun facts -> facts, ArgumentsSource.Read reason)
        finally
          leave ()
    }

  // ---- referenced projects ----

  let private isGeneratedIn (projectFile: string) (path: string) : bool = Staleness.isGeneratedIn (ProjectDirectory(Path.GetDirectoryName projectFile)) path

  let private writeTicks (path: string) : int64 option =
    match currentStamp path with
    | StampState.Present(ticks, _) -> Some ticks
    | StampState.Absent -> None

  /// Whether a referenced project's output is behind what it is built from.
  let private stalenessOf (facts: MsBuildFacts) (output: string) (referenceOutputs: string list) : Staleness =
    let files =
      facts.Arguments.Sources @ Fingerprint.paths facts
      |> List.distinct
      |> List.filter (fun p -> not (isGeneratedIn facts.Arguments.ProjectFile p))
    let inputs = Staleness.inputsOf writeTicks files referenceOutputs
    let state =
      match writeTicks output with
      | Some ticks -> OutputState.OutputAt(output, ticks)
      | None -> OutputState.NoOutput output
    Staleness.decide state inputs

  /// Builds one referenced project without its own references (they are built first, in order) and without a restore.
  let private buildProject (projectFile: string) (configuration: BuildConfiguration) (cancel: CancellationToken) : Task<Result<unit, ProcessEnd>> =
    task {
      let args = [ "build"; projectFile; "-c"; BuildConfiguration.value configuration; "--no-dependencies"; "--no-restore"; "-nodeReuse:false"; "--disable-build-servers" ]
      let! ended = runDotnet (Path.GetDirectoryName projectFile) args Timeouts.projectReferenceBuild cancel
      match ended with
      | ProcessEnd.Exited(0, _, _) -> return Ok()
      | other -> return Error other
    }

  /// What the referenced projects need before the check: their facts (read the same way, and kept the same way), and the
  /// order to build the ones that are behind.
  let rec gatherFacts
    (configuration: BuildConfiguration)
    (cancel: CancellationToken)
    (pending: ReferencedProject list)
    (seen: Map<string, MsBuildFacts * string>)
    : Task<Result<Map<string, MsBuildFacts * string>, CheckOutcome>> =
    task {
      match pending with
      | [] -> return Ok seen
      | r :: rest when Map.containsKey r.Project seen -> return! gatherFacts configuration cancel rest seen
      | r :: rest ->
        let! facts = argumentsFor r.Project configuration cancel
        match facts with
        | Error failure -> return Error(outcomeOfFailure failure)
        | Ok(f, _) -> return! gatherFacts configuration cancel (f.References @ rest) (Map.add r.Project (f, r.Output) seen)
    }

  let referencesToBuild
    (parent: MsBuildFacts)
    (configuration: BuildConfiguration)
    (cancel: CancellationToken)
    : Task<Result<string list, CheckOutcome>> =
    task {
      match! gatherFacts configuration cancel parent.References Map.empty with
      | Error outcome -> return Error outcome
      | Ok closure ->
        let referencesMap = closure |> Map.map (fun _ (f, _) -> f.References |> List.map (fun r -> r.Project))
        let own =
          closure
          |> Map.map (fun _ (f, output) -> stalenessOf f output (f.References |> List.map (fun r -> r.Output)))
        return Ok(Staleness.buildOrder referencesMap own)
    }

  // ---- overlays and the compiler ----

  let writeOverlays (plan: OverlayPlan) : Result<unit, CheckRefusal> =
    let failed (why: string) = Error(CheckRefusal.CompilerFailed(sprintf "could not write the overlay files: %s" why))
    try
      for path, text in plan.Writes do
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let same = File.Exists path && File.ReadAllText path = text
        match same with
        | true -> ()
        | false -> File.WriteAllText(path, text)
      Ok()
    with
    | :? IOException as why -> failed why.Message
    | :? UnauthorizedAccessException as why -> failed why.Message

  let overlayRoot : string = Path.Combine(Path.GetTempPath(), "sagefs-project-check")

  let private severityOf (s: FSharpDiagnosticSeverity) : Severity =
    match s with
    | FSharpDiagnosticSeverity.Error -> Severity.Errored
    | FSharpDiagnosticSeverity.Warning -> Severity.Warning
    | FSharpDiagnosticSeverity.Info -> Severity.Info
    | FSharpDiagnosticSeverity.Hidden -> Severity.Hidden

  let toCheckDiagnostic (plan: OverlayPlan) (d: FSharpDiagnostic) : CheckDiagnostic =
    { Path = Overlays.mapBack plan d.FileName
      Line = d.StartLine
      Column = d.StartColumn
      EndLine = d.EndLine
      EndColumn = d.EndColumn
      Severity = severityOf d.Severity
      Code = d.ErrorNumberText
      Message = d.Message }

  /// The held checker: the same bytes ceiling, idle retention and sweep `list_references` uses, in this process.
  type HeldCache =
    { Holder: CacheHolder<FSharpChecker>
      SweepTimer: Timer }

  let cache : Lazy<HeldCache> =
    lazy
      (let clock = Stopwatch.StartNew()
       let holder =
         CacheHolder<FSharpChecker>(
           { Create = SymbolReferenceCheck.newChecker
             Drop = SymbolReferenceCheck.dropChecker
             ResidentBytes = SymbolReferenceCheck.residentBytes
             Pressure = fun () -> ReferenceCacheRetention.pressureOf (MachineMemory.current ())
             Now = fun () -> clock.Elapsed
             Policy = SymbolReferenceCheck.cachePolicy
             Released = fun reason -> Log.info "check_project: compiler cache dropped, %s" (ReleaseReason.describe reason) })
       let sweepEvery = Timeouts.referencesCacheSweep
       let sweep =
         async {
           let! outcome = Async.Catch(async { holder.Sweep() })
           match outcome with
           | Choice1Of2() -> ()
           | Choice2Of2 failure -> Log.warn "check_project: the cache sweep failed: %s" failure.Message
         }
       { Holder = holder
         SweepTimer = new Timer((fun _ -> Async.Start sweep), null, sweepEvery, sweepEvery) })

  /// Type-checks the project as `facts` describe it with the overlays in place. The disk files of the project are not
  /// touched; the overlays are written under `overlayRoot`.
  let checkWithFacts (facts: MsBuildFacts) (overlays: Overlay list) (notes: RunNotes) (cancel: CancellationToken) : Task<CheckOutcome> =
    task {
      match Overlays.resolve overlayRoot facts.Arguments overlays with
      | Error refusal -> return CheckOutcome.Refused(CheckRefusal.Overlay refusal)
      | Ok plan ->
        match writeOverlays plan with
        | Error refusal -> return CheckOutcome.Refused refusal
        | Ok() ->
          let held = cache.Value.Holder
          try
            let checker = held.Acquire()
            let argv = Array.ofList (facts.Arguments.Options @ plan.Sources)
            let options = checker.GetProjectOptionsFromCommandLineArgs(facts.Arguments.ProjectFile, argv)
            use limit = CancellationTokenSource.CreateLinkedTokenSource cancel
            limit.CancelAfter Timeouts.projectCheck
            try
              let! caught = Async.StartAsTask(Async.Catch(checker.ParseAndCheckProject options), cancellationToken = limit.Token)
              match caught with
              | Choice1Of2 results ->
                let diagnostics = results.Diagnostics |> Array.map (toCheckDiagnostic plan) |> Array.toList
                return CheckOutcome.Checked(diagnostics, notes)
              | Choice2Of2 failure -> return CheckOutcome.Refused(CheckRefusal.CompilerFailed failure.Message)
            with :? OperationCanceledException ->
              return
                (match cancel.IsCancellationRequested with
                 | true -> CheckOutcome.Cancelled Stopped.Compiler
                 | false -> CheckOutcome.TimedOut(Stopped.Compiler, Timeouts.projectCheck))
          finally
            held.Settle()
    }

  /// Builds the projects in the order given, stopping at the first one that does not build.
  let rec buildAll (configuration: BuildConfiguration) (cancel: CancellationToken) (remaining: string list) (built: string list) : Task<Result<string list, CheckOutcome>> =
    task {
      match remaining with
      | [] -> return Ok built
      | project :: rest ->
        match! buildProject project configuration cancel with
        | Ok() -> return! buildAll configuration cancel rest (built @ [ project ])
        | Error(ProcessEnd.Exited(code, stdout, stderr)) ->
          return Error(CheckOutcome.Refused(CheckRefusal.ReferenceBuildFailed(project, sprintf "exit %d: %s" code (errorLines stdout stderr))))
        | Error ended -> return Error(outcomeOfStop (Stopped.ReferenceBuild project) ended)
    }

  /// The whole check: arguments (kept or read), the referenced projects that are behind built once and in order, then the
  /// compiler over the project with the overlays in place. Never builds the project itself and never uses a session.
  let run (projectFile: string) (configuration: BuildConfiguration) (overlays: Overlay list) (cancel: CancellationToken) : Task<CheckOutcome> =
    task {
      let full = Path.GetFullPath projectFile
      match! argumentsFor full configuration cancel with
      | Error failure -> return outcomeOfFailure failure
      | Ok(facts, source) ->
        // An overlay that cannot apply is refused before anything is built.
        match Overlays.resolve overlayRoot facts.Arguments overlays with
        | Error refusal -> return CheckOutcome.Refused(CheckRefusal.Overlay refusal)
        | Ok _ ->
          match! referencesToBuild facts configuration cancel with
          | Error outcome -> return outcome
          | Ok order ->
            match! buildAll configuration cancel order [] with
            | Error outcome -> return outcome
            | Ok built -> return! checkWithFacts facts overlays { Arguments = source; Built = built } cancel
    }

  /// How many diagnostics a reply lists before it counts the rest.
  let maxListedDiagnostics : int = 50

  /// The tool's whole body: the overlays as the caller sent them, the check, and the reply text.
  let answer (project: string) (configuration: string) (overlaysJson: string) (cancel: CancellationToken) : Task<string> =
    task {
      match Request.parseOverlays overlaysJson with
      | Error refusal -> return Report.describeRefusal refusal
      | Ok overlays ->
        let! outcome = run project (Request.configurationOf configuration) overlays cancel
        return Report.render maxListedDiagnostics outcome
    }
