namespace SageFs.Simulation

open System
open SageFs.Features

/// Deterministic simulation of `set_integration_ref` replacing the integration worktree while landings are in the queue, for two
/// repositories (P16, roast 2026-11-08 critical 2). The subject is the daemon's own decisions, not a copy of them: whether a
/// replacement may go ahead is `CohortIntegrationScope.replacementDecision`, the worktree each repository gets is
/// `integrationWorktreeName`, and where each old worktree is removed from is `removalTargets`. The model supplies the world
/// around them: git's registry of worktrees (a repository removes only the worktrees it made), the directories on disk, the
/// landing queue and the session that verifies it.
///
/// What it proves, over seeded event sequences (request a landing, advance the head landing one step, call `set_integration_ref`
/// for repository A or B):
///  - A VERIFICATION KEEPS ITS SOURCES: no landing ends Inconclusive because the worktree or session under it was replaced.
///  - A REFUSAL CHANGES NOTHING: a refused call leaves the binding, the directories and git's registry as they were.
///  - A REPLACEMENT LEAVES NO OLD WORKTREE: after a call that reports it replaced the integration, the previous worktree is gone
///    from disk and from git's registry.
///  - TWO REPOSITORIES NEVER SHARE A PATH: no worktree path ever serves two repositories.
///
/// Three twins carry the pre-fix behaviour so each invariant shows its teeth: `ReplacesDuringVerification` (the call ignores the
/// queue), `RemovesFromTheWrongRepository` (git runs in the new call's repository and nothing reads the answer), and `SharedPath`
/// (one fixed path for every repository).
module IntegrationReplaceSim =

  [<RequireQualifiedAccess>]
  type Repo =
    | A
    | B

  let repoLabel (repo: Repo) : string =
    match repo with
    | Repo.A -> "repo:/work/repo-a"
    | Repo.B -> "repo:/work/repo-b"

  let repoDir (repo: Repo) : string =
    match repo with
    | Repo.A -> "/work/repo-a"
    | Repo.B -> "/work/repo-b"

  [<RequireQualifiedAccess>]
  type Event =
    | RequestLanding
    | Step
    | SetRef of Repo

  [<RequireQualifiedAccess>]
  type Behaviour =
    | Real
    | ReplacesDuringVerification
    | RemovesFromTheWrongRepository
    | SharedPath

  /// `Epoch` names one incarnation of the worktree and its session: a landing that began verifying under one epoch cannot finish under another.
  type Binding = { Repo: Repo; Path: string; SessionAlive: bool; Epoch: int }

  type Landing = { Id: int; Steps: int; Epoch: int option }

  [<RequireQualifiedAccess>]
  type Reply =
    | Replaced
    | Refused
    | RemovalFailed
    | AddFailed

  /// One call of `set_integration_ref`: what it answered and what it did to the world.
  type Call =
    { For: Repo
      Reply: Reply
      /// The world's observable state (binding, directories, registry) before and after: equal for a refusal.
      Before: string
      After: string
      /// The previous binding's worktree, when there was one, and whether it is still on disk and in git's registry afterwards.
      Previous: string option
      PreviousStillThere: bool }

  type World =
    { /// Git's worktree registry: the path of each worktree and the repository that made it.
      Registry: Map<string, Repo>
      Dirs: Set<string>
      Binding: Binding option
      Queue: Landing list
      Landed: int list
      Inconclusive: (int * string) list
      Calls: Call list
      /// Every (repository, path) a binding ever held.
      Paths: Set<Repo * string>
      Next: int }

  type Trace = { Events: Event list; Behaviour: Behaviour; Final: World }

  let private start : World =
    { Registry = Map.empty
      Dirs = Set.empty
      Binding = None
      Queue = []
      Landed = []
      Inconclusive = []
      Calls = []
      Paths = Set.empty
      Next = 1 }

  let private dataDir = "/data"

  let private pathFor (behaviour: Behaviour) (repo: Repo) : string =
    match behaviour with
    | Behaviour.SharedPath -> dataDir + "/cohort-integration"
    | _ -> dataDir + "/" + CohortIntegrationScope.integrationWorktreeName (repoLabel repo)

  let private observable (world: World) : string =
    sprintf "%A|%A|%A" world.Binding (Set.toList world.Dirs) (Map.toList world.Registry)

  /// git worktree remove, run in `repo`: it removes only a worktree that repository made.
  let private gitRemove (repo: Repo) (path: string) (world: World) : Result<World, string> =
    match Map.tryFind path world.Registry with
    | Some owner when owner = repo -> Ok { world with Registry = Map.remove path world.Registry; Dirs = Set.remove path world.Dirs }
    | Some _ -> Error(sprintf "fatal: '%s' is not a working tree" path)
    | None -> Error(sprintf "fatal: '%s' is not a working tree" path)

  /// git worktree add, run in `repo`: it refuses a path that exists.
  let private gitAdd (repo: Repo) (path: string) (world: World) : Result<World, string> =
    match Set.contains path world.Dirs with
    | true -> Error(sprintf "fatal: '%s' already exists" path)
    | false -> Ok { world with Registry = Map.add path repo world.Registry; Dirs = Set.add path world.Dirs }

  let private setRef (behaviour: Behaviour) (repo: Repo) (world: World) : World =
    let before = observable world
    let record (reply: Reply) (after: World) : World =
      let previous = world.Binding |> Option.map (fun b -> b.Path)
      let stillThere =
        match previous with
        | Some path -> Set.contains path after.Dirs || Map.containsKey path after.Registry
        | None -> false
      { after with
          Calls =
            { For = repo; Reply = reply; Before = before; After = observable after; Previous = previous; PreviousStillThere = stillThere }
            :: after.Calls }
    let inFront = world.Queue |> List.map (fun l -> string l.Id)
    let decision =
      match behaviour with
      | Behaviour.ReplacesDuringVerification -> CohortIntegrationScope.Replacement.Allowed
      | _ -> CohortIntegrationScope.replacementDecision inFront
    match decision with
    | CohortIntegrationScope.Replacement.Refused _ -> record Reply.Refused world
    | CohortIntegrationScope.Replacement.Allowed ->
      let newPath = pathFor behaviour repo
      // The previous session is stopped, then the old worktrees removed.
      let stopped = { world with Binding = world.Binding |> Option.map (fun b -> { b with SessionAlive = false }) }
      let previous =
        stopped.Binding
        |> Option.map (fun b -> ({ Repository = repoDir b.Repo; WorktreePath = b.Path } : CohortIntegrationScope.PreviousIntegration))
      let targets =
        match behaviour with
        | Behaviour.RemovesFromTheWrongRepository ->
          // The pre-fix call: git in the new call's repository, against the new path, whose answer nothing reads.
          [ repoDir repo, newPath ]
        | _ -> CohortIntegrationScope.removalTargets previous (repoDir repo) newPath
      let repoOfDir (dir: string) : Repo = if dir = repoDir Repo.A then Repo.A else Repo.B
      let removed, failure =
        targets
        |> List.fold
          (fun (w, failure) (dir, path) ->
            match Set.contains path w.Dirs with
            | false -> w, failure
            | true ->
              match gitRemove (repoOfDir dir) path w with
              | Ok next -> next, failure
              | Error message ->
                match behaviour with
                | Behaviour.RemovesFromTheWrongRepository -> w, failure
                | _ -> w, Some message)
          (stopped, None)
      match failure with
      | Some _ -> record Reply.RemovalFailed { removed with Binding = None }
      | None ->
        match gitAdd repo newPath removed with
        | Error _ -> record Reply.AddFailed { removed with Binding = None }
        | Ok added ->
          let bound = { added with Binding = Some { Repo = repo; Path = newPath; SessionAlive = true; Epoch = added.Next }; Next = added.Next + 1; Paths = Set.add (repo, newPath) added.Paths }
          record Reply.Replaced bound

  /// A landing's verification reads the sources in the worktree through the session; a missing worktree or a stopped session ends
  /// it Inconclusive.
  let private step (world: World) : World =
    match world.Queue with
    | [] -> world
    | landing :: rest ->
      let sourcesPresent =
        match world.Binding with
        | Some b ->
          b.SessionAlive
          && Set.contains b.Path world.Dirs
          && (match landing.Epoch with
              | None -> true
              | Some began -> began = b.Epoch)
        | None -> false
      match sourcesPresent with
      | false -> { world with Queue = rest; Inconclusive = (landing.Id, "the sources the verification reads are gone") :: world.Inconclusive }
      | true ->
        match landing.Steps + 1 with
        | 3 -> { world with Queue = rest; Landed = landing.Id :: world.Landed }
        | steps ->
          let epoch = world.Binding |> Option.map (fun b -> b.Epoch)
          { world with Queue = { landing with Steps = steps; Epoch = (match landing.Epoch with None -> epoch | held -> held) } :: rest }

  let private apply (behaviour: Behaviour) (world: World) (event: Event) : World =
    match event with
    | Event.RequestLanding ->
      match world.Binding with
      | None -> world
      | Some _ -> { world with Queue = world.Queue @ [ { Id = world.Next; Steps = 0; Epoch = None } ]; Next = world.Next + 1 }
    | Event.Step -> step world
    | Event.SetRef repo -> setRef behaviour repo world

  let run (behaviour: Behaviour) (events: Event list) : Trace =
    { Events = events; Behaviour = behaviour; Final = List.fold (apply behaviour) start events }

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  let verificationKeepsItsSources (trace: Trace) : Outcome =
    match trace.Final.Inconclusive with
    | [] -> Outcome.Holds
    | (id, why) :: _ -> Outcome.Violated(sprintf "landing %d ended Inconclusive: %s" id why)

  let refusalChangesNothing (trace: Trace) : Outcome =
    match trace.Final.Calls |> List.tryFind (fun c -> c.Reply = Reply.Refused && c.Before <> c.After) with
    | Some c -> Outcome.Violated(sprintf "a refused call for %A changed the world:\n  before %s\n  after  %s" c.For c.Before c.After)
    | None -> Outcome.Holds

  let replacementLeavesNoOldWorktree (trace: Trace) : Outcome =
    match
      trace.Final.Calls
      |> List.tryFind (fun c -> c.Reply = Reply.Replaced && c.PreviousStillThere && c.Previous <> Some (pathFor trace.Behaviour c.For))
    with
    | Some c -> Outcome.Violated(sprintf "the call for %A reported the integration replaced, but %A is still there" c.For c.Previous)
    | None ->
      // A replacement at the same path removes the old worktree and cuts a new one there: the path is registered again, to the same repository.
      Outcome.Holds

  let twoRepositoriesNeverShareAPath (trace: Trace) : Outcome =
    let clash =
      trace.Final.Paths
      |> Set.toList
      |> List.groupBy snd
      |> List.tryFind (fun (_, bindings) -> bindings |> List.map fst |> List.distinct |> List.length > 1)
    match clash with
    | Some (path, _) -> Outcome.Violated(sprintf "the path %s served both repositories" path)
    | None -> Outcome.Holds

  let invariants : (string * (Trace -> Outcome)) list =
    [ "verification-keeps-its-sources", verificationKeepsItsSources
      "refusal-changes-nothing", refusalChangesNothing
      "replacement-leaves-no-old-worktree", replacementLeavesNoOldWorktree
      "two-repositories-never-share-a-path", twoRepositoriesNeverShareAPath ]

  let violations (trace: Trace) : (string * string) list =
    invariants
    |> List.choose (fun (id, check) ->
      match check trace with
      | Outcome.Holds -> None
      | Outcome.Violated message -> Some(id, message))

  let fromSeed (seed: int) : Event list =
    let rnd = Random(seed)
    let events = [| Event.RequestLanding; Event.Step; Event.Step; Event.SetRef Repo.A; Event.SetRef Repo.B |]
    Event.SetRef Repo.A :: [ for _ in 1 .. rnd.Next(3, 14) -> events.[rnd.Next events.Length] ]

  /// Configure A, queue a landing, call again for A while it is queued: the pre-fix call stopped the session under it.
  let retryDuringVerification : Event list =
    [ Event.SetRef Repo.A; Event.RequestLanding; Event.Step; Event.SetRef Repo.A; Event.Step; Event.Step ]

  /// Configure A, then move to B with nothing queued: A's worktree must go, from A's repository.
  let moveToTheOtherRepository : Event list =
    [ Event.SetRef Repo.A; Event.SetRef Repo.B ]

  /// A then B then A again, a landing in between that finishes first.
  let thereAndBack : Event list =
    [ Event.SetRef Repo.A
      Event.RequestLanding
      Event.Step
      Event.Step
      Event.Step
      Event.SetRef Repo.B
      Event.SetRef Repo.A ]
