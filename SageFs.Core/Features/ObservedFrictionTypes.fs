/// Observed (passive) friction — foundation types.
///
/// See observed-friction-plan.md §(b). The automatic `recordToolResult`
/// channel already logs every MCP tool call + typed outcome to a durable
/// SQLite store (FrictionSqlite.fs). Observed friction is a PURE DERIVATION
/// over that ordered event log: a `FrictionSignal` DU computed by pure
/// detector functions, with illegal states unrepresentable —
/// no bool/Option knobs, stable string ids, thresholds centralized in
/// `DetectorConfig`, never a scattered literal.
module SageFs.Features.ObservedFrictionTypes

open System
open SageFs.Features.FrictionTelemetryTypes

/// The subject a signal is attributed to. A DU, never a nullable string —
/// pre-enrichment (Brief B9) the only honest value is DaemonWide (every
/// `FrictionEvent.Session` is stamped "mcp"); post-B9 it can be a real
/// session or a bound agent, with zero detector changes required.
[<RequireQualifiedAccess>]
type SignalScope =
  | DaemonWide
  | Session of SessionRef
  | Agent of string

/// Confidence is a DU, not a float knob: detectors report a *reason class*,
/// never a numeric score that invites false precision.
[<RequireQualifiedAccess>]
type SignalConfidence =
  | Strong        // structurally certain (e.g. an AffordanceMismatch event exists)
  | Heuristic     // threshold-based; may false-positive on warmup/normal use

/// Ordered [start,end] time span the evidence spans — a record, not a
/// tuple, so the fields are named at every call site.
type EvidenceWindow = {
  FirstAtUtc: DateTimeOffset
  LastAtUtc: DateTimeOffset
  EventCount: int
}

/// Each case carries the evidence that justified it — never raw user
/// code/secrets (any free text a detector stores must be pre-sanitized via
/// FrictionSanitize.sanitizeText; v1 detectors carry only tool names,
/// counts, blocker kinds, and durations — no free text at all).
[<RequireQualifiedAccess>]
type FrictionSignal =
  | ExcessivePolling       of tool: ToolName * calls: int * successes: int * window: EvidenceWindow
  | ResetThrash            of resets: int * window: EvidenceWindow
  | HardResetAfterCreate   of gap: DurationMs                        // hard_reset within N of create
  | UnattributedFailure    of rawTool: string * count: int           // "unknown (missing argument)" class
  | InvalidStateCall       of tool: ToolName * blocked: int          // AffordanceMismatch rejections
  | RepeatedSameError      of blocker: BlockerKind * run: int        // N consecutive identical failures
  | RetryLoop              of tool: ToolName * attempts: int         // err -> retry -> err on the same tool
  | Abandonment            of tool: ToolName * lastBlocker: BlockerKind    // failure, no later success, agent moved on
  | SlowTimeToFirstSuccess of elapsed: DurationMs * failedBefore: int      // create -> first CompletedCleanly

/// Stable id — the ONE exhaustive to-string. Contract-tested against a
/// golden set (ObservedFrictionTypesTests.fs) so a case rename is a visible
/// test break (repo convention: no magic strings anywhere).
module FrictionSignal =
  let id : FrictionSignal -> string =
    function
    | FrictionSignal.ExcessivePolling _       -> "observed.excessive-polling"
    | FrictionSignal.ResetThrash _            -> "observed.reset-thrash"
    | FrictionSignal.HardResetAfterCreate _   -> "observed.hard-reset-after-create"
    | FrictionSignal.UnattributedFailure _    -> "observed.unattributed-failure"
    | FrictionSignal.InvalidStateCall _       -> "observed.invalid-state-call"
    | FrictionSignal.RepeatedSameError _      -> "observed.repeated-same-error"
    | FrictionSignal.RetryLoop _              -> "observed.retry-loop"
    | FrictionSignal.Abandonment _            -> "observed.abandonment"
    | FrictionSignal.SlowTimeToFirstSuccess _ -> "observed.slow-first-success"

/// A detected signal + its provenance. A record, not an anonymous record
/// (repo convention).
type DetectedSignal = {
  Signal: FrictionSignal
  Scope: SignalScope
  Confidence: SignalConfidence
}

/// All thresholds in one place — no literals scattered in detectors.
type DetectorConfig = {
  PollingMinCalls: int                       // default 20  (harvest: 72 get_fsi_status)
  PollingMinCallsPerSuccess: int              // default 8   (calls:successes ratio gate)
  PollingTools: Set<string>                   // default { "get_fsi_status" }
  ResetThrashCount: int                       // default 3   resets within the window
  ResetThrashWindow: TimeSpan                 // default 60s
  HardResetAfterCreateWindow: TimeSpan        // default 30s
  RepeatedErrorRun: int                       // default 3   consecutive identical BlockerKind
  RetryLoopAttempts: int                      // default 3
  SlowFirstSuccess: TimeSpan                  // default 45s (warmup budget) — Heuristic
}

module DetectorConfig =
  /// The single source of truth for every detector threshold.
  let defaults : DetectorConfig = {
    PollingMinCalls = 20
    PollingMinCallsPerSuccess = 8
    PollingTools = Set.ofList [ "get_fsi_status" ]
    ResetThrashCount = 3
    ResetThrashWindow = TimeSpan.FromSeconds 60.0
    HardResetAfterCreateWindow = TimeSpan.FromSeconds 30.0
    RepeatedErrorRun = 3
    RetryLoopAttempts = 3
    SlowFirstSuccess = TimeSpan.FromSeconds 45.0
  }

/// The ordered, scope-tagged slice a detector runs on. `Events` preserves
/// the `ORDER BY id` order from `FrictionSqlite.ReadEvents`.
type PreparedStream = {
  Scope: SignalScope
  Events: FrictionEvent list
}

/// A detector is a pure function over ONE prepared stream. IO-free by
/// type — no detector may touch the filesystem, network, or clock; "now"
/// is never read, only the timestamps already on the events.
type Detector = DetectorConfig -> PreparedStream -> DetectedSignal list
