namespace SageFs

open System
open System.Diagnostics
open System.IO
open System.Threading
open SageFs.Utils

/// The project-build subsystem, extracted from the SessionManager supervisor so
/// that file stops accreting (its line budget is a ratchet — see ArchitectureTests).
/// It resolves the build target, structures `dotnet build` diagnostics, caps
/// build concurrency, and runs a build that self-heals a missing or stale NuGet
/// restore. Shared by the daemon's cold-restart path (SessionManager) and the
/// worker's hard-reset path (AppState) so both surface the same diagnostics and
/// both restore when a project needs it.
module SessionBuild =

  /// Run `dotnet build` for the primary project.
  /// Called from the daemon process (worker is already stopped).
  /// Async so we don't block the MailboxProcessor during build.
  let resolveBuildProjectPath (workingDir: string) (projFile: string) =
    match Path.IsPathRooted projFile with
    | true -> projFile
    | false -> Path.Combine(workingDir, projFile)

  /// The diagnostics from a failed `dotnet build`, as structured data — no
  /// surface-specific call to action baked in (see BuildDiagnostic.describe
  /// and SageFsError.BuildFailed's own doc comment for why).
  let buildDiagnosticsOf (stdout: string list) (stderr: string list) : BuildDiagnostic list =
    let output = stdout @ stderr
    // MSBuild ends each diagnostic with " [<project path>]"; the path is noise on a card.
    let withoutProject (line: string) =
      let trimmed = line.Trim()
      match trimmed.EndsWith("]", StringComparison.Ordinal), trimmed.LastIndexOf(" [", StringComparison.Ordinal) with
      | true, cut when cut > 0 -> trimmed.Substring(0, cut)
      | _ -> trimmed
    let errors =
      output
      |> List.filter (fun l -> l.Contains(": error ", StringComparison.Ordinal))
      |> List.map withoutProject
      |> List.distinct
    match errors with
    | [] ->
      output
      |> List.filter (fun l -> l.Trim() <> "")
      |> List.rev |> List.truncate 15 |> List.rev
      |> List.map BuildDiagnostic.ofLine
    | found -> found |> List.truncate 10 |> List.map BuildDiagnostic.ofLine

  /// The `dotnet` arguments of a session rebuild. Incremental on purpose: a
  /// clean build deletes the last good output before compiling, so one compile
  /// error would leave the project with nothing to run until built by hand.
  ///
  /// `restore = false` is the fast path (`--no-restore`) — correct once the
  /// project has a `project.assets.json`. `restore = true` drops `--no-restore`
  /// so `dotnet build` restores first, which a never-restored project (fresh
  /// .fsproj → NETSDK1004) or a project whose package list just changed needs.
  let buildArguments (restore: bool) (buildProject: string) : string list =
    match restore with
    | false -> [ "build"; buildProject; "--no-restore" ]
    | true  -> [ "build"; buildProject ]

  /// Whether a failed build's output says a NuGet restore is required, so the
  /// build is worth retrying WITH a restore instead of being reported as a
  /// compile failure. Covers the fresh-project assets-missing error (NETSDK1004)
  /// and a newly-added/removed package reference (NU1101/NU1102 and the generic
  /// "run a NuGet package restore" guidance MSBuild emits).
  let buildOutputNeedsRestore (lines: string list) : bool =
    let needle (l: string) =
      l.Contains("NETSDK1004", StringComparison.OrdinalIgnoreCase)
      || l.Contains("run a nuget package restore", StringComparison.OrdinalIgnoreCase)
      || l.Contains("project.assets.json", StringComparison.OrdinalIgnoreCase)
      || l.Contains("NU1101", StringComparison.OrdinalIgnoreCase)
      || l.Contains("NU1102", StringComparison.OrdinalIgnoreCase)
    lines |> List.exists needle

  /// Daemon-wide cap on concurrent `dotnet build` child processes (vision
  /// §3.4 "diff-touches-compiled-file fraction" / roast-6 Phase 0 item 1).
  /// `runBuildAsync` runs off the mailbox loop (each session's cold-restart
  /// build is a separate async), so with no cap here N concurrent warmups
  /// launch N unbounded `dotnet build`s — each its own MSBuild node pool
  /// fighting the others for CPU. Default cores/4 (min 1): a `dotnet build`
  /// is itself internally parallel, so one build slot already uses several
  /// cores; the cap bounds how many *builds* run at once, not how many
  /// cores each one may use.
  /// Not private: `SessionManagerBuildSemaphoreTests` asserts this matches
  /// the documented "cores/4, min 1" policy without needing to spawn real
  /// `dotnet build` processes in the default test suite.
  let buildConcurrencyLimit = max 1 (Environment.ProcessorCount / 4)
  let private buildSemaphore = new SemaphoreSlim(buildConcurrencyLimit, buildConcurrencyLimit)

  /// Free build slots right now — full capacity when no build is in flight.
  /// Lets a test observe the semaphore exists at the right capacity without
  /// spawning a real (multi-second) `dotnet build` in the default suite.
  let availableBuildSlots () = buildSemaphore.CurrentCount

  let private buildTimeoutError () =
    let timeoutDiagnostic =
      { File = None; Line = None; Column = None; Code = None
        Severity = BuildDiagnosticSeverity.Error
        Message = "Build timed out (10 min limit)" }
    SageFsError.BuildFailed(-1, [ timeoutDiagnostic ])

  let runBuildAsync (projects: string list) (workingDir: string) : Async<Result<string, SageFsError>> =
    async {
      let primaryProject = projects |> List.tryHead
      match primaryProject with
      | None -> return Ok "No projects to build"
      | Some projFile ->
        let buildProject = resolveBuildProjectPath workingDir projFile
        let! ct = Async.CancellationToken
        do! buildSemaphore.WaitAsync(ct) |> Async.AwaitTask
        try
          // Run one `dotnet` invocation. Ok on success; Error carries the exit
          // code (None = timed out) plus stdout/stderr for diagnostics/retry.
          let runOnce (args: string list) : Async<Result<string, int option * string list * string list>> =
            async {
              let psi = ProcessStartInfo(
                "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workingDir)
              for arg in args do
                psi.ArgumentList.Add(arg)
              let proc = Process.Start(psi)
              let stderrLines = System.Collections.Generic.List<string>()
              let stderrTask =
                System.Threading.Tasks.Task.Run(fun () ->
                  let mutable line = proc.StandardError.ReadLine()
                  while not (isNull line) do
                    stderrLines.Add(line)
                    line <- proc.StandardError.ReadLine())
              // dotnet build prints compiler errors on stdout, so both streams are kept.
              let stdoutLines = System.Collections.Generic.List<string>()
              let stdoutTask =
                System.Threading.Tasks.Task.Run(fun () ->
                  let mutable line = proc.StandardOutput.ReadLine()
                  while not (isNull line) do
                    stdoutLines.Add(line)
                    line <- proc.StandardOutput.ReadLine())
              let tcs = System.Threading.Tasks.TaskCompletionSource<bool>()
              proc.EnableRaisingEvents <- true
              proc.Exited.Add(fun _ -> tcs.TrySetResult(true) |> ignore)
              match proc.HasExited with
              | true -> tcs.TrySetResult(true) |> ignore
              | false -> ()
              let timeoutTask = System.Threading.Tasks.Task.Delay(600_000, ct)
              let! completed =
                System.Threading.Tasks.Task.WhenAny(tcs.Task, timeoutTask)
                |> Async.AwaitTask
              match Object.ReferenceEquals(completed, timeoutTask) with
              | true ->
                try proc.Kill(entireProcessTree = true) with ex -> Log.warn "[SessionBuild] Kill build process on timeout: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
                proc.Dispose()
                return Error (None, [], [])
              | false ->
                let! _ = System.Threading.Tasks.Task.WhenAll(stderrTask, stdoutTask) |> Async.AwaitTask
                let exitCode = proc.ExitCode
                proc.Dispose()
                match exitCode <> 0 with
                | true -> return Error (Some exitCode, List.ofSeq stdoutLines, List.ofSeq stderrLines)
                | false -> return Ok "Build succeeded"
            }

          let toBuildError (failure: int option * string list * string list) : SageFsError =
            match failure with
            | None, _, _ -> buildTimeoutError ()
            | Some exitCode, stdout, stderr ->
              SageFsError.BuildFailed(exitCode, CompileOrderInsight.enrich buildProject (buildDiagnosticsOf stdout stderr))

          // Fast path: incremental, no restore. If it fails only because a
          // NuGet restore is needed (fresh .fsproj → NETSDK1004, or a changed
          // package list), self-heal by retrying WITH a restore rather than
          // reporting a restore gap as a compile failure.
          let! first = runOnce (buildArguments false buildProject)
          match first with
          | Ok msg -> return Ok msg
          | Error ((Some _, stdout, stderr) as failure) when buildOutputNeedsRestore (stdout @ stderr) ->
            let! second = runOnce (buildArguments true buildProject)
            match second with
            | Ok msg -> return Ok msg
            | Error failure2 -> return Error (toBuildError failure2)
          | Error failure -> return Error (toBuildError failure)
        finally
          buildSemaphore.Release() |> ignore
    }
