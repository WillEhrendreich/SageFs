/// The one startup timer and what Retry does after it fires.
///
/// WHY this module exists: one daemon start had two timers (30 s in `ensureRunning`, 120 s in
/// `startDaemon`) and two messages, and Retry ran `sagefs.restart`, which stops the daemon that
/// was about to come up (roast-5 finding 2). A daemon that loads a project can take a while,
/// so the give-up time is the longer of the two, and Retry probes before it restarts.
///
/// No Fable dependency; tested under `dotnet fsi` (tests/StartupContractTests.fsx).
module SageFs.Vscode.StartupPure

/// How long a started daemon gets to answer before the extension gives up. The poll ticks once
/// a second, so this is also the tick count.
let giveUpSeconds = 120

/// The longest daemon output shown in the give-up toast.
let snippetLimit = 500

/// The one give-up message. `daemonOutput` is whatever the daemon wrote to stderr.
let giveUpMessage (daemonOutput: string) : string =
  let head = sprintf "SageFs daemon did not start within %ds." giveUpSeconds
  match daemonOutput.Trim() with
  | "" -> head
  | s ->
    let shown = if s.Length > snippetLimit then s.Substring(0, snippetLimit) + "…" else s
    sprintf "%s\n\nDaemon output:\n%s" head shown

/// What a probe of the daemon's port found.
type DaemonProbe =
  | Answering
  | Silent

type RetryStep =
  /// The daemon came up after all: connect to it.
  | ConnectToRunningDaemon
  /// Nothing answers: stop what is left and start again.
  | RestartDaemon

let retryStep (probe: DaemonProbe) : RetryStep =
  match probe with
  | Answering -> ConnectToRunningDaemon
  | Silent -> RestartDaemon
