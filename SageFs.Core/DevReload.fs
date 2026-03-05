module SageFs.DevReload

open System
open System.Collections.Concurrent
open System.Threading.Channels

/// Events that flow to browser clients over the long-lived SSE connection.
/// The lifecycle is: Idle → Compiling → (Reload | CompilationFailed).
/// Three cases ensure the browser can never get stuck in "Compiling" state —
/// every Compiling event is eventually followed by Reload or CompilationFailed.
type DevReloadEvent =
  | Compiling of fileName: string option
  | Reload
  | CompilationFailed of errorSummary: string

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
let broadcastCompilationFailed (errorSummary: string) =
  broadcast (CompilationFailed errorSummary)

/// Legacy alias — fires a Reload event to all clients.
let triggerReload () = broadcastReload ()

/// Register a new SSE client. Returns the ChannelReader for reading events.
let registerClient (id: string) =
  let ch = Channel.CreateUnbounded<DevReloadEvent>()
  let channels = getChannels ()
  match channels.TryRemove(id) with
  | true, old -> old.Writer.TryComplete() |> ignore
  | _ -> ()
  channels.[id] <- ch
  Instrumentation.devReloadConnectedClients.Add(1L)
  ch.Reader

/// Unregister a client and close its channel. Idempotent — safe to call
/// multiple times for the same id (second call is a no-op).
let unregisterClient (id: string) =
  let channels = getChannels ()
  match channels.TryRemove(id) with
  | true, ch ->
    ch.Writer.TryComplete() |> ignore
    Instrumentation.devReloadConnectedClients.Add(-1L)
  | _ -> ()
