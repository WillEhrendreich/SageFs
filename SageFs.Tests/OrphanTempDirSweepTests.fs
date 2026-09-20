module SageFs.Tests.OrphanTempDirSweepTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

/// The general mechanism behind reclaiming `sagefs-host-adopt-*` private
/// launch roots and `sagefs-test/<guid>` isolated data dirs after a hard
/// process death (kill -9, OOM, a force-exit sweep) skips the in-process
/// `proc.Exited`/`finally` cleanup entirely. See OrphanTempDirSweep.fs for
/// the design rationale.
///
/// Fail-closed is the property under test throughout: a directory is only
/// ever a sweep candidate when its recorded owner pid is PROVABLY `Gone` —
/// no marker, a live owner, and an unknowable owner must all be left alone.
let private withTempParent (run: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), sprintf "orphan-sweep-test-%s" (Guid.NewGuid().ToString("N")))
  Directory.CreateDirectory dir |> ignore
  try run dir
  finally
    try Directory.Delete(dir, true) with _ -> ()

let private makeCandidate (parent: string) (name: string) : string =
  let dir = Path.Combine(parent, name)
  Directory.CreateDirectory dir |> ignore
  File.WriteAllBytes(Path.Combine(dir, "payload.bin"), Array.zeroCreate<byte> 100)
  dir

