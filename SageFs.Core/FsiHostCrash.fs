namespace SageFs

/// How the isolated FSI host's process was seen to end.
type HostExit =
  /// The process exited and the client read its exit code.
  | ExitedWith of code: int
  /// The connection to the host closed and the process was not reported as exited in time.
  | ConnectionClosed

/// What is known about a host that went away without being asked to: how it ended and the last thing it said.
/// The output is the end of the host's stdout and stderr, which is where an unhandled exception is written.
type HostCrash = { Exit: HostExit; Output: string }

/// How a host's life ended. Retired is the session's own doing (a stop, a reset, a restart) and is never reported as a
/// failure; Crashed is everything else.
type HostEnd =
  | Retired
  | Crashed of HostCrash

module HostCrash =

  /// Lines of host output kept with a crash.
  [<Literal>]
  let maxOutputLines = 40

  /// Characters of host output kept with a crash, so a host that wrote one enormous line cannot bloat a status reply.
  [<Literal>]
  let maxOutputChars = 4000

  let private elision = "...\n"

  /// A crash with the END of `lines` kept, bounded in lines and in characters.
  let ofTail (exit: HostExit) (lines: string array) : HostCrash =
    let kept = lines |> Array.skip (max 0 (lines.Length - maxOutputLines)) |> String.concat "\n"
    let bounded =
      match kept.Length <= maxOutputChars with
      | true -> kept
      | false -> elision + kept.Substring(kept.Length - (maxOutputChars - elision.Length))
    { Exit = exit; Output = bounded }

  /// What happened and what it cost: the state of the session lives in the host, so all of it is gone.
  let describe (crash: HostCrash) : string =
    let how =
      match crash.Exit with
      | ExitedWith code -> sprintf "exit %d" code
      | ConnectionClosed -> "its connection closed"
    let said =
      match System.String.IsNullOrWhiteSpace crash.Output with
      | true -> "The host left no output."
      | false -> sprintf "Last output from the host:\n%s" crash.Output
    sprintf "the FSI host crashed (%s), so the state of this session and all of its definitions are gone. %s" how said

  /// What to do about it.
  let recovery =
    "Call hard_reset_fsi_session to start a fresh host, then run your definitions again. Fix the code that crashed the host first, or it will crash it again."
