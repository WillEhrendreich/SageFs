module SageFs.Tests.ManifestOwnerTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features
open SageFs.Features.DaemonManifest
open SageFs.Features.ManifestTypes

let private silentLogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

let private tempDir () =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-manifest-owner-" + Guid.NewGuid().ToString("N").[..11])
  Directory.CreateDirectory(dir) |> ignore
  dir

let private cleanup (dir: string) =
  try Directory.Delete(dir, true) with _ -> ()

let private manifestPath (dir: string) = Path.Combine(dir, "daemon.sagefm")

let private record (id: string) : DaemonSessionRecord =
  { SessionId = id
    Projects = [ "App.fsproj" ]
    WorkingDir = "/code/app"
    CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_740_000_000_000L)
    StoppedAt = None }

let private manifestOf (ids: string list) : DaemonManifestState =
  { Sessions = ids |> List.map (fun id -> id, record id) |> Map.ofList
    ActiveSessionId = ids |> List.tryHead }

let private loadOrFail (dir: string) : DaemonManifestState =
  match DaemonPersistence.loadManifest dir with
  | Ok loaded -> loaded
  | Error e -> failtestf "manifest unreadable: %A" e

/// A stop time the loader keeps: entries stopped more than 7 days ago are
/// dropped on load, and the format stores whole milliseconds.
let private recentAt () =
  DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())

/// Bytes that are not a manifest: SFM1 magic check fails.
let private corruptBytes = Array.init 96 (fun i -> byte (i * 7))

let private runningSync (live: string list) (at: DateTimeOffset) =
  ManifestMutation.SyncLive (live |> List.map record, List.tryHead live, at, LiveSync.Running)

[<Tests>]
let concurrentWriterTests = testList "ManifestOwner concurrent writers" [

  testTask "a create then stop is durable without a periodic sync" {
    let dir = tempDir ()
    try
      use owner = ManifestOwner.start silentLogger dir
      let created =
        { record "quick" with
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) }
      let stoppedAt = created.CreatedAt.AddSeconds 1.0
      let! createdResult = owner.Commit (ManifestMutation.RecordCreated created)
      createdResult |> Result.isOk |> Expect.isTrue "create record commits"
      let! stoppedResult = owner.Commit (ManifestMutation.MarkStopped ("quick", stoppedAt))
      stoppedResult |> Result.isOk |> Expect.isTrue "stop record commits"
      let loaded = loadOrFail dir
      loaded.Sessions |> Map.containsKey "quick" |> Expect.isTrue "short-lived session remains visible"
      loaded.Sessions |> Map.tryFind "quick" |> Option.bind (fun r -> r.StoppedAt)
      |> Expect.equal "short-lived session is stopped" (Some stoppedAt)
      DaemonManifestState.aliveSessions loaded |> Expect.isEmpty "short-lived session is not resumed"
    finally
      cleanup dir
  }

  testTask "MarkStopped persists before restart and is idempotent" {
    let dir = tempDir ()
    try
      let currentRecord id = { record id with CreatedAt = DateTimeOffset.UtcNow }
      let seeded =
        { Sessions = Map.ofList [ "stop-me", currentRecord "stop-me"; "keep-me", currentRecord "keep-me" ]
          ActiveSessionId = Some "stop-me" }
      DaemonPersistence.saveManifest dir seeded |> ignore
      use owner = ManifestOwner.start silentLogger dir
      let firstAt = recentAt ()
      let secondAt = firstAt.AddMinutes 1.0
      let! first = owner.Commit (ManifestMutation.MarkStopped ("stop-me", firstAt))
      first |> Result.isOk |> Expect.isTrue "first stop commits"
      let! second = owner.Commit (ManifestMutation.MarkStopped ("stop-me", secondAt))
      second |> Result.isOk |> Expect.isTrue "second stop commits idempotently"
      let loaded = loadOrFail dir
      loaded.Sessions |> Map.containsKey "stop-me"
      |> Expect.isTrue "the stopped record remains persisted for the retention window"
      loaded.Sessions |> Map.tryFind "stop-me" |> Option.bind (fun record -> record.StoppedAt)
      |> Expect.equal "original stop timestamp is preserved" (Some firstAt)
      DaemonManifestState.aliveSessions loaded
      |> List.map (fun record -> record.SessionId)
      |> Expect.equal "stopped session is not resumed" [ "keep-me" ]
    finally
      cleanup dir
  }

  testTask "concurrent purges and live syncs never lose an update" {
    let dir = tempDir ()
    try
      let ids = [ for i in 1 .. 40 -> sprintf "s%02d" i ]
      let toRemove, kept = List.splitAt 20 ids
      DaemonPersistence.saveManifest dir (manifestOf ids) |> ignore
      use owner = ManifestOwner.start silentLogger dir
      let sync = runningSync kept DateTimeOffset.UtcNow
      let purges =
        toRemove
        |> List.map (fun id ->
          Task.Run<ManifestOwner.CommitResult>(Func<Task<ManifestOwner.CommitResult>>(fun () ->
            owner.Commit (ManifestMutation.Remove id))))
      let syncs =
        [ for _ in 1 .. 20 -> Task.Run(Action(fun () -> owner.Post(sync, ignore))) ]
      let! purgeResults = Task.WhenAll purges
      do! Task.WhenAll syncs
      do! owner.Flush()
      purgeResults |> Array.forall Result.isOk |> Expect.isTrue "every purge commits"
      let loaded = loadOrFail dir
      loaded.Sessions
      |> Map.keys
      |> Set.ofSeq
      |> Expect.equal "every purged session is gone and every kept session remains" (Set.ofList kept)
      loaded.Sessions
      |> Map.forall (fun _ r -> r.StoppedAt.IsNone)
      |> Expect.isTrue "the live sessions are still alive"
      Directory.GetFiles(dir, "*.tmp") |> Expect.isEmpty "no staging file is left behind"
    finally
      cleanup dir
  }
]

