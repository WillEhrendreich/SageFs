namespace SageFs

/// Pure "has this version already been notified" gate for MCP
/// `resources/updated` push notifications (item 12,
/// sagefs-multiagent-vision.md §5.6: "one read model, no new channel").
///
/// Mirrors two guards this codebase already has for the exact same
/// no-change-suppression problem: the dashboard's SSE no-change suppression,
/// and `McpServer.wireCohortEventSubscription`'s `lastMatrixVersion` ref
/// (`version <> lastMatrixVersion.Value`). Rather than re-deriving the
/// pattern ad hoc at each new notification call site, this gives it one
/// small, pure, unit-testable shape: the same version observed twice in a
/// row must fire nothing, so a `resources/subscribe`r never sees a
/// notification unless the underlying read model actually advanced.
/// Generic over any equatable "version" a read model exposes (a
/// `CohortFrame.Version`, a monotonic session-list revision, ...) — no
/// dependency on `Cohort.fs` or any other read model.
module McpResourceGate =

  /// One gate's state: the last version it fired a notification for.
  /// `None` before the first observation.
  type State<'v> = { LastNotified: 'v option }

  /// The gate before anything has been observed — the first `observe` call
  /// always notifies, exactly like `lastMatrixVersion`'s `-1L` sentinel.
  let initial<'v> : State<'v> = { LastNotified = None }

  /// Observe a fresh version. Returns `(shouldNotify, newState)`:
  /// - First observation of any version: notify, remember it.
  /// - Same version as last time: don't notify, state unchanged (an
  ///   "unchanged tick" — the exact case a subscriber must never be
  ///   spammed for).
  /// - Any different version: notify, remember the new version. The gate
  ///   itself does not assume monotonicity — every production read model
  ///   this wraps today only ever moves forward, but the gate's own
  ///   correctness does not depend on that.
  let observe (version: 'v) (state: State<'v>) : bool * State<'v> =
    match state.LastNotified with
    | Some v when v = version -> false, state
    | _ -> true, { LastNotified = Some version }
