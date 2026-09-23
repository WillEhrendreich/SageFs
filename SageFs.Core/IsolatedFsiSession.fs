/// Starting an FSI session in an isolated host process, for the worker: build (or reuse) the host with the
/// project's own SDK, decide which runtime the host should run on, start it, and wrap it as an IFsiSession.
///
/// The worker itself stays on its own runtime; only the host — the process the user's code actually runs in —
/// is launched on the runtime the project needs, and it shares no assembly with SageFs.
module SageFs.IsolatedFsiSession

open System
open System.IO
open System.Threading.Tasks
open SageFs.FsiHostBuild
open SageFs.FsiHostClient
open SageFs.FsiSession
open SageFs.RemoteFsiSession
open SageFs.Utils

/// Why an isolated session could not be started.
type IsolatedStartError =
  | SdkUnresolved of HostBuildError
  | HostBuildFailed of HostBuildError
  | RuntimeNotInstalled of instructions: string
  | HostStartFailed of StartError
  | AgentAttachFailed of AttachError

/// Every case says what happened and, where the user can act, what to do.
let describeStartError (error: IsolatedStartError) : string =
  match error with
  | SdkUnresolved reason -> describeBuildError reason
  | HostBuildFailed reason -> describeBuildError reason
  | RuntimeNotInstalled instructions -> instructions
  | HostStartFailed reason -> SageFs.FsiHostClient.describeStartError reason
  | AgentAttachFailed reason -> describeAttachError reason

/// The dotnet muxer: DOTNET_HOST_PATH, else the one next to the running runtime.
let dotnetPath () : string =
  match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
  | null
  | "" ->
    Args.muxerFromRuntimeDir
      (System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory())
      (OperatingSystem.IsWindows())
  | path -> path

/// Overrides where built hosts are cached. A host is content-addressed (SDK version + exact sources + its Harmony), so
/// sharing one cache between daemons is safe; a test harness whose daemons each get a fresh data dir uses this to build the
/// host once instead of once per daemon.
[<Literal>]
let HostCacheEnvironmentVariable = "SAGEFS_HOST_CACHE_DIR"

/// Where built hosts are cached: the override, else under the SageFs data dir.
let hostCacheRootWith (getEnv: string -> string | null) (sageFsDir: string) : string =
  match getEnv HostCacheEnvironmentVariable with
  | null -> Path.Combine(sageFsDir, "hosts")
  | value when String.IsNullOrWhiteSpace value -> Path.Combine(sageFsDir, "hosts")
  | value -> value

let hostCacheRoot () : string = hostCacheRootWith Environment.GetEnvironmentVariable DaemonState.SageFsDir

/// The env var that carries the primary project's own build output directory into the isolated host process
/// (issue #142's own suggested name). `AppContext.BaseDirectory` and `Assembly.Location` inside a session
/// point at the HOST's own directory — or, for a referenced project's assembly, wherever FSI's runtime
/// loaded it from, which is not necessarily the project's build output either — never at the project's own
/// bin/<config>/<tfm>/, so any code that resolves config, fixtures or native libs relative to its own
/// assembly breaks. The host sets `AppContext.BaseDirectory` from this at startup (see FsiHost/Program.fs);
/// code that reads the env var directly gets the same answer without the AppContext indirection.
[<Literal>]
let ProjectOutputEnvironmentVariable = "SAGEFS_PROJECT_OUTPUT"

