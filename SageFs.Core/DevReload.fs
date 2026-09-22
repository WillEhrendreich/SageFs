module SageFs.DevReload

open System
open System.Collections.Concurrent
open System.Threading.Channels
open SageFs.Utils

/// Centralized configuration for the DevReload system.
/// All timing/size constants that were previously scattered across
/// FileWatcher.fs, DevReloadMiddleware.fs, and the inline JS.
type DevReloadConfig = {
  /// FileSystemWatcher debounce delay (ms). Default: 200
  FileWatcherDebounceMs: int
  /// FileSystemWatcher internal buffer size (bytes). Default: 65536 (64KB)
  FileWatcherBufferSizeBytes: int
  /// Max response body size for script injection (bytes). Default: 10MB
  MaxBodyBufferSizeBytes: int64
  /// Double-compilation guard window (ms). Default: 500
  DoubleCompileGuardMs: int
  /// Browser SSE connection timeout (ms). Default: 3000
  SseConnectionTimeoutMs: int
  /// Reload-bomb reset window (ms). Default: 5000
  ReloadCountResetWindowMs: int
  /// Max reloads before pause. Default: 3
  ReloadGuardThreshold: int
  /// Compile timer update interval in browser (ms). Default: 200
  CompileTimerUpdateMs: int
  /// If compilation takes longer than this, show "click to reload"
  /// instead of auto-reloading. Prevents blowing away in-progress work. Default: 3000
  AutoReloadThresholdMs: int
  /// Compile timer turns amber after this many ms. Default: 5000
  LongCompileWarningMs: int
  /// How long one save waits for the previous save's compile before giving up
  /// and REPORTING that it did (ms). Default: 60000.
  ///
  /// Chesterton's fence: this wait used to be unbounded, which made one wedged
  /// eval silently disable hot reload for every other file for the rest of the
  /// session. Bounded, a stuck compiler is visible instead of invisible.
  CompileQueueWaitMs: int
  /// Ceiling on ONE save's re-evaluation (ms). Default: 300000 (5 min).
  ///
  /// This is the self-heal: the eval is posted with a token nothing else
  /// cancels, so without a deadline a single wedged submission owns the
  /// compiler forever. With one, the compiler is always handed back and the
  /// next save goes through.
  CompileBudgetMs: int
}

module DevReloadConfig =
  let defaults = {
    FileWatcherDebounceMs = 200
    FileWatcherBufferSizeBytes = 65536
    MaxBodyBufferSizeBytes = 10L * 1024L * 1024L
    DoubleCompileGuardMs = 500
    SseConnectionTimeoutMs = 3000
    ReloadCountResetWindowMs = 5000
    ReloadGuardThreshold = 3
    CompileTimerUpdateMs = 200
    AutoReloadThresholdMs = 3000
    LongCompileWarningMs = 5000
    CompileQueueWaitMs = 60_000
    CompileBudgetMs = 300_000
  }

/// Health status of the DevReload system. Queryable by any component
/// that needs to know if hot-reload is operational.
/// Follows Don Syme's "6-line state machine" pattern: named states with
/// logging at every transition, no framework needed.
type DevReloadHealth =
  | Disabled
  | PatchPending
  | PatchFailed of reason: string
  | Injected
  | Active of clientCount: int
  | Degraded of reason: string

module DevReloadHealthTracker =
  let private healthLock = obj()
  let mutable private currentHealth = Disabled
  let mutable private onTransition: (DevReloadHealth -> unit) option = None

  let current () = currentHealth

  let setTransitionCallback (cb: DevReloadHealth -> unit) =
    onTransition <- Some cb

  let clearTransitionCallback () =
    onTransition <- None

  let transition (newState: DevReloadHealth) =
    lock healthLock (fun () ->
      let prev = currentHealth
      currentHealth <- newState
      Log.info "[DevReload] Health: %A → %A" prev newState
      match onTransition with
      | Some cb -> cb newState
      | None -> ())

  let reset () =
    lock healthLock (fun () ->
      currentHealth <- Disabled
      onTransition <- None)

