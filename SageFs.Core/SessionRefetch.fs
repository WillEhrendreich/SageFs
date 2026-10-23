namespace SageFs

open SageFs.WorkerProtocol

/// The one post every daemon-side caller of an in-worker FSI host swap makes once the swap resolves Ok.
/// A soft reset and an in-worker hard reset both leave the daemon holding a test discovery and an
/// instrumentation map of the copy the OLD host ran; the fresh host instruments a fresh shadow copy, so
/// probe numbering changes under what the daemon holds and the session must be asked again.
module SessionRefetch =

  /// Fire-and-forget: the mailbox's `RefetchLiveTestingState` arm applies `WorkerPostReady.RefetchGate`,
  /// so a session whose worker is not settled is refused (and logged) rather than half-refreshed, and an
  /// answer from a worker that has since been replaced is dropped there. Callers reach this through the
  /// `RefetchLiveTestingState` op their daemon wired; a reset that did NOT resolve Ok calls it zero times —
  /// there is no fresh copy to ask about.
  let postRefetchLiveTestingStateAfterReset (inbox: MailboxProcessor<SessionManager.SessionCommand>) (id: SessionId) : unit =
    inbox.PostAndAsyncReply(fun reply -> SessionManager.SessionCommand.RefetchLiveTestingState(id, reply))
    |> ignore