/// Pure over injected filesystem primitives: the primary project's (the first of `projects` — the one
/// explicitly requested, not a transitive reference) own build output directory — the directory holding
/// the NEWEST (by write time) `<ProjectName>.dll` found anywhere under its `bin/`, mirroring
/// `RuntimeSelection.projectRuntimeRequirement`'s identical "newest match under bin/, by write time" rule
/// so the two can never disagree about which build is "the" one.
///
/// Deliberately NOT derived from the `-r:` references `solutionToFsiArgs` builds: the worker shadow-copies
/// the whole solution to a `/tmp/sagefs-shadow-*` directory before FSI ever sees it, rewriting every `-r:`
/// path to point there — so reading a `-r:` entry would report the SAME shadow-copy temp directory #142
/// already complained about for `Assembly.Location`, not the project's real build output. Reading straight
/// from disk, independent of what FSI was told to load, is what makes this answer actually different from
/// the bug it's fixing.
let primaryProjectOutputDirWith
    (directoryExists: string -> bool)
    (matchingFiles: string -> string -> string list)
    (writeTimeUtc: string -> DateTime)
    (projects: string list)
    : string option =
  match projects with
  | [] -> None
  | primary :: _ ->
    let projectDir = Path.GetDirectoryName(primary: string)
    let binDir = Path.Combine(projectDir, "bin")
    let expectedName = Path.GetFileNameWithoutExtension(primary: string) + ".dll"
    match directoryExists binDir with
    | false -> None
    | true ->
      matchingFiles binDir expectedName
      |> List.sortByDescending writeTimeUtc
      |> List.tryHead
      |> Option.bind (Path.GetDirectoryName >> Option.ofObj)

let primaryProjectOutputDir (projects: string list) : string option =
  primaryProjectOutputDirWith
    Directory.Exists
    (fun dir pattern -> Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories) |> Seq.toList)
    File.GetLastWriteTimeUtc
    projects

/// The isolated host's own FSharp.Core.dll (`typeof<unit>` lives there) — a sibling of the host's own entry
/// assembly, since `ensureBuiltWith` copies the SDK toolset's FSharp.Core into the same build output
/// directory as `FsiHost.dll`.
let private hostFSharpCoreDll (hostDll: string) : string = Path.Combine(Path.GetDirectoryName(hostDll: string), "FSharp.Core.dll")

/// The project's own resolved FSharp.Core.dll, if `-r:` references one — the same lookup as
/// `primaryProjectOutputDir`, by file name rather than by owning project.
let private projectFSharpCoreDll (fsiArgs: string list) : string option =
  fsiArgs
  |> List.tryPick (fun (arg: string) ->
    match arg.StartsWith("-r:", StringComparison.Ordinal) with
    | false -> None
    | true ->
      let path = arg.Substring 3
      match String.Equals(Path.GetFileName path, "FSharp.Core.dll", StringComparison.OrdinalIgnoreCase) with
      | true -> Some path
      | false -> None)

/// A project's FSharp.Core is a different BUILD from the host's own — see #141. Both file sizes are carried
/// because the file VERSION and even the .NET AssemblyVersion are commonly identical between the SDK's own
/// toolset copy and a project's NuGet-restored copy despite being different builds with different members
/// (a `Using` overload present in one, missing in the other) — version strings cannot detect this. File size
/// is the cheap, reliable signal the issue's own diagnosis used (SHA-256 would be stronger but costs a full
/// read of a multi-MB file on every warmup for a mismatch that, once known, never needs re-proving down to
/// the byte).
type FSharpCoreMismatch =
  { HostCopy: string
    HostSizeBytes: int64
    ProjectCopy: string
    ProjectSizeBytes: int64 }

let describeFSharpCoreMismatch (m: FSharpCoreMismatch) : string =
  sprintf
    "This session's code runs against the host's own FSharp.Core (%s, %d bytes), not the project's resolved copy (%s, %d bytes). FSI hosts everything in one process, and the first FSharp.Core loaded wins — even when a project references a different build under the same version number. Code compiled into the project that hits a member only the project's copy has (a known case: `use` inside `task { }`, see SageFs issue #141 — a DEBUG build calls `TaskBuilderBase.Using` as a real virtual method, which a mismatched FSharp.Core may not have; a RELEASE build inlines it away entirely, so building the project with `dotnet build -c Release` sidesteps this specific failure today) will fail with MissingMethodException; that failure is not a bug in your code."
    m.HostCopy
    m.HostSizeBytes
    m.ProjectCopy
    m.ProjectSizeBytes