/// Structured diagnostic for browser error display. Carries source-mapped
/// line numbers (LineOffset already applied) so the browser shows correct
/// positions matching the user's source file.
type DevReloadDiagnostic = {
  File: string
  Line: int
  EndLine: int
  Column: int
  EndColumn: int
  Severity: string  // "error" | "warning" | "info" | "hidden"
  DiagCode: string option  // e.g. "FS0001" — named DiagCode to avoid field collision with EvalRequest.Code
  Message: string
  /// Lines around the error (±1 line context) for browser display.
  SourceContext: string array option
  /// 1-based line number of the first line in SourceContext.
  SourceContextStartLine: int option
}

module DevReloadDiagnostic =
  /// Enrich a diagnostic with source code context (±1 line around error).
  /// Returns the original diagnostic unchanged if the file doesn't exist
  /// or can't be read.
  let addSourceContext (diag: DevReloadDiagnostic) =
    try
      match System.IO.File.Exists(diag.File) with
      | false -> diag
      | true ->
        let lines = System.IO.File.ReadAllLines(diag.File)
        let startIdx = max 0 (diag.Line - 2)
        let endIdx = min (lines.Length - 1) diag.Line
        let context = lines.[startIdx..endIdx]
        { diag with
            SourceContext = Some context
            SourceContextStartLine = Some (startIdx + 1) }
    with _ -> diag

/// One refusal, already rendered into the two strings a client needs.
///
/// A client must never re-derive this wording. Re-deriving the REFRESH decision
/// from a method count is how the shipped bug happened; re-deriving the MESSAGE
/// from an outcome kind is the same mistake one layer up, and it guarantees
/// every client words the same failure differently.
type ReloadRefusal = {
  /// The `RestartReason` case name — a stable token to branch on or grep for,
  /// as opposed to prose that changes when the wording improves.
  Case: string
  /// `RestartReason.describe`: what happened, in terms of the user's own code.
  Message: string
  /// `RestartReason.remedy`: what to do about it.
  ///
  /// Named for the field SageFs's structured errors already use
  /// (`SageFsError.toJson`), deliberately: clients that learn to read
  /// `suggestedAction` then read every surface at once, instead of each
  /// surface inventing a name no client knows.
  SuggestedAction: string
}

/// Live state a save kept instead of resetting (rule 3 of the state spec), in
/// the shape a client renders: which binding, the value it kept, and the
/// initializer that waits for a reset.
type KeptStateReport = {
  Binding: string
  KeptValue: string
  NewInitializer: string
}

/// Everything a client needs to report a save without re-deriving anything.
///
/// Every field exists because some client could not do its job without it: the
/// counts so "0 of 448" reads as the non-event it is, `Outcome` so a client can
/// branch on the case rather than parse prose, `Reasons` so an editor can list
/// what blocked the reload, and `Message`/`SuggestedAction` so nobody has to
/// reconstruct the wording.
type ReloadReport = {
  /// The `ReloadOutcome` case name: Patched | Restarted | NoEffect |
  /// RestartRequired | CompileFailed.
  Outcome: string
  /// How many of the definitions this save changed are now live in the running
  /// process. Zero is meaningful and is never reported as a refresh.
  Patched: int
  /// How many definitions the save put in front of the process. Flutter prints
  /// "Reloaded 1 of 448 libraries" precisely so "0 of 448" is visible.
  Considered: int
  /// `ReloadOutcome.describeForUser` — what happened AND what to do, in one
  /// string. It deliberately repeats `SuggestedAction` because every client
  /// audited reads `message` and none reads `suggestedAction`; the remedy has
  /// to survive in the field clients actually look at.
  Message: string
  /// `ReloadOutcome.remedy`, or "" when the outcome is already resolved and the
  /// user needs to do nothing.
  SuggestedAction: string
  /// Why the save could not be applied, innermost first. Empty for a success.
  Reasons: ReloadRefusal list
  /// Live values the save kept. Empty unless you edited an initializer of
  /// state the app was holding.
  Kept: KeptStateReport list
}

