module SageFs.Tests.LiveTestWatcherCoreTests

/// Tests for the pure single-owner decision core behind LiveTestWatcherManager
/// (roast-9 #10). These are pure `apply`/`reconcile`/`sessionsForPath` calls —
/// no IO, no FileSystemWatcher, no mailbox, no daemon. The real FSW +
/// mailbox behavior (session attribution end to end) is still covered by the
/// integration tests in DaemonStateChangeContractTests.fs, unchanged because
/// the public LiveTestWatcherManager surface did not change.
///
/// T1-T4 below are the four scenarios proven in the SageFs REPL before this
/// module was persisted (dogfood mandate #1).

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.LiveTestWatcherCore
open SageFs.WorkerProtocol
open SageFs.Tests.SharedGenerators

let private sid (raw: string) =
  match SessionId.validate raw with
  | Ok s -> s
  | Error e -> failwithf "test session id %s invalid: %s" raw e

let private s1 = sid "11111111"
let private s2 = sid "22222222"

let private dir1 = System.IO.Path.GetFullPath "/tmp/sagefs-watcher-core-tests/proj1"
let private dir2 = System.IO.Path.GetFullPath "/tmp/sagefs-watcher-core-tests/proj2"
let private fallback = System.IO.Path.GetFullPath "/tmp/sagefs-watcher-core-tests/fallback"
let private filePath = System.IO.Path.Combine(dir1, "Lib.fs")

let scenarioTests =
  testList "LiveTestWatcherCore scenarios (T1-T4, proven in the REPL)" [

    testCase "T1: a claim removed while a save is debouncing drops the reload, not misattributes it" <| fun _ ->
      // AddDirectory(s1) -> FileSaved p -> RemoveDirectory(s1) -> DebounceElapsed.
      // A single owner processes these FIFO, so by the time DebounceElapsed
      // drains, RemoveDirectory has already cleared the claim — the shell's
      // sessionsForPath lookup against the post-removal state finds no
      // owner, and the stale reload is dropped instead of mis-dispatched to
      // whichever session claims dir1 next.
      let state0 = empty
      let state1, addEffects = apply None state0 (AddDirectory(dir1, s1))
      addEffects
      |> Expect.equal "claiming a fresh dir starts a watcher" [ StartWatch dir1 ]
      let state2, saveEffects = apply None state1 (FileSaved filePath)
      saveEffects
      |> Expect.equal "a save arms the debounce" [ ArmDebounce ]
      let state3, removeEffects = apply None state2 (RemoveDirectory(dir1, s1))
      removeEffects
      |> Expect.equal "the only claimant leaving stops the watcher" [ StopWatch dir1 ]
      let state4, drainEffects = apply None state3 DebounceElapsed
      drainEffects
      |> Expect.equal "debounce elapsed always drains everything pending" [ DrainPending [ filePath ] ]
      sessionsForPath state4.DirSessions filePath
      |> Expect.equal "no claim survives the removal, so no session is attributed" []
      // The shell drops the save because dir1 is no longer watched — not merely
      // because no session claims it (a session-less fallback dir stays watched
      // and still fires FileContentChanged).
      isUnderWatchedDir state4.Watched filePath
      |> Expect.isFalse "dir1 is no longer watched, so the stale save is dropped entirely"

    testCase "T2: repeated saves within the debounce window coalesce to one drain" <| fun _ ->
      let state0, _ = apply None empty (AddDirectory(dir1, s1))
      let state1, e1 = apply None state0 (FileSaved filePath)
      let state2, e2 = apply None state1 (FileSaved filePath)
      let state3, e3 = apply None state2 (FileSaved filePath)
      state3.Pending
      |> Expect.equal "N saves of the same path coalesce to one pending entry" (Set.singleton filePath)
      [ e1; e2; e3 ]
      |> List.iter (Expect.equal "every save (re)arms the debounce" [ ArmDebounce ])
      let state4, drainEffects = apply None state3 DebounceElapsed
      drainEffects
      |> Expect.equal "one drain covering the coalesced path" [ DrainPending [ filePath ] ]
      state4.Pending |> Expect.equal "pending clears after the drain" Set.empty

    testCase "T3: a dir shared by two sessions stays watched when only one claim is removed" <| fun _ ->
      let state0, e0 = apply None empty (AddDirectory(dir1, s1))
      e0 |> Expect.equal "first claim starts the watcher" [ StartWatch dir1 ]
      let state1, e1 = apply None state0 (AddDirectory(dir1, s2))
      e1 |> Expect.equal "a second claim on an already-watched dir starts nothing new" []
      let state2, e2 = apply None state1 (RemoveDirectory(dir1, s1))
      e2 |> Expect.equal "the dir is still needed while s2 claims it" []
      state2.Watched
      |> Expect.equal "still watched" (Set.singleton dir1)
      let state3, _ = apply None state2 (FileSaved filePath)
      let state4, _ = apply None state3 DebounceElapsed
      sessionsForPath state4.DirSessions filePath
      |> Expect.equal "the remaining claimant still owns the save" [ s2 ]

    testCase "T4: SyncToSessions stops watchers no longer desired and keeps the fallback" <| fun _ ->
      let state0, e0 = apply (Some fallback) empty (SyncToSessions [ (s1, dir1); (s2, dir2) ])
      e0
      |> List.sort
      |> Expect.equal
        "both session dirs plus the fallback start watching"
        (List.sort [ StartWatch dir1; StartWatch dir2; StartWatch fallback ])
      state0.Watched
      |> Expect.equal "all three are watched" (Set.ofList [ dir1; dir2; fallback ])
      let state1, e1 = apply (Some fallback) state0 (SyncToSessions [ (s1, dir1) ])
      e1
      |> Expect.equal "dir2 is no longer desired by any session" [ StopWatch dir2 ]
      state1.Watched
      |> Expect.equal "dir1 and the fallback remain watched" (Set.ofList [ dir1; fallback ])
  ]

