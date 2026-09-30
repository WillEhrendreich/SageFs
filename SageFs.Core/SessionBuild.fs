namespace SageFs

open System
open System.Diagnostics
open System.IO
open System.Threading
open SageFs.ProcessEnvironment
open SageFs.Utils

/// The project-build subsystem, extracted from the SessionManager supervisor so
/// that file stops accreting (its line budget is a ratchet — see ArchitectureTests).
/// It resolves the build target, structures `dotnet build` diagnostics, caps
/// build concurrency, and runs a build that self-heals a missing or stale NuGet
/// restore. Shared by the daemon's cold-restart path (SessionManager) and the
/// worker's hard-reset path (AppState) so both surface the same diagnostics and
/// both restore when a project needs it.
module SessionBuild =

  /// Run a blocking action on a dedicated background thread, never a
  /// thread-pool thread — see `SessionManager.fs`'s copy of this helper for
  /// the full rationale (observed 2026-09-22: `Task.Run`+blocking
  /// `ReadLine()` loops pinning pool threads for a build's whole duration,
  /// contributing to a daemon-wide lockup under concurrent sessions).
  /// `runOnce` below can run `cores/4` builds concurrently
  /// (`buildConcurrencyLimit`), each holding two of these readers for the
  /// build's full multi-minute duration on a large repo — worth moving off
  /// the pool even though it is bounded, not per-session.
  let private runOnDedicatedThread (name: string) (action: unit -> unit) : System.Threading.Tasks.Task =
    let tcs = System.Threading.Tasks.TaskCompletionSource()
    let thread =
      System.Threading.Thread(fun () ->
        try
          action ()
          tcs.SetResult()
        with ex ->
          tcs.SetException(ex))
    thread.IsBackground <- true
    thread.Name <- name
    thread.Start()
    tcs.Task

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

  /// Optimizations MUST stay off for every session build. This is not a style
  /// preference or an incidental side effect of never passing `-c Release` —
  /// it is a hot-reload correctness precondition, and it is passed explicitly
  /// so it can never again be "accidentally Debug." See
  /// `hot-reload-reach-options.md` (repo root) §1.4/§1.7 for the full
  /// research; the short version, reproduced independently against this SDK
  /// (net10.0, dotnet 10.0.401) before this comment was written:
  ///
  ///  1. **FSC's own Release optimizer is the dominant inliner.** It bakes
  ///     function bodies into closures *in the IL on disk* — e.g. a route
  ///     table's captured handlers — where no runtime knob can reach them.
  ///     A hot-reload detour that targets the original method never fires,
  ///     because the call to it was never emitted.
  ///  2. **The CoreCLR JIT folds reads of `initonly` static fields into
  ///     constants once the assembly is built optimized** (most F#
  ///     module-level `let` bindings compile to exactly such a field).
  ///     Measured directly: a Release-built reader, once promoted past
  ///     tier-0, freezes on whatever value existed at JIT time — a
  ///     hot-reload write after that lands in the field (reflection confirms
  ///     it) but the reader returns the OLD value forever. `-p:Optimize=false`
  ///     (even under `-c Release`) makes every write visible immediately,
  ///     because it flips the assembly's `DebuggableAttribute` from
  ///     `(3)` to `(259)` = `DisableOptimizations`, which the JIT's own
  ///     static-readonly-folding check (`impImportStaticReadOnlyField`) is
  ///     gated on. `DOTNET_JitNoInline=1` does NOT fix this — it stops
  ///     inlining, not constant folding — and neither does keeping
  ///     `--crossoptimize-`/`--nooptimizationdata` while leaving
  ///     `--optimize+`: measured, the assembly still carries
  ///     `DebuggableAttribute(3)` and the freeze still reproduces. Only
  ///     `-p:Optimize=false` (which drives FSC's `--optimize-` AND flips the
  ///     JIT-visible attribute) closes both walls at once.
  ///
  /// `-p:Optimize=false` is passed as an MSBuild command-line property, which
  /// MSBuild treats as a GLOBAL property: a project's own
  /// `Directory.Build.props`/`.fsproj` (even an unconditional
  /// `<Optimize>true</Optimize>`) cannot reassign it during evaluation — this
  /// was verified empirically against exactly that override, and the built
  /// assembly still carried `DebuggableAttribute(259)`. So this is a
  /// structural guarantee, not a convention a user's own build config could
  /// silently defeat, PROVIDED the build actually goes through this
  /// function — a pre-built assembly the daemon never rebuilds (e.g. the
  /// user built Release by hand outside SageFs) is not covered by this and
  /// needs the runtime `DOTNET_JITMinOpts=1` fallback described in the
  /// research doc instead.
  ///
  /// DO NOT remove this flag to "clean up" or because a future Release
  /// pipeline wants a faster build — see `SessionBuildOptimizationGateTests`
  /// for the regression test guarding it.
  let optimizationDisablingProperty = "-p:Optimize=false"

  /// Whether a build should be handed SageFs's OWN assembly as a reference.
  ///
  /// The answer is a DU, not a bool, because the three cases are different
  /// claims and a bool would collapse them: an assembly that IS there, one that
  /// is NOT there, and one nobody looked for are not the same fact. Getting
  /// this wrong in the cheap direction injects a `Reference` to a file that
  /// does not exist, and MSBuild reports that as a missing-assembly error
  /// pointing at a path the user never wrote — the most confusing diagnostic
  /// this subsystem can produce.
  type CoreReference =
    /// The assembly is at this exact path, so inject it.
    | Available of assembly: string
    /// Looked, and it is not there. Say so and build without it.
    | Absent of because: string
    /// Not looked for. Never the same claim as `Absent`.
    | NotChecked

  /// Decides whether to inject the reference. Pure: the caller supplies what it
  /// knows and this decides, so the policy is testable without a filesystem.
  ///
  /// `known` is the answer to "do you know where SageFs.Core.dll is", which is
  /// what distinguishes `Absent` from `NotChecked`. An empty or whitespace path
  /// is `Absent`, not a path — MSBuild would treat "" as a HintPath to the
  /// current directory and fail obscurely.
  let decideCoreReference (known: string option) : CoreReference =
    match known with
    | Some path when not (String.IsNullOrWhiteSpace path) -> Available path
    | Some path -> Absent (sprintf "SageFs.Core.dll path was blank: '%s'" path)
    | None -> NotChecked

  /// The MSBuild property that injects the reference, when there is one to
  /// inject.
  ///
  /// WHY A .targets FILE AND NOT A PROPERTY. This was measured, not chosen:
  ///
  ///   `-p:ReferencePath=<dir>` does NOT work. It tells MSBuild where to SEARCH
  ///   for references that already exist; it can never introduce a new one. A
  ///   project with no `Reference` to SageFs.Core compiles without it, and the
  ///   user sees FS0039 `HolderRegistry is not defined`.
  ///
  ///   `-p:CustomAfterMicrosoftCommonTargets=<file>` DOES work. MSBuild imports
  ///   the named file after its common targets, and that file can add a
  ///   `Reference` item. Verified end to end on a real sample project with its
  ///   `Reference` deleted: it builds and runs.
  ///
  /// The file must be written by the caller; this module only decides whether
  /// the property is warranted and spells it exactly once, so the spelling
  /// cannot drift between call sites.
  let coreReferenceProperty (targetsFile: string) =
    sprintf "-p:CustomAfterMicrosoftCommonTargets=%s" targetsFile

  /// Says "SageFs is the one building this". Sent whenever the reference is
  /// injected, so a project that builds SageFs.Core ITSELF (the ConsoleTicker
  /// sample orders Core first for a raw `dotnet build`) can yield to the
  /// injection instead of adding a second, conflicting one. A project cannot
  /// tell from CustomAfterMicrosoftCommonTargets: the SDK defaults that property
  /// to a path of its own, so it is never empty, and a nested MSBuild task
  /// INHERITS it, which is how Core came to be compiled against itself.
  let managedBuildProperty = "-p:SageFsManagedBuild=true"

  /// Every argument a build that injects Core carries, in one place so the
  /// injection and its marker can never be sent apart.
  let injectionArguments (targetsFile: string) : string list =
    [ coreReferenceProperty targetsFile; managedBuildProperty ]

  /// The CONTENT of the generated .targets file. Pure, so the exact text is
  /// testable without touching a filesystem.
  ///
  /// `Private=true` matters and is not decoration: without it the reference is
  /// compile-time only, and the user's app fails at RUN time with
  /// `FileNotFoundException: SageFs.Core` — long after the build looked fine
  /// and inside a hot-reload session where the stack trace points at the app
  /// rather than at the missing dependency.
  ///
  /// The XML value is escaped, not trusted: the path comes from the process's
  /// own assembly location, and a raw `&` or `<` in it would produce a .targets
  /// file MSBuild rejects with a parse error that names no recognisable cause.
  let coreReferenceTargetsContent (assembly: string) =
    let escaped =
      (assembly
       |> fun s -> s.Replace("&", "&amp;")
       |> fun s -> s.Replace("<", "&lt;")
       |> fun s -> s.Replace(">", "&gt;")
       |> fun s -> s.Replace("\"", "&quot;"))
    $"""<Project>
  <!-- GENERATED BY SAGEFS. Do not edit and do not commit: SageFs writes this
       file on every build so a user project can use the registered-holder API
       (SageFs.Holder / HolderRegistry / RegisteredHolder) without adding a
       dependency to its own .fsproj. It is deleted with the build's temp dir. -->
  <ItemGroup>
    <Reference Include="SageFs.Core">
      <HintPath>{escaped}</HintPath>
      <Private>true</Private>
    </Reference>
  </ItemGroup>
</Project>"""

  /// The `dotnet` arguments of a session rebuild. Incremental on purpose: a
  /// clean build deletes the last good output before compiling, so one compile
  /// error would leave the project with nothing to run until built by hand.
  ///
  /// `restore = false` is the fast path (`--no-restore`) — correct once the
  /// project has a `project.assets.json`. `restore = true` drops `--no-restore`
  /// so `dotnet build` restores first, which a never-restored project (fresh
  /// .fsproj → NETSDK1004) or a project whose package list just changed needs.
  ///
  /// Both paths always carry `optimizationDisablingProperty` — see its doc
  /// comment for why hot reload depends on it structurally.
  let buildArguments (restore: bool) (buildProject: string) : string list =
    match restore with
    | false -> [ "build"; buildProject; "--no-restore"; optimizationDisablingProperty ]
    | true  -> [ "build"; buildProject; optimizationDisablingProperty ]

  /// Whether a failed build's output says a NuGet restore is required, so the
  /// build is worth retrying WITH a restore instead of being reported as a
  /// compile failure. Covers the fresh-project assets-missing error (NETSDK1004)
  /// and a newly-added/removed package reference (NU1101/NU1102 and the generic
  /// "run a NuGet package restore" guidance MSBuild emits).
  ///
  /// It also covers a cold tree whose own props import something restore
  /// generates: MSB4019 ("imported project ... was not found") on
  /// `obj/<proj>.nuget.g.props` or on a path under `$(NuGetPackageRoot)`, both
  /// of which only restore creates or defines. The bare code is NOT enough:
  /// MSB4019 is also what a typo'd import prints, and restore cannot create a
  /// file its author never wrote, so that one is reported as it is.
  let buildOutputNeedsRestore (lines: string list) : bool =
    let importedProjectNotFound = "MSB4019"
    let restoreGeneratedImportMarkers = [ ".nuget.g."; "$(NuGetPackageRoot)" ]
    let missingRestoreGeneratedImport (l: string) =
      l.Contains(importedProjectNotFound, StringComparison.OrdinalIgnoreCase)
      && restoreGeneratedImportMarkers
         |> List.exists (fun marker -> l.Contains(marker, StringComparison.OrdinalIgnoreCase))
    let needle (l: string) =
      missingRestoreGeneratedImport l
      || l.Contains("NETSDK1004", StringComparison.OrdinalIgnoreCase)
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

  /// Where the running Core actually is, or `None` when that cannot be
  /// established.
  ///
  /// `AppContext.BaseDirectory` is NOT used: inside the FSI host it is
  /// deliberately overridden to the USER PROJECT's build output
  /// (`SageFs.FsiHost/Program.fs` sets `APP_CONTEXT_BASE_DIRECTORY`), so it
  /// points at the app, not at the tool. The executing assembly's own location
  /// is the one source that is not overridden, and it is the assembly whose
  /// types the user is about to bind to.
  let private runningCoreAssembly () : string option =
    try
      // `SessionBuild` is a MODULE, so there is no `typeof<SessionBuild>` to
      // name. `HolderRegistry` is a real type declared in this same assembly,
      // which is exactly the point: its location IS the Core we want to inject,
      // so there is no indirection through a path that could be wrong.
      let dir = Path.GetDirectoryName typeof<HolderRegistry>.Assembly.Location
      let candidate = Path.Combine(dir, "SageFs.Core.dll")
      match File.Exists candidate with
      | true -> Some candidate
      | false -> None
    with _ -> None

  /// Prepares the reference injection for one build, and hands back the extra
  /// MSBuild property plus a cleanup to run afterwards.
  ///
  /// The .targets file must OUTLIVE the build process, so it is written to the
  /// system temp directory rather than next to the project: a project-local
  /// file would be picked up by the user's own git status, and would be a file
  /// SageFs created inside a directory it does not own.
  ///
  /// Returns `Ok (property, cleanup)` when the reference will be injected, and
  /// `Ok (None, id)` when it will not — with the reason already recorded on
  /// `CoreReference` rather than swallowed here. A build that cannot inject
  /// still builds; it simply does not offer the holder API.
  let private prepareCoreReference () : string list * (unit -> unit) =
    match decideCoreReference (runningCoreAssembly ()) with
    | CoreReference.Available assembly ->
      let targetsFile =
        Path.Combine(Path.GetTempPath(), sprintf "sagefs-inject-%d.targets" (Environment.ProcessId))
      try
        File.WriteAllText(targetsFile, coreReferenceTargetsContent assembly)
        injectionArguments targetsFile, (fun () ->
          try File.Delete targetsFile with _ -> ())
      with ex ->
        // A failure HERE must not fail the build: the injection is an
        // enhancement, and a user's project that does not use the holder API
        // has no need of it. Say why and carry on.
        Log.warn "[SessionBuild] could not prepare the Core reference injection: %s" ex.Message
        [], id
    | CoreReference.Absent why ->
      Log.debug "[SessionBuild] no Core reference injected: %s" why
      [], id
    | CoreReference.NotChecked ->
      [], id

  /// Free build slots right now — full capacity when no build is in flight.
  /// Lets a test observe the semaphore exists at the right capacity without
  /// spawning a real (multi-second) `dotnet build` in the default suite.
  let availableBuildSlots () = buildSemaphore.CurrentCount

  let private buildTimeoutError () =
    let timeoutDiagnostic =
      { File = None; Line = None; Column = None; Code = None
        Severity = BuildDiagnosticSeverity.Blocking
        Message = "Build timed out (10 min limit)" }
    SageFsError.BuildFailed(-1, [ timeoutDiagnostic ])

  /// How one `dotnet build` invocation failed. `TimedOut` used to be the `None`
  /// of an `int option` exit code, which is why "could not start" had nowhere to
  /// go and escaped as an exception instead.
  [<RequireQualifiedAccess>]
  type private BuildFailure =
    | TimedOut
    /// The process never ran: dotnet is not on the daemon's PATH, or the working
    /// directory does not exist.
    | CouldNotStart of reason: string
    | ExitedWith of exitCode: int * stdout: string list * stderr: string list

  /// A build that could not start, in the shape every caller already handles,
  /// carrying what to check.
  let private couldNotStartError (workingDir: string) (reason: string) =
    let diagnostic =
      { File = None; Line = None; Column = None; Code = None
        Severity = BuildDiagnosticSeverity.Blocking
        Message =
          sprintf "Could not start `dotnet build` in '%s': %s. → Check that the .NET SDK is installed and on the daemon's PATH, and that the directory exists." workingDir reason }
    SageFsError.BuildFailed(-1, [ diagnostic ])

  /// Runs a build so that it ALWAYS yields a result. A rebuild is started in the
  /// background and answers through a reply channel that only its completion
  /// can release, so a build that throws must come back as an `Error` like any
  /// other failure. Left to throw, the exception is rethrown on a thread-pool
  /// thread (which ends the process), and if it did not, the caller would never
  /// be answered and the session would count a finished rebuild as in flight.
  let answeringAlways (build: unit -> Async<Result<string, SageFsError>>) : Async<Result<string, SageFsError>> =
    async {
      try return! build ()
      with ex ->
        Log.warn "[SessionBuild] a build threw instead of answering: %s" ex.Message
        return Error (SageFsError.Unexpected ex)
    }

  let runBuildAsync (projects: string list) (workingDir: string) : Async<Result<string, SageFsError>> =
    async {
      let primaryProject = projects |> List.tryHead
      match primaryProject with
      | None -> return Ok "No projects to build"
      | Some projFile ->
        let buildProject = resolveBuildProjectPath workingDir projFile
        let! ct = Async.CancellationToken
        do! buildSemaphore.WaitAsync(ct) |> Async.AwaitTask
        // Prepared INSIDE the try so the .targets file is removed on every
        // path, including a cancellation between here and the build. It is
        // keyed by process id, so a second build in the same process reuses
        // the same file and must not delete it out from under the first.
        let injectionProperty, cleanupInjection = prepareCoreReference ()
        let withInjection (args: string list) = args @ injectionProperty
        try
          // Run one `dotnet` invocation. Ok on success; Error says how it failed
          // (`BuildFailure`), with stdout/stderr for diagnostics and the retry.
          let runOnce (args: string list) : Async<Result<string, BuildFailure>> =
            async {
              let psi = ProcessStartInfo(
                "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workingDir)
              for arg in args do
                psi.ArgumentList.Add(arg)
              // Strip whatever MSBuild-resolution variables THIS process (daemon
              // or worker) may itself have inherited or picked up, so the
              // session's own build resolves the SDK from `workingDir`, not
              // from whichever project SageFs last loaded internally. See
              // SageFs.ProcessEnvironment.
              applyTo psi []
              // Starting the process can throw (dotnet not on the daemon's PATH,
              // or the working directory is gone). That is a build that did not
              // run, and it has to come back as one: the caller answers through a
              // parked reply channel that only a completion can release.
              let started =
                try Ok (Process.Start(psi))
                with ex -> Error (BuildFailure.CouldNotStart ex.Message)
              match started with
              | Error failure -> return Error failure
              | Ok proc ->
              let stderrLines = System.Collections.Generic.List<string>()
              let stderrTask =
                runOnDedicatedThread "sagefs-build-stderr-reader" (fun () ->
                  let mutable line = proc.StandardError.ReadLine()
                  while not (isNull line) do
                    stderrLines.Add(line)
                    line <- proc.StandardError.ReadLine())
              // dotnet build prints compiler errors on stdout, so both streams are kept.
              let stdoutLines = System.Collections.Generic.List<string>()
              let stdoutTask =
                runOnDedicatedThread "sagefs-build-stdout-reader" (fun () ->
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
                return Error BuildFailure.TimedOut
              | false ->
                let! _ = System.Threading.Tasks.Task.WhenAll(stderrTask, stdoutTask) |> Async.AwaitTask
                let exitCode = proc.ExitCode
                proc.Dispose()
                match exitCode <> 0 with
                | true -> return Error (BuildFailure.ExitedWith(exitCode, List.ofSeq stdoutLines, List.ofSeq stderrLines))
                | false -> return Ok "Build succeeded"
            }

          let toBuildError (failure: BuildFailure) : SageFsError =
            match failure with
            | BuildFailure.TimedOut -> buildTimeoutError ()
            | BuildFailure.CouldNotStart reason -> couldNotStartError workingDir reason
            | BuildFailure.ExitedWith (exitCode, stdout, stderr) ->
              SageFsError.BuildFailed(exitCode, CompileOrderInsight.enrich buildProject (buildDiagnosticsOf stdout stderr))

          // Fast path: incremental, no restore. If it fails only because a
          // NuGet restore is needed (fresh .fsproj → NETSDK1004, or a changed
          // package list), self-heal by retrying WITH a restore rather than
          // reporting a restore gap as a compile failure.
          let! first = runOnce (withInjection (buildArguments false buildProject))
          match first with
          | Ok msg -> return Ok msg
          | Error (BuildFailure.ExitedWith (_, stdout, stderr)) when buildOutputNeedsRestore (stdout @ stderr) ->
            let! second = runOnce (withInjection (buildArguments true buildProject))
            match second with
            | Ok msg -> return Ok msg
            | Error failure2 -> return Error (toBuildError failure2)
          | Error failure -> return Error (toBuildError failure)
        finally
          cleanupInjection ()
          buildSemaphore.Release() |> ignore
    }