/// Events that flow to clients (browser overlay, editors) over the long-lived
/// SSE connection.
///
/// The lifecycle is: Idle → Compiling → one terminal event, so a client can
/// never get stuck in "Compiling".
///
/// There is deliberately NO bare `Reload`. A page refreshing into byte-identical
/// code is the failure users read as "the tool is broken", and a case that says
/// only "refresh" cannot distinguish it from a real one. Every terminal case
/// names what happened to the RUNNING PROCESS and carries the whole report, so
/// "succeeded, changed nothing" is not something a caller can express as a
/// refresh: that is `NotApplied`, and `broadcastPatched` enforces it even for a
/// caller that tries.
type DevReloadEvent =
  | Compiling of fileName: string option
  /// Definitions were re-pointed into the running process, so the bytes a page
  /// would fetch are genuinely new.
  | Patched of report: ReloadReport
  /// SageFs restarted the app it started. The process IS current; the user is
  /// told what happened rather than asked to do anything.
  | Restarted of report: ReloadReport
  /// The save was processed and NOTHING in the running process changed. This is
  /// the case that used to be broadcast as a plain reload.
  | NotApplied of report: ReloadReport
  /// The file did not compile. The app is untouched and still serving the last
  /// code that did — which is a feature, and the report says so.
  | CompilationFailed of errorSummary: string * report: ReloadReport * diagnostics: DevReloadDiagnostic list

module ReloadReport =
  /// The report for an event that carries no outcome of its own.
  let none = { Outcome = ""; Patched = 0; Considered = 0; Message = ""; SuggestedAction = ""; Reasons = []; Kept = [] }

module DevReloadEvent =

  /// Will the page fetch different bytes than it already has? The single
  /// question every surface has, answered once — re-deriving it from a method
  /// count is exactly how the shipped bug happened.
  let refreshes =
    function
    | Patched _
    | Restarted _ -> true
    | Compiling _
    | NotApplied _
    | CompilationFailed _ -> false

  /// The outcome a client renders. `Compiling` has none — it is not terminal.
  let report =
    function
    | Patched r
    | Restarted r
    | NotApplied r
    | CompilationFailed(_, r, _) -> r
    | Compiling _ -> ReloadReport.none

  let private json (value: obj) = System.Text.Json.JsonSerializer.Serialize value

  let private refusalJson (r: ReloadRefusal) =
    sprintf """{"case":%s,"message":%s,"suggestedAction":%s}""" (json r.Case) (json r.Message) (json r.SuggestedAction)

  let private keptJson (k: KeptStateReport) =
    sprintf """{"binding":%s,"keptValue":%s,"newInitializer":%s}""" (json k.Binding) (json k.KeptValue) (json k.NewInitializer)

  /// `kept` only appears when something was kept, so every payload from before
  /// rule 3 existed is byte-for-byte what it was.
  let private reportFields (r: ReloadReport) =
    let kept =
      match r.Kept with
      | [] -> ""
      | ks -> sprintf ""","kept":[%s]""" (ks |> List.map keptJson |> String.concat ",")
    sprintf
      """"outcome":%s,"patched":%d,"considered":%d,"message":%s,"suggestedAction":%s,"reasons":[%s]%s"""
      (json r.Outcome)
      r.Patched
      r.Considered
      (json r.Message)
      (json r.SuggestedAction)
      (r.Reasons |> List.map refusalJson |> String.concat ",")
      kept

  /// The bare JSON payload for one event — no SSE framing. `sseData` wraps
  /// this into a `data: ...\n\n` frame for the long-lived stream; `LastReload`
  /// (below) hands the SAME string to a client that polls over plain HTTP
  /// instead — one function, so the pushed and the polled shape can never
  /// drift apart.
  ///
  /// `type` is the cue the browser overlay switches on; the rest is the full
  /// report, so a non-browser client (the Neovim plugin, an editor extension)
  /// can render the outcome without re-deriving a thing.
  let payloadJson (evt: DevReloadEvent) : string =
    match evt with
    | Compiling None -> """{"type":"compiling"}"""
    | Compiling (Some file) -> sprintf """{"type":"compiling","file":%s}""" (json file)
    | Patched r -> sprintf """{"type":"reload",%s}""" (reportFields r)
    | Restarted r -> sprintf """{"type":"restarted",%s}""" (reportFields r)
    | NotApplied r -> sprintf """{"type":"noeffect",%s}""" (reportFields r)
    | CompilationFailed(summary, r, diagnostics) ->
      // Chesterton's fence: send "error" (legacy string) alongside
      // "diagnostics" (structured array) and the report. The browser script
      // checks for diagnostics first and falls back to the error string —
      // backward compatible with older injected scripts.
      sprintf """{"type":"failed","error":%s,%s,"diagnostics":%s}""" (json summary) (reportFields r) (json diagnostics)

  /// One SSE `data:` frame. Every string is JSON-serialised, so a filename, a
  /// multi-line message or a compiler diagnostic can never break the frame it
  /// travels in — an embedded newline would split one event into two and a
  /// client would parse half a message.
  let sseData (evt: DevReloadEvent) : string =
    "data: " + payloadJson evt + "\n\n"

