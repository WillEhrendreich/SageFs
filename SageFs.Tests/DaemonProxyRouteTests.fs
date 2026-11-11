module SageFs.Tests.DaemonProxyRouteTests

open System.Net.Http
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Server

// The dashboard's hot-reload controls reach a worker through the daemon, which forwards `/api/sessions/{sid}<worker path>`.
// The worker path is the worker's own route, so it is named by `WorkerRoute` and written nowhere else: the daemon used to
// carry eleven copies of those strings, and a renamed route would have left the proxy forwarding to a path nothing serves.

let prefix = DaemonMode.workerProxyPrefix

/// What the daemon forwards, as the two things a request is routed by.
let forwarded : RequestRoute list =
  DaemonMode.createHotReloadProxyEndpoints (fun _ -> None) (new HttpClient()) (Event<SseEvent>())
  |> List.collect (fun endpoint ->
    endpoint.Handlers
    |> Seq.map (fun (verb, _) -> { HttpMethod = string verb; Path = endpoint.Pattern.Substring prefix.Length })
    |> List.ofSeq)

/// The routes the hot-reload panel and the warmup context read need from a worker, by name.
let expected : WorkerRoute list =
  [ WorkerRoute.HotReload
    WorkerRoute.HotReloadToggle
    WorkerRoute.HotReloadResetState
    WorkerRoute.HotReloadReflectionMode
    WorkerRoute.HotReloadWatchAll
    WorkerRoute.HotReloadUnwatchAll
    WorkerRoute.HotReloadWatchProject
    WorkerRoute.HotReloadUnwatchProject
    WorkerRoute.HotReloadWatchDirectory
    WorkerRoute.HotReloadUnwatchDirectory
    WorkerRoute.SessionWarmupContext ]

[<Tests>]
let daemonProxyRouteTests =
  testList "daemon worker proxy routes" [
    testCase "WHY — the proxy forwards exactly the worker routes it names, each by the method and path the worker serves it on" <| fun _ ->
      let named =
        expected |> List.map (fun route -> { HttpMethod = WorkerRoute.httpMethod route; Path = WorkerRoute.path route })
      forwarded |> List.sort |> Expect.equal "forwarded routes" (List.sort named)

    testCase "WHY — the proxy table is the named routes, so a route added to it is a case of WorkerRoute" <| fun _ ->
      DaemonMode.hotReloadProxyRoutes |> List.sort |> Expect.equal "table" (List.sort expected)
  ]
