namespace SageFs

// ── Readiness: what a session's "Ready" is worth ─────────────────────────────────────────────────────────────────────
//
// `SessionState.Ready` says the worker is alive and can take an eval. It does not say the code the session runs is the
// code in the files. That is `SourceState` (the disk is ahead of the build) and `ReplFreshness` (the REPL is behind the
// app). Surfaces that print "Ready" beside a stale build tell two stories about one session.
//
// This is the one place that joins the three. The header badge, the daemon health bar, the statusline and the session
// card all read it, so they cannot disagree. Pure: the inputs are DUs the edge already produced.

/// How a surface says a session is doing.
[<RequireQualifiedAccess>]
type Readiness =
  /// The session is in a state other than Ready (warming up, evaluating, faulted, not started). It reads as that state's own word.
  | InState of SessionState
  /// Ready, and the build it runs is the build in the files (or there is no project, so there is no build to be behind).
  | ReadyCurrent
  /// Ready, and files the build was made from changed after it was made.
  | ReadyOlderCode
  /// Ready, and a rebuild is under way. The session keeps serving the build from before it.
  | ReadyRebuilding
  /// Ready, and the app was patched in place after the REPL's build was made.
  | ReadyReplBehindApp
  /// Ready, and whether the build is current could not be told.
  | ReadyUnchecked

/// The weight a surface gives a readiness. Red is for failure only: a build that is behind the files is a caution.
[<RequireQualifiedAccess>]
type ReadinessTone =
  | Healthy
  | Caution
  | Failing
  | Quiet

[<RequireQualifiedAccess>]
module Readiness =

  /// The decision. Only a Ready session is qualified: any other state already says it is not simply ready. For a Ready
  /// session a changed file outranks a rebuild, a rebuild outranks the REPL being behind the app, and a fact outranks not
  /// knowing. A session with no project is current: there is no build for it to be behind.
  let decide (state: SessionState) (source: SourceState) (freshness: ReplFreshness) : Readiness =
    match state with
    | SessionState.Uninitialized
    | SessionState.WarmingUp
    | SessionState.Evaluating
    | SessionState.Faulted -> Readiness.InState state
    | SessionState.Ready ->
      match source with
      | SourceState.Stale _ -> Readiness.ReadyOlderCode
      | SourceState.Rebuilding _ -> Readiness.ReadyRebuilding
      | SourceState.InSync _
      | SourceState.Unknown UnknownReason.NoProjectLoaded ->
        match freshness with
        | ReplFreshness.BehindApp _
        | ReplFreshness.Leveling _
        | ReplFreshness.CannotLevel _ -> Readiness.ReadyReplBehindApp
        | ReplFreshness.Unknown _ -> Readiness.ReadyUnchecked
        | ReplFreshness.InSync -> Readiness.ReadyCurrent
      | SourceState.Unknown _ ->
        match freshness with
        | ReplFreshness.BehindApp _
        | ReplFreshness.Leveling _
        | ReplFreshness.CannotLevel _ -> Readiness.ReadyReplBehindApp
        | ReplFreshness.Unknown _
        | ReplFreshness.InSync -> Readiness.ReadyUnchecked

  /// What a Ready session's build adds to the word "Ready", in lower case so it can follow it. A plain Ready and a state other
  /// than Ready add nothing. Every surface's words come from this one phrase: the long label, the short phrase, the card's
  /// badge and the strip's marker.
  let qualification (readiness: Readiness) : string option =
    match readiness with
    | Readiness.InState _
    | Readiness.ReadyCurrent -> None
    | Readiness.ReadyOlderCode -> Some "changes not live"
    | Readiness.ReadyRebuilding -> Some "rebuilding"
    | Readiness.ReadyReplBehindApp -> Some "REPL behind the app"
    | Readiness.ReadyUnchecked -> Some "build not checked"

  /// The words a surface prints. A state other than Ready keeps the daemon's own word, which clients wait on.
  let label (readiness: Readiness) : string =
    match readiness, qualification readiness with
    | _, Some phrase -> sprintf "Ready, %s" phrase
    // The daemon's token "WarmingUp" is a name in code. A person reads "Warming up", and every surface says it that way.
    | Readiness.InState SessionState.WarmingUp, None -> "Warming up"
    | Readiness.InState state, None -> SessionState.label state
    | _, None -> "Ready"

  /// The qualification as a short phrase on its own ("Changes not live"), for a place with no room for "Ready, " in front of
  /// it: the note in the live bindings pane and the marker on the sessions strip.
  let brief (readiness: Readiness) : string option =
    qualification readiness
    |> Option.map (fun phrase -> sprintf "%c%s" (System.Char.ToUpperInvariant phrase.[0]) (phrase.Substring 1))

  let tone (readiness: Readiness) : ReadinessTone =
    match readiness with
    | Readiness.InState SessionState.Faulted -> ReadinessTone.Failing
    | Readiness.InState SessionState.WarmingUp -> ReadinessTone.Caution
    | Readiness.InState SessionState.Evaluating -> ReadinessTone.Healthy
    | Readiness.InState SessionState.Uninitialized -> ReadinessTone.Quiet
    | Readiness.InState SessionState.Ready -> ReadinessTone.Healthy
    | Readiness.ReadyCurrent -> ReadinessTone.Healthy
    | Readiness.ReadyOlderCode
    | Readiness.ReadyRebuilding
    | Readiness.ReadyReplBehindApp
    | Readiness.ReadyUnchecked -> ReadinessTone.Caution

  /// The readiness for a state alone, for a caller that has read neither the source nor the REPL's freshness. It is never
  /// ReadyCurrent for a Ready session: not having looked is `ReadyUnchecked`, which says so.
  let ofStateAlone (state: SessionState) : Readiness =
    decide state (SourceState.Unknown UnknownReason.NotAssessed) ReplFreshness.InSync

/// What can be done about a build that is not current, decided once from the source state. The session card's button, the
/// live bindings pane's note and the pane's button all read this, so they appear together or not at all.
[<RequireQualifiedAccess>]
type Remedy =
  | NothingToDo
  /// Files changed after the build: the hard reset with a rebuild loads them.
  | RebuildAndReload

[<RequireQualifiedAccess>]
module Remedy =
  /// A rebuild under way needs no second one, and a build nobody could read is not known to need one.
  let ofSource (source: SourceState) : Remedy =
    match source with
    | SourceState.Stale _ -> Remedy.RebuildAndReload
    | SourceState.InSync _
    | SourceState.Rebuilding _
    | SourceState.Unknown _ -> Remedy.NothingToDo

  /// The words that name what is wrong when a rebuild is the remedy ("Running older code"), the same phrase the card, the
  /// header and the sessions strip use for it. Empty when there is nothing to do.
  let headline (remedy: Remedy) : string =
    match remedy with
    | Remedy.NothingToDo -> ""
    | Remedy.RebuildAndReload -> Readiness.brief Readiness.ReadyOlderCode |> Option.defaultValue ""

  /// The same fact in the live bindings pane's terms: what it means for the values the pane shows. The session's phrase is on the
  /// card, the header and the strip already; a fourth copy beside the list told the reader nothing new, and three reviewers counted it.
  let paneWords (remedy: Remedy) : string =
    match remedy with
    | Remedy.NothingToDo -> ""
    | Remedy.RebuildAndReload -> "These values are not live: the files have changes this session has not run."