[<Tests>]
let ownerTests = testList "ManifestOwner" [

  testTask "a shutdown sync is on disk when its reply arrives" {
    let dir = tempDir ()
    try
      DaemonPersistence.saveManifest dir (manifestOf [ "a"; "b" ]) |> ignore
      use owner = ManifestOwner.start silentLogger dir
      let at = recentAt ()
      let! committed = owner.Commit (ManifestMutation.SyncLive ([ record "a" ], Some "a", at, LiveSync.ShuttingDown))
      match committed with
      | Ok (c: ManifestOwner.Committed) -> c.Persisted |> Expect.equal "the shutdown sync was written" (ManifestOwner.Persisted.WrittenTo (manifestPath dir))
      | Error e -> failtestf "shutdown sync failed: %A" e
      let loaded = loadOrFail dir
      loaded.Sessions |> Map.keys |> Set.ofSeq |> Expect.equal "both sessions are recorded" (set [ "a"; "b" ])
      loaded.Sessions
      |> Map.forall (fun _ r -> r.StoppedAt = Some at)
      |> Expect.isTrue "every session is stamped stopped at the shutdown time"
    finally
      cleanup dir
  }

  testTask "a live sync after the shutdown sync is refused, so it cannot revive stopped sessions" {
    let dir = tempDir ()
    try
      use owner = ManifestOwner.start silentLogger dir
      let at = recentAt ()
      let! _ = owner.Commit (ManifestMutation.SyncLive ([ record "a" ], Some "a", at, LiveSync.ShuttingDown))
      let! late = owner.Commit (runningSync [ "a" ] (at.AddSeconds 5.0))
      late |> Expect.equal "the late live sync is refused" (Error ManifestOwner.CommitError.AfterShutdown)
      (loadOrFail dir).Sessions.["a"].StoppedAt |> Expect.equal "the session stays stopped" (Some at)
    finally
      cleanup dir
  }

  testTask "a mutation that changes nothing writes nothing" {
    let dir = tempDir ()
    try
      DaemonPersistence.saveManifest dir (manifestOf [ "a" ]) |> ignore
      use owner = ManifestOwner.start silentLogger dir
      let! committed = owner.Commit (ManifestMutation.Remove "ghost")
      match committed with
      | Ok (c: ManifestOwner.Committed) -> c.Persisted |> Expect.equal "nothing to write" ManifestOwner.Persisted.Unchanged
      | Error e -> failtestf "purge of an unknown id failed: %A" e
    finally
      cleanup dir
  }

  testTask "an unreadable manifest is never overwritten" {
    let dir = tempDir ()
    try
      File.WriteAllBytes(manifestPath dir, corruptBytes)
      use owner = ManifestOwner.start silentLogger dir
      let! committed = owner.Commit (runningSync [ "a" ] DateTimeOffset.UtcNow)
      match committed with
      | Error (ManifestOwner.CommitError.BaseUnreadable (CorruptData _)) -> ()
      | other -> failtestf "expected the corrupt base to be refused, got %A" other
      File.ReadAllBytes(manifestPath dir) |> Expect.equal "the corrupt file is untouched" corruptBytes
    finally
      cleanup dir
  }

  testTask "quarantining a corrupt manifest renames it aside and unblocks commits" {
    let dir = tempDir ()
    try
      File.WriteAllBytes(manifestPath dir, corruptBytes)
      use owner = ManifestOwner.start silentLogger dir
      let! renamed = owner.QuarantineCorrupt()
      renamed |> Expect.isTrue "the corrupt manifest is renamed"
      Directory.GetFiles(dir, "daemon.sagefm.corrupt.*") |> Array.length |> Expect.equal "the corrupt bytes are kept aside" 1
      let! committed = owner.Commit (runningSync [ "a" ] DateTimeOffset.UtcNow)
      committed |> Result.isOk |> Expect.isTrue "commits write again"
      (loadOrFail dir).Sessions |> Map.containsKey "a" |> Expect.isTrue "the new session is recorded"
    finally
      cleanup dir
  }

  testTask "a readable manifest is never quarantined" {
    let dir = tempDir ()
    try
      DaemonPersistence.saveManifest dir (manifestOf [ "a" ]) |> ignore
      use owner = ManifestOwner.start silentLogger dir
      let! renamed = owner.QuarantineCorrupt()
      renamed |> Expect.isFalse "a readable manifest stays where it is"
      (loadOrFail dir).Sessions |> Map.containsKey "a" |> Expect.isTrue "its contents are intact"
    finally
      cleanup dir
  }

  testTask "a commit callback that throws does not stop the owner" {
    let dir = tempDir ()
    try
      use owner = ManifestOwner.start silentLogger dir
      owner.Post(runningSync [ "a" ] DateTimeOffset.UtcNow, fun _ -> failwith "callback boom")
      let! committed = owner.Commit (ManifestMutation.Remove "a")
      committed |> Result.isOk |> Expect.isTrue "the owner keeps serving"
      (loadOrFail dir).Sessions |> Map.isEmpty |> Expect.isTrue "both mutations were applied in order"
    finally
      cleanup dir
  }
]

