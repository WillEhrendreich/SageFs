namespace SageFs

/// Running a callback the session manager's loop hands control to, so that one that throws is reported and the loop
/// carries on. A callback that escaped would restart the loop from the state before the command, losing what the
/// command did, so each one goes through here.
module Guarded =

  /// Run `action`; if it throws, hand the exception to `onFailure` instead of to the caller.
  let call (onFailure: exn -> unit) (action: unit -> unit) : unit =
    try action ()
    with ex -> onFailure ex