/// The most recent TERMINAL outcome, for a client that reaches the worker
/// over plain HTTP instead of holding the long-lived `/__sagefs__/reload`
/// stream open — a dashboard poll, a health check, or an editor extension
/// with no standing SSE connection to this port (roast: "Carry ReloadOutcome
/// past the server boundary" — this is that boundary). `Compiling` is never
/// recorded here: a poller must never be able to observe a phase that is
/// guaranteed to resolve into a terminal one, only the last outcome that
/// already did, so a request racing a save either sees the PREVIOUS save's
/// verdict or the new one once it lands — never a stuck "compiling".
module LastReload =
  // Chesterton's fence: same AppDomain-shared-state technique as `getChannels`
  // below, but storing the rendered JSON STRING rather than the
  // `DevReloadEvent` value itself. The host process and FSI-evaluated user
  // code can each hold their own shadow copy of SageFs.Core.dll, and a
  // broadcast from one copy must be readable from a poll against another —
  // a `System.String` has stable identity across copies of the same
  // assembly; a boxed DU value from one copy is not guaranteed to downcast
  // cleanly against another copy's type token, so the string sidesteps that
  // question entirely rather than relying on it working out.
  let private domainKey = "SageFs.DevReload.lastTerminalPayloadJson"

  /// Record the payload for the most recent terminal event. Called from the
  /// single `broadcast` choke point, so every `broadcastXxx` function updates
  /// this the same way it notifies live SSE subscribers.
  let record (evt: DevReloadEvent) =
    match evt with
    | Compiling _ -> ()
    | terminal ->
      let interned = String.Intern(domainKey)
      lock interned (fun () -> AppDomain.CurrentDomain.SetData(domainKey, DevReloadEvent.payloadJson terminal))

  /// The exact JSON a live SSE subscriber would have received for the last
  /// terminal event — `{outcome, patched, considered, message,
  /// suggestedAction, reasons}` alongside its `type`, so a polling client
  /// renders `ReloadOutcome.describeForUser` verbatim instead of
  /// re-deriving it. `{"type":"none"}` before the first save resolves.
  let json () : string =
    match AppDomain.CurrentDomain.GetData(domainKey) with
    | :? string as payload -> payload
    | _ -> """{"type":"none"}"""

// Pure broadcaster — no ASP.NET dependency.
// The ASP.NET middleware lives in SageFs/DevReloadMiddleware.fs.
//
// Chesterton's fence: clients is stored in AppDomain.CurrentDomain so that
// all copies of SageFs.Core.dll loaded in the same process (host + FSI
// shadow copies) share the same ConcurrentDictionary. Without this,
// broadcastReload() in the host DLL would iterate an empty dict while
// the browser's SSE client registered against the FSI shadow-copy DLL's
// dict. The String.Intern lock ensures exactly-once initialization even
// under concurrent access from multiple assemblies.
let private domainKey = "SageFs.DevReload.channels"

let private getChannels () : ConcurrentDictionary<string, Channel<DevReloadEvent>> =
  let interned = String.Intern(domainKey)
  lock interned (fun () ->
    match AppDomain.CurrentDomain.GetData(domainKey) with
    | :? ConcurrentDictionary<string, Channel<DevReloadEvent>> as dict -> dict
    | _ ->
      let dict = ConcurrentDictionary<string, Channel<DevReloadEvent>>()
      AppDomain.CurrentDomain.SetData(domainKey, dict)
      dict
  )

// Chesterton's fence: We iterate the ConcurrentDictionary directly rather
// than snapshotting to a list. ConcurrentDictionary supports concurrent
// enumeration — the snapshot was unnecessary allocation per broadcast.
let private eventLabel (evt: DevReloadEvent) =
  match evt with
  | Compiling None -> "Compiling"
  | Compiling (Some f) -> sprintf "Compiling(%s)" f
  | Patched r -> sprintf "Patched(%d of %d)" r.Patched r.Considered
  | Restarted _ -> "Restarted"
  | NotApplied r -> sprintf "NotApplied(0 of %d)" r.Considered
  | CompilationFailed _ -> "CompilationFailed"

