namespace SageFs

open System
open System.IO
open System.Reflection

/// roast-4 #0(a): adopt a session project's own build of SageFs.Core into
/// the worker process the daemon is about to spawn.
///
/// SageFs.Host statically links SageFs.Core, and SageFs.Core.dll is listed
/// in SageFs.Host.deps.json — a Trusted-Platform-Assembly (TPA) for the host
/// process. TPA-listed dependencies of the ENTRY assembly are resolved by
/// the native hostfxr/hostpolicy binder, which the CLR consults BEFORE any
/// managed AssemblyLoadContext's "is this identity already loaded" check
/// ever runs. This was verified empirically, not assumed: pre-loading the
/// session project's own SageFs.Core.dll via
/// AssemblyLoadContext.Default.LoadFromAssemblyPath as the very first
/// action in SageFs.Host's `main` — before any other Core-typed code in the
/// process was JIT-compiled (confirmed via
/// AppDomain.CurrentDomain.GetAssemblies() returning empty at that point) —
/// still lost: a live `/eval` of a running host resolved
/// `typeof<SageFs.SageFsError>.Assembly.Location` to the host's own TPA
/// path, and the "adopted" assembly never even appeared in
/// AppDomain.CurrentDomain.GetAssemblies() afterward. No in-process trick
/// inside the SPAWNED host process — including a module-level `do`, since
/// F# does not run every compiled file's top-level bindings before
/// `[<EntryPoint>] main`, only whichever module the call graph actually
/// touches — can win that race.
///
/// The fix has to happen BEFORE the process exists: give the launched
/// process a directory whose SageFs.Core.dll already IS the session
/// project's own build, so the native binder resolves to it as a matter of
/// course. Concurrent sessions all launch from the SAME shared host/
/// directory (the daemon's own, or the installed tool's) — mutating that
/// directory in place would race any other session's spawn or the running
/// worker's own file handles. So when adoption applies, a fresh PER-SESSION
/// private copy of the host directory is materialized instead; the shared
/// directory is never touched.
module HostCoreAdoption =

  let assemblyName = "SageFs.Core"

  /// Why the daemon did or did not adopt a session project's own build of
  /// SageFs.Core for the worker it is about to spawn.
  [<RequireQualifiedAccess>]
  type Adoption =
    /// A same-version candidate was found at this (pre-materialization)
    /// path — adopt it into a fresh private host root.
    | Adopted of path: string
    /// A candidate exists but its version differs from the running
    /// daemon's own SageFs.Core — fail closed rather than spawn a worker
    /// whose Core build cannot possibly match the daemon supervising it.
    | Refused of reason: string

  /// Every build of `SageFs.Core.dll` found under a session project's `bin`
  /// tree, newest `LastWriteTimeUtc` first — the same "newest copy wins"
  /// convention `ProjectLoading.ManualProjectParse.collectBinReferences`
  /// uses when it references project DLLs into FSI. Excludes the host's own
  /// output (`/host/`) and reference-assembly folders (`/ref/`) — neither
  /// is ever a session project's real build.
  /// The distinct directories that own the given project files — the roots
  /// whose `bin` trees hold their built SageFs.Core, if any. Unreadable
  /// paths are dropped rather than throwing.
  let projectDirsOf (projects: string list) : string list =
    projects
    |> List.choose (fun p ->
      try Some(Path.GetDirectoryName(Path.GetFullPath p))
      with _ -> None)
    |> List.distinct

  let findCandidates (projectDirs: string list) : string list =
    let dllFileName = assemblyName + ".dll"
    projectDirs
    |> List.collect (fun projectDir ->
      let binDir = Path.Combine(projectDir, "bin")
      match Directory.Exists binDir with
      | false -> []
      | true ->
        try
          Directory.GetFiles(binDir, dllFileName, SearchOption.AllDirectories)
          |> Array.toList
        with _ -> [])
    |> List.filter (fun path ->
      let normalized = path.Replace('\\', '/')
      not (normalized.Contains "/host/") && not (normalized.Contains "/ref/"))
    |> List.distinct
    |> List.sortByDescending (fun path ->
      try File.GetLastWriteTimeUtc path
      with _ -> DateTime.MinValue)

  /// Decide whether to adopt a candidate build of SageFs.Core against the
  /// running daemon's own version.
  ///
  /// An EQUAL version is the dogfooding case, not a coincidence to be
  /// suspicious of: the user edited SageFs.Core, `dotnet build` on the
  /// session project rebuilt Core (and, via ProjectReference, the daemon's
  /// own binaries too) — within that window both builds are one build, and
  /// the project's copy is exactly the code the user wants running. Only a
  /// genuinely DIFFERENT version is refused.
  let decide (hostVersion: Version) (candidatePath: string) (candidateVersion: Version) : Adoption =
    match candidateVersion = hostVersion with
    | true -> Adoption.Adopted candidatePath
    | false ->
      Adoption.Refused(
        sprintf
          "The running SageFs daemon (SageFs.Core %s) and the session project's own build (SageFs.Core %s) come from different builds — rebuild SageFs so both come from one build, then retry."
          (hostVersion.ToString())
          (candidateVersion.ToString()))

  /// Copies `sharedHostDir` into a fresh `<privateRoot>/host/` directory and
  /// substitutes `SageFs.Core.dll` (+ `.pdb`, if either side has one) with
  /// `candidatePath`. Every other file (including `host-manifest.json`, so
  /// the host's own fail-closed startup check still passes) is copied
  /// through unchanged. Never mutates `sharedHostDir` itself.
  let materialize (sharedHostDir: string) (privateRoot: string) (candidatePath: string) : unit =
    let privateHostDir = Path.Combine(privateRoot, "host")
    Directory.CreateDirectory privateHostDir |> ignore
    for file in Directory.GetFiles(sharedHostDir, "*", SearchOption.AllDirectories) do
      let relative = Path.GetRelativePath(sharedHostDir, file)
      let dest = Path.Combine(privateHostDir, relative)
      Directory.CreateDirectory(Path.GetDirectoryName dest) |> ignore
      File.Copy(file, dest, true)
    let destDll = Path.Combine(privateHostDir, assemblyName + ".dll")
    File.Copy(candidatePath, destDll, true)
    let destPdb = Path.ChangeExtension(destDll, ".pdb")
    let candidatePdb = Path.ChangeExtension(candidatePath, ".pdb")
    match File.Exists candidatePdb with
    | true -> File.Copy(candidatePdb, destPdb, true)
    | false ->
      // The shared host's own .pdb (just copied through above) would
      // otherwise describe the WRONG dll's symbols — remove it rather than
      // leave a mismatched pdb next to the substituted dll.
      try File.Delete destPdb with _ -> ()

  /// Best-effort recursive delete of a private launch root, once the worker
  /// process using it has exited. Idempotent: a root that is already gone
  /// (or was never created) is not an error.
  let cleanup (privateRoot: string) : unit =
    try
      match Directory.Exists privateRoot with
      | true -> Directory.Delete(privateRoot, true)
      | false -> ()
    with _ -> ()

  /// The outcome of resolving where a worker should launch from: the
  /// directory to pass to `Args.resolveHostLaunch`, an optional cleanup
  /// action the caller must run once the spawned process exits, and — when a
  /// session project's own SageFs.Core was adopted — the identity
  /// `(assemblyVersion, originalBuildWriteTimeUtc)` of that adopted build.
  /// `AdoptedCore` is the ground truth for the self-host staleness signal:
  /// it is exactly what the worker loaded, captured before the process
  /// existed, so a later rebuild on disk can be compared against it.
  type LaunchPlan = {
    LaunchRoot: string
    Cleanup: (unit -> unit) option
    AdoptedCore: (string * DateTime) option
  }

  /// The full decision for one worker spawn: given the shared daemon-base
  /// directory (the same one `Args.resolveHostLaunch` would otherwise use
  /// directly), the session's id and project paths, and the running
  /// daemon's own SageFs.Core version — returns a `LaunchPlan` whose
  /// `LaunchRoot` is the shared directory unchanged when no project ships its
  /// own SageFs.Core, or a fresh private root (with a `Cleanup` and an
  /// `AdoptedCore` identity) when one does.
  ///
  /// Fail-closed: a candidate that cannot be inspected (unreadable, not a
  /// valid assembly) is an `Error`, never silently treated as absent.
  let resolveLaunchRoot
    (sharedDaemonBaseDir: string)
    (sessionId: string)
    (projects: string list)
    (hostVersion: Version)
    : Result<LaunchPlan, string> =
    let projectDirs = projectDirsOf projects
    match findCandidates projectDirs with
    | [] -> Ok { LaunchRoot = sharedDaemonBaseDir; Cleanup = None; AdoptedCore = None }
    | best :: _ ->
      try
        let candidateVersion = AssemblyName.GetAssemblyName(best).Version
        match decide hostVersion best candidateVersion with
        | Adoption.Refused reason -> Error reason
        | Adoption.Adopted path ->
          let sharedHostDir = Path.Combine(sharedDaemonBaseDir, "host")
          let privateRoot =
            Path.Combine(
              Path.GetTempPath(),
              sprintf "sagefs-host-adopt-%s-%s" sessionId (Guid.NewGuid().ToString("N").[..7]))
          materialize sharedHostDir privateRoot path
          let adopted = (candidateVersion.ToString(), File.GetLastWriteTimeUtc path)
          Ok { LaunchRoot = privateRoot; Cleanup = Some(fun () -> cleanup privateRoot); AdoptedCore = Some adopted }
      with ex ->
        Error(sprintf "Could not verify the session project's SageFs.Core build at %s: %s" best ex.Message)

  /// How the currently-loaded SageFs.Core build compares to the newest
  /// build found on disk — the F5b Phase 1 self-hosting signal: a loaded
  /// build must never silently run stale code without the daemon knowing
  /// it.
  [<RequireQualifiedAccess>]
  type SelfHostFreshness =
    /// The loaded build IS the newest build on disk.
    | Current
    /// A newer (or differently versioned) build exists on disk.
    | Stale of loaded: string * newest: string
    /// Freshness cannot be determined — no loaded copy, or no candidate
    /// found on disk.
    | Indeterminate of reason: string

  /// Small tolerance against filesystem write-time jitter (copy/build
  /// pipelines can nudge a timestamp by a few hundred ms without the bytes
  /// actually changing) so a same-version build is never flagged Stale
  /// purely from clock noise.
  let private freshnessEpsilon = TimeSpan.FromSeconds 2.0

  /// Pure: is the loaded SageFs.Core the newest build on disk?
  ///
  /// `loaded` and `newest` are each `(assemblyVersion, fileWriteTimeUtc)`,
  /// or `None` when there is nothing to compare (no build currently
  /// loaded, or no on-disk candidate at all). Stale when the newest
  /// candidate's version differs from the loaded one, OR its write time is
  /// newer than the loaded one's by more than `freshnessEpsilon`.
  let selfHostFreshness
    (loaded: (string * DateTime) option)
    (newest: (string * DateTime) option)
    : SelfHostFreshness =
    match loaded, newest with
    | None, _ -> SelfHostFreshness.Indeterminate "no SageFs.Core build is currently loaded to compare against"
    | _, None -> SelfHostFreshness.Indeterminate "no SageFs.Core build was found on disk to compare against"
    | Some(loadedVersion, loadedWriteTime), Some(newestVersion, newestWriteTime) ->
      let versionDiffers = newestVersion <> loadedVersion
      let newerOnDisk = newestWriteTime - loadedWriteTime > freshnessEpsilon
      match versionDiffers || newerOnDisk with
      | true -> SelfHostFreshness.Stale(loadedVersion, newestVersion)
      | false -> SelfHostFreshness.Current

  /// The newest SageFs.Core build currently on disk for these projects, as
  /// `(assemblyVersion, fileWriteTimeUtc)` — the "newest" side to feed
  /// `selfHostFreshness` at status time. `None` when no project ships its own
  /// SageFs.Core (the common non-self-hosting case) or nothing is readable.
  /// Fail-safe: any inspection error yields `None`, never throws — a status
  /// call must stay total.
  let newestCandidateIdentity (projects: string list) : (string * DateTime) option =
    match findCandidates (projectDirsOf projects) with
    | [] -> None
    | best :: _ ->
      try Some(AssemblyName.GetAssemblyName(best).Version.ToString(), File.GetLastWriteTimeUtc best)
      with _ -> None

  /// The one-line, actionable affordance to surface for a self-host session's
  /// freshness — `None` unless the loaded build is genuinely `Stale`, so
  /// normal (non-self-hosting or up-to-date) sessions show nothing. The
  /// message names both the loaded and the newer on-disk build and the exact
  /// remediation (`hard_reset_fsi_session rebuild=true`).
  let formatFreshnessAffordance (freshness: SelfHostFreshness) : string option =
    match freshness with
    | SelfHostFreshness.Current -> None
    | SelfHostFreshness.Indeterminate _ -> None
    | SelfHostFreshness.Stale(loaded, newest) ->
      Some(
        sprintf
          "⚠ Self-host staleness: this session loaded SageFs.Core %s, but a newer build (%s) is on disk. Run hard_reset_fsi_session with rebuild=true to reload the current build."
          loaded newest)
