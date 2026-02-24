namespace SageFs

open System

/// The kind of output line in the REPL output area
[<RequireQualifiedAccess>]
type OutputKind =
  | Result
  | Error
  | Info
  | System

/// A single line of REPL output
type OutputLine = {
  Kind: OutputKind
  Text: string
  Timestamp: DateTime
  SessionId: string
}

/// File change actions for the event system
[<RequireQualifiedAccess>]
type FileWatchAction =
  | Changed
  | Created
  | Deleted
  | Renamed

/// Events that flow through the Elm loop, driving all UI updates.
/// Every state change in SageFs is expressed as one of these events.
[<RequireQualifiedAccess>]
type SageFsEvent =
  // ── Eval lifecycle ──
  | EvalStarted of sessionId: string * code: string
  | EvalCompleted of sessionId: string * output: string * diagnostics: Features.Diagnostics.Diagnostic list
  | EvalFailed of sessionId: string * error: string
  | EvalCancelled of sessionId: string
  // ── Session lifecycle ──
  | SessionCreated of SessionSnapshot
  | SessionsRefreshed of SessionSnapshot list
  | SessionStatusChanged of sessionId: string * status: SessionDisplayStatus
  | SessionSwitched of fromId: string option * toId: string
  | SessionStopped of sessionId: string
  | SessionStale of sessionId: string * inactiveDuration: TimeSpan
  // ── File watcher ──
  | FileChanged of path: string * action: FileWatchAction
  | FileReloaded of path: string * duration: TimeSpan * result: Result<string, string>
  // ── Editor state ──
  | CompletionReady of items: CompletionItem list
  | DiagnosticsUpdated of sessionId: string * diagnostics: Features.Diagnostics.Diagnostic list
  // ── Warmup ──
  | WarmupProgress of step: int * total: int * assemblyName: string
  | WarmupCompleted of duration: TimeSpan * failures: string list
  | WarmupContextUpdated of SessionContext
  // ── Live testing ──
  | TestLocationsDetected of locations: Features.LiveTesting.SourceTestLocation array
  | TestsDiscovered of tests: Features.LiveTesting.TestCase array
  | TestRunStarted of testIds: Features.LiveTesting.TestId array
  | TestResultsBatch of results: Features.LiveTesting.TestRunResult array
  | LiveTestingToggled of enabled: bool
  | AffectedTestsComputed of testIds: Features.LiveTesting.TestId array
  | CoverageUpdated of coverage: Features.LiveTesting.CoverageState
  | RunPolicyChanged of category: Features.LiveTesting.TestCategory * policy: Features.LiveTesting.RunPolicy
  | ProvidersDetected of providers: Features.LiveTesting.ProviderDescription list

/// The complete view state for any SageFs frontend.
/// Pure data — renderers read this to produce UI.
type SageFsView = {
  Buffer: ValidatedBuffer
  CompletionMenu: CompletionMenu option
  ActiveSession: SessionSnapshot
  RecentOutput: OutputLine list
  Diagnostics: Features.Diagnostics.Diagnostic list
  WatchStatus: WatchStatus option
}
