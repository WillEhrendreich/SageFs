namespace SageFs.Features.LiveTesting

/// A keystroke's tests run against code the live session EVALUATED, not against a real build. This
/// is the pure decision that follows an evaluated run: wait for the editing to go quiet, build the
/// content for real, run the same tests against what the compiler produced, and say whether the two
/// agree. It folds events into effects and owns no clock, no process and no state of the world:
/// the daemon feeds it, and `SageFs.Simulation.BuildConfirmationSim` drives the same fold under
/// seeded chaos.
///
/// What it promises, and what the simulation checks:
///   * the evaluated result is never held back (the mark `Evaluated` leaves in the same step);
///   * a typing burst costs one build, not one per pause (the quiet window restarts);
///   * at most one build is in flight, and a build of older content is abandoned;
///   * an answer about content that is no longer the latest is never applied;
///   * a build that fails or never answers is said so on the rows, loudly, never silence.

/// What a test run said, reduced to what a comparison needs.
[<RequireQualifiedAccess>]
type RunVerdict =
  | Green
  | Red of reason: string
  /// Skipped, not run, or never reported: no verdict to compare.
  | NoVerdict of reason: string

/// The evaluated run a confirmation is about: the content that was evaluated, the file it belongs to (a build
/// can only confirm text that is on disk) and what each test said.
type Confirmation =
  { Content: AnalysisIdentity
    File: string
    Evaluated: Map<TestId, RunVerdict> }

[<RequireQualifiedAccess>]
type ConfirmationPhase =
  | Idle
  /// An evaluated run finished; waiting for the editing to go quiet before a build is spent on it.
  | Quiet of Confirmation
  /// A build of exactly this content is running.
  | Building of Confirmation * generation: int64
  /// The build finished, and the same tests are running against what it produced.
  | RunningBuilt of Confirmation * generation: int64

type ConfirmationMachine =
  { Phase: ConfirmationPhase
    NextGeneration: int64 }

/// What the test run in flight ran against, which is what its results are marked with when they land.
[<RequireQualifiedAccess>]
type RunBasis =
  /// Binaries a build produced: the session's start, an explicit run, a rebuild.
  | Compiled
  /// Code evaluated in the live session from this content, running these tests.
  | Evaluated of content: AnalysisIdentity * tests: TestId list
  /// The run against the build of this confirmation generation.
  | ConfirmingBuild of generation: int64

/// How the build a confirmation asked for ended.
[<RequireQualifiedAccess>]
type BuildAnswer =
  /// The project built, and the session came back up on what it made.
  | Built
  /// It did not, and this is why, in the compiler's or the session's own words.
  | DidNotBuild of message: string

[<RequireQualifiedAccess>]
type ConfirmationEvent =
  /// An eval succeeded and its tests ran. Never raised when nothing was evaluated (a type error).
  | EvaluatedRunFinished of Confirmation
  /// A buffer other than the evaluated one arrived.
  | ContentEdited of AnalysisIdentity
  /// The editing has been quiet for the whole quiet window.
  | QuietElapsed
  | BuildFinished of generation: int64 * answer: BuildAnswer
  | BuiltRunFinished of generation: int64 * verdicts: Map<TestId, RunVerdict>
  /// The wait for this generation's build or built run has gone on too long.
  | DeadlineReached of generation: int64

[<RequireQualifiedAccess>]
type ConfirmationEffect =
  /// Start (or restart) the quiet window.
  | StartQuietWindow
  | StartBuild of generation: int64 * Confirmation
  | AbandonBuild of generation: int64
  | RunAgainstBuild of generation: int64 * tests: TestId list
  /// What the rows of these tests say now, for this content.
  | Mark of content: AnalysisIdentity * provenance: Map<TestId, ResultProvenance>

