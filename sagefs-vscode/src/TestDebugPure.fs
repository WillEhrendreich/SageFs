module SageFs.Vscode.TestDebugPure

// WHY — debugging a failing test from the editor needs four decisions that must be right and cannot be seen from the
// extension host: is there a .NET debugger to attach with (and if not, exactly what to install), what to attach it to,
// what to tell the person at each step the daemon reports, and when to stop waiting. They are pure, so they live here, with
// NO Fable dependency, and `tests/TestDebugContractTests.fsx` runs them under plain `dotnet fsi` in seconds, mirroring
// AppRunPure.fs. The Fable-aware TestDebugCommand.fs only performs what this module decides.
//
// The daemon side is SageFs.Core/DebugTestRequest.fs. `DebugAnswerView` mirrors its `DebugWire` field for field, and the
// contract test reads that source file as data, so a status the daemon starts to send that this module does not know
// fails the test instead of falling through to a blank dialog.

open SageFs.Vscode.LiveTestingTypes

// ── What the daemon answers ──────────────────────────────────────

/// Mirrors DebugTestRequest.DebugWire (SageFs.Core/DebugTestRequest.fs), field for field. A field that does not apply to the
/// status is empty or zero.
type DebugAnswerView = {
  Status: string
  Message: string
  Pid: int
  Ticket: string
  TestId: string
  TestName: string
  Symbols: string
  SymbolsNote: string
  Access: string
  AccessNote: string
  HoldMs: int
  Outcome: string
  Detail: string
  DurationMs: float
}

/// Every status the daemon spells (DebugTestRequest.DebugStatus.wire), plus the one this client cannot read. A status it
/// does not know is carried, never dropped, so the person still gets the daemon's own message.
[<RequireQualifiedAccess>]
type DebugStatus =
  | Held
  | StillRunning
  | Attached
  | NoDebuggerWithin
  | ReleasedWithoutDebugger
  | NoSuchHold
  | HostLost
  | HoldAlreadyOpen
  | HostUnavailable
  | NotDiscovered
  | NoTestMatched
  | AmbiguousTest
  | NoWorker
  | NoSession
  | WorkerFailed
  | BadRequest
  | Unrecognised of wire: string

let statusOfWire (wire: string) : DebugStatus =
  match wire with
  | "held" -> DebugStatus.Held
  | "still_running" -> DebugStatus.StillRunning
  | "attached" -> DebugStatus.Attached
  | "no_debugger_within" -> DebugStatus.NoDebuggerWithin
  | "released_without_debugger" -> DebugStatus.ReleasedWithoutDebugger
  | "no_such_hold" -> DebugStatus.NoSuchHold
  | "host_lost" -> DebugStatus.HostLost
  | "hold_already_open" -> DebugStatus.HoldAlreadyOpen
  | "host_unavailable" -> DebugStatus.HostUnavailable
  | "not_discovered" -> DebugStatus.NotDiscovered
  | "no_test_matched" -> DebugStatus.NoTestMatched
  | "ambiguous_test" -> DebugStatus.AmbiguousTest
  | "no_worker" -> DebugStatus.NoWorker
  | "no_session" -> DebugStatus.NoSession
  | "worker_failed" -> DebugStatus.WorkerFailed
  | "bad_request" -> DebugStatus.BadRequest
  | other -> DebugStatus.Unrecognised other

// ── Something to say ─────────────────────────────────────────────

[<RequireQualifiedAccess>]
type Severity =
  | Info
  | Warning
  | Error

/// One thing to tell the person, and how loudly.
type Notice = { Severity: Severity; Text: string }

let private info (text: string) = { Severity = Severity.Info; Text = text }
let private warning (text: string) = { Severity = Severity.Warning; Text = text }
let private error (text: string) = { Severity = Severity.Error; Text = text }

// ── The .NET debugger ────────────────────────────────────────────

/// The debug types an installed extension contributes, read from its package.json (`contributes.debuggers[].type`).
type ExtensionDebuggers = {
  ExtensionId: string
  DebuggerTypes: string list
}

/// The debug type that attaches to a running .NET process.
[<Literal>]
let CoreclrDebuggerType = "coreclr"

/// The extension that provides it in Microsoft's VS Code.
[<Literal>]
let RecommendedExtensionId = "ms-dotnettools.csharp"

[<RequireQualifiedAccess>]
type DebuggerAvailability =
  /// An installed extension contributes the `coreclr` debug type.
  | Installed of extensionId: string
  | Missing

