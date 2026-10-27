namespace SageFs.Features.LiveTesting

open System
open SageFs
open SageFs.Measures

// --- Debounce + Cancellation (Phase 4 as-you-type) ---

type DebouncedOp<'a> = {
  Payload: 'a
  RequestedAt: DateTimeOffset
  DelayMs: int<ms>
  Generation: int64
}

type DebounceChannel<'a> = {
  CurrentGeneration: int64
  Pending: DebouncedOp<'a> option
  LastCompleted: DateTimeOffset option
}

module DebounceChannel =
  let empty<'a> : DebounceChannel<'a> = {
    CurrentGeneration = 0L
    Pending = None
    LastCompleted = None
  }

  let submit (payload: 'a) (delayMs: int<ms>) (now: DateTimeOffset) (ch: DebounceChannel<'a>) =
    let gen = ch.CurrentGeneration + 1L
    { ch with
        CurrentGeneration = gen
        Pending = Some { Payload = payload; RequestedAt = now; DelayMs = delayMs; Generation = gen } }

  let tryFire (now: DateTimeOffset) (ch: DebounceChannel<'a>) =
    match ch.Pending with
    | None -> None, ch
    | Some op ->
      let elapsed = toMs (now - op.RequestedAt)
      match op.Generation < ch.CurrentGeneration, elapsed >= float op.DelayMs * 1.0<ms> with
      | true, _ ->
        None, { ch with Pending = None }
      | _, true ->
        Some op.Payload, { ch with Pending = None; LastCompleted = Some now }
      | false, false ->
        None, ch

  /// Cancel any pending operation by bumping the generation so tryFire will discard it.
  let cancel (ch: DebounceChannel<'a>) =
    match ch.Pending with
    | None -> ch
    | Some _ -> { ch with CurrentGeneration = ch.CurrentGeneration + 1L }

  let isStale (ch: DebounceChannel<'a>) =
    match ch.Pending with
    | Some op -> op.Generation < ch.CurrentGeneration
    | None -> false

type TestCycleDebounce = {
  TreeSitter: DebounceChannel<string>
  Fcs: DebounceChannel<string>
}

module TestCycleDebounce =
  let empty = {
    TreeSitter = DebounceChannel.empty
    Fcs = DebounceChannel.empty
  }

  let treeSitterDelayMs = int Timeouts.liveTestTreeSitterDebounce.TotalMilliseconds * 1<ms>

  let onKeystroke (content: string) (filePath: string) (fcsDelay: int<ms>) (now: DateTimeOffset) (db: TestCycleDebounce) =
    { db with
        TreeSitter = db.TreeSitter |> DebounceChannel.submit content treeSitterDelayMs now
        Fcs = db.Fcs |> DebounceChannel.submit filePath fcsDelay now }

  let onFileSave (filePath: string) (now: DateTimeOffset) (db: TestCycleDebounce) =
    { db with
        Fcs = db.Fcs |> DebounceChannel.submit filePath 50<ms> now }

  let tick (now: DateTimeOffset) (db: TestCycleDebounce) =
    let tsPayload, tsChannel = DebounceChannel.tryFire now db.TreeSitter
    let fcsPayload, fcsChannel = DebounceChannel.tryFire now db.Fcs
    // Return same reference when nothing changed (enables model reference equality skip)
    let db' =
      match obj.ReferenceEquals(tsChannel, db.TreeSitter) && obj.ReferenceEquals(fcsChannel, db.Fcs) with
      | true -> db
      | false -> { db with TreeSitter = tsChannel; Fcs = fcsChannel }
    (tsPayload, fcsPayload), db'

  /// Whether a tick could fire anything: some channel holds a pending operation.
  /// The test-cycle tick exists only to fire these, so without one it has
  /// nothing to do and the daemon's timer can idle.
  let hasPending (db: TestCycleDebounce) =
    db.TreeSitter.Pending.IsSome || db.Fcs.Pending.IsSome

/// Shared payload for RunAffectedTests / RequestRebuild —
/// the 6 fields that both effect cases always carry together.
type TestRunRequest = {
  Tests: TestCase array
  Trigger: RunTrigger
  TreeSitterElapsed: System.TimeSpan
  FcsElapsed: System.TimeSpan
  SessionId: string option
  InstrumentationMaps: InstrumentationMap array
}

module TestRunRequest =
  let empty = {
    Tests = [||]
    Trigger = RunTrigger.Keystroke
    TreeSitterElapsed = System.TimeSpan.Zero
    FcsElapsed = System.TimeSpan.Zero
    SessionId = None
    InstrumentationMaps = [||]
  }

/// Payload for RequestFcsTypeCheck — the 5 fields that define a type-check request.
type TypeCheckRequest = {
  SessionId: string option
  FilePath: string
  Content: string option
  AnalysisIdentity: AnalysisIdentity option
  TreeSitterElapsed: System.TimeSpan
}

module TypeCheckRequest =
  let empty = {
    SessionId = None
    FilePath = ""
    Content = None
    AnalysisIdentity = None
    TreeSitterElapsed = System.TimeSpan.Zero
  }

/// Payload for `TestCycleEffect.EvalBufferThenRunAffected` — a sibling of
/// `TestRunRequest` (live-testing-asyoutype-plan.md §2/Brief 4) rather than
/// an extension of it, so every EXISTING `TestRunRequest` construction site
/// (RunAffectedTests/RequestRebuild) is untouched. `FilePath`/`Content` are
/// the debounced, type-check-passing buffer that must be identity-preserving
/// eval'd (Brief 3's `WorkerMessage.EvalLiveTestFile`) before `Run.Tests`
/// (already coverage/graph-selected by `decideAfterTypeCheck`) can be run
/// against FRESH code instead of the stale compiled DLL.
type EvalThenRunRequest = {
  FilePath: string
  Content: string
  Run: TestRunRequest
}

[<RequireQualifiedAccess>]
type TestCycleEffect =
  | RequestInitialDiscovery
  | ParseTreeSitter of content: string * filePath: string
  | RequestFcsTypeCheck of TypeCheckRequest
  | RunAffectedTests of TestRunRequest
  /// An explicitly requested run whose generation was allocated at request
  /// time (`RequestedRuns.request`). The performer starts it WITH that
  /// generation (`TuiEvent.TestRunStartedAt`) instead of bumping a new one, so
  /// the run's identity survives from request to results.
  | RunRequestedTests of request: TestRunRequest * generation: RunGeneration
  /// Identity-preserving eval of the edited buffer, then run of the tests
  /// `Run.Tests` already selected as affected — replaces the compiled-DLL
  /// decision for a compiled `.fs` file on BOTH Keystroke and FileSave (see
  /// `TestCycleEffects.redirectToEvalBuffer`). `.fsx`/expression paths, and
  /// any compiled-file decision made without known buffer content, are left
  /// as plain `RunAffectedTests`/`RequestRebuild` — unchanged.
  | EvalBufferThenRunAffected of EvalThenRunRequest
  | CancelRebuild of sessionId: string option * generation: int64
  | RequestRebuild of generation: int64 * TestRunRequest
  | RegisterFileWatcher of sessionId: string * directory: string
  | DisposeFileWatcher of sessionId: string * directory: string
