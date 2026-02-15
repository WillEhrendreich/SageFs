module SageFs.ShadowCopy

open System
open System.IO
open SageFs.ProjectLoading

/// Creates a unique temporary directory for shadow-copied assemblies.
let createShadowDir () : string =
  let dir =
    Path.Combine(
      Path.GetTempPath(),
      sprintf "sagefs-shadow-%s" (Guid.NewGuid().ToString("N").[..7]))
  Directory.CreateDirectory dir |> ignore
  dir

/// Copies a DLL (and companion .pdb) to the shadow directory.
/// Returns the new path if copied, or the original path if source doesn't exist.
let shadowCopyFile (shadowDir: string) (sourcePath: string) : string =
  if not (File.Exists sourcePath) then
    sourcePath
  else
    let fileName = Path.GetFileName sourcePath
    let destPath = Path.Combine(shadowDir, fileName)
    File.Copy(sourcePath, destPath, true)
    let pdbSource = Path.ChangeExtension(sourcePath, ".pdb")
    if File.Exists pdbSource then
      let pdbDest = Path.ChangeExtension(destPath, ".pdb")
      File.Copy(pdbSource, pdbDest, true)
    destPath

/// Rewrites a Solution's assembly references to point at shadow copies.
/// Project TargetPaths and explicit References are shadow-copied.
let shadowCopySolution (shadowDir: string) (sln: Solution) : Solution =
  let shadowProjects =
    sln.Projects
    |> List.map (fun po ->
      { po with TargetPath = shadowCopyFile shadowDir po.TargetPath })
  let shadowRefs =
    sln.References
    |> List.map (shadowCopyFile shadowDir)
  { sln with
      Projects = shadowProjects
      References = shadowRefs }

/// Tries to remove the shadow directory and all its contents.
/// If deletion fails (e.g., DLLs still loaded by CLR), schedules for cleanup on exit.
/// Safe if dir doesn't exist.
let private pendingCleanups = System.Collections.Concurrent.ConcurrentBag<string>()

let cleanupShadowDir (shadowDir: string) : unit =
  if Directory.Exists shadowDir then
    try
      Directory.Delete(shadowDir, true)
    with
    | :? UnauthorizedAccessException
    | :? IO.IOException ->
      pendingCleanups.Add(shadowDir)

let cleanupAllPending () : unit =
  for dir in pendingCleanups do
    try
      if Directory.Exists dir then
        Directory.Delete(dir, true)
    with _ -> ()

/// Removes all sagefs-shadow-* directories except the most recent one.
/// Best-effort: locked dirs are silently skipped.
let cleanupStaleDirs () : unit =
  try
    let tempDir = Path.GetTempPath()
    let shadowDirs =
      Directory.GetDirectories(tempDir, "sagefs-shadow-*")
      |> Array.sortByDescending (fun d ->
        try Directory.GetLastWriteTimeUtc d
        with _ -> DateTime.MinValue)
    // Keep the most recent, clean the rest
    if shadowDirs.Length > 1 then
      for dir in shadowDirs.[1..] do
        try Directory.Delete(dir, true)
        with _ -> ()
  with _ -> ()

