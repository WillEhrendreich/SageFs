module SageFs.Tests.DaemonPruneTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.ManifestTypes
open SageFs.Features.DaemonManifest
open Microsoft.Extensions.Logging.Abstractions

// `DaemonMode.handlePrune` is the one-shot `--prune` path and
// `DaemonMode.mergeManifestWithExisting` is what the periodic save and the graceful shutdown
// write through. Both are driven here with a throwaway directory and an injected daemon probe,
// so nothing touches the developer's real ~/.SageFs.

let nullLog = NullLogger.Instance

/// A probe that finds no daemon.
let noDaemon () : Task<DaemonInfo option> = Task.FromResult(None)

/// The pid the fake running daemon reports. Nothing reads it back except the refusal message.
let runningDaemonPid = 12345

/// A probe that finds a daemon with the given pid.
let runningDaemon (pid: int) () : Task<DaemonInfo option> =
  Task.FromResult(
    Some
      { Pid = pid
        Port = SageFsConfig.DefaultMcpPort
        DashboardPort = SageFsConfig.DefaultDashboardPort
        StartedAt = DateTime.UtcNow
        WorkingDirectory = "/"
        Version = "test"
        ApiVersion = None
        SessionCount = None
        ComponentFailures = [] })

let pruneFlags = { Args.DaemonFlags.defaults with Prune = true }
let noPruneFlags = Args.DaemonFlags.defaults

/// A directory name that does not exist yet.
let freshDir () =
  IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-daemon-prune-%s" (Guid.NewGuid().ToString("N")))

/// Removes `dir` if the code under test, or the test, created it.
let removeIfPresent (dir: string) =
  if IO.Directory.Exists dir then IO.Directory.Delete(dir, true)

/// Runs `body` with a directory that exists, and removes it afterwards.
let withDir (body: string -> unit) =
  let dir = freshDir ()
  IO.Directory.CreateDirectory(dir) |> ignore
  try body dir
  finally removeIfPresent dir

/// Runs `body` with a directory that does not exist, and removes whatever the code under test made there.
let withMissingDir (body: string -> unit) =
  let dir = freshDir ()
  try body dir
  finally removeIfPresent dir

/// `withDir` for a body that awaits.
let withDirTask (body: string -> Task<unit>) : Task<unit> =
  task {
    let dir = freshDir ()
    IO.Directory.CreateDirectory(dir) |> ignore
    try do! body dir
    finally removeIfPresent dir
  }

/// `withMissingDir` for a body that awaits.
let withMissingDirTask (body: string -> Task<unit>) : Task<unit> =
  task {
    let dir = freshDir ()
    try do! body dir
    finally removeIfPresent dir
  }

let manifestFile (dir: string) = IO.Path.Combine(dir, "daemon.sagefm")

/// Bytes that are not a manifest.
let corruptBytes = [| 0xFFuy; 0xFEuy; 0xFDuy |]

[<Tests>]
let handlePruneTests = testList "DaemonMode.handlePrune" [

  testTask "a corrupt manifest returns Error that says so, not Ok" {
    do!
      withDirTask (fun dir -> task {
        IO.File.WriteAllBytes(manifestFile dir, corruptBytes)
        let! result = SageFs.Server.DaemonMode.handlePrune dir nullLog noDaemon pruneFlags
        match result with
        | Result.Error msg -> msg |> Expect.stringContains "the error mentions corruption" "corrupt"
        | Result.Ok _ -> failtest "handlePrune with a corrupt manifest should return Error, not Ok"
      })
  }

  testTask "no manifest returns Ok true: there is nothing to prune, and that is not an error" {
    do!
      withMissingDirTask (fun dir -> task {
        let! result = SageFs.Server.DaemonMode.handlePrune dir nullLog noDaemon pruneFlags
        result |> Expect.equal "a missing manifest is Ok true" (Result.Ok true)
      })
  }

  testTask "Prune=false returns Ok false: it was not requested" {
    do!
      withMissingDirTask (fun dir -> task {
        let! result = SageFs.Server.DaemonMode.handlePrune dir nullLog noDaemon noPruneFlags
        result |> Expect.equal "Prune=false is Ok false" (Result.Ok false)
      })
  }

  testTask "Prune=false does not consult the daemon probe" {
    do!
      withMissingDirTask (fun dir -> task {
        let! result = SageFs.Server.DaemonMode.handlePrune dir nullLog (runningDaemon runningDaemonPid) noPruneFlags
        result |> Expect.equal "Prune=false is Ok false whatever the daemon state" (Result.Ok false)
      })
  }

  testTask "a running daemon returns Error naming its pid, so a live daemon is never pruned underneath" {
    do!
      withMissingDirTask (fun dir -> task {
        let! result = SageFs.Server.DaemonMode.handlePrune dir nullLog (runningDaemon runningDaemonPid) pruneFlags
        match result with
        | Result.Error msg -> msg |> Expect.stringContains "the error names the daemon's pid" (string runningDaemonPid)
        | Result.Ok _ -> failtest "handlePrune should return Error when a daemon is running"
      })
  }
]

