namespace SageFs.Features

open System
open System.Collections.Concurrent
open FSharp.Data.Adaptive

/// Adaptive store for live binding snapshots, per session.
///
/// Backed by FSharp.Data.Adaptive changeable cells (`cval`). Writers call
/// `update` inside a transaction; subscribers attached via `subscribe` fire
/// ONLY when the snapshot actually changed (structural equality), so
/// unchanged bindings produce no recompute, no SSE, no render — the
/// reactive/incremental behavior of a debugger watch window.
module LiveBindingsAdaptive =

  type State = {
    /// Per-session current snapshot — the changeable cell subscribers watch.
    SessionSnapshots: ConcurrentDictionary<string, cval<LiveValueTree.LiveValueSnapshot option>>
    /// Per-session generation counter — the last generation applied.
    SessionGenerations: ConcurrentDictionary<string, cval<int64>>
  }

  let create () : State = {
    SessionSnapshots = ConcurrentDictionary<string, cval<LiveValueTree.LiveValueSnapshot option>>()
    SessionGenerations = ConcurrentDictionary<string, cval<int64>>()
  }

  /// Apply a new snapshot for a session. Runs inside a transaction so
  /// multiple per-session updates batch atomically. Subscribers only fire
  /// when the snapshot value actually differs.
  let update (state: State) (sessionId: string) (snap: LiveValueTree.LiveValueSnapshot) : unit =
    transact (fun () ->
      let cell =
        state.SessionSnapshots.GetOrAdd(sessionId, fun _ -> cval None)
      cell.Value <- Some snap
      let gen =
        state.SessionGenerations.GetOrAdd(sessionId, fun _ -> cval 0L)
      gen.Value <- snap.Generation)

  /// Subscribe to a session's snapshot. The callback fires on every real
  /// change (and once immediately with the current value if present).
  /// Returns an IDisposable that unsubscribes.
  let subscribe
    (state: State)
    (sessionId: string)
    (onChange: LiveValueTree.LiveValueSnapshot -> unit)
    : IDisposable =
    let cell =
      state.SessionSnapshots.GetOrAdd(sessionId, fun _ -> cval None)
    let mapped =
      cell |> AVal.map (Option.map (fun s -> s, s.Generation))
    mapped.AddCallback(fun v ->
      match v with
      | Some (s, _) -> onChange s
      | None -> ())

  /// Current snapshot for a session, or None if never updated.
  let tryGet (state: State) (sessionId: string) : LiveValueTree.LiveValueSnapshot option =
    match state.SessionSnapshots.TryGetValue sessionId with
    | true, cell -> AVal.force cell
    | _ -> None

  /// Last applied generation for a session (0 if none).
  let tryGetGeneration (state: State) (sessionId: string) : int64 =
    match state.SessionGenerations.TryGetValue sessionId with
    | true, cell -> AVal.force cell
    | _ -> 0L

  /// Remove a session's cells entirely (e.g. session stopped/purged).
  let remove (state: State) (sessionId: string) : unit =
    state.SessionSnapshots.TryRemove sessionId |> ignore
    state.SessionGenerations.TryRemove sessionId |> ignore
