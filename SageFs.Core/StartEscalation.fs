namespace SageFs

open System

/// The last thing a starting worker said before it went quiet. A DU, not an option: "it has not
/// said anything yet" is a stated case, and it is the common one on a slow machine, where the
/// project load, the FSI host build and the host start all come before the first report.
[<RequireQualifiedAccess>]
type ProgressSeen =
  | NoneYet
  | Last of step: string

/// What one attempt to start a worker was given.
type AttemptRecord =
  { /// How long silence was allowed to last in this attempt.
    Budget: TimeSpan
    /// How long the attempt actually waited before it gave up.
    Waited: TimeSpan }

/// How patient one attempt to start a worker is. `Inactivity` is how long the worker may say nothing;
/// `Absolute` is the longest the whole attempt may take however much it says.
type StartBudget =
  { /// 1 for the first attempt.
    Attempt: int
    Inactivity: TimeSpan
    Absolute: TimeSpan
    /// What the attempts before this one were given and how long each waited, oldest first.
    Earlier: AttemptRecord list }

/// What this machine has taught SageFs about how long a start takes. A DU, not an option.
[<RequireQualifiedAccess>]
type Expectation =
  | NoHistory
  | Typically of duration: TimeSpan * samples: int

/// One attempt that ran out of patience.
type StartTimeout =
  { Stage: StartStage
    Budget: StartBudget
    Waited: TimeSpan
    Progress: ProgressSeen }

/// A start that was given up on, with everything a person needs to act on it: what it was waiting
/// for, how long, how many times, on what kind of machine, and what that machine usually does.
type StartFailure =
  { Stage: StartStage
    Attempts: int
    /// The silence each attempt allowed, oldest first. Strictly increasing.
    Budgets: TimeSpan list
    TotalWaited: TimeSpan
    Progress: ProgressSeen
    Tier: MachineTier
    Expectation: Expectation }

/// What to do after an attempt ran out of patience.
[<RequireQualifiedAccess>]
type EscalationStep =
  /// Try again with more patience. Never with less, never with the same.
  | RetryWith of StartBudget
  | GiveUp of StartFailure