module RunVerdict =
  /// What a test run said, reduced to what a comparison needs.
  let ofResult (result: TestResult) : RunVerdict =
    match result with
    | TestResult.Passed _ -> RunVerdict.Green
    | TestResult.Failed (failure, _) ->
      RunVerdict.Red (
        match failure with
        | TestFailure.AssertionFailed message -> message
        | TestFailure.ExceptionThrown (message, _) -> message
        | TestFailure.TimedOut after -> sprintf "timed out after %.0fs" after.TotalSeconds)
    | TestResult.Skipped reason -> RunVerdict.NoVerdict reason
    | TestResult.NotRun -> RunVerdict.NoVerdict "it did not run"
    | TestResult.NoResult reason -> RunVerdict.NoVerdict (NoResultReason.describe reason)

  /// Whether two verdicts say the same thing. A red is a red whatever its reason says: the question is
  /// whether the build agrees the test fails, not whether it words the failure the same.
  let agree (evaluated: RunVerdict) (built: RunVerdict) : bool =
    match evaluated, built with
    | RunVerdict.Green, RunVerdict.Green
    | RunVerdict.Red _, RunVerdict.Red _
    | RunVerdict.NoVerdict _, RunVerdict.NoVerdict _ -> true
    | _ -> false

  /// One phrase for a row to show.
  let describe (verdict: RunVerdict) : string =
    match verdict with
    | RunVerdict.Green -> "passed"
    | RunVerdict.Red reason -> sprintf "failed (%s)" reason
    | RunVerdict.NoVerdict reason -> sprintf "no verdict (%s)" reason