[<Tests>]
let mergeManifestWithExistingTests = testList "DaemonMode.mergeManifestWithExisting" [

  let emptySnapshot = SessionManager.QuerySnapshot.empty

  /// A session the manifest still lists as alive.
  let aliveRecord (id: string) (createdHoursAgo: float) : DaemonSessionRecord =
    { SessionId = id
      Projects = []
      WorkingDir = "/tmp/" + id
      CreatedAt = DateTimeOffset.UtcNow.AddHours(-createdHoursAgo)
      StoppedAt = None }

  /// Saves a manifest that holds exactly `records`.
  let saveRecords (dir: string) (records: DaemonSessionRecord list) =
    let state : DaemonManifestState =
      { Sessions = records |> List.map (fun r -> r.SessionId, r) |> Map.ofList
        ActiveSessionId = None }
    DaemonPersistence.saveManifest dir state |> ignore

  testCase "an empty directory merges to an empty session map" <| fun _ ->
    withDir (fun dir ->
      match SageFs.Server.DaemonMode.mergeManifestWithExisting dir nullLog emptySnapshot None None with
      | Result.Ok state -> state.Sessions.Count |> Expect.equal "an empty merge produces an empty session map" 0
      | Result.Error err -> failtestf "Expected Ok for an empty directory, got %A" err)

  testCase "a missing manifest is converted to Ok, never propagated as NotFound" <| fun _ ->
    withMissingDir (fun dir ->
      match SageFs.Server.DaemonMode.mergeManifestWithExisting dir nullLog emptySnapshot None None with
      | Result.Ok _ -> ()
      | Result.Error ManifestLoadError.NotFound -> failtest "mergeManifestWithExisting must not propagate NotFound to callers"
      | Result.Error err -> failtestf "Unexpected error from merge: %A" err)

  testCase "a phantom session (absent from the snapshot, StoppedAt = None) is stamped with the current time" <| fun _ ->
    withDir (fun dir ->
      saveRecords dir [ aliveRecord "phantom-001" 2.0 ]
      let beforeMerge = DateTimeOffset.UtcNow
      match SageFs.Server.DaemonMode.mergeManifestWithExisting dir nullLog emptySnapshot None None with
      | Result.Error err -> failtestf "merge should succeed, got %A" err
      | Result.Ok merged ->
        match merged.Sessions |> Map.tryFind "phantom-001" with
        | None -> failtest "the phantom session should still be present in the merged state"
        | Some session ->
          match session.StoppedAt with
          | None -> failtest "the phantom session should be stamped with StoppedAt"
          | Some stamp -> (stamp, beforeMerge) |> Expect.isGreaterThanOrEqual "the stamp is at or after the merge started")

  testCase "an already-stopped session keeps its original StoppedAt" <| fun _ ->
    withDir (fun dir ->
      // The binary manifest stores milliseconds, so truncate to that precision.
      let originalStop = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddHours(-1.0).ToUnixTimeMilliseconds())
      saveRecords dir [ { aliveRecord "stopped-001" 3.0 with StoppedAt = Some originalStop } ]
      match SageFs.Server.DaemonMode.mergeManifestWithExisting dir nullLog emptySnapshot None None with
      | Result.Error err -> failtestf "merge failed: %A" err
      | Result.Ok merged ->
        match merged.Sessions |> Map.tryFind "stopped-001" with
        | None -> failtest "the stopped session should still be present"
        | Some session -> session.StoppedAt |> Expect.equal "the original StoppedAt is preserved" (Some originalStop))

  testCase "during shutdown a phantom session is stamped with the shutdown timestamp" <| fun _ ->
    withDir (fun dir ->
      saveRecords dir [ aliveRecord "phantom-002" 1.0 ]
      let shutdownTime = DateTimeOffset.UtcNow
      match SageFs.Server.DaemonMode.mergeManifestWithExisting dir nullLog emptySnapshot None (Some shutdownTime) with
      | Result.Error err -> failtestf "merge failed: %A" err
      | Result.Ok merged ->
        match merged.Sessions |> Map.tryFind "phantom-002" with
        | None -> failtest "the phantom session should be present"
        | Some session -> session.StoppedAt |> Expect.equal "a phantom during shutdown gets the shutdown timestamp" (Some shutdownTime))
]