module StartEscalation =

  /// How many attempts a start gets. With each one's silence allowance doubling, four attempts
  /// from the 30 second baseline allow 30, 60, 120 and 240 seconds of silence: the last is eight
  /// times the first, which is past the slowest start measured on any tier (docs/TROUBLESHOOTING.md),
  /// and a start that needs more than that is not slow, it is stuck.
  [<Literal>]
  let MaxAttempts = 4

  /// How much more patient each attempt is than the one before. Doubling, as a TCP sender doubles
  /// its retransmission timer after a timeout (RFC 6298 section 5.5).
  [<Literal>]
  let Growth = 2.0

  /// The patience of the first attempt: what the tier allows (`staticInactivity` is already scaled
  /// by the tier), or what this machine's own history says a start needs, whichever is longer, and
  /// never past `absolute`. A start is never given less patience than the machine has shown it needs.
  let firstBudget (history: StageHistory) (staticInactivity: TimeSpan) (absolute: TimeSpan) : StartBudget =
    let wanted =
      match history with
      | StageHistory.NeverSeen -> staticInactivity
      | StageHistory.Seen estimate -> max staticInactivity (StageEstimate.timeout estimate)
    { Attempt = 1; Inactivity = min wanted absolute; Absolute = absolute; Earlier = [] }

  /// The history as the failure message states it.
  let expectation (history: StageHistory) : Expectation =
    match history with
    | StageHistory.NeverSeen -> Expectation.NoHistory
    | StageHistory.Seen estimate -> Expectation.Typically (TimeSpan.FromMilliseconds estimate.SmoothedMs, estimate.Samples)

  /// What to do about a timeout. A retry always has strictly more patience than the attempt that just
  /// failed (`Growth` times, capped at `Absolute`); once the patience is at `Absolute`, or the
  /// attempts are used up, the start is given up with the whole story.
  let next (tier: MachineTier) (history: StageHistory) (timeout: StartTimeout) : EscalationStep =
    let budget = timeout.Budget
    let attempts = budget.Earlier @ [ { Budget = budget.Inactivity; Waited = timeout.Waited } ]
    let exhausted = budget.Attempt >= MaxAttempts || budget.Inactivity >= budget.Absolute
    match exhausted with
    | false ->
      let grown = TimeSpan.FromTicks(int64 (float budget.Inactivity.Ticks * Growth))
      EscalationStep.RetryWith
        { Attempt = budget.Attempt + 1
          Inactivity = min grown budget.Absolute
          Absolute = budget.Absolute
          Earlier = attempts }
    | true ->
      EscalationStep.GiveUp
        { Stage = timeout.Stage
          Attempts = budget.Attempt
          Budgets = attempts |> List.map (fun a -> a.Budget)
          TotalWaited = attempts |> List.sumBy (fun a -> a.Waited.Ticks) |> TimeSpan.FromTicks
          Progress = timeout.Progress
          Tier = tier
          Expectation = expectation history }

  let private seconds (span: TimeSpan) : string = sprintf "%.0f s" span.TotalSeconds

  let private describeProgress (progress: ProgressSeen) : string =
    match progress with
    | ProgressSeen.NoneYet ->
      "It had not reported any progress, so it was still loading the project, building or starting the FSI host."
    | ProgressSeen.Last step -> sprintf "Its last report was '%s'." step

  let private describeExpectation (tier: MachineTier) (expectation: Expectation) : string =
    match expectation with
    | Expectation.NoHistory ->
      sprintf "This machine counts as %s and has not finished a start yet, so SageFs has no history to compare with." (MachineTier.toString tier)
    | Expectation.Typically (duration, samples) ->
      sprintf "This machine counts as %s and a start here has taken about %s (over %d starts), so this one was far off that." (MachineTier.toString tier) (seconds duration) samples

  /// The failure in words a person or an agent can act on. Says what it was waiting for, how long and
  /// how many times, what the machine usually does, and what to do. Never just "faulted".
  let describe (failure: StartFailure) : string =
    sprintf
      "The session could not start: it gave up after %d attempt%s waiting %s for %s. Each attempt waited longer (%s). %s %s What to do: close whatever is using the CPU or the disk and run hard_reset_fsi_session with rebuild=true. To wait longer, set SAGEFS_WARMUP_INACTIVITY_SECONDS, or tell SageFs this is a slower machine with %s=%s. The worker's log (daemon log directory, workers/<session id>.log) says where it stopped."
      failure.Attempts
      (match failure.Attempts with 1 -> "" | _ -> "s")
      (seconds failure.TotalWaited)
      (StartStage.describe failure.Stage)
      (failure.Budgets |> List.map seconds |> String.concat ", ")
      (describeProgress failure.Progress)
      (describeExpectation failure.Tier failure.Expectation)
      MachineTier.envVar
      (match failure.Tier with
       | MachineTier.Minimal -> MachineTier.toString MachineTier.Minimal
       | tier -> MachineTier.toString (MachineTier.all |> List.item (min (MachineTier.rank tier + 1) (MachineTier.rank MachineTier.Minimal))))

  /// What a person is told while the session starts on a machine that is slow, so they know the wait
  /// is expected and roughly how long it is. `NotSlow` on a machine where a start takes a few seconds.
  [<RequireQualifiedAccess>]
  type SlowStartNotice =
    | NotSlow
    | Expected of tier: MachineTier * typically: Expectation

  /// A start is called slow, and announced as such, from this tier down.
  let private slowFrom = MachineTier.Constrained

  let notice (tier: MachineTier) (history: StageHistory) : SlowStartNotice =
    match MachineTier.rank tier >= MachineTier.rank slowFrom with
    | true -> SlowStartNotice.Expected (tier, expectation history)
    | false -> SlowStartNotice.NotSlow

  let describeNotice (notice: SlowStartNotice) : string =
    match notice with
    | SlowStartNotice.NotSlow -> ""
    | SlowStartNotice.Expected (tier, Expectation.NoHistory) ->
      sprintf "Starting. This machine counts as %s, so the first start can take a minute or more. That is normal here." (MachineTier.toString tier)
    | SlowStartNotice.Expected (tier, Expectation.Typically (duration, _)) ->
      sprintf "Starting. This machine counts as %s: a start here usually takes about %s. That is normal here." (MachineTier.toString tier) (seconds duration)
