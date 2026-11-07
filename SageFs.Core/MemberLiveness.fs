namespace SageFs

open System

/// Whether a member is still there, as the cohort's lease reaper needs to know it.
///
/// The reaper used to renew a seat only for a member that had evaluated code in the last two minutes. A conductor
/// that waits on its agents, reads status and lands what they send never evaluates, so its lease ran out after
/// 30 minutes of perfectly good work (finding C5 of the 2026-11-07 cohort run). Two things say a member is present
/// without an eval: it made a tool call lately, and it holds an open push stream to the daemon (the stdio bridge
/// keeps one open for as long as its client lives, so a conductor that is only thinking is still connected).
///
/// A member is gone when it has no open stream and has made no call for the window. The keys are the text
/// `MemberTable.MemberId.display` gives, which is what the cohort's own member ids print as.
module MemberLiveness =

  type Liveness =
    { /// Open push streams per member. More than one is normal for a moment: a bridge that reconnects opens its new
      /// stream before the daemon notices the old one is dead.
      Streams: Map<string, int>
      LastCall: Map<string, DateTime> }

  let empty : Liveness = { Streams = Map.empty; LastCall = Map.empty }

  /// RED stub: remembers nothing.
  let streamOpened (key: string) (state: Liveness) : Liveness = state

  /// RED stub: remembers nothing.
  let streamClosed (key: string) (state: Liveness) : Liveness = state

  /// RED stub: remembers nothing.
  let callMade (key: string) (now: DateTime) (state: Liveness) : Liveness = state

  /// RED stub: nobody is live.
  let isLive (key: string) (now: DateTime) (window: TimeSpan) (state: Liveness) : bool = false

  /// The daemon's one table: the pure state behind a lock, for the HTTP edge (which opens and closes streams) and the
  /// reaper (which asks).
  type Table() =
    let gate = obj ()
    let mutable state = empty
    member _.StreamOpened(key: string) = lock gate (fun () -> state <- streamOpened key state)
    member _.StreamClosed(key: string) = lock gate (fun () -> state <- streamClosed key state)
    member _.CallMade(key: string, now: DateTime) = lock gate (fun () -> state <- callMade key now state)
    member _.IsLive(key: string, now: DateTime, window: TimeSpan) = lock gate (fun () -> isLive key now window state)
