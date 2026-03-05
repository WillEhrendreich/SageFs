module SageFs.DevReload

open System
open System.Collections.Concurrent
open System.Threading.Channels

/// Events that flow to browser clients over the long-lived SSE connection.
type DevReloadEvent =
  | Compiling
  | Reload

// Pure broadcaster — no ASP.NET dependency.
// The ASP.NET middleware lives in SageFs/DevReloadMiddleware.fs.
//
// clients is stored in AppDomain.CurrentDomain so that all copies of
// SageFs.Core.dll loaded in the same process (host + FSI shadow copies)
// share the same ConcurrentDictionary. Without this, broadcastReload() in
// the host DLL would iterate an empty dict while the browser's SSE client
// registered against the FSI shadow-copy DLL's dict.
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

let private broadcast (evt: DevReloadEvent) =
  let channels = getChannels ()
  let snapshot = channels |> Seq.map (fun kvp -> kvp.Key, kvp.Value) |> Seq.toList
  for (_id, ch) in snapshot do
    ch.Writer.TryWrite(evt) |> ignore

/// Signal all browsers that recompilation has started.
let broadcastCompiling () = broadcast Compiling

/// Signal all browsers that hot-reload is complete — time to refresh.
let broadcastReload () = broadcast Reload

/// Legacy alias — fires a Reload event to all clients.
let triggerReload () = broadcastReload ()

/// Register a new SSE client. Returns the ChannelReader for reading events.
let registerClient (id: string) =
  let ch = Channel.CreateUnbounded<DevReloadEvent>()
  let channels = getChannels ()
  // Close any existing channel for this id (shouldn't happen with GUIDs, but be safe)
  match channels.TryRemove(id) with
  | true, old -> old.Writer.TryComplete() |> ignore
  | _ -> ()
  channels.[id] <- ch
  Instrumentation.devReloadConnectedClients.Add(1L)
  ch.Reader

/// Unregister a client and close its channel.
let unregisterClient (id: string) =
  let channels = getChannels ()
  match channels.TryRemove(id) with
  | true, ch ->
    ch.Writer.TryComplete() |> ignore
    Instrumentation.devReloadConnectedClients.Add(-1L)
  | _ -> ()
