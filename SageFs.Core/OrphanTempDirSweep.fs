module SageFs.OrphanTempDirSweep

open System
open System.IO

/// The general mechanism behind every "materializes a private directory
/// under the OS temp root, per session/process, with no shared owner" leak
/// in SageFs: `sagefs-host-adopt-*` private launch roots (680-835MB each —
/// see HostCoreAdoption) and isolated `sagefs-test/<guid>` daemon data dirs
/// (SAGEFS_DATA_DIR). Both are created by one process for another process's
/// (or its own future) exclusive use, and both are only ever cleaned up by
/// an in-process `finally`/`Exited` handler — which never runs if that
/// process is killed (`kill -9`, OOM, a force-exit sweep) before it gets a
/// chance. The fix is the same shape as `ShadowCopy.staleShadowDirs`,
/// generalized: a directory records its owning process's pid in a marker
/// file as soon as that owner is known, so a LATER process — not
/// necessarily the one that created it, and not necessarily even a restart
/// of the same daemon — can prove the owner is gone before deleting
/// anything it did not create itself.
///
/// Fail-closed throughout: a directory with no marker (predates this fix,
/// or its owner hasn't finished registering yet), an unparsable marker, or
/// an owner whose liveness cannot be determined is NEVER a sweep candidate.
/// Deleting a live process's directory out from under it is far worse than
/// leaking one — this is a "prove orphaned", never a "prove still needed",
/// check.

/// Record `pid` as the owner of `dir` by writing `markerFileName` inside
/// it. Best-effort: a write failure just means this directory can never be
/// proven orphaned later (the fail-closed direction), not that the
/// caller's own work fails.
let writeOwnerPid (markerFileName: string) (dir: string) (pid: int) : unit =
  try File.WriteAllText(Path.Combine(dir, markerFileName), string pid)
  with _ -> ()

/// The pid recorded for `dir` via `markerFileName`, if the marker exists
/// and parses to a positive pid. `None` on any other outcome (missing,
/// unreadable, garbled) — never throws.
let readOwnerPid (markerFileName: string) (dir: string) : int option =
  let path = Path.Combine(dir, markerFileName)
  try
    match File.Exists path with
    | false -> None
    | true ->
      match Int32.TryParse((File.ReadAllText path).Trim()) with
      | true, pid when pid > 0 -> Some pid
      | _ -> None
  with _ -> None

/// Pure: which of `dirs` are provably orphaned — they have a recorded
/// owner pid AND that owner's liveness is `Gone`. A directory with no
/// marker, a live owner, or an owner whose liveness cannot be determined
/// is always kept.
let stale
  (readMarker: string -> int option)
  (liveness: int -> ShadowCopy.OwnerLiveness)
  (dirs: string list)
  : string list =
  dirs
  |> List.filter (fun dir ->
    match readMarker dir with
    | None -> false
    | Some pid ->
      match liveness pid with
      | ShadowCopy.OwnerLiveness.Gone -> true
      | ShadowCopy.OwnerLiveness.Running
      | ShadowCopy.OwnerLiveness.Unknown -> false)

/// Best-effort size of a directory tree, for reclaimed-space reporting.
/// Never throws: an unreadable file or subdirectory contributes 0 rather
/// than failing the whole measurement.
let directorySizeBytes (dir: string) : int64 =
  try
    Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
    |> Seq.sumBy (fun f -> try FileInfo(f).Length with _ -> 0L)
  with _ -> 0L

/// Deletes every directory matching `namePattern` directly under
/// `parentDir` whose owner (recorded via `markerFileName`) is provably
/// gone. Best-effort per directory: a locked or partially-deleted
/// directory is skipped, never thrown. A missing `parentDir` (nothing has
/// ever leaked there) sweeps to nothing rather than erroring. Returns
/// `(path, reclaimedBytes)` for every directory actually removed, for
/// startup/periodic-log visibility.
let sweep
  (parentDir: string)
  (namePattern: string)
  (markerFileName: string)
  (liveness: int -> ShadowCopy.OwnerLiveness)
  : (string * int64) list =
  try
    match Directory.Exists parentDir with
    | false -> []
    | true ->
      Directory.GetDirectories(parentDir, namePattern)
      |> Array.toList
      |> stale (readOwnerPid markerFileName) liveness
      |> List.choose (fun dir ->
        let size = directorySizeBytes dir
        try
          Directory.Delete(dir, true)
          Some(dir, size)
        with _ -> None)
  with _ -> []