// ─── Pure core: ManifestMutation.apply ──────────────────────────────

let private idPool = [ "a"; "b"; "c"; "d"; "e"; "f"; "g"; "h" ]

let rec private genAll (gens: Gen<'a> list) : Gen<'a list> =
  match gens with
  | [] -> Gen.constant []
  | g :: rest -> gen {
      let! x = g
      let! xs = genAll rest
      return x :: xs }

let private genTime =
  Gen.choose (0, 1_000_000)
  |> Gen.map (fun s -> DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L + int64 s * 1000L))

let private genRecord (id: string) : Gen<DaemonSessionRecord> = gen {
  let! created = genTime
  let! stopped = Gen.oneof [ Gen.constant None; genTime |> Gen.map Some ]
  return
    { SessionId = id
      Projects = [ id + ".fsproj" ]
      WorkingDir = "/code/" + id
      CreatedAt = created
      StoppedAt = stopped } }

let private genSubsetOf (ids: string list) : Gen<string list> =
  ids
  |> List.map (fun id -> Gen.elements [ Some id; None ])
  |> genAll
  |> Gen.map (List.choose id)

let private genState : Gen<DaemonManifestState> = gen {
  let! ids = genSubsetOf idPool
  let! records = ids |> List.map genRecord |> genAll
  let! active = Gen.elements (None :: (ids |> List.map Some))
  return
    { Sessions = records |> List.map (fun r -> r.SessionId, r) |> Map.ofList
      ActiveSessionId = active } }