/// Whether anything installed can attach to a .NET process. Decided by what an extension CONTRIBUTES, not by its name,
/// so a debugger SageFs has never heard of counts. When several do, the recommended one wins, then the first by id, so
/// the answer does not depend on the order VS Code lists extensions in.
let chooseDebugger (extensions: ExtensionDebuggers list) : DebuggerAvailability =
  let providers =
    extensions
    |> List.filter (fun e -> e.DebuggerTypes |> List.contains CoreclrDebuggerType)
    |> List.sortBy (fun e -> e.ExtensionId)
  match providers |> List.tryFind (fun e -> e.ExtensionId = RecommendedExtensionId), providers with
  | Some recommended, _ -> DebuggerAvailability.Installed recommended.ExtensionId
  | None, first :: _ -> DebuggerAvailability.Installed first.ExtensionId
  | None, [] -> DebuggerAvailability.Missing

/// What to say when there is no debugger: what is missing, exactly what to install, and how.
let missingDebuggerNotice : Notice =
  error (
    sprintf
      "SageFs debugs a test by attaching a .NET debugger to the process that runs it, and no installed extension provides the '%s' debug type. Install the C# extension (%s), then debug the test again. From a terminal: code --install-extension %s. Microsoft's debugger is licensed for Microsoft's own build of VS Code; in another build, install an extension that provides the '%s' debug type."
      CoreclrDebuggerType
      RecommendedExtensionId
      RecommendedExtensionId
      CoreclrDebuggerType)

/// The VS Code configuration that attaches to the test host.
type AttachConfiguration = {
  Type: string
  Request: string
  Name: string
  ProcessId: int
}

/// What to attach to: the process the daemon named. The name carries the ticket, so a debug session can be told from any
/// other the person has open.
let attachConfiguration (view: DebugAnswerView) : AttachConfiguration =
  { Type = CoreclrDebuggerType
    Request = "attach"
    Name = sprintf "SageFs test: %s (%s)" view.TestName view.Ticket
    ProcessId = view.Pid }

// ── Beginning ────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type BeginStep =
  /// Attach with this configuration, after saying these things.
  | AttachDebugger of configuration: AttachConfiguration * notices: Notice list
  | Refuse of Notice

/// What to do with the daemon's answer to "debug this test".
let beginStep (view: DebugAnswerView) : BeginStep =
  let reason (severity: Severity) = BeginStep.Refuse { Severity = severity; Text = view.Message }
  match statusOfWire view.Status with
  | DebugStatus.Held ->
    let symbols =
      match view.Symbols with
      | "eval" -> [ warning view.SymbolsNote ]
      | _ -> []
    let access =
      match view.Access with
      | "blocked" -> [ warning (sprintf "The operating system may refuse the attach. %s" view.AccessNote) ]
      | _ -> []
    BeginStep.AttachDebugger(attachConfiguration view, symbols @ access)
  | DebugStatus.NotDiscovered
  | DebugStatus.NoTestMatched
  | DebugStatus.AmbiguousTest
  | DebugStatus.HoldAlreadyOpen -> reason Severity.Warning
  | DebugStatus.NoSession
  | DebugStatus.NoWorker
  | DebugStatus.HostUnavailable
  | DebugStatus.WorkerFailed
  | DebugStatus.BadRequest -> reason Severity.Error
  // A continue-only status answering a begin, or one this client does not know: still the daemon's own words.
  | DebugStatus.StillRunning
  | DebugStatus.Attached
  | DebugStatus.NoDebuggerWithin
  | DebugStatus.ReleasedWithoutDebugger
  | DebugStatus.NoSuchHold
  | DebugStatus.HostLost
  | DebugStatus.Unrecognised _ -> reason Severity.Error

/// What to say when the debugger would not start attaching (VS Code answered that the session did not start).
let attachFailedNotice (view: DebugAnswerView) : Notice =
  error (
    sprintf
      "The debugger did not attach to process %d, so %s was not run. The test host drops the hold by itself. Check the Debug Console for why, then debug the test again."
      view.Pid
      view.TestName)

// ── Continuing ───────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type ContinueStep =
  /// The test is still running under the debugger: ask again.
  | KeepWaiting
  /// The test finished under the debugger: say how, and detach.
  | Finished of Notice
  /// The test did not run, or the host went away: say why, and detach.
  | Abandon of Notice

let private durationText (ms: float) : string =
  match ms > 0.0 with
  | true -> sprintf " (%.0f ms)" ms
  | false -> ""