/// Pure over file lengths, so it's testable without real FSharp.Core.dll files on disk. `None` when either
/// length can't be read (fine: it means "not proven mismatched", never a false positive) or the lengths
/// happen to agree.
let detectFSharpCoreMismatchWith
    (fileLength: string -> int64 option)
    (hostFSharpCore: string)
    (projectFSharpCore: string)
    : FSharpCoreMismatch option =
  match fileLength hostFSharpCore, fileLength projectFSharpCore with
  | Some hostLen, Some projLen when hostLen <> projLen ->
    Some
      { HostCopy = hostFSharpCore
        HostSizeBytes = hostLen
        ProjectCopy = projectFSharpCore
        ProjectSizeBytes = projLen }
  | _ -> None

let private fileLengthOrNone (path: string) : int64 option =
  try Some(FileInfo(path).Length)
  with _ -> None

/// #141 resolution: an IL-level identity rewrite so the PROJECT's own resolved FSharp.Core is what its
/// compiled code actually runs against — the same technique `FsiHostBuild.renameAssembly` already uses to
/// keep the agent's own Harmony from colliding with a user's Lib.Harmony, applied the other direction: not
/// "rename OUR dependency before WE compile against it" (Harmony's case — the consumer is compiled fresh,
/// so it never has a wrong-identity reference to begin with) but "rename an ALREADY-COMPILED third party's
/// dependency after the fact" — the project's DLL exists before SageFs ever sees it, so there is no
/// "compile fresh against the renamed name" option here.
///
/// Deliberately NOT a wholesale swap of the host's own FSharp.Core.dll: per #141's own diagnosis the two
/// builds are not superset/subset (the host's copy has FEWER `Using` overloads than the project's), so
/// swapping the file the host's OWN FsiHost.dll/FSharp.Compiler.Service depend on could just as easily
/// break the host's eval machinery as fix the project's code. This rewrite instead gives the project's own
/// FSharp.Core a NEW identity nothing else in the process uses, so it coexists with the host's untouched
/// original copy in the same process: two different builds of "the types named Microsoft.FSharp.Core.*",
/// living under two different assembly names, at once. The host's own code never references the new name,
/// so nothing about how it runs changes.
///
/// Renaming ONLY the shared `AssemblyNameReference` entry (not each TypeRef/MemberRef individually) is what
/// makes this a one-line edit per assembly: Cecil resolves every TypeRef whose scope is "FSharp.Core"
/// through that ONE reference object, so mutating its `.Name` retargets all of them transparently — the
/// same reason `renameAssembly` only ever had to touch `AssemblyDefinition.Name`, never walk the type table.
///
/// WIRED IN, via `ActorCreation.createActorImmediate` calling `fixShadowCopiedFSharpCoreReferences` (below)
/// immediately after `ShadowCopy.shadowCopySolution` — before namespace scanning, coverage instrumentation,
/// or FSI's own `-r:` loading ever touch the shadow-copied file, so there is exactly one physical file for
/// each project assembly and it is already correct before anything reads it. Two real findings from getting
/// here, both live-verified:
///
/// 1. Exposing the renamed FSharp.Core as an explicit `-r:` reference corrupts session-wide type-checking
///    (bare `Some 42;;` crashed FCS internally with `convMethodRef: could not bind to method`, and the
///    project's own function calls failed FCS's unification of two different-identity `unit`s). `--lib:`
///    (a search directory, not a compiler reference) avoids this — the CLR resolves the renamed assembly
///    lazily, on demand, invisible to FCS's own type-checking until something actually needs it.
///
/// 2. A rewrite applied late, as a patch on the flattened `fsiArgs: string list` inside
///    `IsolatedFsiSession.start`, is provably bypassed: `ShadowCopy.shadowCopySolution` re-derives every
///    project's own assembly from its TRUE original `bin/` output for IL coverage instrumentation,
///    operating on the rich `Solution` record — a step `start` cannot see because it only ever receives
///    the already-flattened strings. Fixed by moving the rewrite to run ON the shadow copy itself, in
///    `ActorCreation.fs`, before anything downstream can re-derive from the original.
///
/// A third, narrower finding surfaced once the rewrite actually took hold: F#'s cross-module inlining.
/// Every F# assembly embeds `FSharpOptimizationCompressedData(B)` — the data FCS reads to inline small,
/// simple functions from a referenced assembly directly into the CALLING code. For a trivial one-liner
/// (`let f () = typeof<int option>.Assembly.Location`), FCS inlines the body into the submission and
/// RECOMPILES it there — against FSI's own ambient FSharp.Core, not the rewritten identity — even though
/// the SAME function, invoked via plain reflection (`MethodInfo.Invoke`, bypassing FCS's compile-time
/// inlining) or called from inside a larger, non-inlinable function (proven: `useInTask`'s whole
/// resumable-code state machine is far too large to inline), correctly uses the rewritten copy. Live,
/// side-by-side, in the SAME submission: a direct call to `Repro.fsharpCoreLocation()` returned the host's
/// path; `MethodInfo.Invoke` on the exact same `MethodInfo` returned the project's own rewritten path.
/// `rewriteReferenceWith` strips `FSharpOptimizationCompressedData*` (inlining hints only) while leaving
/// `FSharpSignatureCompressedData*` untouched (still needed for FCS to type-check calls into the assembly
/// at all) — this forces every call into a rewritten assembly to be a real method call, never an inline.
module ProjectFSharpCoreIdentity =

  /// The identity the project's own FSharp.Core is renamed to. Distinct from `FsiHostBuild.HostHarmonyName`
  /// in kind (that one is compiled fresh against; this one is rewritten after the fact) but the same idea:
  /// a name nothing else in the process could ever collide with.
  [<Literal>]
  let RewrittenName = "SageFs.ProjectFSharpCore"

  /// F#-compiler-embedded manifest resources that let FCS INLINE a referenced assembly's simple functions
  /// into the calling code, recompiled fresh against whatever FSharp.Core the CALLER (not the callee) is
  /// ambient in — see the module doc comment for the live, side-by-side proof this causes for a rewritten
  /// assembly. `FSharpSignatureCompressedData*` (the F# signature data FCS needs to type-check calls into
  /// the assembly at all) is a DIFFERENT resource and is never touched.
  let private optimizationResourcePrefixes = [ "FSharpOptimizationCompressedData."; "FSharpOptimizationCompressedDataB." ]

  /// Pure over injected bytes: rewrite `dllBytes`'s AssemblyReference entry for "FSharp.Core", if it has
  /// one, to `RewrittenName` instead, and strip its cross-module inlining data so every call into it is a
  /// real method call FCS cannot recompile against a different FSharp.Core. `None` when the assembly has no
  /// FSharp.Core reference at all — most referenced assemblies in a typical -r: list won't, and are left
  /// completely untouched.
  let rewriteReferenceWith (readAssembly: byte[] -> Mono.Cecil.AssemblyDefinition) (dllBytes: byte[]) : byte[] option =
    use assembly = readAssembly dllBytes
    let reference =
      assembly.MainModule.AssemblyReferences
      |> Seq.tryFind (fun r -> String.Equals(r.Name, "FSharp.Core", StringComparison.Ordinal))
    match reference with
    | None -> None
    | Some r ->
      r.Name <- RewrittenName
      assembly.MainModule.Resources
      |> Seq.filter (fun res -> optimizationResourcePrefixes |> List.exists (fun prefix -> res.Name.StartsWith(prefix, StringComparison.Ordinal)))
      |> Seq.toList // materialize before mutating the collection we're iterating
      |> List.iter (fun res -> assembly.MainModule.Resources.Remove res |> ignore)
      use output = new MemoryStream()
      assembly.Write output
      Some(output.ToArray())

  let private readAssemblyFromBytes (bytes: byte[]) : Mono.Cecil.AssemblyDefinition =
    Mono.Cecil.AssemblyDefinition.ReadAssembly(new MemoryStream(bytes))

  /// Rewrite one on-disk assembly's FSharp.Core reference, IO and all. `None` (untouched, no file written)
  /// when the assembly has no FSharp.Core reference — includes both "genuinely doesn't reference it" and
  /// "couldn't be read as a .NET assembly at all" (never a session-killing exception; worst case, that one
  /// assembly keeps resolving against whatever it did before, exactly today's behavior).
  let rewriteFileReference (dllPath: string) : byte[] option =
    try rewriteReferenceWith readAssemblyFromBytes (File.ReadAllBytes dllPath)
    with _ -> None

  /// All of the work: given a detected mismatch and the `-r:` list FSI was about to receive, materialize
  /// rewritten copies of the project's FSharp.Core (renamed) and every referenced assembly that pointed at
  /// it (retargeted) into `outputDir`, and return the fsiArgs FSI should actually get — every rewritten
  /// entry swapped in, everything else (the large majority: framework and third-party references with no
  /// FSharp.Core dependency at all) passed through byte-identical to what was already there.
  let rewriteFsiArgs (outputDir: string) (mismatch: FSharpCoreMismatch) (fsiArgs: string list) : string list =
    Directory.CreateDirectory outputDir |> ignore
    let renamedFSharpCoreBytes = renameAssembly RewrittenName (File.ReadAllBytes mismatch.ProjectCopy)
    let renamedFSharpCorePath = Path.Combine(outputDir, RewrittenName + ".dll")
    File.WriteAllBytes(renamedFSharpCorePath, renamedFSharpCoreBytes)
    let rewritten =
      fsiArgs
      |> List.map (fun (arg: string) ->
        match arg.StartsWith("-r:", StringComparison.Ordinal) with
        | false -> arg
        | true ->
          let path = arg.Substring 3
          match String.Equals(path, mismatch.ProjectCopy, StringComparison.Ordinal) with
          | true -> arg // the project's own FSharp.Core is replaced wholesale below, not rewritten in place
          | false ->
            match rewriteFileReference path with
            | None -> arg
            | Some bytes ->
              let rewrittenPath = Path.Combine(outputDir, Path.GetFileName path)
              File.WriteAllBytes(rewrittenPath, bytes)
              "-r:" + rewrittenPath)
      |> List.filter (fun arg -> arg <> "-r:" + mismatch.ProjectCopy)
    // Deliberately --lib:, never -r:, for the renamed FSharp.Core: an explicit -r: reference makes FCS
    // treat it as a first-class source of ambient type names for the WHOLE session (proven live — it broke
    // "unit"/"option" resolution for code that never touches the project's rewritten assemblies at all).
    // --lib: only adds a search directory; the CLR resolves "SageFs.ProjectFSharpCore" lazily, on demand,
    // the moment the rewritten assemblies actually need it — invisible to FCS's own type-checking until then.
    rewritten @ [ "--lib:" + outputDir ]