let private genPermutation (xs: 'a list) : Gen<'a list> = gen {
  let! keys = xs |> List.map (fun _ -> Gen.choose (0, 1_000_000)) |> genAll
  return List.zip keys xs |> List.sortBy fst |> List.map snd }

let private genLive (ids: string list) : Gen<DaemonSessionRecord list> =
  ids
  |> List.map (fun id -> genRecord id |> Gen.map (fun r -> { r with StoppedAt = None }))
  |> genAll

let private applyAll (mutations: ManifestMutation list) (state: DaemonManifestState) =
  mutations |> List.fold (fun s m -> ManifestMutation.apply m s) state

let private keysOf (state: DaemonManifestState) = state.Sessions |> Map.keys |> Set.ofSeq

let private config = { FsCheckConfig.defaultConfig with maxTest = 300 }

[<Tests>]
let mutationProperties = testList "ManifestMutation properties" [

  testPropertyWithConfig config "purges commute: any order of the same removes yields the same manifest" <|
    Prop.forAll
      (Arb.fromGen (gen {
        let! state = genState
        let! removes = genSubsetOf idPool
        let! shuffled = genPermutation removes
        return state, removes, shuffled }))
      (fun (state, removes, shuffled) ->
        let run order = applyAll (order |> List.map ManifestMutation.Remove) state
        run removes = run shuffled)

  testPropertyWithConfig config "no purge is lost however purges and live syncs interleave" <|
    Prop.forAll
      (Arb.fromGen (gen {
        let! state = genState
        let! removed = genSubsetOf idPool
        let! liveIds = genSubsetOf (idPool |> List.filter (fun id -> not (List.contains id removed)))
        let! live = genLive liveIds
        let! at = genTime
        let! syncCount = Gen.choose (1, 4)
        let sync = ManifestMutation.SyncLive (live, List.tryHead liveIds, at, LiveSync.Running)
        let! interleaving =
          genPermutation ((removed |> List.map ManifestMutation.Remove) @ List.replicate syncCount sync)
        return state, removed, liveIds, interleaving }))
      (fun (state, removed, liveIds, interleaving) ->
        let final = applyAll interleaving state
        let expected = Set.difference (Set.union (keysOf state) (Set.ofList liveIds)) (Set.ofList removed)
        keysOf final = expected
        && liveIds |> List.forall (fun id -> final.Sessions.[id].StoppedAt.IsNone))

  testPropertyWithConfig config "a purged session never stays the active session" <|
    Prop.forAll
      (Arb.fromGen (gen {
        let! state = genState
        let! target = Gen.elements idPool
        return state, target }))
      (fun (state, target) ->
        let after = ManifestMutation.apply (ManifestMutation.Remove target) state
        match after.ActiveSessionId with
        | None -> true
        | Some active -> active <> target && after.Sessions.ContainsKey active)

  testPropertyWithConfig config "a running sync keeps live sessions alive and stops every vanished one" <|
    Prop.forAll
      (Arb.fromGen (gen {
        let! state = genState
        let! liveIds = genSubsetOf idPool
        let! live = genLive liveIds
        let! active = Gen.elements (None :: (liveIds |> List.map Some))
        let! at = genTime
        return state, live, active, at }))
      (fun (state, live, active, at) ->
        let after = ManifestMutation.apply (ManifestMutation.SyncLive (live, active, at, LiveSync.Running)) state
        let liveIds = live |> List.map (fun r -> r.SessionId) |> Set.ofList
        let liveAlive = liveIds |> Set.forall (fun id -> after.Sessions.[id].StoppedAt.IsNone)
        let vanishedStopped =
          after.Sessions
          |> Map.forall (fun id r ->
            liveIds.Contains id
            || (match state.Sessions.TryFind id |> Option.bind (fun before -> before.StoppedAt) with
                | Some original -> r.StoppedAt = Some original
                | None -> r.StoppedAt = Some at))
        liveAlive && vanishedStopped && after.ActiveSessionId = active)

  testPropertyWithConfig config "a shutdown sync leaves no session alive" <|
    Prop.forAll
      (Arb.fromGen (gen {
        let! state = genState
        let! liveIds = genSubsetOf idPool
        let! live = genLive liveIds
        let! at = genTime
        return state, live, at }))
      (fun (state, live, at) ->
        let after = ManifestMutation.apply (ManifestMutation.SyncLive (live, None, at, LiveSync.ShuttingDown)) state
        after.Sessions |> Map.forall (fun _ r -> r.StoppedAt.IsSome))

  testPropertyWithConfig config "a live sync is idempotent, so repeated periodic saves write nothing new" <|
    Prop.forAll
      (Arb.fromGen (gen {
        let! state = genState
        let! liveIds = genSubsetOf idPool
        let! live = genLive liveIds
        let! at = genTime
        let! mode = Gen.elements [ LiveSync.Running; LiveSync.ShuttingDown ]
        return state, ManifestMutation.SyncLive (live, List.tryHead liveIds, at, mode) }))
      (fun (state, sync) ->
        let once = ManifestMutation.apply sync state
        ManifestMutation.apply sync once = once)

  testPropertyWithConfig config "prune stops every alive session and keeps every stopped timestamp" <|
    Prop.forAll
      (Arb.fromGen (gen {
        let! state = genState
        let! at = genTime
        return state, at }))
      (fun (state, at) ->
        let after = ManifestMutation.apply (ManifestMutation.StampAllStopped at) state
        keysOf after = keysOf state
        && after.Sessions
           |> Map.forall (fun id r ->
             match state.Sessions.[id].StoppedAt with
             | Some original -> r.StoppedAt = Some original
             | None -> r.StoppedAt = Some at))
]
