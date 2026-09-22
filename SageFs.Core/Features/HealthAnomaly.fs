namespace SageFs.Features

open System

/// Classical anomaly detection over the daemon's own telemetry: EWMA plus a
/// CUSUM-style change detector, so a degraded daemon can say so in its own
/// status instead of going quiet. Built after two real incidents nobody
/// noticed: Kestrel's request thread stuck for a full minute at a time while
/// `/health` and `/api/sessions` timed out, and a worker's RSS climbing to
/// 37GB before anyone looked. The daemon already collects both numbers. It
/// has no notion of "this is abnormal for me" — that's what this module is.
///
/// NOT WIRED IN YET. This is a pure module: feed it observations, get a
/// verdict back, nothing else. Where it should be hooked up, once wired:
///   - `SageFs/DaemonMode.fs`: one `HealthAnomaly.Detector` per tracked
///     signal, fed on the same tick that already samples `/health` latency,
///     mailbox queue depth and warmup duration; the verdict folds into
///     `HealthSnapshot` (DaemonHealth.fs) so `OverallHealth` can go
///     `Degraded`/`Unhealthy` on a `Broken` verdict, not just on a faulted
///     session.
///   - `SageFs.Core/SessionManager.fs`: worker RSS, sampled wherever the
///     manager already polls process stats for the standby pool / liveness
///     checks.
///   - `SageFs.Core/AppState.fs`: eval latency, sampled at the same point
///     `EvalCompleted` is published.
///   - Surfacing: `sprintf`-free, structured evidence (`SignalEvidence`)
///     should flow into `SageFsError`/`describeForAgent` (see the roast's
///     §10 finding that the error algebra exists and nothing calls it) and
///     into the dashboard's health panel, not a second bespoke formatter.
///
/// THE MATH, AND WHY THIS SHAPE:
/// Two running statistics per signal, both from a single pass, O(1) memory:
///   - An EWMA mean/variance is "what normal looks like for this signal, on
///     this machine, right now" — no baseline is hand-configured, and it
///     adapts if a genuinely new normal sets in (a bigger project really is
///     slower forever, and the detector has to accept that instead of
///     alarming forever).
///   - A CUSUM (Page's test) over the standardized residual is "has this
///     signal been consistently off from that normal for a while" — CUSUM
///     is the classical answer to "small sustained shift" vs "one-off noise"
///     because it accumulates evidence across samples instead of judging
///     each one alone.
/// Standardizing against the EWMA's own standard deviation (z-score) is what
/// makes ONE set of constants (slack, drift/break thresholds) work whether
/// the signal is milliseconds, megabytes or a queue depth — scale enters
/// only through the learned mean/stddev, never through the thresholds. A
/// signal that is a flat constant with no natural noise (a fixed value that
/// never varies) still gets a usable stddev floor: `MinRelativeStdDev` is a
/// FRACTION of the mean's magnitude, not an absolute epsilon, so the floor
/// itself stays scale-free too.
///
/// The baseline update AND the CUSUM accumulator are fed the SAME Winsorized
/// (clipped) z-score, not the raw one. This one choice is what makes every
/// other property hold at once:
///   - A single wild sample (a spike) can move the mean by at most
///     `Alpha * ClipSigmas` standard deviations — bounded, so it can't
///     poison "normal" for the rest of the signal's life.
///   - The CUSUM accumulator grows by at most that same bounded amount per
///     sample, which is what bounds its DECAY after a real incident ends —
///     "recovery clears it" is the same mechanism running in reverse, not a
///     separate reset rule. (The bigger the incident's peak-to-normal
///     ratio, the more samples decay takes; that is inherent, not a knob to
///     chase away.)
///
/// The variance update gets a WIDER clip than the mean/CUSUM one
/// (`VarianceClipSigmas`, several times `ClipSigmas`). This is the fix for a
/// real self-poisoning failure mode found while proving this module against
/// wide, randomized property tests: if the warmup window's small sample
/// happens to slightly UNDERESTIMATE the true noise, the standard deviation
/// starts out too small — and clipping the variance update to that same
/// too-small `ClipSigmas` window means ordinary, perfectly normal noise
/// keeps getting Winsorized down before it can push the variance estimate
/// back up. The detector gets permanently stuck believing its own bad early
/// guess, because the very defense meant to protect it from outliers also
/// blocks it from ever re-learning from ordinary data. Widening ONLY the
/// variance update's clip breaks that feedback loop: normal noise several
/// times wider than the current (possibly wrong) estimate can still recalibrate
/// it within a handful of samples, while a truly extreme sample is still
/// bounded, just at a looser bound.
///
/// The breach/clear rule is a three-way hysteresis on the CUSUM magnitude,
/// not a single threshold: at or above `DriftThreshold` the breach streak
/// grows; at or below `DriftThreshold * ClearFactor` the streak (and the
/// CUSUM itself) is reset to zero — a genuine recovery; strictly between the
/// two, the streak just holds where it is. Collapsing this to two states
/// (either "breach" or "reset", using the SAME threshold for both) was an
/// earlier bug here: `ClearFactor`'s only job was to compute a slightly
/// lower re-arm bar, but with no separate breach-entry bar, three ordinary
/// noisy samples that happened to nudge the CUSUM above that low bar were
/// enough to read as `Drifting` on a perfectly flat signal. A `Drifting` or
/// `Broken` verdict must mean the CUSUM cleared the real, higher
/// `DriftThreshold` for `MinSustainSamples` in a row — not merely that it
/// drifted above the RESET bar.
///
/// A slow drift produces a small unclipped z each sample (the EWMA mean is
/// chasing it too), so its CUSUM grows slowly and it reads as `Drifting`
/// well before it might ever reach `Broken`; a sudden step produces a large
/// z the mean cannot immediately absorb, so its CUSUM crosses the break
/// threshold within a handful of samples. Note what this module does NOT
/// claim: a drift that is sustained for long enough, however gently, will
/// eventually cross any fixed CUSUM threshold — that is inherent to CUSUM,
/// not a bug, and it is arguably correct (a metric that never stops
/// climbing is a real, escalating problem). The actual guarantee is
/// ORDERING: a drift is read as `Drifting` before it is ever read as
/// `Broken`, so severity escalates gradually instead of snapping straight
/// to the page-someone case the way a sudden step does.
///
/// The `MinSustainSamples` streak gate exists to keep a single spike from
/// firing at all — without it, one giant sample's CUSUM contribution alone
/// can already exceed `BreakThreshold`. Requiring the breach to persist
/// across several consecutive samples is what turns "instantaneous
/// magnitude" into "sustained for a while", which is the actual claim a
/// `Broken`/`Drifting` verdict makes.
///
/// SCALE-FREE VS PER-SIGNAL: `Params` is one record shared by every signal
/// by default (`defaultParams`) because standardization makes the
/// thresholds scale-free. A signal whose failure mode genuinely needs
/// different sensitivity (e.g. `MinSustainSamples` for a signal sampled once
/// a minute vs one sampled every eval) can still override `Params`
/// per-signal — the API takes `Params` explicitly rather than hiding it, so
/// that override is a caller decision, not a fork of this module.
///
/// KNOWN LIMITATION: a periodic signal whose full swing is wider than what
/// the warmup window observed (a sawtooth reset larger than one warmup's
/// worth of history) can trip transient `Drifting` verdicts once per cycle
/// until enough history accumulates that the EWMA variance reflects the
/// whole cycle, not just the rising part the warmup window happened to see.
/// Each such transient clears itself within a few samples (it is not a
/// stuck alarm) — it is a real but minor false-positive rate on genuinely
/// periodic signals, not a violation of the no-fire-on-flat-noise or
/// recovery-clears-it invariants this module is held to.
module HealthAnomaly =

  /// The signals this module is shaped for. `Custom` covers anything else a
  /// caller wants to track without widening this type — see AGENTS.md: a
  /// closed set becomes a DU, but this one is deliberately still open at the
  /// edge because "what telemetry the daemon collects" keeps growing.
  [<RequireQualifiedAccess>]
  type SignalId =
    | HealthLatency
    | EvalLatency
    | WorkerRss
    | MailboxQueueDepth
    | WarmupDuration
    | Custom of string

  /// Exhaustive name for a signal — the one place a `SignalId` becomes text.
  let signalName =
    function
    | SignalId.HealthLatency -> "health_latency"
    | SignalId.EvalLatency -> "eval_latency"
    | SignalId.WorkerRss -> "worker_rss"
    | SignalId.MailboxQueueDepth -> "mailbox_queue_depth"
    | SignalId.WarmupDuration -> "warmup_duration"
    | SignalId.Custom name -> name

  /// Which way the signal moved. Carried explicitly so a message can say
  /// "went from 4ms to 900ms" (Increased) rather than just "changed".
  [<RequireQualifiedAccess>]
  type SignalDirection =
    | Increased
    | Decreased

  /// One observation: a value at a point in time. The caller decides the
  /// sampling cadence; this module has no clock of its own.
  type Observation = { At: DateTimeOffset; Value: float }

  /// The evidence behind a `Drifting` or `Broken` verdict: what the signal
  /// was, what normal looks like for it, how far out it is, and for how
  /// long. This is what turns "anomaly detected" into "my own health
  /// endpoint went from 4ms to 900ms over the last minute".
  type SignalEvidence = {
    Signal: SignalId
    ObservedAt: DateTimeOffset
    ObservedValue: float
    /// The learned baseline BEFORE this observation was folded in — "what
    /// normal looked like" at the moment the anomaly was seen.
    BaselineMean: float
    BaselineStdDev: float
    Direction: SignalDirection
    /// Raw (unclipped) standardized deviation of this observation from the
    /// baseline — the honest number for a human-facing message, even though
    /// the detector's own internals use a clipped version of it.
    DeviationInSigmas: float
    /// How long the current breach has been continuously in progress.
    SustainedFor: TimeSpan
    /// How many consecutive observations have been in breach, including
    /// this one.
    SamplesSustained: int
  }

  /// The verdict. Not a bool, not an `Option` standing in for "nothing
  /// wrong": every non-Normal case carries the evidence that justifies it.
  [<RequireQualifiedAccess>]
  type Verdict =
    /// Not enough observations yet to know what normal looks like. A cold
    /// daemon reports this, never `Normal`, for its first `MinWarmupSamples`
    /// observations — reporting `Normal` would be a claim about a baseline
    /// that does not exist yet.
    | InsufficientHistory
    | Normal
    /// Sustained deviation below the break threshold: worth watching, not
    /// (yet, or perhaps ever, if it's a slow drift into a stable new normal)
    /// worth paging anyone.
    | Drifting of SignalEvidence
    /// Sustained deviation at or above the break threshold: this is the
    /// "say so in your own status" case.
    | Broken of SignalEvidence

  /// Tuning constants. Every one of them operates in standard-deviation
  /// units except `MinWarmupSamples`, `MinSustainSamples` (sample counts)
  /// and `MinRelativeStdDev` (a fraction of the mean) — see the module doc
  /// comment for why that makes one `Params` value scale-free across very
  /// differently-scaled signals.
  type Params = {
    /// EWMA smoothing factor for the learned mean/variance. Effective memory
    /// is roughly `2/Alpha - 1` samples.
    Alpha: float
    /// Robust ("Winsorized") clip applied to the standardized residual
    /// before it updates the baseline mean or feeds the CUSUM accumulator.
    ClipSigmas: float
    /// A separate, wider clip for the VARIANCE update only — see the module
    /// doc comment for why the variance needs more room than the mean/CUSUM
    /// to recalibrate away from a possibly-too-small warmup estimate.
    VarianceClipSigmas: float
    /// CUSUM slack (Page's `k`), in standard deviations — the size of
    /// deviation the detector shrugs off as noise before it starts
    /// accumulating evidence in either direction.
    Slack: float
    /// CUSUM magnitude above which a sustained breach reads as `Drifting`.
    DriftThreshold: float
    /// CUSUM magnitude above which a sustained breach reads as `Broken`.
    BreakThreshold: float
    /// A breach clears (streak resets, CUSUM zeroed) once its magnitude
    /// falls to `DriftThreshold * ClearFactor` or below. Strictly less than
    /// 1.0 so a value hovering right at the drift threshold doesn't flap.
    ClearFactor: float
    /// Observations needed before any verdict other than
    /// `InsufficientHistory` is possible.
    MinWarmupSamples: int
    /// Consecutive breaching observations needed before a verdict leaves
    /// `Normal` — the mechanism that keeps a single spike from firing (see
    /// module doc comment).
    MinSustainSamples: int
    /// Floor for the learned standard deviation, as a fraction of the
    /// learned mean's magnitude, so a signal with genuinely near-zero noise
    /// doesn't divide by (near) zero.
    MinRelativeStdDev: float
  }

  /// One `Params` value for every signal, because standardizing against the
  /// learned mean/stddev already makes these constants scale-free. Override
  /// per-signal only when a signal's failure mode genuinely needs different
  /// sensitivity (see module doc comment).
  let defaultParams: Params = {
    Alpha = 0.15
    ClipSigmas = 4.0
    VarianceClipSigmas = 10.0
    Slack = 1.0
    DriftThreshold = 5.0
    BreakThreshold = 10.0
    ClearFactor = 0.5
    MinWarmupSamples = 20
    MinSustainSamples = 4
    MinRelativeStdDev = 1e-3
  }

  /// Fixed-size accumulator per signal: bounded memory no matter how long
  /// the daemon runs. During warmup (`Count <= Params.MinWarmupSamples`),
  /// `Variance` holds Welford's running M2 (sum of squared deviations from
  /// the running mean), not the variance itself — it is converted to the
  /// unbiased sample variance exactly once, the sample after warmup ends,
  /// and is an EWMA variance from then on. Reusing the field instead of
  /// carrying two never-both-populated fields keeps the type honest about
  /// there being exactly one number here at a time.
  type State = {
    Count: int
    Mean: float
    Variance: float
    CusumPos: float
    CusumNeg: float
    BreachStreak: int
    BreachSince: DateTimeOffset option
  }

  /// The detector's state before it has seen anything.
  let initial: State = {
    Count = 0
    Mean = 0.0
    Variance = 0.0
    CusumPos = 0.0
    CusumNeg = 0.0
    BreachStreak = 0
    BreachSince = None
  }

  let private clamp lo hi v = max lo (min hi v)

  let private stdDevOf (p: Params) (mean: float) (variance: float) =
    let learned = sqrt (max 0.0 variance)
    let floor = max 1e-9 (abs mean * p.MinRelativeStdDev)
    max learned floor

  /// Fold one observation through the detector, returning the next state and
  /// the verdict for THIS observation. Pure: same inputs, same outputs,
  /// every time — this is what makes the detector replayable and DST-able.
  let step (p: Params) (signal: SignalId) (obs: Observation) (s: State) : State * Verdict =
    let count = s.Count + 1
    if count <= p.MinWarmupSamples then
      // Welford's online mean/M2: honest running stats. An EWMA variance
      // seeded at zero would read as artificially tiny for its first few
      // samples and hand back inflated z-scores the moment warmup ends —
      // Welford gives the first real verdict an honestly-learned baseline
      // instead of one still ramping up from zero.
      let delta = obs.Value - s.Mean
      let mean' = s.Mean + delta / float count
      let delta2 = obs.Value - mean'
      let m2' = s.Variance + delta * delta2
      { s with Count = count; Mean = mean'; Variance = m2' }, Verdict.InsufficientHistory
    else
      // The sample right after warmup converts Welford's M2 into the
      // unbiased sample variance once; every sample after that is already
      // holding a proper EWMA variance and this is a no-op.
      let priorVariance =
        if s.Count = p.MinWarmupSamples then
          s.Variance / float (max 1 (s.Count - 1))
        else
          s.Variance
      let sigma = stdDevOf p s.Mean priorVariance
      let rawResidual = obs.Value - s.Mean
      let rawZ = rawResidual / sigma

      // The Winsorized z: the same clipped value feeds the baseline update
      // AND the CUSUM accumulator. See the module doc comment for why this
      // single choice bounds spike poisoning, break-detection speed, AND
      // recovery decay all at once.
      let z = clamp (-p.ClipSigmas) p.ClipSigmas rawZ

      let cusumPos = max 0.0 (s.CusumPos + z - p.Slack)
      let cusumNeg = min 0.0 (s.CusumNeg + z + p.Slack)
      let magnitude = max cusumPos -cusumNeg

      let mean' = s.Mean + p.Alpha * z * sigma
      // Variance recalibration uses its OWN, wider clip on the raw residual
      // — see the module doc comment for why this must not reuse `z`/`sigma`
      // from the mean/CUSUM path (that clip is what caused the self-poisoning
      // bug this comment documents).
      let varianceClip = p.VarianceClipSigmas * sigma
      let varianceResidual = clamp -varianceClip varianceClip rawResidual
      let variance' = (1.0 - p.Alpha) * (priorVariance + p.Alpha * varianceResidual * varianceResidual)

      let clearThreshold = p.DriftThreshold * p.ClearFactor
      let breachStreak', breachSince', cusumPos', cusumNeg' =
        if magnitude <= clearThreshold then
          // Full recovery: reset everything, no lingering bias into the next episode.
          0, None, 0.0, 0.0
        elif magnitude >= p.DriftThreshold then
          // Real breach growth.
          let since =
            match s.BreachSince with
            | Some existing -> Some existing
            | None -> Some obs.At
          s.BreachStreak + 1, since, cusumPos, cusumNeg
        else
          // Hysteresis dead zone: neither a fresh breach nor a full clear —
          // hold the streak where it is and let CUSUM keep evolving.
          s.BreachStreak, s.BreachSince, cusumPos, cusumNeg

      let s' = {
        Count = count
        Mean = mean'
        Variance = variance'
        CusumPos = cusumPos'
        CusumNeg = cusumNeg'
        BreachStreak = breachStreak'
        BreachSince = breachSince'
      }

      let verdict =
        if breachStreak' < p.MinSustainSamples then
          Verdict.Normal
        else
          let direction =
            if rawZ >= 0.0 then SignalDirection.Increased else SignalDirection.Decreased
          let sustainedFor =
            match breachSince' with
            | Some since -> obs.At - since
            | None -> TimeSpan.Zero
          let evidence = {
            Signal = signal
            ObservedAt = obs.At
            ObservedValue = obs.Value
            BaselineMean = s.Mean
            BaselineStdDev = sigma
            Direction = direction
            DeviationInSigmas = rawZ
            SustainedFor = sustainedFor
            SamplesSustained = breachStreak'
          }
          if magnitude >= p.BreakThreshold then Verdict.Broken evidence else Verdict.Drifting evidence

      s', verdict

  /// Fold a whole observation series through the detector with
  /// `defaultParams`, starting from `initial` — the convenience form for
  /// tests and callers that don't need to keep the intermediate state
  /// themselves. Returns one verdict per observation, in order.
  let evaluateSeries (signal: SignalId) (observations: Observation list) : Verdict list =
    observations
    |> List.fold
      (fun (s, verdicts) obs ->
        let s', verdict = step defaultParams signal obs s
        s', verdict :: verdicts)
      (initial, [])
    |> snd
    |> List.rev

  /// One line for a human or an agent: exactly the shape the module exists
  /// to produce ("my own health endpoint went from 4ms to 900ms over the
  /// last minute"), not "anomaly detected".
  let describe (verdict: Verdict) : string option =
    let phraseFor (kind: string) (e: SignalEvidence) =
      let directionWord =
        match e.Direction with
        | SignalDirection.Increased -> "went up"
        | SignalDirection.Decreased -> "went down"
      sprintf
        "%s %s %s: baseline %.3g (±%.3g), now %.3g (%.1fσ), sustained for %s over %d samples"
        (signalName e.Signal)
        kind
        directionWord
        e.BaselineMean
        e.BaselineStdDev
        e.ObservedValue
        e.DeviationInSigmas
        (e.SustainedFor.ToString("g"))
        e.SamplesSustained
    match verdict with
    | Verdict.InsufficientHistory
    | Verdict.Normal -> None
    | Verdict.Drifting e -> Some(phraseFor "is drifting" e)
    | Verdict.Broken e -> Some(phraseFor "is broken" e)