/// #141, the REAL fix: rewrite a shadow-copied project assembly's FSharp.Core reference IN PLACE — the
/// SAME file every downstream step (namespace scanning, IL coverage instrumentation, FSI's own `-r:`
/// loading) already reads from, not a new copy at a new path. `ActorCreation.createActorImmediate` calls
/// this immediately after `ShadowCopy.shadowCopySolution`, before anything else touches
/// `sln.Projects[i].TargetPath` — earlier than `IsolatedFsiSession.start` even runs, let alone finishes,
/// which is exactly why the `fsiArgs`-string-level rewrite this module used to attempt there never took:
/// whatever reads `sln.Projects` first (proven live to be namespace scanning or coverage instrumentation,
/// not FSI's own loader) always wins the race and re-derives the loaded bytes from the project's original,
/// un-rewritten build output. There is only ever ONE physical file for each project assembly; making it
/// correct before anything reads it removes the race instead of trying to out-run it.
///
/// Resolves and builds the isolated host itself (same `resolveSdk`/`ensureBuiltWith` calls
/// `IsolatedFsiSession.start` makes later) — an extra call, but a cheap one: the host is content-addressed
/// and cached, so every session after the first for a given SDK finds it already built. Returns the shadow
/// directory a `--lib:` entry must be added for (only when a rewrite actually happened — the renamed
/// FSharp.Core needs to be on FSI's search path for the CLR to resolve it at runtime), so the caller can
/// fold it into `sln.LibPaths` before `solutionToFsiArgs` ever runs.
let fixShadowCopiedFSharpCoreReferences
    (logger: ILogger)
    (workingDir: string)
    (sln: SageFs.ProjectLoading.Solution)
    : string option =
  let projectFSharpCoreOf (po: Ionide.ProjInfo.Types.ProjectOptions) : string option =
    po.PackageReferences
    |> List.tryFind (fun pr -> String.Equals(Path.GetFileNameWithoutExtension(pr.FullPath: string), "FSharp.Core", StringComparison.OrdinalIgnoreCase))
    |> Option.map (fun pr -> pr.FullPath)
  match sln.Projects with
  | [] -> None
  | projects ->
    let dotnet = dotnetPath ()
    match resolveSdk dotnet workingDir with
    | Error _ -> None // can't resolve the host from here; the later detect-and-warn path in `start` still covers it
    | Ok sdk ->
      match ensureBuiltWith dotnet sdk (hostCacheRoot ()) with
      | Error _ -> None
      | Ok build ->
        let hostDll = match build with Built d -> d | Reused d -> d
        let hostFSharpCore = hostFSharpCoreDll hostDll
        let mutable fixedShadowDir = None
        for po in projects do
          match projectFSharpCoreOf po with
          | None -> ()
          | Some projectFSharpCore ->
            match detectFSharpCoreMismatchWith fileLengthOrNone hostFSharpCore projectFSharpCore with
            | None -> ()
            | Some mismatch ->
              try
                match ProjectFSharpCoreIdentity.rewriteFileReference po.TargetPath with
                | None -> ()
                | Some rewrittenBytes ->
                  File.WriteAllBytes(po.TargetPath, rewrittenBytes)
                  let shadowDir = Path.GetDirectoryName(po.TargetPath: string)
                  let renamedPath = Path.Combine(shadowDir, ProjectFSharpCoreIdentity.RewrittenName + ".dll")
                  match File.Exists renamedPath with
                  | true -> ()
                  | false ->
                    let renamedBytes = renameAssembly ProjectFSharpCoreIdentity.RewrittenName (File.ReadAllBytes mismatch.ProjectCopy)
                    File.WriteAllBytes(renamedPath, renamedBytes)
                  fixedShadowDir <- Some shadowDir
                  logger.LogWarning(
                    sprintf
                      "  Rewrote %s's FSharp.Core reference to its own identity in place (host: %s, %d bytes; project: %s, %d bytes)"
                      (Path.GetFileName(po.TargetPath: string))
                      mismatch.HostCopy
                      mismatch.HostSizeBytes
                      mismatch.ProjectCopy
                      mismatch.ProjectSizeBytes
                  )
              with ex ->
                logger.LogWarning(
                  sprintf
                    "  Could not rewrite %s's FSharp.Core reference (%s) — continuing with the host's copy, which is the pre-existing (potentially mismatched) behavior"
                    (Path.GetFileName(po.TargetPath: string))
                    ex.Message
                )
        fixedShadowDir

