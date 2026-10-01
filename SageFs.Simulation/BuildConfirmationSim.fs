namespace SageFs.Simulation

open SageFs.Features.LiveTesting

/// Deterministic Simulation Testing for the claim a keystroke's result cannot make by itself:
/// "a real build agrees with what the live eval said".
///
/// Evaluated runs finish, newer buffers arrive, the editing goes quiet, builds start and finish or
/// fail or hang, runs against the build finish, and each wait's deadline fires, in any order. The
/// fold is the REAL decision (`BuildConfirmation.step`); everything else here is the world around
/// it: which builds are really in flight, which finished late for content nobody cares about any
/// more, and what the newest content really is.
///
/// Same design rules as `PatchConfirmationSim`:
///   * Chaos is DATA: a `Scenario` is an ordered op list. Same ops, same trace. The op order IS
///     the scheduler: a `BuildDone` before a `Deadline` means the build answered in time.
///   * Ground truth is tracked by an independent fold that never reads the decision under test.
///   * Twins reintroduce the bugs the invariants exist to catch.
module BuildConfirmationSim =

  /// How a build ends.
  [<RequireQualifiedAccess>]
  type BuildEnds =
    | Builds
    | Fails

  /// Whether the tests, run against what the build produced, say what the eval said.
  [<RequireQualifiedAccess>]
  type Agreement =
    | Agrees
    | Differs

  [<RequireQualifiedAccess>]
  type Op =
    /// An eval of this content succeeded and its tests ran.
    | Evaluated of content: int
    /// An eval ran and found nothing to run (no tests selected).
    | EvaluatedNothing
    /// A buffer other than the evaluated one arrived.
    | Edited of content: int
    /// The editing has been quiet for the whole quiet window.
    | Quiet
    /// A started build (by order of start, modulo the builds not yet answered) answers.
    | BuildDone of which: int * ends: BuildEnds
    /// A run against a finished build answers.
    | RunDone of which: int * agreement: Agreement
    /// The wait for a started build's generation reaches its deadline.
    | Deadline of which: int

  type Scenario = { Seed: int; Ops: Op list }

  /// The tests every confirmation covers.
  let testIds : TestId list = [ for i in 0 .. 2 -> TestId.TestId (sprintf "t%d" i) ]

  let contentId (n: int) : AnalysisIdentity = AnalysisIdentity.AnalysisIdentity (sprintf "c%d" n)

  /// What the eval says about a test for a content: a pure function of both, so a scenario needs no
  /// verdict data. Some content is red for some tests, so a mark is never vacuous.
  let evaluatedVerdict (content: int) (test: int) : RunVerdict =
    match (content + test) % 4 with
    | 0 -> RunVerdict.Red (sprintf "t%d fails for c%d" test content)
    | _ -> RunVerdict.Green

  let private confirmationOf (content: int) : Confirmation =
    { Content = contentId content
      File = "Sim.fs"
      Evaluated = testIds |> List.mapi (fun i id -> id, evaluatedVerdict content i) |> Map.ofList }

  /// One step of the fold, with the world as it was.
  type Step =
    { Op: Op
      Event: ConfirmationEvent
      Before: ConfirmationMachine
      After: ConfirmationMachine
      Effects: ConfirmationEffect list
      /// The newest content any op has produced so far (-1 before any), by the simulation's own record.
      Latest: int }

  type Trace =
    { Scenario: Scenario
      Steps: Step list
      Final: ConfirmationMachine
      Reducer: string }

  [<RequireQualifiedAccess>]
  type private Policy =
    /// The real decision.
    | Real
    /// TWIN: an answer for any generation is taken as an answer for the one in flight.
    | AppliesStaleAnswers
    /// TWIN: a build starts the moment an evaluated run finishes, with no quiet window.
    | BuildsWithoutWaiting
    /// TWIN: a failed build is swallowed: the rows stay as they were.
    | SwallowsFailures

  type private World =
    { Machine: ConfirmationMachine
      /// Builds started and not yet answered, by generation, with what they were started for.
      Started: Map<int64, Confirmation>
      /// Finished builds whose run against them has not answered yet.
      Running: Map<int64, Confirmation>
      Latest: int
      Steps: Step list }

  let private initial : World =
    { Machine = BuildConfirmation.initial; Started = Map.empty; Running = Map.empty; Latest = -1; Steps = [] }

  let private activeGeneration (machine: ConfirmationMachine) : int64 voption =
    match machine.Phase with
    | ConfirmationPhase.Building (_, g)
    | ConfirmationPhase.RunningBuilt (_, g) -> ValueSome g
    | ConfirmationPhase.Idle
    | ConfirmationPhase.Quiet _ -> ValueNone

  /// The policy's version of the fold: the real step, or the real step with a bug put back.
  let private stepWith (policy: Policy) (machine: ConfirmationMachine) (event: ConfirmationEvent) : ConfirmationMachine * ConfirmationEffect list =
    match policy, event with
    | Policy.AppliesStaleAnswers, ConfirmationEvent.BuildFinished (_, outcome) ->
      match activeGeneration machine with
      | ValueSome g -> BuildConfirmation.step machine (ConfirmationEvent.BuildFinished (g, outcome))
      | ValueNone -> BuildConfirmation.step machine event
    | Policy.AppliesStaleAnswers, ConfirmationEvent.BuiltRunFinished (_, verdicts) ->
      match activeGeneration machine with
      | ValueSome g -> BuildConfirmation.step machine (ConfirmationEvent.BuiltRunFinished (g, verdicts))
      | ValueNone -> BuildConfirmation.step machine event
    | Policy.BuildsWithoutWaiting, ConfirmationEvent.EvaluatedRunFinished _ ->
      let afterEval, effects = BuildConfirmation.step machine event
      let started, more = BuildConfirmation.step afterEval ConfirmationEvent.QuietElapsed
      started, effects @ more
    | Policy.SwallowsFailures, ConfirmationEvent.BuildFinished (_, BuildAnswer.DidNotBuild _) ->
      let next, effects = BuildConfirmation.step machine event
      next,
      effects
      |> List.filter (function
        | ConfirmationEffect.Mark (_, marks) ->
          marks |> Map.forall (fun _ p -> match p with ResultProvenance.BuildDisagrees (BuildDisagreement.BuildFailed _) -> false | _ -> true)
        | _ -> true)
    | _ -> BuildConfirmation.step machine event

  let private nth (index: int) (keys: int64 list) : int64 voption =
    match keys with
    | [] -> ValueNone
    | _ -> ValueSome (List.item (((index % List.length keys) + List.length keys) % List.length keys) keys)

  /// The event an op raises in this world, if it raises one.
  let private eventOf (world: World) (op: Op) : ConfirmationEvent voption =
    match op with
    | Op.Evaluated content -> ValueSome (ConfirmationEvent.EvaluatedRunFinished (confirmationOf content))
    | Op.EvaluatedNothing ->
      ValueSome (ConfirmationEvent.EvaluatedRunFinished { Content = contentId (max 0 world.Latest); File = "Sim.fs"; Evaluated = Map.empty })
    | Op.Edited content -> ValueSome (ConfirmationEvent.ContentEdited (contentId content))
    | Op.Quiet -> ValueSome ConfirmationEvent.QuietElapsed
    | Op.BuildDone (which, ends) ->
      nth which (world.Started |> Map.toList |> List.map fst)
      |> ValueOption.map (fun g ->
        ConfirmationEvent.BuildFinished (g, (match ends with | BuildEnds.Builds -> BuildAnswer.Built | BuildEnds.Fails -> BuildAnswer.DidNotBuild "the build failed")))
    | Op.RunDone (which, agreement) ->
      nth which (world.Running |> Map.toList |> List.map fst)
      |> ValueOption.map (fun g ->
        let confirmation = Map.find g world.Running
        let built =
          confirmation.Evaluated
          |> Map.map (fun id verdict ->
            match agreement, id = List.head testIds with
            | Agreement.Differs, true ->
              (match verdict with
               | RunVerdict.Green -> RunVerdict.Red "the build says red"
               | _ -> RunVerdict.Green)
            | _ -> verdict)
        ConfirmationEvent.BuiltRunFinished (g, built))
    | Op.Deadline which ->
      nth which ((world.Started |> Map.toList |> List.map fst) @ (world.Running |> Map.toList |> List.map fst) |> List.distinct)
      |> ValueOption.map ConfirmationEvent.DeadlineReached

  /// Fold the effects into the world: what is now really started or running.
  let private applyEffects (world: World) (effects: ConfirmationEffect list) (event: ConfirmationEvent) : World =
    let withEffects =
      effects
      |> List.fold
        (fun w effect ->
          match effect with
          | ConfirmationEffect.StartBuild (g, confirmation) -> { w with Started = Map.add g confirmation w.Started }
          | ConfirmationEffect.RunAgainstBuild (g, _) ->
            match Map.tryFind g w.Started with
            | Some confirmation -> { w with Started = Map.remove g w.Started; Running = Map.add g confirmation w.Running }
            | None -> w
          | ConfirmationEffect.AbandonBuild _
          | ConfirmationEffect.StartQuietWindow
          | ConfirmationEffect.Mark _ -> w)
        world
    // The answer was delivered, so the thing that answered is no longer waiting. A build that just finished
    // has been moved to the runs waiting above, and a run that just finished leaves them here.
    match event with
    | ConfirmationEvent.BuildFinished (g, _) -> { withEffects with Started = Map.remove g withEffects.Started }
    | ConfirmationEvent.BuiltRunFinished (g, _) -> { withEffects with Running = Map.remove g withEffects.Running }
    | ConfirmationEvent.DeadlineReached g ->
      { withEffects with Started = Map.remove g withEffects.Started; Running = Map.remove g withEffects.Running }
    | _ -> withEffects

  let private runOp (policy: Policy) (world: World) (op: Op) : World =
    let latest =
      match op with
      | Op.Evaluated content | Op.Edited content -> content
      | _ -> world.Latest
    match eventOf world op with
    | ValueNone -> { world with Latest = latest }
    | ValueSome event ->
      let after, effects = stepWith policy world.Machine event
      let step =
        { Op = op; Event = event; Before = world.Machine; After = after; Effects = effects; Latest = latest }
      let world' = applyEffects { world with Machine = after; Latest = latest } effects event
      { world' with Steps = step :: world'.Steps }

  /// Fairness: the quiet window ends and every wait reaches its deadline, so nothing is left pending.
  let private drainOps (world: World) : Op list =
    let waits = (world.Started |> Map.toList |> List.map fst) @ (world.Running |> Map.toList |> List.map fst) |> List.distinct
    [ Op.Quiet; yield! waits |> List.mapi (fun i _ -> Op.Deadline i) ]

  let private runWith (name: string) (policy: Policy) (scenario: Scenario) : Trace =
    let folded = scenario.Ops |> List.fold (runOp policy) initial
    // Quiet first (a Quiet confirmation starts its build), then every started build's deadline, repeated
    // until nothing is waiting, so a long chain of newer content cannot leave one stuck.
    let rec drain (world: World) (rounds: int) =
      match rounds, world.Machine.Phase with
      | 0, _ -> world
      | _, ConfirmationPhase.Idle -> world
      | _, _ -> drain (drainOps world |> List.fold (runOp policy) world) (rounds - 1)
    let final = drain folded (List.length scenario.Ops + 4)
    { Scenario = scenario; Steps = List.rev final.Steps; Final = final.Machine; Reducer = name }

  /// The real decision.
  let run (scenario: Scenario) : Trace = runWith "real (BuildConfirmation.step)" Policy.Real scenario

  /// TWIN: answers for old generations are applied to the one in flight.
  let runAppliesStaleAnswers (scenario: Scenario) : Trace = runWith "twin-applies-stale-answers" Policy.AppliesStaleAnswers scenario

  /// TWIN: a build starts the moment an evaluated run finishes.
  let runBuildsWithoutWaiting (scenario: Scenario) : Trace = runWith "twin-builds-without-waiting" Policy.BuildsWithoutWaiting scenario

  /// TWIN: a failed build is swallowed.
  let runSwallowsFailures (scenario: Scenario) : Trace = runWith "twin-swallows-failures" Policy.SwallowsFailures scenario
