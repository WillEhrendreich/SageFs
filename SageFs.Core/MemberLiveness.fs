namespace SageFs

open System
open SageFs.MemberTable

/// Whether a member is still there, as the cohort's lease reaper needs to know it.
///
/// The reaper used to renew a seat only for a member that had evaluated code in the last two minutes. A conductor
/// that waits on its agents, reads status and lands what they send never evaluates, so its lease ran out after
/// 30 minutes of perfectly good work (finding C5 of the 2026-11-07 cohort run). Two things say a member is present
/// without an eval: it made a tool call lately, and it holds an open push stream to the daemon (the stdio bridge
/// keeps one open for as long as its client lives, so a conductor that is only thinking is still connected).
///
/// A member is gone when it has no open stream and has made no call for the window. The key is the `MemberId`
/// itself, the same value the cohort's own member table holds. It used to be the text `MemberId.display` prints,
/// which two different ids can share (a raw connection handle and its fingerprint print the same).
module MemberLiveness =

  type Liveness =
    { /// Open push streams per member. More than one is normal for a moment: a bridge that reconnects opens its new
      /// stream before the daemon notices the old one is dead.
      Streams: Map<MemberId, int>
      LastCall: Map<MemberId, DateTime> }

  let empty : Liveness = { Streams = Map.empty; LastCall = Map.empty }

  let streamOpened (key: MemberId) (state: Liveness) : Liveness =
    let open' = Map.tryFind key state.Streams |> Option.defaultValue 0
    { state with Streams = Map.add key (open' + 1) state.Streams }

  /// A close that has no open stream to match is dropped, so it can never leave a debt that hides the next stream.
  let streamClosed (key: MemberId) (state: Liveness) : Liveness =
    match Map.tryFind key state.Streams with
    | Some count when count > 1 -> { state with Streams = Map.add key (count - 1) state.Streams }
    | Some _ -> { state with Streams = Map.remove key state.Streams }
    | None -> state

  /// The latest call wins: a call recorded with an earlier clock than one already held does not move it back.
  let callMade (key: MemberId) (now: DateTime) (state: Liveness) : Liveness =
    match Map.tryFind key state.LastCall with
    | Some latest when latest >= now -> state
    | Some _
    | None -> { state with LastCall = Map.add key now state.LastCall }

  /// Live is an open stream, or a call inside `window` of `now`.
  let isLive (key: MemberId) (now: DateTime) (window: TimeSpan) (state: Liveness) : bool =
    Map.containsKey key state.Streams
    || (match Map.tryFind key state.LastCall with
        | Some at -> now - at <= window
        | None -> false)

  /// Forget the members that are not live at `now`: a call older than `window` says nothing `isLive` would act on,
  /// and the record of a member that has left was never removed. A member with an open stream keeps its record.
  /// `isLive` answers the same for every member before and after.
  let pruned (now: DateTime) (window: TimeSpan) (state: Liveness) : Liveness =
    { state with LastCall = state.LastCall |> Map.filter (fun _ at -> now - at <= window) }

  /// How many members the state holds a record for, streams and calls together.
  let tracked (state: Liveness) : int =
    Set.union (state.Streams |> Map.keys |> Set.ofSeq) (state.LastCall |> Map.keys |> Set.ofSeq) |> Set.count

  /// The daemon's one table: the pure state behind a lock, for the HTTP edge (which opens and closes streams) and the
  /// reaper (which asks and prunes).
  type Table() =
    let gate = obj ()
    let mutable state = empty
    member _.StreamOpened(key: MemberId) = lock gate (fun () -> state <- streamOpened key state)
    member _.StreamClosed(key: MemberId) = lock gate (fun () -> state <- streamClosed key state)
    member _.CallMade(key: MemberId, now: DateTime) = lock gate (fun () -> state <- callMade key now state)
    member _.IsLive(key: MemberId, now: DateTime, window: TimeSpan) = lock gate (fun () -> isLive key now window state)
    /// Drop the members that are not live (see `pruned`). The reaper calls it on each tick.
    member _.Prune(now: DateTime, window: TimeSpan) = lock gate (fun () -> state <- pruned now window state)
    /// How many members the table holds a record for.
    member _.Tracked() = lock gate (fun () -> tracked state)
