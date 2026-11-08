/// The REAL `HygieneEdge`, driven over a fake git that answers from the sweep sim's model world (`HygieneSweepSim`) and can fail on a
/// schedule. This is what lets the sim see the edge's own fail-open, restore points and lock rule: nothing here re-implements them.
/// The fake only says what git would say (a tip, whether a commit is an ancestor of the base, a worktree list with its locks, a
/// refusal to remove a worktree with changes) and does what git would do to the model (remove a worktree, delete a branch, keep a ref).
///
/// A twin is a fake git that lies in one way: a restore ref that reports success and keeps nothing, a lock list that shows every
/// lock as a dead agent's, a status that never lists ignored files, a remove that does not refuse a worktree with changes.
module SageFs.Tests.HygieneSweepSimEdge

open System
open System.IO
open System.Threading.Tasks
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.HygieneGather
open SageFs.Simulation
open SageFs.Simulation.HygieneSweepSim

let private baseSha = "basecommit"

/// The commits on the branch that holds commit `k`, up to and including it: what a ref at `k` keeps reachable.
let private prefixOf (world: World) (k: int) : int list =
  world.Branches
  |> Map.toList
  |> List.tryPick (fun (_, commits) ->
    match List.tryFindIndex ((=) k) commits with
    | Some i -> Some(List.take (i + 1) commits)
    | None -> None)
  |> Option.defaultValue [ k ]

let private tipOfBranch (world: World) (name: string) : int option =
  Map.tryFind name world.Branches |> Option.bind List.tryLast

let private worktreeAt (world: World) (dir: string) : Worktree option =
  world.Worktrees |> Map.toList |> List.tryPick (fun (id, wt) -> if pathOf id = dir then Some wt else None)

/// Which commit a revision names in the model: HEAD of a worktree, a branch (with or without refs/heads/), or `c<k>`.
let private resolve (world: World) (dir: string) (rev: string) : int option =
  match rev with
  | "HEAD" -> worktreeAt world dir |> Option.bind (fun wt -> tipOfBranch world (HygieneSim.branchOf wt.Id))
  | r when r.StartsWith "refs/heads/" -> tipOfBranch world (r.Substring "refs/heads/".Length)
  | r when r.StartsWith "c" && (fst (Int32.TryParse(r.Substring 1))) -> Some(int (r.Substring 1))
  | name -> tipOfBranch world name

let private sha (k: int) : string = sprintf "c%d" k

let private lockText (behavior: Behavior) (wt: Worktree) : string option =
  let deadAgent = sprintf "claude agent agent-%d (pid 999999 start 1)" wt.Id
  match wt.Lock, behavior with
  | Lock.NoLock, _ -> None
  | Lock.AgentDead, _ -> Some deadAgent
  | Lock.AgentLive, _ -> Some(sprintf "claude agent agent-%d (pid 1 start 1)" wt.Id)
  // The old rule read anything without a live pid as a dead agent's lock.
  | Lock.PersonNoPid, Behavior.UnlocksAnyLock
  | Lock.PersonPidLike, Behavior.UnlocksAnyLock -> Some deadAgent
  | Lock.PersonNoPid, _ -> Some "keep"
  | Lock.PersonPidLike, _ -> Some "see ticket (pid 5 was the old one)"

let private listing (behavior: Behavior) (world: World) : string =
  let block (path: string) (head: string) (branch: string) (lock: string option) =
    String.concat "\n"
      [ yield sprintf "worktree %s" path
        yield sprintf "HEAD %s" head
        yield sprintf "branch refs/heads/%s" branch
        match lock with
        | Some reason -> yield sprintf "locked %s" reason
        | None -> () ]
  let main = block (repoPath ()) baseSha "master" None
  let worktrees =
    world.Worktrees
    |> Map.toList
    |> List.map (fun (id, wt) ->
      let name = HygieneSim.branchOf id
      block (pathOf id) (tipOfBranch world name |> Option.map sha |> Option.defaultValue "none") name (lockText behavior wt))
  String.concat "\n\n" (main :: worktrees) + "\n"

