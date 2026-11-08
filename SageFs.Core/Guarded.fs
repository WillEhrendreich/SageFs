namespace SageFs

/// Running a callback the session manager's loop hands control to, so that one that throws is reported and the loop
/// carries on. A callback that escaped would restart the loop from the state before the command, losing what the
/// command did, so each one goes through here.
module Guarded =

  /// How a piece of work ended: with its answer, or with what it threw.
  [<RequireQualifiedAccess>]
  type Attempt<'a> =
    | Completed of 'a
    | Threw of exn

  /// Run `work` and say how it ended, so a caller reads the outcome as a value and keeps no cell to be filled in by
  /// a handler.
  let attempt (work: unit -> 'a) : Attempt<'a> =
    try Attempt.Completed (work ())
    with ex -> Attempt.Threw ex

  /// Run `action`; if it throws, hand the exception to `onFailure` instead of to the caller.
  let call (onFailure: exn -> unit) (action: unit -> unit) : unit =
    match attempt action with
    | Attempt.Completed () -> ()
    | Attempt.Threw ex -> onFailure ex