/// Start an isolated FSI session for `projects`, run from `workingDir`. `recorder` receives everything the user's
/// code and FSI write to stdout (so per-eval output capture works exactly as it does in-process).
let start
  (logger: ILogger)
  (recorder: TextWriter)
  (fsiArgs: string list)
  (workingDir: string)
  (projects: string list)
  (agent: HostAgent.AgentInit)
  : Async<Result<IFsiSession, IsolatedStartError>> =
  async {
    let dotnet = dotnetPath ()
    match resolveSdk dotnet workingDir with
    | Error reason -> return Error(SdkUnresolved reason)
    | Ok sdk ->
      // Building is a blocking process run; keep it off the caller's thread.
      let! built = Async.AwaitTask(Task.Run(fun () -> ensureBuiltWith dotnet sdk (hostCacheRoot ())))
      match built with
      | Error reason -> return Error(HostBuildFailed reason)
      | Ok build ->
        let dll =
          match build with
          | Built dll -> dll
          | Reused dll -> dll
        // The host is built for its SDK's target framework, so that major is the runtime it runs on by default.
        let hostMajor = int (sdk.Version.Split('.').[0])
        match RuntimeSelection.resolveRuntimeChoiceFor hostMajor projects with
        | RuntimeCompat.RuntimeMissing _ as choice -> return Error(RuntimeNotInstalled(RuntimeCompat.describe choice))
        | choice ->
          match choice with
          | RuntimeCompat.RollForward _ -> logger.LogInfo(sprintf "  Isolated FSI host: %s" (RuntimeCompat.describe choice))
          | _ -> ()
          // #142: give the project's own code a way back to its real build output directory — the host
          // process sets AppContext.BaseDirectory from this at startup (see FsiHost/Program.fs).
          let projectOutputEnv =
            match primaryProjectOutputDir projects with
            | Some dir -> [ ProjectOutputEnvironmentVariable, dir ]
            | None -> []
          // #141: the project's FSharp.Core, if it differs in build from the host's own (both commonly
          // report the same version), silently loses at runtime — warn now instead of waiting for the
          // MissingMethodException that only shows up when user code happens to hit a missing member. NOT
          // yet rewriting it: see ProjectFSharpCoreIdentity's doc comment for why a late, fsiArgs-string-
          // level rewrite is provably bypassed by ShadowCopy's own re-derivation from the project's
          // original build output, and what the real fix needs.
          match projectFSharpCoreDll fsiArgs with
          | None -> ()
          | Some projectFSharpCore ->
            match detectFSharpCoreMismatchWith fileLengthOrNone (hostFSharpCoreDll dll) projectFSharpCore with
            | Some mismatch -> logger.LogWarning("  " + describeFSharpCoreMismatch mismatch)
            | None -> ()
          let options =
            { HostDll = dll
              Dotnet = dotnet
              FsiArgs = fsiArgs
              WorkingDir = workingDir
              Environment = projectOutputEnv @ RuntimeCompat.rollForwardEnv choice @ Middleware.ValueReadTracking.processEnvironment agent.ValueReads
              OnOutput =
                fun stream text ->
                  match stream with
                  | FsiHost.FsiProtocol.StdOut -> recorder.Write text
                  | FsiHost.FsiProtocol.StdErr -> logger.LogDebug(sprintf "[fsihost stderr] %s" (text.TrimEnd()))
              OnLog = fun line -> logger.LogDebug(sprintf "[fsihost] %s" line)
              StartupTimeoutMs = 120_000 }
          match! start options with
          | Ok host ->
            logger.LogInfo(sprintf "  Isolated FSI host started: %s, FSharp.Core %s (pid %d)" host.Runtime host.FSharpCoreVersion host.ProcessId)
            match! attach host agent with
            | Result.Ok session -> return Ok(session :> IFsiSession)
            | Result.Error reason -> return Error(AgentAttachFailed reason)
          | Error reason -> return Error(HostStartFailed reason)
  }
