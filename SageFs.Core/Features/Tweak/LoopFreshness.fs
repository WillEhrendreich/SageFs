/// How current the values in the live bindings pane are, said from what the test loop is doing (docs/plans/live-bindings-watch-view.md,
/// step 10). The loop already knows (`LiveTestActivity`); the pane borrows its answer and names it in a word, a sentence for the tooltip
/// and a semantic tone, so the pane never says "current" while the loop is held back by compile errors.
module SageFs.Features.Tweak.LoopFreshness

open SageFs.Features.LiveTestActivity

/// What the pane says about its values, from the loop.
[<RequireQualifiedAccess>]
type Freshness =
  /// Tests are running or the session is rebuilding to run them: the values are from the last eval until the loop settles.
  | Updating
  /// The loop settled: the values are the ones the last eval left.
  | Current
  /// A file has compile errors, so the loop is held back and the values are from the last good eval.
  | HeldByErrors of file: string * errorCount: int
  /// The rebuild failed, so the loop could not re-run and the values are from the last good eval.
  | HeldByRebuild of reason: string

/// The semantic colour a freshness takes, by meaning. Red is for failure only.
[<RequireQualifiedAccess>]
type FreshnessTone =
  | Current
  | Moving
  | Held
  | Failed

/// What the chip draws.
type Chip = { Word: string; Detail: string; Tone: FreshnessTone }

/// What the loop says about the values, or nothing when the loop has no say (live testing is off, or no tests are known yet).
let ofActivity (activity: LiveTestActivity) : Freshness option = None

let word (freshness: Freshness) : string = ""

let detail (freshness: Freshness) : string = ""

let tone (freshness: Freshness) : FreshnessTone = FreshnessTone.Current

/// The chip for an activity, or nothing.
let chipOf (activity: LiveTestActivity) : Chip option = None
