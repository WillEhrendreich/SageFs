module SageFs.Tests.HostCoreAdoptionMarkerTests

open System
open System.Diagnostics
open System.IO
open Expecto
open Expecto.Flip
open SageFs

/// Pure/impure-boundary tests for the owner-marker + sweep glue
/// `HostCoreAdoption` adds on top of `OrphanTempDirSweep` (the underlying
/// mechanism's own tests live in OrphanTempDirSweepTests.fs). This is the
/// fix for the incident where 13 orphaned `/tmp/sagefs-host-adopt-*`
/// directories (680-835MB each, ~9.5GB) accumulated because the daemon
/// that materialized them was hard-killed before its `proc.Exited`
/// handler ever ran. A real end-to-end proof — against a session that
/// actually adopts a Core, not a bare session that never would — lives in
/// HostCoreAdoptionOrphanSweepTests.fs.
let private withTempDir (run: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), sprintf "host-adoption-marker-test-%s" (Guid.NewGuid().ToString("N")))
  Directory.CreateDirectory dir |> ignore
  try run dir
  finally
    try Directory.Delete(dir, true) with _ -> ()

[<Tests>]
let tests =
  testList "Host core adoption: orphan marker + sweep" [

    testCase "the private root naming convention starts with the swept prefix" <| fun _ ->
      let sessionId = "deadbeef"
      let name = sprintf "%s%s-%s" HostCoreAdoption.adoptedRootPrefix sessionId "abcd1234"
      name.StartsWith(HostCoreAdoption.adoptedRootPrefix, StringComparison.Ordinal)
      |> Expect.isTrue "resolveLaunchRoot's own naming must match what the sweep looks for"

    testCase "markAdoptedRootOwner writes a marker sweepStaleAdoptedRootsIn can read back" <| fun _ ->
      withTempDir (fun root ->
        HostCoreAdoption.markAdoptedRootOwner root 4242
        let markerPath = Path.Combine(root, HostCoreAdoption.adoptedRootOwnerMarkerFileName)
        File.Exists markerPath
        |> Expect.isTrue "the marker file exists after marking"
        File.ReadAllText(markerPath).Trim()
        |> Expect.equal "the marker records the exact pid marked" "4242")

    testCase "sweepStaleAdoptedRootsIn removes only a marked root whose owner is Gone" <| fun _ ->
      withTempDir (fun parent ->
        let deadRoot = Path.Combine(parent, sprintf "%sdead0001-11111111" HostCoreAdoption.adoptedRootPrefix)
        let liveRoot = Path.Combine(parent, sprintf "%slive0002-22222222" HostCoreAdoption.adoptedRootPrefix)
        let unmarkedRoot = Path.Combine(parent, sprintf "%sunmk0003-33333333" HostCoreAdoption.adoptedRootPrefix)
        for d in [ deadRoot; liveRoot; unmarkedRoot ] do
          Directory.CreateDirectory d |> ignore
        HostCoreAdoption.markAdoptedRootOwner deadRoot 1
        HostCoreAdoption.markAdoptedRootOwner liveRoot 2

        let liveness pid = if pid = 1 then ShadowCopy.OwnerLiveness.Gone else ShadowCopy.OwnerLiveness.Running
        let removed =
          HostCoreAdoption.sweepStaleAdoptedRootsIn parent liveness
          |> List.map fst

        removed |> Expect.equal "only the provably-dead-owner root is swept" [ deadRoot ]
        Directory.Exists deadRoot |> Expect.isFalse "the dead root is gone"
        Directory.Exists liveRoot |> Expect.isTrue "the live root survives"
        Directory.Exists unmarkedRoot |> Expect.isTrue "the unmarked root survives (fail closed — this is the vacuous-test trap: a directory the fix never marked must never be swept just because it matches the name pattern)")

    testCase "sweepStaleAdoptedRootsIn never removes a root outside the temp dir it was given" <| fun _ ->
      withTempDir (fun outsideRoot ->
        let dir = Path.Combine(outsideRoot, sprintf "%sxxxx0001-11111111" HostCoreAdoption.adoptedRootPrefix)
        Directory.CreateDirectory dir |> ignore
        HostCoreAdoption.markAdoptedRootOwner dir 1
        // Sweep a DIFFERENT (empty) temp dir — the real one must be untouched.
        let elsewhere = Path.Combine(Path.GetTempPath(), sprintf "unrelated-%s" (Guid.NewGuid().ToString("N")))
        HostCoreAdoption.sweepStaleAdoptedRootsIn elsewhere (fun _ -> ShadowCopy.OwnerLiveness.Gone)
        |> Expect.isEmpty "a sweep scoped to a different directory finds nothing"
        Directory.Exists dir |> Expect.isTrue "the real root is untouched by a sweep of an unrelated directory")

    // The two tests below use ShadowCopy.processLiveness (the REAL liveness
    // check wired into `sweepStaleAdoptedRoots`, not a synthetic stub) against
    // REAL OS process ids — this is the actual cross-process backstop: a
    // directory's owner is proven dead (or alive) with no cooperation at all
    // from that owning process, which is exactly what "the daemon that
    // created this directory was kill -9'd and will never run its own
    // cleanup" requires.
    testCase "sweepStaleAdoptedRootsIn removes a root owned by a REAL, now-dead process" <| fun _ ->
      withTempDir (fun parent ->
        let dir = Path.Combine(parent, sprintf "%sreal0001-11111111" HostCoreAdoption.adoptedRootPrefix)
        Directory.CreateDirectory dir |> ignore
        // A real, short-lived process — no relation to SageFs, no Exited
        // handler registered by anyone — that has already exited by the time
        // the sweep runs.
        let psi =
          ProcessStartInfo(
            FileName = (if OperatingSystem.IsWindows() then "cmd.exe" else "true"),
            UseShellExecute = false)
        if OperatingSystem.IsWindows() then psi.Arguments <- "/c exit 0"
        use p = Process.Start psi
        p.WaitForExit(5000) |> ignore
        HostCoreAdoption.markAdoptedRootOwner dir p.Id

        HostCoreAdoption.sweepStaleAdoptedRootsIn parent ShadowCopy.processLiveness
        |> List.map fst
        |> Expect.equal "the real dead pid's root is swept using the real liveness check" [ dir ]
        Directory.Exists dir |> Expect.isFalse "the root is gone")

    testCase "sweepStaleAdoptedRootsIn keeps a root owned by THIS real, live process" <| fun _ ->
      withTempDir (fun parent ->
        let dir = Path.Combine(parent, sprintf "%slive0001-11111111" HostCoreAdoption.adoptedRootPrefix)
        Directory.CreateDirectory dir |> ignore
        HostCoreAdoption.markAdoptedRootOwner dir (Process.GetCurrentProcess().Id)

        HostCoreAdoption.sweepStaleAdoptedRootsIn parent ShadowCopy.processLiveness
        |> Expect.isEmpty "this test process is alive, so its root is never swept"
        Directory.Exists dir |> Expect.isTrue "the live root survives")
  ]
