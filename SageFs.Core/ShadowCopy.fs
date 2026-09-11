module SageFs.ShadowCopy

open System
open System.IO
open SageFs.ProjectLoading

/// Shadow directories live in the shared temp root and every worker process
/// creates its own, so a directory's name records the process that owns it:
/// `sagefs-shadow-<pid>-<guid8>`. A sweep may only ever remove directories
/// whose owner is provably gone.
[<RequireQualifiedAccess>]
type ShadowDirOwner =
  | OwnedBy of pid: int
  /// A name without an owner (created by an older SageFs) — its owner can
  /// never be proven dead, so it is never swept.
  | UnknownOwner

/// Whether a process is still running, as far as can be told. A process the
/// runtime cannot find is Gone; any other failure to look is Unknown.
[<RequireQualifiedAccess>]
type OwnerLiveness =
  | Running
  | Gone
  | Unknown

module ShadowDirOwner =
  let private prefix = "sagefs-shadow-"

  let ofDirName (dirName: string) : ShadowDirOwner =
    let name = Path.GetFileName(dirName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
    match name.StartsWith(prefix, StringComparison.Ordinal) with
    | false -> ShadowDirOwner.UnknownOwner
    | true ->
      match name.Substring(prefix.Length).Split('-') with
      | [| pidText; guid |] when guid.Length > 0 ->
        match Int32.TryParse pidText with
        | true, pid when pid > 0 -> ShadowDirOwner.OwnedBy pid
        | _ -> ShadowDirOwner.UnknownOwner
      | _ -> ShadowDirOwner.UnknownOwner

/// The shadow directories a sweep may delete: exactly those whose owning
/// process is provably Gone. A live owner, an owner whose liveness cannot be
/// determined, and a directory without a recorded owner are all kept — a sweep
/// must fail closed, because deleting a live worker's shadow copy breaks every
/// compile in that session ("Could not find a part of the path …/SageFs.dll").
let staleShadowDirs (liveness: int -> OwnerLiveness) (dirs: string list) : string list =
  dirs
  |> List.filter (fun dir ->
    match ShadowDirOwner.ofDirName dir with
    | ShadowDirOwner.OwnedBy pid ->
      match liveness pid with
      | OwnerLiveness.Gone -> true
      | OwnerLiveness.Running
      | OwnerLiveness.Unknown -> false
    | ShadowDirOwner.UnknownOwner -> false)

/// Liveness of a process id on this machine.
let processLiveness (pid: int) : OwnerLiveness =
  try
    use p = Diagnostics.Process.GetProcessById pid
    match p.HasExited with
    | true -> OwnerLiveness.Gone
    | false -> OwnerLiveness.Running
  with
  | :? ArgumentException -> OwnerLiveness.Gone
  | _ -> OwnerLiveness.Unknown

/// Creates a unique temporary directory for shadow-copied assemblies, named
/// for this process so other processes' sweeps never remove it while it runs.
let createShadowDir () : string =
  let dir =
    Path.Combine(
      Path.GetTempPath(),
      sprintf "sagefs-shadow-%d-%s" Environment.ProcessId (Guid.NewGuid().ToString("N").[..7]))
  Directory.CreateDirectory dir |> ignore
  dir

/// Copies a DLL (and companion .pdb) to the shadow directory.
/// Returns the new path if copied, or the original path if source doesn't exist.
let shadowCopyFile (shadowDir: string) (sourcePath: string) : string =
  match File.Exists sourcePath with
  | false -> sourcePath
  | true ->
    let fileName = Path.GetFileName sourcePath
    let destPath = Path.Combine(shadowDir, fileName)
    File.Copy(sourcePath, destPath, true)
    let pdbSource = Path.ChangeExtension(sourcePath, ".pdb")
    match File.Exists pdbSource with
    | true ->
      let pdbDest = Path.ChangeExtension(destPath, ".pdb")
      File.Copy(pdbSource, pdbDest, true)
    | false -> ()
    destPath

/// Rewrites a Solution's project TargetPaths to point at shadow copies.
/// References (dependency DLLs) are intentionally left in place — see below.
let shadowCopySolution (shadowDir: string) (sln: Solution) : Solution =
  let shadowProjects =
    sln.Projects
    |> List.map (fun po ->
      { po with TargetPath = shadowCopyFile shadowDir po.TargetPath })
  // Only the project's OWN assemblies get shadow-copied (for IL coverage
  // instrumentation). Dependency/reference DLLs (NuGet + framework) must stay
  // in place: shadowing them breaks FSI #load resolution — FSI needs to
  // resolve transitive dependencies from their original locations, and the
  // shadow dir only contains the top-level DLLs.
  { sln with
      Projects = shadowProjects }

/// Tries to remove the shadow directory and all its contents.
/// If deletion fails (e.g., DLLs still loaded by CLR), schedules for cleanup on exit.
/// Safe if dir doesn't exist.
let pendingCleanups = System.Collections.Concurrent.ConcurrentBag<string>()

let cleanupShadowDir (shadowDir: string) : unit =
  match Directory.Exists shadowDir with
  | true ->
    try
      Directory.Delete(shadowDir, true)
    with
    | :? UnauthorizedAccessException
    | :? IO.IOException ->
      pendingCleanups.Add(shadowDir)
  | false -> ()

let cleanupAllPending () : unit =
  for dir in pendingCleanups do
    try
      match Directory.Exists dir with
      | true -> Directory.Delete(dir, true)
      | false -> ()
    with _ -> ()

/// Removes the sagefs-shadow-* directories under `tempDir` whose owning
/// process is gone (see staleShadowDirs). Best-effort: locked dirs are skipped.
let cleanupStaleDirsIn (tempDir: string) (liveness: int -> OwnerLiveness) : unit =
  try
    Directory.GetDirectories(tempDir, "sagefs-shadow-*")
    |> Array.toList
    |> staleShadowDirs liveness
    |> List.iter (fun dir ->
      try Directory.Delete(dir, true)
      with _ -> ())
  with _ -> ()

/// Sweeps shadow directories left behind by worker processes that have
/// exited. Every other process's live shadow copy is left alone — a sweep that
/// kept only "the most recent" directory deleted the shadow copies of every
/// other running session (and every concurrently running test daemon).
let cleanupStaleDirs () : unit =
  cleanupStaleDirsIn (Path.GetTempPath()) processLiveness

