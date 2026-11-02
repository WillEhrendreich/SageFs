module SageFs.Tests.RespawnAfterReapTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol

// #179: a hard reset of a session whose startup profile runs at load must not start the replacement while the old
// worker still holds the app's port. These pin the two decisions on their own: which sessions are held to that order,
// and when a parked replacement may start. The mailbox that applies them is covered in
// SessionManagerRespawnAfterReapTests.fs.

let private session = SessionId.newId ()

/// A scratch folder that is removed afterwards, with the paths a test needs inside it.
let private withFolder (run: string -> unit) : unit =
  let root = Path.Combine(Path.GetTempPath(), "respawn-after-reap-" + Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory root |> ignore
  try run root
  finally try Directory.Delete(root, true) with _ -> ()

let private writeProfile (folder: string) : string =
  let dir = Path.Combine(folder, ".SageFs")
  Directory.CreateDirectory dir |> ignore
  let path = Path.Combine(dir, "init.fsx")
  File.WriteAllText(path, "let app = 1")
  path

let private projectIn (folder: string) : SessionProjectTarget list =
  [ SessionProjectTarget.Project (Path.Combine(folder, "App.fsproj")) ]

let private scriptPath (script: StartupProfile.InitScript option) : string option =
  script |> Option.map (fun s -> s.Path)

[<Tests>]
let respawnAfterReapTests =
  testList "RespawnAfterReap" [

    testList "which sessions the old worker must leave first" [

      testCase "WHY — an init.fsx in the project's folder holds the session to reap-then-spawn" <| fun _ ->
        withFolder (fun root ->
          let projectDir = Path.Combine(root, "proj")
          let profile = writeProfile projectDir
          RespawnAfterReap.sequencingFor (projectIn projectDir) root
          |> Expect.equal "the profile's path travels with the decision" (RespawnAfterReap.Sequencing.ReapThenSpawn profile))

      testCase "WHY — an init.fsx in the session's working directory counts when the project folder has none" <| fun _ ->
        withFolder (fun root ->
          let projectDir = Path.Combine(root, "proj")
          Directory.CreateDirectory projectDir |> ignore
          let profile = writeProfile root
          RespawnAfterReap.tryFindStartupScript (projectIn projectDir) root
          |> scriptPath
          |> Expect.equal "found beside the working directory" (Some profile))

      testCase "WHY — a .SageFsrc is a startup profile too, as the worker loads it" <| fun _ ->
        withFolder (fun root ->
          let rc = Path.Combine(root, ".SageFsrc")
          File.WriteAllText(rc, "let x = 1")
          RespawnAfterReap.tryFindStartupScript [ SessionProjectTarget.Bare ] root
          |> scriptPath
          |> Expect.equal "the rc file is found" (Some rc))

      testCase "WHY — a solution target looks in the solution's folder, since the worker's primary project is not known here" <| fun _ ->
        withFolder (fun root ->
          let profile = writeProfile root
          RespawnAfterReap.tryFindStartupScript [ SessionProjectTarget.Solution (Path.Combine(root, "All.slnx")) ] (Path.Combine(root, "elsewhere"))
          |> scriptPath
          |> Expect.equal "found beside the solution" (Some profile))

      testCase "WHY — a session with no startup profile keeps spawn-first, so its old worker serves until the new one is Ready" <| fun _ ->
        withFolder (fun root ->
          RespawnAfterReap.sequencingFor (projectIn root) root
          |> Expect.equal "no profile, no change in order" RespawnAfterReap.Sequencing.SpawnFirst)

      testCase "WHY — the decision is a plain function of whether a profile was found" <| fun _ ->
        let script : StartupProfile.InitScript = { Folder = "/p"; Path = "/p/.SageFs/init.fsx" }
        RespawnAfterReap.sequencingOf (Some script)
        |> Expect.equal "found" (RespawnAfterReap.Sequencing.ReapThenSpawn "/p/.SageFs/init.fsx")
        RespawnAfterReap.sequencingOf None
        |> Expect.equal "not found" RespawnAfterReap.Sequencing.SpawnFirst
    ]

    testList "when a parked replacement may start" [

      testCase "WHY — a session with nothing parked is left alone, so a stop of an ordinary session never spawns" <| fun _ ->
        let verdict, book = RespawnAfterReap.verdict session (WorkerReaper.Standing.Done WorkerReaper.Settled.AllGone) RespawnAfterReap.empty<string>
        verdict |> Expect.equal "nothing to do" RespawnAfterReap.Verdict.NothingParked
        book |> Expect.equal "the book is unchanged" RespawnAfterReap.empty<string>

      testCase "WHY — a replacement waits while any old worker is still exiting" <| fun _ ->
        let parked = RespawnAfterReap.empty<string> |> RespawnAfterReap.park session "replacement"
        let verdict, book = RespawnAfterReap.verdict session (WorkerReaper.Standing.StillExiting [ 101 ]) parked
        verdict |> Expect.equal "keeps waiting, naming who" (RespawnAfterReap.Verdict.StillWaiting [ 101 ])
        book |> Expect.equal "still parked" parked

      testCase "WHY — a replacement starts once every old worker is gone, and is taken off the book so it starts once" <| fun _ ->
        let parked = RespawnAfterReap.empty<string> |> RespawnAfterReap.park session "replacement"
        let verdict, book = RespawnAfterReap.verdict session (WorkerReaper.Standing.Done WorkerReaper.Settled.AllGone) parked
        verdict |> Expect.equal "spawn" (RespawnAfterReap.Verdict.SpawnNow "replacement")
        RespawnAfterReap.isParked session book |> Expect.isFalse "no longer parked"
        let again, _ = RespawnAfterReap.verdict session (WorkerReaper.Standing.Done WorkerReaper.Settled.AllGone) book
        again |> Expect.equal "a second report cannot start a second worker" RespawnAfterReap.Verdict.NothingParked

      testCase "WHY — a worker that outlived its kill does not hold the session in Restarting forever, and the verdict names it" <| fun _ ->
        let parked = RespawnAfterReap.empty<string> |> RespawnAfterReap.park session "replacement"
        let verdict, _ = RespawnAfterReap.verdict session (WorkerReaper.Standing.Done (WorkerReaper.Settled.Survivors [ 7 ])) parked
        verdict |> Expect.equal "spawn, naming the survivor" (RespawnAfterReap.Verdict.SpawnDespiteSurvivors ("replacement", [ 7 ]))

      testCase "WHY — parking is per session: another session's reaping does not start this one's replacement" <| fun _ ->
        let other = SessionId.newId ()
        let parked = RespawnAfterReap.empty<string> |> RespawnAfterReap.park session "replacement"
        let verdict, book = RespawnAfterReap.verdict other (WorkerReaper.Standing.Done WorkerReaper.Settled.AllGone) parked
        verdict |> Expect.equal "not this session" RespawnAfterReap.Verdict.NothingParked
        RespawnAfterReap.isParked session book |> Expect.isTrue "still parked"

      testCase "WHY — dropping a parked replacement (the session was stopped) means nothing starts later" <| fun _ ->
        let parked = RespawnAfterReap.empty<string> |> RespawnAfterReap.park session "replacement" |> RespawnAfterReap.drop session
        RespawnAfterReap.isParked session parked |> Expect.isFalse "gone"
        let verdict, _ = RespawnAfterReap.verdict session (WorkerReaper.Standing.Done WorkerReaper.Settled.AllGone) parked
        verdict |> Expect.equal "nothing to start" RespawnAfterReap.Verdict.NothingParked

      testProperty "WHY — whatever order the old workers leave in, the replacement starts at the last one and never before" <| fun (count: byte) (order: int list) ->
        let pids = [ 1 .. 1 + int count % 8 ]
        let arrival =
          pids
          |> List.sortBy (fun pid -> order |> List.tryItem (pid % (max 1 order.Length)) |> Option.defaultValue pid, pid)
        let book = WorkerReaper.empty<unit> |> WorkerReaper.start session pids
        let parked = RespawnAfterReap.empty<string> |> RespawnAfterReap.park session "replacement"
        let _, _, started =
          arrival
          |> List.fold (fun (reaping, parkedNow, verdicts) pid ->
            let reaping, _ = WorkerReaper.finish session pid WorkerReaper.Fate.Left reaping
            let verdict, parkedNext = RespawnAfterReap.verdict session (WorkerReaper.standing session reaping) parkedNow
            reaping, parkedNext, verdicts @ [ verdict ]) (book, parked, [])
        started
        |> List.take (pids.Length - 1)
        |> List.forall (function RespawnAfterReap.Verdict.StillWaiting _ -> true | _ -> false)
        |> Expect.isTrue "every earlier report waits"
        started |> List.last |> Expect.equal "the last report starts it" (RespawnAfterReap.Verdict.SpawnNow "replacement")
    ]
  ]
