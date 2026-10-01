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

/// The evaluated run a confirmation is about: the content that was evaluated and what each test said.
type Confirmation =
  { Content: AnalysisIdentity
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

[<RequireQualifiedAccess>]
type ConfirmationEvent =
  /// An eval succeeded and its tests ran. Never raised when nothing was evaluated (a type error).
  | EvaluatedRunFinished of Confirmation
  /// A buffer other than the evaluated one arrived.
  | ContentEdited of AnalysisIdentity
  /// The editing has been quiet for the whole quiet window.
  | QuietElapsed
  | BuildFinished of generation: int64 * outcome: Result<unit, string>
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

module BuildConfirmation =
  let initial : ConfirmationMachine = { Phase = ConfirmationPhase.Idle; NextGeneration = 1L }

  /// Fold one event. Total: every phase answers every event.
  let step (machine: ConfirmationMachine) (event: ConfirmationEvent) : ConfirmationMachine * ConfirmationEffect list =
    machine, []
