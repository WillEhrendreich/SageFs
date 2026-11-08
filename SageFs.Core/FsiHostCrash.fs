namespace SageFs

/// How the isolated FSI host's process was seen to end.
type HostExit =
  /// The process exited and the client read its exit code.
  | ExitedWith of code: int
  /// The connection to the host closed and the process was not reported as exited in time.
  | ConnectionClosed
  /// The runtime wrote "Stack overflow." on the host's stderr before the process ended: code in an eval recursed
  /// past the eval thread's stack, and the runtime does not let a process survive that.
  | OverflowedStack
  /// An eval was cancelled and the host did not confirm that it had stopped within `waited`, so SageFs killed it.
  /// A native call, a loop that nothing can interrupt, or a very deep unwind keeps the eval thread from answering.
  | StoppedAfterCancel of waited: System.TimeSpan

/// What is known about a host that went away without being asked to: how it ended and the last thing it said.
/// The output is the end of the host's stdout and stderr, which is where an unhandled exception is written. For a
/// stack overflow it is the runtime's own report instead (its first lines), because the end of that output is a
/// few hundred identical frames.
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

  /// Lines of the runtime's stack overflow report kept: "Stack overflow.", then "Repeated N times:", the separator and the
  /// repeating frame, which names the function that recursed.
  [<Literal>]
  let stackOverflowReportLines = 4

  /// Whether a line of host output is the runtime announcing a stack overflow.
  let isStackOverflowLine (line: string) : bool =
    not (isNull line) && line.StartsWith("Stack overflow", System.StringComparison.Ordinal)

  /// A crash whose cause is a stack overflow, carrying the runtime's own report of it.
  let ofStackOverflow (report: string array) : HostCrash =
    { Exit = OverflowedStack
      Output = report |> Array.truncate stackOverflowReportLines |> String.concat "\n" }

  /// What happened and what it cost: the state of the session lives in the host, so all of it is gone.
  let describe (crash: HostCrash) : string =
    let lost = "so the state of this session and all of its definitions are gone."
    let said (heading: string) =
      match System.String.IsNullOrWhiteSpace crash.Output with
      | true -> "The host left no output."
      | false -> sprintf "%s\n%s" heading crash.Output
    match crash.Exit with
    | ExitedWith code -> sprintf "the FSI host crashed (exit %d), %s %s" code lost (said "Last output from the host:")
    | ConnectionClosed -> sprintf "the FSI host crashed (its connection closed), %s %s" lost (said "Last output from the host:")
    | OverflowedStack ->
      sprintf
        "the FSI host crashed (stack overflow), %s Code in the eval recursed past the end of the eval thread's stack, and the runtime ends the whole process when that happens. Make the recursion tail-recursive, or loop. %s"
        lost
        (said "The runtime reported:")
    | StoppedAfterCancel waited ->
      sprintf
        "the FSI host was stopped, because an eval did not stop within %.0f seconds of being cancelled (a native call, a loop nothing can interrupt, or a very deep unwind), %s %s"
        waited.TotalSeconds
        lost
        (said "Last output from the host:")

  /// What to do about it.
  let recovery =
    "Call hard_reset_fsi_session to start a fresh host, then run your definitions again. Fix the code that crashed the host first, or it will crash it again."
