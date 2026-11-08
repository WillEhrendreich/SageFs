namespace SageFs.Simulation

open System
open SageFs.Features

/// Deterministic simulation of the cohort's integration worktree and the build its session holds, across landings of
/// every outcome (P14, findings C7 and C8). The subject is the daemon's own decision, not a copy of it: each landing's
/// verification path comes from `CohortLandingPlan.strategy` and `buildOffHead`, and the reset after a landing that did
/// not land from `idleResetTarget`. The model only supplies the world around them: commits, what the session's build
/// holds, where the worktree sits.
///
/// What it proves, over seeded scenarios:
///  - SOUND VERIFICATION: every run of tests happens against a build that holds the compiled content of the commit being
///    landed (the head plus the landing). The first cohort run broke this twice: a refused landing left the session on
///    its own commit, and the next landing was verified against that.
///  - IDLE AT THE HEAD: when no landing is in flight, the worktree is at the integration head, whatever the last landing did.
///  - THE CHEAP PATH SURVIVES: a run of test-only landings, with nothing refused, never rebuilds.
///
/// Three twins carry the pre-fix behaviour so the invariants show their teeth: `NeverChecksTheBuild` (verification trusts the
/// session's build whatever it was made from), `NeverResets` (a refused landing leaves the worktree where it was) and
/// `NotesSuccessOnly` (a build that fails is not recorded, though it wrote the refused commit's DLLs: P16, roast improvement 6).
/// The rebuild itself runs through the daemon's own `CohortRebuildGate.restartAt`, so the order it records in is the real one.
module IntegrationTreeSim =

  [<RequireQualifiedAccess>]
  type Kind =
    /// Changes compiled source of a project the session loads.
    | SourceChange
    /// Changes only a test project's files.
    | TestOnly

  [<RequireQualifiedAccess>]
  type Outcome =
    /// The tests pass, the landing lands.
    | Good
    /// The tests fail, the landing is refused.
    | FailingTests
    /// The build of the rebased commit fails (only meaningful for a `SourceChange`; a test-only change builds).
    | BuildBreaks
    /// The requester withdraws it after the rebase, before it lands.
    | Withdrawn

  type Landing = { Kind: Kind; Outcome: Outcome }

  [<RequireQualifiedAccess>]
  type Behaviour =
    | Real
    | NeverChecksTheBuild
    | NeverResets
    | NotesSuccessOnly

  /// A commit, by name, with the version of compiled content its tree holds.
  type Commit = { Name: string; Compiled: int }

  type Verification = { Landing: int; BuildHolds: int; CommitCompiled: int; Rebuilt: bool }

  type World =
    { Head: Commit
      History: Set<string>
      WorktreeAt: Commit
      /// The compiled content the session's build holds.
      SessionHolds: int
      RebuiltFor: Commit option
      Verifications: Verification list
      Rebuilds: int
      Next: int }

  type Trace = { Scenario: Landing list; Behaviour: Behaviour; Final: World; WorktreeAfterEach: (Commit * Commit) list }

  let private genesis = { Name = "c0"; Compiled = 0 }

  let private start : World =
    { Head = genesis
      History = Set.singleton genesis.Name
      WorktreeAt = genesis
      SessionHolds = 0
      RebuiltFor = None
      Verifications = []
      Rebuilds = 0
      Next = 1 }

  let private classified (kind: Kind) : CohortLandingPlan.ChangedFile list =
    match kind with
    | Kind.SourceChange -> [ CohortLandingPlan.ChangedFile.CompiledSource("Core/File.fs", "Core/Core.fsproj") ]
    | Kind.TestOnly -> [ CohortLandingPlan.ChangedFile.TestSource "Tests/Tests.fs" ]

  /// One landing, rebase to verdict. Returns the world after it and whether the landing landed.
  let private runLanding (behaviour: Behaviour) (index: int) (world: World) (landing: Landing) : World =
    let rebased =
      { Name = sprintf "c%d" world.Next
        Compiled = world.Head.Compiled + (match landing.Kind with Kind.SourceChange -> 1 | Kind.TestOnly -> 0) }
    let world = { world with Next = world.Next + 1; WorktreeAt = rebased }
    let offHead =
      match behaviour with
      | Behaviour.NeverChecksTheBuild -> None
      | Behaviour.Real
      | Behaviour.NeverResets
      | Behaviour.NotesSuccessOnly ->
        CohortLandingPlan.buildOffHead (fun name -> Set.contains name world.History) (world.RebuiltFor |> Option.map (fun c -> c.Name))
    let rebuild =
      match CohortLandingPlan.strategy offHead (classified landing.Kind) with
      | CohortLandingPlan.Strategy.RebuildAtHead _ -> true
      | CohortLandingPlan.Strategy.EvalOverBuild -> false
    // The rebuild runs through the daemon's own gate. A build that breaks still wrote the refused commit's DLLs, which the session
    // loads the next time it restarts, so what the session holds follows the attempt, success or not.
    let broken = rebuild && landing.Outcome = Outcome.BuildBreaks && landing.Kind = Kind.SourceChange
    let rebuilt (world: World) : World * bool =
      let noted = ref world.RebuiltFor
      let holds = ref world.SessionHolds
      let ports : CohortRebuildGate.Ports =
        { WorktreeHead = fun () -> async { return Ok rebased.Name }
          Restore = fun () -> async { return Ok() }
          NoteAttempt =
            fun _ ->
              match behaviour with
              | Behaviour.NotesSuccessOnly -> ()
              | _ -> noted.Value <- Some rebased
          Restart =
            fun () ->
              async {
                holds.Value <- rebased.Compiled
                match broken with
                | true -> return Error(SageFs.SageFsError.BuildFailed(1, [ { File = Some "Core/File.fs"; Line = Some 1; Column = Some 1; Severity = SageFs.BuildDiagnosticSeverity.Blocking; Code = Some "FS0001"; Message = "type mismatch" } ]))
                | false -> return Ok()
              }
          AwaitSettled = fun () -> async { return Ok() } }
      let result = CohortRebuildGate.restartAt ports false rebased.Name |> Async.RunSynchronously
      let succeeded = Result.isOk result
      // The behaviour that noted only successes records the commit once the build has succeeded.
      let rebuiltFor =
        match behaviour, succeeded with
        | Behaviour.NotesSuccessOnly, true -> Some rebased
        | _ -> noted.Value
      { world with SessionHolds = holds.Value; RebuiltFor = rebuiltFor; Rebuilds = world.Rebuilds + 1 }, succeeded
    match rebuild with
    | true when broken -> fst (rebuilt world)
    | _ ->
      let afterBuild =
        match rebuild with
        | true -> fst (rebuilt world)
        | false -> world
      let verified =
        { afterBuild with
            Verifications =
              { Landing = index; BuildHolds = afterBuild.SessionHolds; CommitCompiled = rebased.Compiled; Rebuilt = rebuild }
              :: afterBuild.Verifications }
      match landing.Outcome with
      | Outcome.Good ->
        { verified with Head = rebased; History = Set.add rebased.Name verified.History }
      | Outcome.FailingTests
      | Outcome.Withdrawn
      | Outcome.BuildBreaks -> verified

  /// The reset the daemon does when a landing ended without landing and nothing else is queued.
  let private idleReset (behaviour: Behaviour) (queueIsEmpty: bool) (world: World) : World =
    match behaviour with
    | Behaviour.NeverResets -> world
    | Behaviour.Real
    | Behaviour.NeverChecksTheBuild
    | Behaviour.NotesSuccessOnly ->
      match CohortLandingPlan.idleResetTarget queueIsEmpty world.WorktreeAt.Name world.Head.Name with
      | Some _ -> { world with WorktreeAt = world.Head }
      | None -> world

  let run (behaviour: Behaviour) (scenario: Landing list) : Trace =
    let total = List.length scenario
    let final, idle =
      scenario
      |> List.indexed
      |> List.fold
        (fun (world, idle) (index, landing) ->
          let after = runLanding behaviour index world landing
          // A reset is considered after every landing; it acts only when the queue is empty, which is after the last one.
          let reset = idleReset behaviour (index = total - 1) after
          reset, (reset.WorktreeAt, reset.Head) :: idle)
        (start, [])
    { Scenario = scenario; Behaviour = behaviour; Final = final; WorktreeAfterEach = List.rev idle }

  [<RequireQualifiedAccess>]
  type Outcome' =
    | Holds
    | Violated of message: string

  /// Every run of tests was against a build holding the compiled content of the commit being landed.
  let soundVerification (trace: Trace) : Outcome' =
    match trace.Final.Verifications |> List.tryFind (fun v -> v.BuildHolds <> v.CommitCompiled) with
    | Some v ->
      Outcome'.Violated(
        sprintf "landing %d was verified against a build holding compiled content %d, but its commit holds %d" v.Landing v.BuildHolds v.CommitCompiled)
    | None -> Outcome'.Holds

  /// When nothing is in flight, the worktree is at the head.
  let idleAtTheHead (trace: Trace) : Outcome' =
    match trace.Final.WorktreeAt = trace.Final.Head with
    | true -> Outcome'.Holds
    | false ->
      Outcome'.Violated(sprintf "the worktree is at %s while the integration head is %s and nothing is in flight" trace.Final.WorktreeAt.Name trace.Final.Head.Name)

  /// A run of test-only landings that all land never rebuilds.
  let cheapPathSurvives (trace: Trace) : Outcome' =
    let onlyCheapAndGood =
      trace.Scenario |> List.forall (fun l -> l.Kind = Kind.TestOnly && l.Outcome = Outcome.Good)
    match onlyCheapAndGood, trace.Final.Rebuilds with
    | true, rebuilds when rebuilds > 0 -> Outcome'.Violated(sprintf "%d rebuilds for landings that only touch tests" rebuilds)
    | _ -> Outcome'.Holds

  let invariants : (string * (Trace -> Outcome')) list =
    [ "sound-verification", soundVerification
      "idle-at-the-head", idleAtTheHead
      "cheap-path-survives", cheapPathSurvives ]

  let violations (trace: Trace) : (string * string) list =
    invariants
    |> List.choose (fun (id, check) ->
      match check trace with
      | Outcome'.Holds -> None
      | Outcome'.Violated message -> Some(id, message))

  let fromSeed (seed: int) : Landing list =
    let rnd = Random(seed)
    let kinds = [| Kind.SourceChange; Kind.SourceChange; Kind.TestOnly |]
    let outcomes = [| Outcome.Good; Outcome.Good; Outcome.FailingTests; Outcome.BuildBreaks; Outcome.Withdrawn |]
    [ for _ in 1 .. rnd.Next(2, 9) ->
        { Kind = kinds.[rnd.Next kinds.Length]; Outcome = outcomes.[rnd.Next outcomes.Length] } ]

  /// A refused source landing, then a test-only landing that lands: the pre-fix daemon verified the second against the
  /// first's build.
  let refusedSourceThenTestOnly : Landing list =
    [ { Kind = Kind.SourceChange; Outcome = Outcome.FailingTests }
      { Kind = Kind.TestOnly; Outcome = Outcome.Good } ]

  /// A refused landing last: the worktree is left on it unless something puts it back.
  let refusedLast : Landing list =
    [ { Kind = Kind.SourceChange; Outcome = Outcome.Good }
      { Kind = Kind.SourceChange; Outcome = Outcome.FailingTests } ]

  let onlyTestsLanding : Landing list =
    [ { Kind = Kind.TestOnly; Outcome = Outcome.Good }
      { Kind = Kind.TestOnly; Outcome = Outcome.Good }
      { Kind = Kind.TestOnly; Outcome = Outcome.Good } ]

  /// A landing whose build fails, then a test-only landing: the failed build wrote the refused commit's DLLs, so the second must
  /// be verified against a rebuild.
  let refusedBuildThenTestOnly : Landing list =
    [ { Kind = Kind.SourceChange; Outcome = Outcome.BuildBreaks }
      { Kind = Kind.TestOnly; Outcome = Outcome.Good } ]