let private broadcast (evt: DevReloadEvent) =
  LastReload.record evt
  let channels = getChannels ()
  let count = channels.Count
  match count with
  | 0 -> Log.debug "[DevReload] Broadcast %s — no clients connected" (eventLabel evt)
  | _ ->
    Log.debug "[DevReload] Broadcasting %s to %d client(s)" (eventLabel evt) count
    for kvp in channels do
      match kvp.Value.Writer.TryWrite(evt) with
      | true -> ()
      | false -> Log.warn "[DevReload] TryWrite failed for client %s (channel closed)" kvp.Key

/// Signal all browsers that recompilation has started.
/// Pass the filename for richer UI: "⟳ Recompiling Handlers.fs..."
let broadcastCompiling (fileName: string option) = broadcast (Compiling fileName)

/// Signal every client that the running process now serves new code.
///
/// Chesterton's fence — the demotion below is the whole point of this function.
/// The shipped bug was a caller that had re-pointed nothing (or only incidental
/// helpers) still telling every page to refresh, so the browser fetched the old
/// code while the tool reported success. A refresh is honest only when the bytes
/// are new, so "patched nothing" is turned into the outcome it actually is
/// rather than trusted. That makes the lie impossible at the lowest layer, not
/// merely discouraged at the highest.
let broadcastPatched (report: ReloadReport) =
  match report.Patched > 0 with
  | true -> broadcast (Patched report)
  | false ->
    Log.warn
      "[DevReload] Refusing to refresh clients for a save that patched nothing (0 of %d): %s"
      report.Considered report.Message
    broadcast (NotApplied report)

/// Signal every client that SageFs restarted the app it started. The page waits
/// for the app to come back and then refetches — the process IS current.
let broadcastRestarted (report: ReloadReport) = broadcast (Restarted report)

/// Signal every client that the save was processed and nothing in the running
/// process changed. Closes the Compiling overlay WITHOUT a refresh, and carries
/// what happened plus what to do about it.
let broadcastNotApplied (report: ReloadReport) = broadcast (NotApplied report)

/// Signal every client that compilation failed — show the error in the browser.
/// This prevents the "stuck Recompiling..." overlay that occurs when FSI eval
/// fails without sending any completion event.
/// Carries structured diagnostics with source-mapped line numbers for rich
/// error display. The errorSummary is kept for backward-compatible display.
let broadcastCompilationFailed (errorSummary: string) (report: ReloadReport) (diagnostics: DevReloadDiagnostic list) =
  broadcast (CompilationFailed(errorSummary, report, diagnostics))

/// Register a new SSE client. Returns the ChannelReader for reading events.
/// Also transitions health to Active with current client count.
let registerClient (id: string) =
  let ch = Channel.CreateUnbounded<DevReloadEvent>()
  let channels = getChannels ()
  match channels.TryRemove(id) with
  | true, old ->
    old.Writer.TryComplete() |> ignore
    Log.debug "[DevReload] Replaced duplicate client %s" id
  | _ -> ()
  channels.[id] <- ch
  Instrumentation.devReloadConnectedClients.Add(1L)
  let count = channels.Count
  DevReloadHealthTracker.transition (Active count)
  Log.debug "[DevReload] Client %s connected (now %d)" id count
  ch.Reader

/// Unregister a client and close its channel. Idempotent — safe to call
/// multiple times for the same id (second call is a no-op).
let unregisterClient (id: string) =
  let channels = getChannels ()
  match channels.TryRemove(id) with
  | true, ch ->
    ch.Writer.TryComplete() |> ignore
    Instrumentation.devReloadConnectedClients.Add(-1L)
    let remaining = channels.Count
    Log.debug "[DevReload] Client %s disconnected (now %d)" id remaining
    match remaining > 0 with
    | true -> DevReloadHealthTracker.transition (Active remaining)
    | false -> DevReloadHealthTracker.transition Injected
  | _ ->
    Log.debug "[DevReload] Unregister no-op for unknown client %s" id