let private status (behavior: Behavior) (wt: Worktree) : string =
  let showIgnored =
    match behavior with
    | Behavior.RemovesIgnoredFiles -> false
    | Behavior.Real
    | Behavior.GitFaults
    | Behavior.ForcesAndSkipsSalvage
    | Behavior.NoRestorePoint
    | Behavior.IgnoresLedger
    | Behavior.UnlocksAnyLock
    | Behavior.ForgetsVanished -> true
  String.concat
    ""
    [ if not (Set.isEmpty wt.Dirty) then "?? src/work.fs\000"
      if showIgnored && not (Set.isEmpty wt.Ignored) then "!! .env\000"
      if wt.HasGenerated then " M packages.lock.json\000" ]

/// What the fake git answers and does. `cell` is the model world, changed as git would change the repository.
let private fakeGit (behavior: Behavior) (cell: World ref) (removed: int option ref) (calls: int ref) : Git =
  fun dir args ->
    calls.Value <- calls.Value + 1
    let world = cell.Value
    // The schedule: every third call to a command that decides or keeps something fails, as a git out of memory or timed out would.
    let faulty =
      match behavior with
      | Behavior.GitFaults -> calls.Value % 3 = 0
      | Behavior.Real
      | Behavior.ForcesAndSkipsSalvage
      | Behavior.NoRestorePoint
      | Behavior.IgnoresLedger
      | Behavior.UnlocksAnyLock
      | Behavior.RemovesIgnoredFiles
      | Behavior.ForgetsVanished -> false
    let ok (text: string) = GitResult.Output text
    let no (code: int) = GitResult.Exit(code, "fake git")
    let injected = GitResult.Exit(128, "fatal: injected failure")
    let result =
      match args with
      | [ "rev-parse"; "--verify"; "--quiet"; _ ] when faulty -> injected
      | [ "rev-parse"; "--verify"; "--quiet"; "refs/heads/master" ] -> ok baseSha
      | [ "rev-parse"; "--verify"; "--quiet"; rev ] when rev.StartsWith "refs/sagefs/removed/" -> no 1
      | [ "rev-parse"; "--verify"; "--quiet"; rev ] ->
        match resolve world dir rev with
        | Some k -> ok (sha k)
        | None -> no 1
      | [ "rev-parse"; "--abbrev-ref"; "HEAD" ] ->
        match worktreeAt world dir with
        | Some wt -> ok (HygieneSim.branchOf wt.Id)
        | None -> no 128
      | [ "merge-base"; "--is-ancestor"; _; _ ] when faulty -> injected
      | [ "merge-base"; "--is-ancestor"; head; _ ] ->
        match resolve world dir head with
        | Some k ->
          match evidenceOf world (prefixOf world k) with
          | MergeEvidence.MergedInto MergeHow.Ancestor -> ok ""
          | MergeEvidence.MergedInto _
          | MergeEvidence.UnmergedCommits _
          | MergeEvidence.MergeNotDecidable _ -> no 1
        | None -> no 128
      | [ "cherry"; "-v"; _; _ ] when faulty -> injected
      | [ "cherry"; "-v"; _; head ] ->
        match resolve world dir head with
        | Some k ->
          prefixOf world k
          |> List.map (fun c ->
            match Map.tryFind c world.Base with
            | Some MergeHow.TreeEqual
            | None -> sprintf "+ %s commit %d" (sha c) c
            | Some MergeHow.Ancestor
            | Some MergeHow.PatchEquivalent -> sprintf "- %s commit %d" (sha c) c)
          |> String.concat "\n"
          |> ok
        | None -> no 128
      | [ "diff"; "--quiet"; _; _ ] when faulty -> injected
      | [ "diff"; "--quiet"; _; head ] ->
        match resolve world dir head with
        | Some k ->
          match evidenceOf world (prefixOf world k) with
          | MergeEvidence.MergedInto MergeHow.TreeEqual -> ok ""
          | MergeEvidence.MergedInto _
          | MergeEvidence.UnmergedCommits _
          | MergeEvidence.MergeNotDecidable _ -> no 1
        | None -> no 128
      | [ "update-ref"; _; _ ] when faulty -> injected
      | [ "update-ref"; _; commit ] ->
        match behavior, resolve world dir commit with
        | Behavior.NoRestorePoint, _ -> ok ""
        | _, Some k ->
          cell.Value <- { world with Restore = Set.union world.Restore (Set.ofList (prefixOf world k)) }
          ok ""
        | _, None -> no 128
      | [ "worktree"; "list"; "--porcelain" ] -> ok (listing behavior world)
      | [ "worktree"; "prune" ] -> ok ""
      | [ "worktree"; "unlock"; path ] ->
        match worktreeAt world path with
        | Some wt ->
          cell.Value <- { world with Worktrees = Map.add wt.Id { wt with Lock = Lock.NoLock } world.Worktrees }
          ok ""
        | None -> no 128
      | "worktree" :: "remove" :: rest ->
        let path = List.last rest
        let forced = rest |> List.contains "--force"
        match worktreeAt world path with
        | None -> no 128
        | Some wt ->
          // git refuses a worktree with modified or untracked files unless forced; it deletes ignored files without a word.
          let refuses =
            match behavior with
            | Behavior.ForcesAndSkipsSalvage -> false
            | Behavior.Real
            | Behavior.GitFaults
            | Behavior.NoRestorePoint
            | Behavior.IgnoresLedger
            | Behavior.UnlocksAnyLock
            | Behavior.RemovesIgnoredFiles
            | Behavior.ForgetsVanished -> not forced && not (Set.isEmpty wt.Dirty)
          let locked =
            match wt.Lock with
            | Lock.NoLock -> false
            | Lock.AgentDead
            | Lock.AgentLive
            | Lock.PersonNoPid
            | Lock.PersonPidLike -> true
          match refuses, locked with
          | true, _ -> no 128
          | _, true -> no 128
          | false, false ->
            cell.Value <- { world with Worktrees = Map.remove wt.Id world.Worktrees }
            removed.Value <- Some wt.Id
            (try Directory.Delete(path, true) with :? IOException | :? UnauthorizedAccessException -> ())
            ok ""
      | "status" :: _ ->
        match worktreeAt world dir with
        | Some wt -> ok (status behavior wt)
        | None -> no 128
      | [ "branch"; _; name ] ->
        let checkedOut = world.Worktrees |> Map.exists (fun id _ -> HygieneSim.branchOf id = name)
        match checkedOut with
        | true -> no 1
        | false ->
          cell.Value <- { world with Branches = Map.remove name world.Branches }
          ok ""
      | _ -> no 129
    Task.FromResult result

/// The model's worktrees are real directories at paths every trace shares, and the tests run traces side by side, so one
/// operation at a time touches them.
let private disk = obj ()

/// The REAL edge over the fake git, for the sim to hand to the sweep as its performer.
let performer : Performer =
  fun behavior world operation ->
   lock disk (fun () ->
    let cell = ref world
    let removed : int option ref = ref None
    let calls = ref 0
    // The edge asks the file system whether a worktree is there, so the model's worktree has to be.
    match operation with
    | Operation.RemoveWorktree(accepted, _, _) -> Directory.CreateDirectory(Guard.path accepted) |> ignore
    | Operation.RemoveTree _
    | Operation.RemoveFile _
    | Operation.DeleteBranch _
    | Operation.Terminate _ -> ()
    let ctx : HygieneEdge.EdgeContext =
      { MakeScan = (fun () -> failwith "the sim never looks again through the edge: the executor does")
        Git = fakeGit behavior cell removed calls
        IsAlive = fun pid _ -> pid = 1 }
    // The fake git answers at once, so the edge's task is already complete: reading it blocks on nothing.
    let outcome = (HygieneEdge.performWith HygieneEdge.noJournal ctx operation).Result
    { Outcome = outcome; World = cell.Value; Removed = removed.Value })
