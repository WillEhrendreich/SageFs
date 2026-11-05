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
        | ReplFreshness.BehindApp _ -> Readiness.ReadyReplBehindApp
        | ReplFreshness.Unknown _ -> Readiness.ReadyUnchecked
        | ReplFreshness.InSync -> Readiness.ReadyCurrent
      | SourceState.Unknown _ ->
        match freshness with
        | ReplFreshness.BehindApp _ -> Readiness.ReadyReplBehindApp
        | ReplFreshness.Unknown _
        | ReplFreshness.InSync -> Readiness.ReadyUnchecked

  /// The words a surface prints. A state other than Ready keeps the daemon's own word, which clients wait on.
  let label (readiness: Readiness) : string =
    match readiness with
    | Readiness.InState state -> SessionState.label state
    | Readiness.ReadyCurrent -> "Ready"
    | Readiness.ReadyOlderCode -> "Ready, running older code"
    | Readiness.ReadyRebuilding -> "Ready, rebuilding"
    | Readiness.ReadyReplBehindApp -> "Ready, REPL behind the app"
    | Readiness.ReadyUnchecked -> "Ready, build not checked"

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
