namespace SageFs

open System
open System.Diagnostics
open SageFs.WorkerProtocol

/// Standby session lifecycle states.
[<RequireQualifiedAccess>]
type StandbyState =
  | Warming      // Worker process started, waiting for port/FSI warmup
  | Ready        // Fully warmed, ready to swap
  | Invalidated  // File change detected, needs re-warm

/// A pre-warmed worker process ready to swap in on restart.
type StandbySession = {
  Process: Process
  Proxy: SessionProxy option
  State: StandbyState
  Projects: string list
  WorkingDir: string
  CreatedAt: DateTime
}

/// Identifies a unique session configuration for standby matching.
[<CustomEquality; CustomComparison>]
type StandbyKey = {
  Projects: string list
  WorkingDir: string
}
with
  override x.Equals(obj) =
    match obj with
    | :? StandbyKey as y ->
      x.Projects = y.Projects && x.WorkingDir = y.WorkingDir
    | _ -> false
  override x.GetHashCode() =
    hash (x.Projects, x.WorkingDir)
  interface IComparable with
    member x.CompareTo(obj) =
      match obj with
      | :? StandbyKey as y ->
        let c = compare x.Projects y.Projects
        if c <> 0 then c else compare x.WorkingDir y.WorkingDir
      | _ -> 1

module StandbyKey =
  let fromSession (projects: string list) (workingDir: string) =
    { Projects = List.sort projects; WorkingDir = workingDir }

/// What should RestartSession do?
[<RequireQualifiedAccess>]
type RestartDecision =
  | SwapStandby of StandbySession
  | ColdRestart

/// Pure decision functions for standby lifecycle.
/// No side effects — all IO is handled by SessionManager.
module StandbyPool =

  /// Should we start warming a standby?
  let shouldWarmStandby
    (primaryStatus: SessionStatus)
    (currentStandby: StandbySession option)
    (enabled: bool) =
    enabled
    && primaryStatus = SessionStatus.Ready
    && currentStandby.IsNone

  /// Can we swap the standby instead of cold restart?
  let canSwap (standby: StandbySession option) =
    match standby with
    | Some s when s.State = StandbyState.Ready && s.Proxy.IsSome -> true
    | _ -> false

  /// Should the standby be invalidated on file change?
  let shouldInvalidate (standby: StandbySession option) =
    match standby with
    | Some s when s.State = StandbyState.Warming
                  || s.State = StandbyState.Ready -> true
    | _ -> false

  /// After a swap, the standby is consumed.
  let afterSwap (_standby: StandbySession option) : StandbySession option =
    None

  /// Decide whether to swap or cold-restart.
  /// rebuild=true forces ColdRestart because binaries changed.
  let decideRestart
    (rebuild: bool)
    (standby: StandbySession option)
    : RestartDecision =
    match rebuild, standby with
    | false, Some s when s.State = StandbyState.Ready && s.Proxy.IsSome ->
      RestartDecision.SwapStandby s
    | _ ->
      RestartDecision.ColdRestart

/// Summary of standby pool state for UI display
type StandbyInfo =
  | NoPool
  | Warming
  | Ready
  | Invalidated

module StandbyInfo =
  let label = function
    | NoPool -> ""
    | Warming -> "⏳ standby"
    | Ready -> "✓ standby"
    | Invalidated -> "⚠ standby"

/// Tracks standby sessions keyed by config.
type PoolState = {
  Standbys: Map<StandbyKey, StandbySession>
  Enabled: bool
}

module PoolState =
  let empty = { Standbys = Map.empty; Enabled = true }

  let getStandby key state =
    Map.tryFind key state.Standbys

  let setStandby key standby state =
    { state with Standbys = Map.add key standby state.Standbys }

  let removeStandby key state =
    { state with Standbys = Map.remove key state.Standbys }

  /// Try to consume a ready standby for the given config.
  let tryConsumeStandby key state =
    match getStandby key state with
    | Some s when StandbyPool.canSwap (Some s) ->
      Some s, removeStandby key state
    | _ ->
      None, state

  /// Invalidate all standbys matching a working directory.
  let invalidateForDir (workingDir: string) state =
    let updated =
      state.Standbys
      |> Map.map (fun key standby ->
        if key.WorkingDir = workingDir
           && StandbyPool.shouldInvalidate (Some standby) then
          { standby with State = StandbyState.Invalidated }
        else
          standby)
    { state with Standbys = updated }
