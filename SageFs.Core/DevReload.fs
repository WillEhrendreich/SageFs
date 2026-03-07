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
}

/// Events that flow to browser clients over the long-lived SSE connection.
/// The lifecycle is: Idle → Compiling → (Reload | CompilationFailed).
/// Three cases ensure the browser can never get stuck in "Compiling" state —
/// every Compiling event is eventually followed by Reload or CompilationFailed.
type DevReloadEvent =
  | Compiling of fileName: string option
  | Reload
  | CompilationFailed of errorSummary: string * diagnostics: DevReloadDiagnostic list

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
let private broadcast (evt: DevReloadEvent) =
  let channels = getChannels ()
  for kvp in channels do
    kvp.Value.Writer.TryWrite(evt) |> ignore

/// Signal all browsers that recompilation has started.
/// Pass the filename for richer UI: "⟳ Recompiling Handlers.fs..."
let broadcastCompiling (fileName: string option) = broadcast (Compiling fileName)

/// Signal all browsers that hot-reload is complete — time to refresh.
let broadcastReload () = broadcast Reload

/// Signal all browsers that compilation failed — show the error in the browser.
/// This prevents the "stuck Recompiling..." overlay that occurs when FSI eval
/// fails without sending any completion event.
/// Carries structured diagnostics with source-mapped line numbers for rich
/// error display. The errorSummary is kept for backward-compatible display.
let broadcastCompilationFailed (errorSummary: string) (diagnostics: DevReloadDiagnostic list) =
  broadcast (CompilationFailed(errorSummary, diagnostics))

/// Legacy alias — fires a Reload event to all clients.
let triggerReload () = broadcastReload ()

/// Register a new SSE client. Returns the ChannelReader for reading events.
/// Also transitions health to Active with current client count.
let registerClient (id: string) =
  let ch = Channel.CreateUnbounded<DevReloadEvent>()
  let channels = getChannels ()
  match channels.TryRemove(id) with
  | true, old -> old.Writer.TryComplete() |> ignore
  | _ -> ()
  channels.[id] <- ch
  Instrumentation.devReloadConnectedClients.Add(1L)
  DevReloadHealthTracker.transition (Active channels.Count)
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
    match remaining > 0 with
    | true -> DevReloadHealthTracker.transition (Active remaining)
    | false -> DevReloadHealthTracker.transition Injected
  | _ -> ()
