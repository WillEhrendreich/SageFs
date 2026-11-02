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

/// A project the worker reported loaded, the way `classifiedProjectsOf` lists it: in the order of its `Solution.Projects`.
let private loadedProject (path: string) (mode: ProjectLoading.LoadMode) : ProjectLoading.ClassifiedProject =
  { Path = path
    Role = ProjectLoading.ProjectRole.Library
    PackageRefs = []
    LoadMode = mode
    Build = BuildOptimization.Unknown "a fixture" }

let private evaluated (path: string) = loadedProject path ProjectLoading.LoadMode.Evaluated

let private solutionIn (folder: string) : SessionProjectTarget list =
  [ SessionProjectTarget.Solution (Path.Combine(folder, "All.slnx")) ]

[<Tests>]
let respawnAfterReapTests =
  testList "RespawnAfterReap" [

    testList "which sessions the old worker must leave first" [

      testCase "WHY — an init.fsx in the project's folder holds the session to reap-then-spawn" <| fun _ ->
        withFolder (fun root ->
          let projectDir = Path.Combine(root, "proj")
          let profile = writeProfile projectDir
          RespawnAfterReap.sequencingFor (projectIn projectDir) root []
          |> Expect.equal "the profile's path travels with the decision" (RespawnAfterReap.Sequencing.ReapThenSpawn profile))

      testCase "WHY — an init.fsx in the session's working directory counts when the project folder has none" <| fun _ ->
        withFolder (fun root ->
          let projectDir = Path.Combine(root, "proj")
          Directory.CreateDirectory projectDir |> ignore
          let profile = writeProfile root
          RespawnAfterReap.tryFindStartupScript (projectIn projectDir) root []
          |> scriptPath
          |> Expect.equal "found beside the working directory" (Some profile))

      testCase "WHY — a .SageFsrc is a startup profile too, as the worker loads it" <| fun _ ->
        withFolder (fun root ->
          let rc = Path.Combine(root, ".SageFsrc")
          File.WriteAllText(rc, "let x = 1")
          RespawnAfterReap.tryFindStartupScript [ SessionProjectTarget.Bare ] root []
          |> scriptPath
          |> Expect.equal "the rc file is found" (Some rc))

      testCase "WHY — a solution session sees a profile kept only in the first loaded project's folder, where the worker looks first" <| fun _ ->
        withFolder (fun root ->
          let first = Path.Combine(root, "src", "First")
          let second = Path.Combine(root, "src", "Second")
          Directory.CreateDirectory second |> ignore
          let profile = writeProfile first
          let loaded = [ evaluated (Path.Combine(first, "First.fsproj")); evaluated (Path.Combine(second, "Second.fsproj")) ]
          RespawnAfterReap.sequencingFor (solutionIn root) root loaded
          |> Expect.equal "the replacement waits for the old worker" (RespawnAfterReap.Sequencing.ReapThenSpawn profile))

      testCase "WHY — a profile kept only in a later project's folder is one the worker never loads, so the session keeps spawn-first" <| fun _ ->
        withFolder (fun root ->
          let first = Path.Combine(root, "src", "First")
          let second = Path.Combine(root, "src", "Second")
          Directory.CreateDirectory first |> ignore
          writeProfile second |> ignore
          let loaded = [ evaluated (Path.Combine(first, "First.fsproj")); evaluated (Path.Combine(second, "Second.fsproj")) ]
          RespawnAfterReap.sequencingFor (solutionIn root) root loaded
          |> Expect.equal "the worker looks in the first project and the session directory only" RespawnAfterReap.Sequencing.SpawnFirst)

      testCase "WHY — a solution session with no profile anywhere keeps spawn-first" <| fun _ ->
        withFolder (fun root ->
          let first = Path.Combine(root, "src", "First")
          Directory.CreateDirectory first |> ignore
          RespawnAfterReap.sequencingFor (solutionIn root) root [ evaluated (Path.Combine(first, "First.fsproj")) ]
          |> Expect.equal "nothing to wait for" RespawnAfterReap.Sequencing.SpawnFirst)

      testCase "WHY — a solution session's profile in its working directory counts, as it does for the worker" <| fun _ ->
        withFolder (fun root ->
          let first = Path.Combine(root, "src", "First")
          Directory.CreateDirectory first |> ignore
          let profile = writeProfile root
          RespawnAfterReap.tryFindStartupScript (solutionIn root) root [ evaluated (Path.Combine(first, "First.fsproj")) ]
          |> scriptPath
          |> Expect.equal "found in the session directory" (Some profile))

      testCase "WHY — a project the loader parsed by hand is not the worker's primary project, because Solution.Projects does not hold it" <| fun _ ->
        withFolder (fun root ->
          let handParsed = Path.Combine(root, "src", "Hand")
          Directory.CreateDirectory handParsed |> ignore
          writeProfile handParsed |> ignore
          let cause = ProjectLoading.FallbackCause.Threw "boom"
          let loaded = [ loadedProject (Path.Combine(handParsed, "Hand.fsproj")) (ProjectLoading.LoadMode.ManualFallback cause) ]
          RespawnAfterReap.sequencingFor (solutionIn root) root loaded
          |> Expect.equal "only the session directory is searched" RespawnAfterReap.Sequencing.SpawnFirst)

      testCase "WHY — before a worker has reported its projects, a solution is looked for in its own folder, the nearest the manager can know" <| fun _ ->
        withFolder (fun root ->
          let profile = writeProfile root
          RespawnAfterReap.tryFindStartupScript (solutionIn root) (Path.Combine(root, "elsewhere")) []
          |> scriptPath
          |> Expect.equal "found beside the solution" (Some profile))

      testCase "WHY — a session with no startup profile keeps spawn-first, so its old worker serves until the new one is Ready" <| fun _ ->
        withFolder (fun root ->
          RespawnAfterReap.sequencingFor (projectIn root) root []
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