module BuildConfirmation =
  let initial : ConfirmationMachine = { Phase = ConfirmationPhase.Idle; NextGeneration = 1L }

  let private markAll (confirmation: Confirmation) (provenance: ResultProvenance) : Map<TestId, ResultProvenance> =
    confirmation.Evaluated |> Map.map (fun _ _ -> provenance)

  /// What each row says once the run against the build has answered: agreed, or both verdicts.
  let private judge (confirmation: Confirmation) (built: Map<TestId, RunVerdict>) : Map<TestId, ResultProvenance> =
    confirmation.Evaluated
    |> Map.map (fun id evaluated ->
      let answered = Map.tryFind id built |> Option.defaultValue (RunVerdict.NoVerdict "it was not run against the build")
      match RunVerdict.agree evaluated answered with
      | true -> ResultProvenance.VerifiedByBuild
      | false ->
        ResultProvenance.BuildDisagrees (
          BuildDisagreement.ResultDiffers (RunVerdict.describe evaluated, RunVerdict.describe answered)))

  /// Fold one event. Total: every phase answers every event, and an answer about a generation that is not the
  /// one in flight is no answer at all.
  let step (machine: ConfirmationMachine) (event: ConfirmationEvent) : ConfirmationMachine * ConfirmationEffect list =
    let evaluatedMarks (confirmation: Confirmation) =
      ConfirmationEffect.Mark (confirmation.Content, markAll confirmation ResultProvenance.Evaluated)
    match machine.Phase, event with
    // A run that evaluated nothing has nothing to confirm.
    | _, ConfirmationEvent.EvaluatedRunFinished confirmation when Map.isEmpty confirmation.Evaluated -> machine, []
    // The same content again, while a build of it is already running: that build still stands. The rows are
    // evaluated again (the run was repeated), and the newer verdicts are what the build is compared with.
    | ConfirmationPhase.Building (current, generation), ConfirmationEvent.EvaluatedRunFinished confirmation
        when current.Content = confirmation.Content ->
      { machine with Phase = ConfirmationPhase.Building (confirmation, generation) }, [ evaluatedMarks confirmation ]
    | ConfirmationPhase.RunningBuilt (current, generation), ConfirmationEvent.EvaluatedRunFinished confirmation
        when current.Content = confirmation.Content ->
      { machine with Phase = ConfirmationPhase.RunningBuilt (confirmation, generation) }, [ evaluatedMarks confirmation ]
    // Newer content replaces whatever was waiting; a build of older content is abandoned. The evaluated verdict
    // is marked in this same step: confirming it never holds it back.
    | ConfirmationPhase.Building (_, generation), ConfirmationEvent.EvaluatedRunFinished confirmation
    | ConfirmationPhase.RunningBuilt (_, generation), ConfirmationEvent.EvaluatedRunFinished confirmation ->
      { machine with Phase = ConfirmationPhase.Quiet confirmation },
      [ ConfirmationEffect.AbandonBuild generation; evaluatedMarks confirmation; ConfirmationEffect.StartQuietWindow ]
    | ConfirmationPhase.Idle, ConfirmationEvent.EvaluatedRunFinished confirmation
    | ConfirmationPhase.Quiet _, ConfirmationEvent.EvaluatedRunFinished confirmation ->
      { machine with Phase = ConfirmationPhase.Quiet confirmation }, [ evaluatedMarks confirmation; ConfirmationEffect.StartQuietWindow ]
    // A buffer other than the evaluated one: the text under confirmation is not the text being edited.
    | ConfirmationPhase.Quiet current, ConfirmationEvent.ContentEdited content when current.Content <> content ->
      { machine with Phase = ConfirmationPhase.Idle }, []
    | ConfirmationPhase.Building (current, generation), ConfirmationEvent.ContentEdited content
    | ConfirmationPhase.RunningBuilt (current, generation), ConfirmationEvent.ContentEdited content when current.Content <> content ->
      { machine with Phase = ConfirmationPhase.Idle }, [ ConfirmationEffect.AbandonBuild generation ]
    // The editing went quiet: one build, of exactly the content that was evaluated.
    | ConfirmationPhase.Quiet confirmation, ConfirmationEvent.QuietElapsed ->
      let generation = machine.NextGeneration
      { Phase = ConfirmationPhase.Building (confirmation, generation); NextGeneration = generation + 1L },
      [ ConfirmationEffect.StartBuild (generation, confirmation) ]
    | ConfirmationPhase.Building (confirmation, generation), ConfirmationEvent.BuildFinished (answered, BuildAnswer.Built) when answered = generation ->
      { machine with Phase = ConfirmationPhase.RunningBuilt (confirmation, generation) },
      [ ConfirmationEffect.RunAgainstBuild (generation, confirmation.Evaluated |> Map.keys |> List.ofSeq) ]
    | ConfirmationPhase.Building (confirmation, generation), ConfirmationEvent.BuildFinished (answered, BuildAnswer.DidNotBuild message) when answered = generation ->
      // Answered, so the deadline that was watching this generation has nothing left to watch.
      { machine with Phase = ConfirmationPhase.Idle },
      [ ConfirmationEffect.Mark (confirmation.Content, markAll confirmation (ResultProvenance.BuildDisagrees (BuildDisagreement.BuildFailed message)))
        ConfirmationEffect.AbandonBuild generation ]
    | ConfirmationPhase.RunningBuilt (confirmation, generation), ConfirmationEvent.BuiltRunFinished (answered, verdicts) when answered = generation ->
      { machine with Phase = ConfirmationPhase.Idle },
      [ ConfirmationEffect.Mark (confirmation.Content, judge confirmation verdicts)
        ConfirmationEffect.AbandonBuild generation ]
    // The wait went on too long: the rows say so, rather than looking evaluated forever.
    | ConfirmationPhase.Building (confirmation, generation), ConfirmationEvent.DeadlineReached answered
    | ConfirmationPhase.RunningBuilt (confirmation, generation), ConfirmationEvent.DeadlineReached answered when answered = generation ->
      { machine with Phase = ConfirmationPhase.Idle },
      [ ConfirmationEffect.AbandonBuild generation
        ConfirmationEffect.Mark (confirmation.Content, markAll confirmation (ResultProvenance.BuildDisagrees (BuildDisagreement.BuildUnanswered "its deadline"))) ]
    | _ -> machine, []