let neededDirsTests =
  testList "LiveTestWatcherCore.neededDirs" [

    testCase "no fallback, no claims -> nothing needed" <| fun _ ->
      neededDirs None Map.empty |> Expect.equal "empty" Set.empty

    testCase "a claim with zero session IDs is not needed" <| fun _ ->
      neededDirs None (Map.ofList [ dir1, [] ]) |> Expect.equal "empty claims don't count" Set.empty

    testCase "fallback is always needed even with no claims" <| fun _ ->
      neededDirs (Some fallback) Map.empty
      |> Expect.equal "fallback alone" (Set.singleton fallback)
  ]

let isUnderWatchedDirTests =
  testList "LiveTestWatcherCore.isUnderWatchedDir" [
    // The shell fires FileContentChanged (which triggers a rebuild/rerun) for
    // any path under a currently-watched dir, and drops a path whose dir is no
    // longer watched. This is the decision that must NOT be gated on a session
    // claim (the fallback dir is watched but claims no session).

    testCase "a file under a claimed, watched dir fires FileContentChanged" <| fun _ ->
      isUnderWatchedDir (Set.singleton dir1) filePath
      |> Expect.isTrue "a file under a watched dir is under-watched"

    testCase "a file under the session-less fallback dir still fires FileContentChanged" <| fun _ ->
      isUnderWatchedDir (Set.singleton fallback) (System.IO.Path.Combine(fallback, "F.fs"))
      |> Expect.isTrue "the fallback dir is watched even with no session claim"

    testCase "a file whose dir is no longer watched is dropped (the stale case)" <| fun _ ->
      isUnderWatchedDir Set.empty filePath
      |> Expect.isFalse "a removed dir's queued save must be dropped"

    testCase "a file outside every watched dir is not under-watched" <| fun _ ->
      isUnderWatchedDir (Set.singleton dir2) filePath
      |> Expect.isFalse "filePath is under dir1, not dir2"
  ]

let propertyTests =
  testList "LiveTestWatcherCore properties" [

    testPropertyWithConfig propConfig
      "Watched always equals neededDirs fallback DirSessions after any single message"
      <| fun () ->
        let genDir = Gen.elements [ dir1; dir2 ]
        let genSession = Gen.elements [ s1; s2 ]
        let genMsg =
          Gen.oneof [
            gen {
              let! d = genDir
              let! sess = genSession
              return AddDirectory(d, sess)
            }
            gen {
              let! d = genDir
              let! sess = genSession
              return RemoveDirectory(d, sess)
            }
            gen {
              let! pairs =
                Gen.zip genSession genDir
                |> Gen.listOfLength 2
              return SyncToSessions pairs
            }
            gen {
              let! d = genDir
              return FileSaved(System.IO.Path.Combine(d, "F.fs"))
            }
            Gen.constant DebounceElapsed
          ]
        let genFallback = Gen.oneof [ Gen.constant None; Gen.constant (Some fallback) ]
        let genCase =
          gen {
            let! fb = genFallback
            let! msgs = Gen.listOfLength 8 genMsg
            return fb, msgs
          }
        Prop.forAll (Arb.fromGen genCase) (fun (fb, msgs) ->
          // Start from a reconciled base, not raw `empty`: `empty.Watched` is
          // deliberately empty (fallback-agnostic), while `neededDirs (Some f) _`
          // includes the fallback. The fallback only enters Watched on the first
          // reconcile — which in the shell is the first Add/Sync, and a FileSaved
          // can only arrive from a watcher that a reconcile already created. So
          // the invariant this asserts is inductive over reachable states: given
          // a consistent start, every message keeps Watched = neededDirs.
          let baseState = reconcile fb empty |> fst
          let finalState = msgs |> List.fold (fun state msg -> apply fb state msg |> fst) baseState
          finalState.Watched = neededDirs fb finalState.DirSessions)

    testPropertyWithConfig propConfig
      "sessionsForPath only ever returns claimants of a dir that actually prefixes the path"
      <| fun () ->
        let genDirSessions =
          gen {
            let! claimDir1 = Gen.elements [ true; false ]
            let! claimDir2 = Gen.elements [ true; false ]
            let entries =
              [ if claimDir1 then yield dir1, [ s1 ]
                if claimDir2 then yield dir2, [ s2 ] ]
            return Map.ofList entries
          }
        Prop.forAll (Arb.fromGen genDirSessions) (fun dirSessions ->
          let path = System.IO.Path.Combine(dir1, "Sub", "F.fs")
          let owners = sessionsForPath dirSessions path
          match Map.tryFind dir1 dirSessions with
          | Some claims when not (List.isEmpty claims) -> owners = claims
          | _ -> owners = [])
  ]

[<Tests>]
let liveTestWatcherCoreTests =
  testList "LiveTestWatcherCore" [ scenarioTests; neededDirsTests; isUnderWatchedDirTests; propertyTests ]