/// What to do with the daemon's answer to "release it and tell me when it finishes".
let continueStep (testName: string) (view: DebugAnswerView) : ContinueStep =
  match statusOfWire view.Status with
  | DebugStatus.StillRunning -> ContinueStep.KeepWaiting
  | DebugStatus.Attached ->
    match view.Outcome with
    | "passed" -> ContinueStep.Finished(info (sprintf "%s passed under the debugger%s." testName (durationText view.DurationMs)))
    | "failed" ->
      ContinueStep.Finished(warning (sprintf "%s failed under the debugger%s: %s" testName (durationText view.DurationMs) view.Detail))
    | "skipped" -> ContinueStep.Finished(info (sprintf "%s was skipped: %s" testName view.Detail))
    | "not_run" -> ContinueStep.Finished(warning (sprintf "%s did not run: the host does not know the test." testName))
    | _ -> ContinueStep.Finished(warning (sprintf "%s ended without a result: %s" testName view.Detail))
  | DebugStatus.NoDebuggerWithin
  | DebugStatus.ReleasedWithoutDebugger
  | DebugStatus.NoSuchHold -> ContinueStep.Abandon(warning view.Message)
  | DebugStatus.HostLost
  | DebugStatus.HostUnavailable
  | DebugStatus.NoSession
  | DebugStatus.NoWorker
  | DebugStatus.WorkerFailed
  | DebugStatus.BadRequest
  | DebugStatus.Held
  | DebugStatus.NotDiscovered
  | DebugStatus.NoTestMatched
  | DebugStatus.AmbiguousTest
  | DebugStatus.HoldAlreadyOpen
  | DebugStatus.Unrecognised _ -> ContinueStep.Abandon(error view.Message)

/// What to say when the debug session ended while the test was still running: the test is not stopped by that, so its
/// result arrives with the next live-testing run instead.
let sessionEndedNotice (testName: string) : Notice =
  info (sprintf "The debug session ended while %s was still running. The test host finishes it without a debugger, and the result shows up with the next live-testing run." testName)

/// What to say when the daemon cannot be reached at all.
let unreachableNotice (detail: string) : Notice =
  error (sprintf "Could not reach SageFs to debug the test: %s" detail)

// ── Where the debug entry points are ─────────────────────────────

/// Whether a test's result offers a Debug action next to it. A passing test has nothing to chase; one that failed or
/// errored does. (The Test Explorer's Debug profile offers it for every test regardless.)
[<RequireQualifiedAccess>]
type DebugOffer =
  | Offered
  | NotOffered

let debugOfferFor (outcome: VscTestOutcome) : DebugOffer =
  match outcome with
  | VscTestOutcome.Failed _
  | VscTestOutcome.Errored _ -> DebugOffer.Offered
  | VscTestOutcome.Passed
  | VscTestOutcome.Skipped _
  | VscTestOutcome.Running
  | VscTestOutcome.Stale
  | VscTestOutcome.PolicyDisabled
  | VscTestOutcome.NotYetRun -> DebugOffer.NotOffered

/// The command the Debug lens, the hover link and the Test Explorer profile all run.
[<Literal>]
let DebugCommandId = "sagefs.debugTest"

/// The CodeLens title of the Debug action: plain words, like the lenses beside it.
[<Literal>]
let DebugLensTitle = "Debug"

/// What the Debug lens says when hovered.
let debugLensTooltip (testName: string) : string =
  sprintf "Attach a .NET debugger to the test host and run %s under it" testName

/// Text as the inside of a JSON string: the quote and backslash, and every control character, which JSON forbids bare.
let private escapeJson (text: string) : string =
  text
  |> Seq.map (fun c ->
    match c with
    | '"' -> "\\\""
    | '\\' -> "\\\\"
    | c when c < ' ' -> sprintf "\\u%04x" (int c)
    | c -> string c)
  |> String.concat ""

/// A markdown link that runs the debug command for one test, for a hover. The argument travels as a URL-encoded JSON array,
/// which is how VS Code passes arguments through a `command:` link.
let debugCommandLink (testId: string) : string =
  let json = sprintf "[\"%s\"]" (escapeJson testId)
  sprintf "[Debug this test](command:%s?%s)" DebugCommandId (System.Uri.EscapeDataString json)

/// Make text safe to show inside markdown: a test's own failure message must read as written, not as formatting or a link.
let escapeMarkdown (text: string) : string =
  let special = set [ '\\'; '`'; '*'; '_'; '['; ']'; '('; ')'; '<'; '>'; '#'; '|'; '~' ]
  text
  |> Seq.map (fun c -> match Set.contains c special with | true -> sprintf "\\%c" c | false -> string c)
  |> String.concat ""

/// The hover of a failing test's gutter mark, shown as markdown, with the Debug link under it.
let hoverWithDebugLink (hover: string) (testId: string) : string =
  sprintf "%s\n\n%s" (escapeMarkdown hover) (debugCommandLink testId)
