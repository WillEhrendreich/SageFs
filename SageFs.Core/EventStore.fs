/// ROLE: Event persistence abstraction — noop-only since Marten removal (v0.5.545).
///   Binary manifest is the sole source of truth for session restoration.
/// Weight: Minimal — kept only as a thin abstraction to avoid touching every caller.
///   Will be fully inlined once all callers are audited.
module SageFs.EventStore

open System

/// Persistence abstraction: noop-only. Marten/PostgreSQL has been removed.
type EventPersistence = {
  AppendEvents: string -> Features.Events.SageFsEvent list -> Threading.Tasks.Task<Result<unit, string>>
  FetchStream: string -> Threading.Tasks.Task<(DateTimeOffset * Features.Events.SageFsEvent) list>
  CountEvents: string -> Threading.Tasks.Task<int>
  SetValue: string -> string -> Threading.Tasks.Task<Result<unit, string>>
  GetValue: string -> Threading.Tasks.Task<string option>
}

module EventPersistence =
  /// No-op persistence: silently drops writes, returns empty on reads.
  /// This is the only implementation — Marten has been removed.
  let noop : EventPersistence = {
    AppendEvents = fun _ _ -> Threading.Tasks.Task.FromResult(Ok ())
    FetchStream = fun _ -> Threading.Tasks.Task.FromResult([])
    CountEvents = fun _ -> Threading.Tasks.Task.FromResult(0)
    SetValue = fun _ _ -> Threading.Tasks.Task.FromResult(Ok ())
    GetValue = fun _ -> Threading.Tasks.Task.FromResult(None)
  }
