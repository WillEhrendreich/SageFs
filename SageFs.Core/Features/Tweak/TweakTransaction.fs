/// A pure state machine for one tweak's journey from "text the user just
/// typed or dragged to" through parse, type-check, evaluate, apply and
/// journal. Generic over the applied value's type so this module stays
/// decoupled from `LiteralEdit`/`ExpressionEdit`'s own value types.
///
/// Illegal states are unrepresentable by construction: the only case that
/// carries a candidate value evaluate produced is `Evaluated`, the only
/// transition that reaches `Applied` consumes that candidate, and every
/// failure keeps the `live` value the state was already carrying rather
/// than losing it, there is no `option`/`bool` anywhere in the state, only
/// DU cases for what could actually be true.
module SageFs.Features.Tweak.TweakTransaction

[<RequireQualifiedAccess>]
type TweakStep =
  | Parse
  | TypeCheck
  | Evaluate
  | Apply
  | Journal
  /// An event arrived that made no sense for the state it arrived in (for
  /// example a `TypeChecked` while nothing had been parsed yet). Kept
  /// distinct from the five real steps so a caller can tell "your own step
  /// failed" from "something fed this machine garbage".
  | Unexpected

[<RequireQualifiedAccess>]
type TweakState<'Value> =
  /// No tweak in flight. `live` is the value the app is currently showing.
  | Idle of live: 'Value
  /// A new attempt started with this text; parsing hasn't reported back yet.
  | Parsing of live: 'Value * text: string
  | Parsed of live: 'Value * text: string
  | TypeChecked of live: 'Value * text: string
  /// Type-checked and evaluated: `candidate` is the value ready to apply.
  | Evaluated of live: 'Value * text: string * candidate: 'Value
  /// Applied: `live` now EQUALS the candidate that was evaluated, the app
  /// is showing it.
  | Applied of live: 'Value * text: string
  | Journaled of live: 'Value * text: string
  /// A step failed. `live` is unchanged from whatever it was before this
  /// attempt (except when `step = Journal`, where `Apply` already
  /// succeeded, so `live` is the value that landed and journaling merely
  /// failed to make it durable).
  | Failed of live: 'Value * step: TweakStep * reason: string

[<RequireQualifiedAccess>]
type TweakEvent<'Value> =
  /// The user started a new attempt (a drag tick, or an expression edit).
  /// Valid from ANY state, the newest attempt always wins, per the spec's
  /// "re-evaluation is latest-wins".
  | Started of text: string
  | Parsed
  | ParseFailed of reason: string
  | TypeChecked
  | TypeCheckFailed of reason: string
  | Evaluated of value: 'Value
  | EvaluateFailed of reason: string
  | Applied
  | ApplyFailed of reason: string
  | Journaled
  | JournalFailed of reason: string

/// The value the app is showing right now, in every state.
let liveOf (state: TweakState<'Value>) : 'Value =
  match state with
  | TweakState.Idle live -> live
  | TweakState.Parsing(live, _) -> live
  | TweakState.Parsed(live, _) -> live
  | TweakState.TypeChecked(live, _) -> live
  | TweakState.Evaluated(live, _, _) -> live
  | TweakState.Applied(live, _) -> live
  | TweakState.Journaled(live, _) -> live
  | TweakState.Failed(live, _, _) -> live

let initial (live: 'Value) : TweakState<'Value> = TweakState.Idle live

/// The one place a `TweakState` moves. Total: every `(state, event)` pair
/// this module doesn't recognize as a valid forward step lands on
/// `Failed(live, TweakStep.Unexpected, ...)` rather than being silently
/// accepted or throwing, a fuzzer feeding this function garbage can never
/// produce a state outside this DU.
let step (state: TweakState<'Value>) (event: TweakEvent<'Value>) : TweakState<'Value> =
  match event with
  // A fresh attempt is always allowed, from any state, and always keeps
  // whatever `live` currently is.
  | TweakEvent.Started text -> TweakState.Parsing(liveOf state, text)

  | TweakEvent.Parsed ->
    match state with
    | TweakState.Parsing(live, text) -> TweakState.Parsed(live, text)
    | _ -> TweakState.Failed(liveOf state, TweakStep.Unexpected, "Parsed arrived without a Started in flight")

  | TweakEvent.ParseFailed reason ->
    match state with
    | TweakState.Parsing(live, _) -> TweakState.Failed(live, TweakStep.Parse, reason)
    | _ -> TweakState.Failed(liveOf state, TweakStep.Unexpected, "ParseFailed arrived without a Started in flight")

  | TweakEvent.TypeChecked ->
    match state with
    | TweakState.Parsed(live, text) -> TweakState.TypeChecked(live, text)
    | _ -> TweakState.Failed(liveOf state, TweakStep.Unexpected, "TypeChecked arrived before Parsed")

  | TweakEvent.TypeCheckFailed reason ->
    match state with
    | TweakState.Parsed(live, _) -> TweakState.Failed(live, TweakStep.TypeCheck, reason)
    | _ -> TweakState.Failed(liveOf state, TweakStep.Unexpected, "TypeCheckFailed arrived before Parsed")

  | TweakEvent.Evaluated value ->
    match state with
    | TweakState.TypeChecked(live, text) -> TweakState.Evaluated(live, text, value)
    | _ -> TweakState.Failed(liveOf state, TweakStep.Unexpected, "Evaluated arrived before TypeChecked")

  | TweakEvent.EvaluateFailed reason ->
    match state with
    | TweakState.TypeChecked(live, _) -> TweakState.Failed(live, TweakStep.Evaluate, reason)
    | _ -> TweakState.Failed(liveOf state, TweakStep.Unexpected, "EvaluateFailed arrived before TypeChecked")

  | TweakEvent.Applied ->
    match state with
    | TweakState.Evaluated(_, text, candidate) -> TweakState.Applied(candidate, text)
    | _ -> TweakState.Failed(liveOf state, TweakStep.Unexpected, "Applied arrived before Evaluated")

  | TweakEvent.ApplyFailed reason ->
    match state with
    | TweakState.Evaluated(live, _, _) -> TweakState.Failed(live, TweakStep.Apply, reason)
    | _ -> TweakState.Failed(liveOf state, TweakStep.Unexpected, "ApplyFailed arrived before Evaluated")

  | TweakEvent.Journaled ->
    match state with
    | TweakState.Applied(live, text) -> TweakState.Journaled(live, text)
    | _ -> TweakState.Failed(liveOf state, TweakStep.Unexpected, "Journaled arrived before Applied")

  | TweakEvent.JournalFailed reason ->
    match state with
    // `live` already reflects the applied value, journaling only affects
    // durability, not what the app is showing.
    | TweakState.Applied(live, _) -> TweakState.Failed(live, TweakStep.Journal, reason)
    | _ -> TweakState.Failed(liveOf state, TweakStep.Unexpected, "JournalFailed arrived before Applied")

/// Fold a whole event sequence from a starting live value.
let run (initialLive: 'Value) (events: TweakEvent<'Value> list) : TweakState<'Value> =
  events |> List.fold step (initial initialLive)

let isApplied (state: TweakState<'Value>) =
  match state with
  | TweakState.Applied _
  | TweakState.Journaled _ -> true
  | _ -> false

let isJournaled (state: TweakState<'Value>) =
  match state with
  | TweakState.Journaled _ -> true
  | _ -> false

let isFailed (state: TweakState<'Value>) =
  match state with
  | TweakState.Failed _ -> true
  | _ -> false