[<Tests>]
let tests =
  testList "OrphanTempDirSweep" [

    testCase "readOwnerPid is None when no marker file exists" <| fun _ ->
      withTempParent (fun parent ->
        let dir = makeCandidate parent "no-marker"
        OrphanTempDirSweep.readOwnerPid "owner.pid" dir
        |> Expect.isNone "no marker written yet")

    testCase "writeOwnerPid then readOwnerPid round-trips the pid" <| fun _ ->
      withTempParent (fun parent ->
        let dir = makeCandidate parent "with-marker"
        OrphanTempDirSweep.writeOwnerPid "owner.pid" dir 4242
        OrphanTempDirSweep.readOwnerPid "owner.pid" dir
        |> Expect.equal "the pid written is the pid read back" (Some 4242))

    testCase "readOwnerPid is None for a garbled marker" <| fun _ ->
      withTempParent (fun parent ->
        let dir = makeCandidate parent "garbled-marker"
        File.WriteAllText(Path.Combine(dir, "owner.pid"), "not-a-pid")
        OrphanTempDirSweep.readOwnerPid "owner.pid" dir
        |> Expect.isNone "an unparsable marker is never a valid owner")

    testCase "readOwnerPid is None for a non-positive pid" <| fun _ ->
      withTempParent (fun parent ->
        let dir = makeCandidate parent "zero-pid"
        File.WriteAllText(Path.Combine(dir, "owner.pid"), "0")
        OrphanTempDirSweep.readOwnerPid "owner.pid" dir
        |> Expect.isNone "pid 0 (or negative) is never a valid owner")

    testCase "stale keeps a directory with no marker" <| fun _ ->
      withTempParent (fun parent ->
        let dir = makeCandidate parent "no-marker"
        OrphanTempDirSweep.stale
          (OrphanTempDirSweep.readOwnerPid "owner.pid")
          (fun _ -> ShadowCopy.OwnerLiveness.Gone)
          [ dir ]
        |> Expect.isEmpty "a directory whose owner cannot even be identified is never provably orphaned")

    testCase "stale keeps a directory whose owner is still Running" <| fun _ ->
      withTempParent (fun parent ->
        let dir = makeCandidate parent "live-owner"
        OrphanTempDirSweep.writeOwnerPid "owner.pid" dir 1
        OrphanTempDirSweep.stale
          (OrphanTempDirSweep.readOwnerPid "owner.pid")
          (fun _ -> ShadowCopy.OwnerLiveness.Running)
          [ dir ]
        |> Expect.isEmpty "a live owner's directory is never a sweep candidate")

    testCase "stale keeps a directory whose owner liveness is Unknown" <| fun _ ->
      withTempParent (fun parent ->
        let dir = makeCandidate parent "unknown-owner"
        OrphanTempDirSweep.writeOwnerPid "owner.pid" dir 1
        OrphanTempDirSweep.stale
          (OrphanTempDirSweep.readOwnerPid "owner.pid")
          (fun _ -> ShadowCopy.OwnerLiveness.Unknown)
          [ dir ]
        |> Expect.isEmpty "fail closed: an owner we cannot prove dead is never swept, exactly like a live one")

    testCase "stale includes a directory whose owner is provably Gone" <| fun _ ->
      withTempParent (fun parent ->
        let dir = makeCandidate parent "dead-owner"
        OrphanTempDirSweep.writeOwnerPid "owner.pid" dir 1
        OrphanTempDirSweep.stale
          (OrphanTempDirSweep.readOwnerPid "owner.pid")
          (fun _ -> ShadowCopy.OwnerLiveness.Gone)
          [ dir ]
        |> Expect.equal "a provably dead owner's directory is the only kind of sweep candidate" [ dir ])

    testCase "sweep deletes only the provably orphaned directory among several" <| fun _ ->
      withTempParent (fun parent ->
        let deadOwner = makeCandidate parent "sagefs-host-adopt-aaaa0001-11111111"
        let liveOwner = makeCandidate parent "sagefs-host-adopt-bbbb0002-22222222"
        let noMarker = makeCandidate parent "sagefs-host-adopt-cccc0003-33333333"
        OrphanTempDirSweep.writeOwnerPid "owner.pid" deadOwner 1
        OrphanTempDirSweep.writeOwnerPid "owner.pid" liveOwner 2

        let liveness pid = if pid = 1 then ShadowCopy.OwnerLiveness.Gone else ShadowCopy.OwnerLiveness.Running
        let removed =
          OrphanTempDirSweep.sweep parent "sagefs-host-adopt-*" "owner.pid" liveness
          |> List.map fst

        removed |> Expect.equal "only the dead-owner directory is removed" [ deadOwner ]
        Directory.Exists deadOwner |> Expect.isFalse "the dead owner's directory is gone"
        Directory.Exists liveOwner |> Expect.isTrue "the live owner's directory survives the sweep"
        Directory.Exists noMarker |> Expect.isTrue "the markerless directory survives the sweep (fail closed)")

    testCase "sweep reports reclaimed bytes for a removed directory" <| fun _ ->
      withTempParent (fun parent ->
        let dir = makeCandidate parent "dead-owner"
        File.WriteAllBytes(Path.Combine(dir, "extra.bin"), Array.zeroCreate<byte> 900)
        OrphanTempDirSweep.writeOwnerPid "owner.pid" dir 1

        match OrphanTempDirSweep.sweep parent "dead-owner*" "owner.pid" (fun _ -> ShadowCopy.OwnerLiveness.Gone) with
        | [ (removedPath, bytes) ] ->
          removedPath |> Expect.equal "the removed path is the candidate directory" dir
          (bytes, 900L) |> Expect.isGreaterThanOrEqual "reclaimed bytes covers at least the payload + marker"
        | other -> failtestf "expected exactly one removed entry, got %A" other)

    testCase "sweep tolerates a parent directory that does not exist" <| fun _ ->
      let missingParent = Path.Combine(Path.GetTempPath(), sprintf "orphan-sweep-missing-%s" (Guid.NewGuid().ToString("N")))
      OrphanTempDirSweep.sweep missingParent "*" "owner.pid" (fun _ -> ShadowCopy.OwnerLiveness.Gone)
      |> Expect.isEmpty "a parent that was never created sweeps to nothing rather than throwing"

    testCase "sweep never touches a directory that does not match namePattern" <| fun _ ->
      withTempParent (fun parent ->
        let unrelated = makeCandidate parent "some-other-dir"
        OrphanTempDirSweep.writeOwnerPid "owner.pid" unrelated 1
        OrphanTempDirSweep.sweep parent "sagefs-host-adopt-*" "owner.pid" (fun _ -> ShadowCopy.OwnerLiveness.Gone)
        |> Expect.isEmpty "an unrelated directory name is never a candidate, dead owner or not"
        Directory.Exists unrelated |> Expect.isTrue "unrelated directory survives untouched")

    testCase "directorySizeBytes sums file sizes recursively" <| fun _ ->
      withTempParent (fun parent ->
        let dir = Path.Combine(parent, "sized")
        Directory.CreateDirectory dir |> ignore
        File.WriteAllBytes(Path.Combine(dir, "a.bin"), Array.zeroCreate<byte> 10)
        let nested = Path.Combine(dir, "nested")
        Directory.CreateDirectory nested |> ignore
        File.WriteAllBytes(Path.Combine(nested, "b.bin"), Array.zeroCreate<byte> 20)

        OrphanTempDirSweep.directorySizeBytes dir
        |> Expect.equal "10 + 20 bytes across the tree" 30L)

    testCase "directorySizeBytes is 0 for a directory that does not exist" <| fun _ ->
      OrphanTempDirSweep.directorySizeBytes (Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")))
      |> Expect.equal "an unreadable/missing directory contributes 0, never throws" 0L
  ]
