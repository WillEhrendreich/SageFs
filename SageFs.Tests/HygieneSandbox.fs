/// A real repository in a temp dir, with the worktrees an orchestrator leaves behind, for tests that run the
/// hygiene gather and the executor against real git.
module SageFs.Tests.HygieneSandbox

open System
open System.IO
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.HygieneGather
open SageFs.HygieneEdge

let git (dir: string) (args: string list) : string =
  match runGit dir args with
  | GitResult.Output out -> out
  | GitResult.Exit(code, err) -> failwithf "git %s in %s exited %d: %s" (String.Join(" ", args)) dir code err
  | GitResult.Unavailable detail -> failwithf "git unavailable: %s" detail

let commitAll (dir: string) (message: string) =
  git dir [ "add"; "-A" ] |> ignore
  git dir [ "-c"; "user.name=t"; "-c"; "user.email=t@t"; "commit"; "-m"; message ] |> ignore

let write (path: string) (text: string) =
  Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
  File.WriteAllText(path, text)

/// A repository on master with a tracked lock file and a worktree area, in a fresh temp dir. Disposing removes it all.
type Sandbox() =
  let root = Directory.CreateTempSubdirectory("sagefs-hygiene-test-").FullName
  let repo = Path.Combine(root, "repo")
  let locations : Locations =
    { Repo = repo
      GateDir = Path.Combine(root, "gate")
      DataDir = Path.Combine(root, "data")
      HostCacheDir = Path.Combine(root, "hosts")
      TempDir = Path.Combine(root, "tmp")
      Processes = ProcessScope.WholeMachine }
  do
    Directory.CreateDirectory repo |> ignore
    git repo [ "init"; "-b"; "master" ] |> ignore
    write (Path.Combine(repo, "packages.lock.json")) "{}"
    write (Path.Combine(repo, "src", "a.fs")) "module A"
    write (Path.Combine(repo, ".gitignore")) "bin/\nobj/\n.claude/\n"
    commitAll repo "init"

  member _.Root = root
  member _.Repo = repo
  member _.Locations = locations
  /// How far ahead of the real clock every scan believes it is, to age things without waiting.
  member val Skew = TimeSpan.Zero with get, set
  /// The process table and live facts every scan, including the executor's second looks, sees.
  member val Procs : Proc list = [] with get, set
  member val Live : LiveFacts = LiveFacts.none with get, set

  /// A worktree under the agent area, on its own branch, at master.
  member _.AddWorktree(name: string) : string =
    let path = Path.Combine(repo, ".claude", "worktrees", name)
    git repo [ "worktree"; "add"; "-b"; "worktree-agent-" + name; path; "master" ] |> ignore
    Path.GetFullPath path

  member this.ScanWith(alive: int -> int64 option -> bool, processes: Proc list, live: LiveFacts) : Scan =
    { Now = DateTime.UtcNow + this.Skew
      Loc = locations
      Git = runGit
      Processes = processes
      Live = live
      IsAlive = alive }

  member this.Scan() : Scan = this.ScanWith((fun _ _ -> false), this.Procs, this.Live)

  member _.Roots : Roots = rootsOf locations

  member this.Effects(alive: int -> int64 option -> bool) : Effects =
    effects { MakeScan = (fun () -> this.ScanWith(alive, this.Procs, this.Live)); Git = runGit; IsAlive = alive }

  interface IDisposable with
    member _.Dispose() =
      // The worktrees are this test's own, and git leaves read-only objects: best effort.
      try Directory.Delete(root, true) with _ -> ()

  /// So a `task` block can `use` it too.
  interface IAsyncDisposable with
    member this.DisposeAsync() =
      (this :> IDisposable).Dispose()
      System.Threading.Tasks.ValueTask()

/// Builds the six kinds of worktree an orchestrator leaves behind and returns their paths by name.
let populate (sb: Sandbox) : Map<string, string> =
  let repo = sb.Repo
  let clean = sb.AddWorktree "clean-merged"
  let ff = sb.AddWorktree "ff-merged"
  write (Path.Combine(ff, "src", "ff.fs")) "module Ff"
  commitAll ff "ff work"
  git repo [ "merge"; "--ff-only"; "worktree-agent-ff-merged" ] |> ignore
  let squashed = sb.AddWorktree "squashed"
  write (Path.Combine(squashed, "src", "sq.fs")) "module Sq"
  commitAll squashed "squash work"
  // The base moves on first, so the cherry-picked commit is a new commit and not the same one.
  write (Path.Combine(repo, "src", "other.fs")) "module Other"
  commitAll repo "other work on master"
  let sha = (git squashed [ "rev-parse"; "HEAD" ]).Trim()
  git repo [ "-c"; "user.name=t"; "-c"; "user.email=t@t"; "cherry-pick"; sha ] |> ignore
  let unmerged = sb.AddWorktree "unmerged"
  write (Path.Combine(unmerged, "src", "un.fs")) "module Un"
  commitAll unmerged "unmerged work"
  let generated = sb.AddWorktree "dirty-generated"
  write (Path.Combine(generated, "packages.lock.json")) "{ \"changed\": true }"
  let real = sb.AddWorktree "dirty-real"
  write (Path.Combine(real, "src", "precious.fs")) "module Precious"
  Map.ofList
    [ "clean-merged", clean; "ff-merged", ff; "squashed", squashed
      "unmerged", unmerged; "dirty-generated", generated; "dirty-real", real ]

let standingOfPath (leftovers: Leftover list) (path: string) : Standing =
  leftovers
  |> List.find (fun l -> Leftover.target l = Target.Directory path)
  |> fun l -> (Leftover.entry l).Standing

let branchExists (sb: Sandbox) (name: string) : bool =
  match runGit sb.Repo [ "rev-parse"; "--verify"; "--quiet"; "refs/heads/" + name ] with
  | GitResult.Output _ -> true
  | _ -> false
