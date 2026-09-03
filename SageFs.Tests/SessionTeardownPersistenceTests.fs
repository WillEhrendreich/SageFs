module SageFs.Tests.SessionTeardownPersistenceTests

open System
open System.IO
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

let private withTempDir (f: string -> unit) =
  let dir =
    Path.Combine(Path.GetTempPath(), "sagefs-teardown-" + Guid.NewGuid().ToString("N").[..7])
  Directory.CreateDirectory(dir) |> ignore
  try
    f dir
  finally
    try Directory.Delete(dir, true) with _ -> ()

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

[<Tests>]
let removeManifestEntryTests = testList "DaemonPersistence.removeManifestEntry" [

  testCase "removes only the target entry, preserving others" <| fun _ ->
    withTempDir (fun dir ->
      let state = sampleManifest [ "s1"; "s2"; "s3" ]
      DaemonPersistence.saveManifest dir state |> ignore
      DaemonPersistence.removeManifestEntry dir "s2" |> Result.isOk |> Expect.isTrue "remove should succeed"
      match DaemonPersistence.loadManifest dir with
      | Ok loaded ->
        loaded.Sessions |> Map.containsKey "s2" |> Expect.isFalse "s2 should be removed"
        loaded.Sessions |> Map.containsKey "s1" |> Expect.isTrue "s1 should remain"
        loaded.Sessions |> Map.containsKey "s3" |> Expect.isTrue "s3 should remain"
      | Error e -> failwithf "load failed: %A" e
    )

  testCase "removes the active session id when the active entry is purged" <| fun _ ->
    withTempDir (fun dir ->
      let state = sampleManifest [ "s1"; "s2" ]
      DaemonPersistence.saveManifest dir state |> ignore
      DaemonPersistence.removeManifestEntry dir "s1" |> Result.isOk |> Expect.isTrue "remove should succeed"
      match DaemonPersistence.loadManifest dir with
      | Ok loaded ->
        loaded.ActiveSessionId |> Expect.equal "active should move off purged session" (Some "s2")
      | Error e -> failwithf "load failed: %A" e
    )

  testCase "missing manifest is Ok (idempotent)" <| fun _ ->
    withTempDir (fun dir ->
      DaemonPersistence.removeManifestEntry dir "sess_ghost" |> Result.isOk |> Expect.isTrue "no manifest should be Ok"
    )

  testCase "missing entry in existing manifest is Ok" <| fun _ ->
    withTempDir (fun dir ->
      let state = sampleManifest [ "s1" ]
      DaemonPersistence.saveManifest dir state |> ignore
      DaemonPersistence.removeManifestEntry dir "sess_ghost" |> Result.isOk |> Expect.isTrue "missing entry should be Ok"
    )
]
