module SageFs.DevReload

open System
open System.Collections.Concurrent
open System.Threading.Tasks

// Pure broadcaster — no ASP.NET dependency.
// The ASP.NET middleware lives in SageFs/DevReloadMiddleware.fs.
//
// clients is stored in AppDomain.CurrentDomain so that all copies of
// SageFs.Core.dll loaded in the same process (host + FSI shadow copies)
// share the same ConcurrentDictionary. Without this, triggerReload() in
// the host DLL would iterate an empty dict while the browser's SSE client
// registered against the FSI shadow-copy DLL's dict.
let private domainKey = "SageFs.DevReload.clients"

let private getClients () : ConcurrentDictionary<string, TaskCompletionSource<unit>> =
  let interned = String.Intern(domainKey)
  lock interned (fun () ->
    match AppDomain.CurrentDomain.GetData(domainKey) with
    | :? ConcurrentDictionary<string, TaskCompletionSource<unit>> as dict -> dict
    | _ ->
      let dict = ConcurrentDictionary<string, TaskCompletionSource<unit>>()
      AppDomain.CurrentDomain.SetData(domainKey, dict)
      dict
  )

let triggerReload () =
  let clients = getClients ()
  let snapshot = clients |> Seq.map (fun kvp -> kvp.Key, kvp.Value) |> Seq.toList
  for (id, tcs) in snapshot do
    clients.TryRemove(id) |> ignore
    tcs.TrySetResult() |> ignore

let registerClient (id: string) =
  let tcs = TaskCompletionSource<unit>()
  let clients = getClients ()
  clients.[id] <- tcs
  tcs

let unregisterClient (id: string) =
  let clients = getClients ()
  clients.TryRemove(id) |> ignore
