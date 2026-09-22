namespace SageFs.Simulation

open SageFs.Features.HealthAnomaly
open SageFs.Simulation.HealthAnomalySim

/// Invariants over `HealthAnomalySim`'s trace. Each `State` in the trace is
/// "after processing event `i`"; `segments` pairs each event with exactly
/// the (observation, verdict) samples it produced, by diffing that state's
/// `History` against the previous one's.
module HealthAnomalySimInvariants =

  type Violation = { EventIndex: int; Event: ShapeEvent; Why: string }

  let private fires =
    function
    | Verdict.Drifting _
    | Verdict.Broken _ -> true
    | Verdict.Normal
    | Verdict.InsufficientHistory -> false

  let private isBroken =
    function
    | Verdict.Broken _ -> true
    | _ -> false

  /// `states` is `List.scan (step behavior) initial events` — one more
  /// state than there are events, `states.[0]` being the pre-event initial
  /// state. Pairs each event with the slice of `History` it alone produced.
  let segments (states: State list) (events: ShapeEvent list) : (int * ShapeEvent * (Observation * Verdict) list) list =
    events
    |> List.indexed
    |> List.map (fun (i, ev) ->
      let before = states.[i].History.Length
      let after = states.[i + 1].History
      i, ev, after |> List.skip before)

  /// A `Flat` segment never fires — no exceptions once its samples are past
  /// warmup. `InsufficientHistory` samples (only ever the first
  /// `MinWarmupSamples` of the whole scenario, always inside the very first
  /// `Flat`) are not failures either way, since they carry no verdict about
  /// normal vs abnormal.
  ///
  /// `isSettling` marks the `Flat` segments that immediately follow a
  /// `Drift`/`Sawtooth` and inherit that segment's still-in-progress breach
  /// state — those are genuinely still settling into whatever level the
  /// prior segment left the baseline at (the same shape as a legitimate
  /// scale change, not a false alarm on quiet data), so they are held to
  /// `flatSegmentsEventuallySettle` instead of this stricter check.
  let flatSegmentsNeverFire (isSettling: int -> bool) (states: State list) (events: ShapeEvent list) : Violation list =
    segments states events
    |> List.choose (fun (i, ev, samples) ->
      match ev with
      | ShapeEvent.Flat _ when not (isSettling i) ->
        let fired = samples |> List.filter (fun (_, v) -> fires v)
        match fired with
        | [] -> None
        | bad -> Some { EventIndex = i; Event = ev; Why = sprintf "%d of %d flat samples fired" bad.Length samples.Length }
      | _ -> None)

  /// The counterpart for the `isSettling` `Flat` segments `flatSegmentsNeverFire`
  /// excludes: the TAIL of the segment must have settled to `Normal`, even
  /// though its start is allowed to still be clearing a prior segment's
  /// breach.
  let flatSegmentsEventuallySettle (isSettling: int -> bool) (tailSamples: int) (states: State list) (events: ShapeEvent list) : Violation list =
    segments states events
    |> List.choose (fun (i, ev, samples) ->
      match ev with
      | ShapeEvent.Flat _ when isSettling i ->
        let tail = samples |> List.rev |> List.truncate tailSamples
        match tail |> List.filter (fun (_, v) -> fires v) with
        | [] -> None
        | bad -> Some { EventIndex = i; Event = ev; Why = sprintf "%d of the last %d samples were still firing" bad.Length tail.Length }
      | _ -> None)

  /// A sustained `Step` always fires (Drifting or Broken) within the first
  /// `boundSamples` of the segment. This is the invariant the twin has to
  /// fail: `AlwaysFineTwin` never fires at all, so running this against it
  /// must produce a violation for every `Step` segment, proving the
  /// invariant is actually exercising the real detector's behavior and not
  /// vacuously true.
  let sustainedStepAlwaysFiresWithinBound (boundSamples: int) (states: State list) (events: ShapeEvent list) : Violation list =
    segments states events
    |> List.choose (fun (i, ev, samples) ->
      match ev with
      | ShapeEvent.Step(_, _) ->
        let withinBound = samples |> List.truncate boundSamples
        match withinBound |> List.exists (fun (_, v) -> fires v) with
        | true -> None
        | false -> Some { EventIndex = i; Event = ev; Why = sprintf "no fire in the first %d samples of a sustained step" boundSamples }
      | _ -> None)

  /// A `Drift` segment never jumps straight to `Broken`: if it ever reads
  /// `Broken`, an earlier sample in the same segment already read
  /// `Drifting`. CUSUM means a persistent-enough drift CAN legitimately end
  /// up `Broken` eventually (see HealthAnomaly.fs's module doc comment) —
  /// what must hold is that severity escalates gradually, the way a real
  /// worsening problem should read, rather than snapping straight to the
  /// page-someone case the way a sudden step does.
  let driftNeverJumpsStraightToBroken (states: State list) (events: ShapeEvent list) : Violation list =
    segments states events
    |> List.choose (fun (i, ev, samples) ->
      match ev with
      | ShapeEvent.Drift(_, _) ->
        let verdicts = samples |> List.map snd
        match verdicts |> List.tryFindIndex isBroken with
        | None -> None
        | Some brokenAt ->
          let driftingBefore =
            verdicts
            |> List.truncate brokenAt
            |> List.exists (function Verdict.Drifting _ -> true | _ -> false)
          match driftingBefore with
          | true -> None
          | false -> Some { EventIndex = i; Event = ev; Why = "the drift segment reached Broken without ever reading Drifting first" }
      | _ -> None)

  /// A single `Spike` sample never fires by itself.
  let singleSpikeNeverFires (states: State list) (events: ShapeEvent list) : Violation list =
    segments states events
    |> List.choose (fun (i, ev, samples) ->
      match ev with
      | ShapeEvent.Spike _ ->
        match samples |> List.filter (fun (_, v) -> fires v) with
        | [] -> None
        | bad -> Some { EventIndex = i; Event = ev; Why = sprintf "the spike sample itself fired (%d verdict(s))" bad.Length }
      | _ -> None)

  /// After a `Recovery` segment runs its course, the tail of it is back to
  /// `Normal` — never stuck firing forever once the signal is genuinely back
  /// to how it was.
  let recoveryEventuallyClearsToNormal (tailSamples: int) (states: State list) (events: ShapeEvent list) : Violation list =
    segments states events
    |> List.choose (fun (i, ev, samples) ->
      match ev with
      | ShapeEvent.Recovery _ ->
        let tail = samples |> List.rev |> List.truncate tailSamples
        match tail |> List.filter (fun (_, v) -> fires v) with
        | [] -> None
        | bad -> Some { EventIndex = i; Event = ev; Why = sprintf "%d of the last %d recovery samples were still firing" bad.Length tail.Length }
      | _ -> None)

  /// A `Step` with no matching `Recovery` after it — a legitimate,
  /// permanent scale change — eventually settles back to `Normal` by the
  /// end of the segment. `isPermanent` tells the caller which `Step`
  /// segments in a given scenario are meant to never be recovered from
  /// (`scenarioOf`'s scenarios have exactly one: the last event).
  let legitimateScaleChangeEventuallySettles
    (isPermanent: int -> bool)
    (tailSamples: int)
    (states: State list)
    (events: ShapeEvent list)
    : Violation list =
    segments states events
    |> List.choose (fun (i, ev, samples) ->
      match ev with
      | ShapeEvent.Step(_, _) when isPermanent i ->
        let tail = samples |> List.rev |> List.truncate tailSamples
        match tail |> List.filter (fun (_, v) -> fires v) with
        | [] -> None
        | bad -> Some { EventIndex = i; Event = ev; Why = sprintf "%d of the last %d samples of a permanent scale change were still firing" bad.Length tail.Length }
      | _ -> None)
