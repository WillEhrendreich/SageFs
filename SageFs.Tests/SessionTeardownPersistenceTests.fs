module SageFs.Tests.SessionTeardownPersistenceTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.DaemonManifest

/// Tests for the two-tier session teardown persistence:
///   Stop — unload (manifest entry stamped stopped, kept for resume picker)
///   Purge — stop + remove the .sagefm manifest entry entirely
///
/// The per-session .sagefs replay binary no longer exists (event-sourcing
/// story), so teardown is manifest-only. The daemon-mode wiring is exercised
/// end-to-end by the dashboard tests.

let private withTempDirTask (f: string -> Task<unit>) : Task<unit> =
  task {
    let dir =
      Path.Combine(Path.GetTempPath(), "sagefs-teardown-" + Guid.NewGuid().ToString("N").[..7])
    Directory.CreateDirectory(dir) |> ignore
    try
      do! f dir
    finally
      try Directory.Delete(dir, true) with _ -> ()
  }

let private sampleManifest (ids: string list) : DaemonManifestState =
  let sessions =
    ids
    |> List.map (fun id ->
      id,
      { DaemonSessionRecord.SessionId = id
        Projects = [ "App.fsproj" ]
        WorkingDir = "C:\\Code"
        CreatedAt = DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero)
        StoppedAt = None })
    |> Map.ofList
  { Sessions = sessions
    ActiveSessionId = ids |> List.tryHead }

let private silentLogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

/// Purge through the manifest owner — the only writer of daemon.sagefm.
let private purge (dir: string) (sessionId: string) : Task<ManifestOwner.CommitResult> =
  task {
    use owner = ManifestOwner.start silentLogger dir
    return! owner.Commit (ManifestMutation.Remove sessionId)
  }

[<Tests>]
let purgeManifestEntryTests = testList "ManifestOwner purge" [

  testTask "removes only the target entry, preserving others" {
    do! withTempDirTask (fun dir -> task {
      DaemonPersistence.saveManifest dir (sampleManifest [ "s1"; "s2"; "s3" ]) |> ignore
      let! removed = purge dir "s2"
      removed |> Result.isOk |> Expect.isTrue "remove should succeed"
      match DaemonPersistence.loadManifest dir with
      | Ok loaded ->
        loaded.Sessions |> Map.containsKey "s2" |> Expect.isFalse "s2 should be removed"
        loaded.Sessions |> Map.containsKey "s1" |> Expect.isTrue "s1 should remain"
        loaded.Sessions |> Map.containsKey "s3" |> Expect.isTrue "s3 should remain"
      | Error e -> failtestf "load failed: %A" e
    })
  }

  testTask "removes the active session id when the active entry is purged" {
    do! withTempDirTask (fun dir -> task {
      DaemonPersistence.saveManifest dir (sampleManifest [ "s1"; "s2" ]) |> ignore
      let! removed = purge dir "s1"
      removed |> Result.isOk |> Expect.isTrue "remove should succeed"
      match DaemonPersistence.loadManifest dir with
      | Ok loaded ->
        loaded.ActiveSessionId |> Expect.equal "active should move off purged session" (Some "s2")
      | Error e -> failtestf "load failed: %A" e
    })
  }

  testTask "missing manifest is Ok (idempotent)" {
    do! withTempDirTask (fun dir -> task {
      let! removed = purge dir "sess_ghost"
      removed |> Result.isOk |> Expect.isTrue "no manifest should be Ok"
    })
  }

  testTask "missing entry in existing manifest is Ok" {
    do! withTempDirTask (fun dir -> task {
      DaemonPersistence.saveManifest dir (sampleManifest [ "s1" ]) |> ignore
      let! removed = purge dir "sess_ghost"
      removed |> Result.isOk |> Expect.isTrue "missing entry should be Ok"
    })
  }
]
