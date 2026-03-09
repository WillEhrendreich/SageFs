namespace SageFs

open System.Threading.Tasks

/// Pure escalation logic for one-button session reset.
/// Tries soft reset first, auto-escalates to hard reset on failure.
module SmartReset =

  /// Result of a smart reset attempt.
  [<Struct; RequireQualifiedAccess>]
  type Outcome =
    | SoftResetSucceeded
    | EscalatedToHardReset of message: string
    | AllResetsFailed of softError: string * hardError: string

  /// Execute a smart reset: soft first, escalate to hard on failure.
  let execute
    (softReset: unit -> Task<Result<unit, string>>)
    (hardReset: unit -> Task<Result<string, string>>)
    : Task<Outcome> = task {
    match! softReset () with
    | Ok () -> return Outcome.SoftResetSucceeded
    | Error softErr ->
      match! hardReset () with
      | Ok msg -> return Outcome.EscalatedToHardReset msg
      | Error hardErr -> return Outcome.AllResetsFailed(softErr, hardErr)
  }

  /// Human-readable description of what happened.
  let describe = function
    | Outcome.SoftResetSucceeded ->
      "Session reset. All definitions cleared."
    | Outcome.EscalatedToHardReset msg ->
      $"Soft reset failed — escalated to hard reset. {msg}"
    | Outcome.AllResetsFailed(s, h) ->
      $"All resets failed. Soft: {s}. Hard: {h}"
