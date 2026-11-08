/// Two ways the daemon's own sweep could lose what a person kept, found by the roast of 2026-11-08 (item 4):
/// a lock a person made (`git worktree lock --reason "keep"`, which carries no pid) read as "no live process, so unlock it",
/// and `git worktree remove` deletes ignored files (a local `.env`, a `*.db`) that status without `--ignored` never listed.
/// Real git in throwaway repositories, through the sweep that runs on a timer.
module SageFs.Tests.HygieneLockAndIgnoredTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.HygieneGather
open SageFs.HygieneSweep
open SageFs.Tests.HygieneSandbox

let private sweepTwice (sb: Sandbox) : Task<unit> =
  task {
    let context : SweepContext =
      { MakeScan = fun () -> { sb.Scan() with Loc = { sb.Locations with WorktreeGrace = TimeSpan.Zero } }
        Git = runGit
        GitEnv = runGitEnv
        IsAlive = fun _ _ -> false
        AutoTidy = fun () -> On }
    // The first sweep records the worktree, the second is the first that may tidy it.
    let! _ = sweep context
    let! _ = sweep context
    let! _ = sweep context
    return ()
  }

let private lockOf (sb: Sandbox) (path: string) : string =
  let listing = git sb.Repo [ "worktree"; "list"; "--porcelain" ]
  let blocks = listing.Split([| "\n\n" |], StringSplitOptions.RemoveEmptyEntries)
  match blocks |> Array.tryFind (fun b -> b.Contains(Path.GetFileName path)) with
  | Some block -> (match block.Split('\n') |> Array.tryFind (fun l -> l.StartsWith "locked") with | Some l -> l | None -> "")
  | None -> ""

let private ignoreInRepo (sb: Sandbox) (patterns: string list) : unit =
  File.AppendAllLines(Path.Combine(sb.Repo, ".git", "info", "exclude"), patterns)

[<Tests>]
let lockTests =
  testList "Workspace hygiene: a lock a person made" [

    testTask "a lock with a reason and no pid is a person's: the sweep neither unlocks nor removes the worktree" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "kept"
      git sb.Repo [ "worktree"; "lock"; "--reason"; "keep"; path ] |> ignore
      do! sweepTwice sb
      Directory.Exists path |> Expect.isTrue "the worktree is still there"
      lockOf sb path |> Expect.stringContains "and still locked, with the reason" "keep"
    }

    testTask "a lock whose reason only looks like it names a pid is still a person's" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "looks-agentish"
      git sb.Repo [ "worktree"; "lock"; "--reason"; "do not prune, see ticket (pid 999999 was the old one)"; path ] |> ignore
      do! sweepTwice sb
      Directory.Exists path |> Expect.isTrue "the worktree is still there"
      lockOf sb path |> Expect.stringContains "and still locked" "do not prune"
    }

    testTask "a lock in the harness's format whose process is gone is released, and the merged worktree goes" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "agent-locked"
      git sb.Repo [ "worktree"; "lock"; "--reason"; "claude agent agent-locked (pid 999999 start 1)"; path ] |> ignore
      do! sweepTwice sb
      Directory.Exists path |> Expect.isFalse "a dead agent's lock does not keep a merged worktree"
    }

    testTask "a lock in the harness's format whose process is alive keeps the worktree" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "agent-alive"
      git sb.Repo [ "worktree"; "lock"; "--reason"; "claude agent agent-alive (pid 777 start 1)"; path ] |> ignore
      let context : SweepContext =
        { MakeScan = fun () -> { sb.ScanWith((fun pid _ -> pid = 777), [], LiveFacts.none) with Loc = { sb.Locations with WorktreeGrace = TimeSpan.Zero } }
          Git = runGit
          GitEnv = runGitEnv
          IsAlive = fun pid _ -> pid = 777
          AutoTidy = fun () -> On }
      let! _ = sweep context
      let! _ = sweep context
      let! _ = sweep context
      Directory.Exists path |> Expect.isTrue "a live agent's worktree stays"
    }
  ]

[<Tests>]
let ignoredTests =
  testList "Workspace hygiene: ignored files are somebody's work unless a rule says they are regenerated" [

    testTask "a merged worktree holding an ignored .env is not removed, and the .env is saved to the salvage branch" {
      use sb = new Sandbox()
      ignoreInRepo sb [ ".env"; "*.db" ]
      let path = sb.AddWorktree "has-env"
      write (Path.Combine(path, ".env")) "SECRET=1"
      write (Path.Combine(path, "data", "local.db")) "rows"
      do! sweepTwice sb
      Directory.Exists path |> Expect.isTrue "the worktree is still there"
      File.ReadAllText(Path.Combine(path, ".env")) |> Expect.equal "the .env is intact" "SECRET=1"
      let tip = (git sb.Repo [ "rev-parse"; "refs/sagefs/salvage/has-env" ]).Trim()
      git sb.Repo [ "show"; tip + ":.env" ] |> Expect.equal "the .env is on the salvage branch" "SECRET=1"
      git sb.Repo [ "show"; tip + ":data/local.db" ] |> Expect.equal "so is the .db" "rows"
    }

    testTask "ignored build output is regenerated by definition, so a merged worktree holding only that is still tidied" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "only-output"
      write (Path.Combine(path, "bin", "Release", "x.dll")) "binary"
      write (Path.Combine(path, "obj", "project.assets.json")) "{}"
      do! sweepTwice sb
      Directory.Exists path |> Expect.isFalse "build output does not keep a merged worktree"
    }

    testTask "the standing of a worktree with an ignored .env is DirtyReal and names the file" {
      use sb = new Sandbox()
      ignoreInRepo sb [ ".env" ]
      let path = sb.AddWorktree "named"
      write (Path.Combine(path, ".env")) "SECRET=1"
      let! leftovers = gather (sb.Scan()) None
      match standingOfPath leftovers path with
      | Standing.DirtyReal files -> NonEmpty.toList files |> List.map (fun f -> f.Path) |> Expect.contains "names the file" ".env"
      | other -> failtestf "expected DirtyReal, got %A" other
    }
  ]

[<Tests>]
let lockMakerTests =
  testList "Workspace hygiene: whose lock it is" [

    testCase "only the harness's exact format, with its pid, is the harness's; everything else is a person's" <| fun _ ->
      Parse.lockMaker "claude agent agent-1 (pid 314668 start 793942)" |> Expect.equal "the harness's" (LockMaker.Agent 314668)
      for reason in [ "keep"; ""; "  "; "see ticket (pid 12 was the old one)"; "claude agent agent-1 (pid 5)"; "claude agent agent-1"; "my claude agent (pid 5 start 1) note" ] do
        Parse.lockMaker reason |> Expect.equal (sprintf "%A is a person's" reason) LockMaker.Person

    testCase "a lock a person made is said in words, with the reason quoted and the way to release it" <| fun _ ->
      let text = InUseReason.describe (InUseReason.LockedByPerson "keep until the demo")
      text |> Expect.stringContains "quotes the reason" "keep until the demo"
      text |> Expect.stringContains "names the next action" "git worktree unlock"

    testTask "the gather reads a person's lock as a use, whatever the pid in its text" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "person"
      git sb.Repo [ "worktree"; "lock"; "--reason"; "keep"; path ] |> ignore
      let! leftovers = gather (sb.Scan()) None
      match standingOfPath leftovers path with
      | Standing.InUse(InUseReason.LockedByPerson "keep", _) -> ()
      | other -> failtestf "expected InUse LockedByPerson, got %A" other
    }
  ]
