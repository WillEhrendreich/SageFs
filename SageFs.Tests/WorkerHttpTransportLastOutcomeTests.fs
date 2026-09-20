/// HTTP round-trip for `GET /hotreload/last-outcome` — the actual server
/// boundary a client crosses (sagefs-ux-roast.md §11, Island C). The pure
/// `DevReload.LastReload` behaviour is pinned in DevReloadLastOutcomeTests;
/// this file proves the route is declared, mapped, loopback-reachable, and
/// serves the exact JSON `DevReload.LastReload.json()` produces — not a
/// re-derived shape.
module SageFs.Tests.WorkerHttpTransportLastOutcomeTests

open System.Net.Http
open Expecto
open Expecto.Flip
open SageFs
open SageFs.DevReload
open SageFs.Features.ReloadOutcome

module Broadcast = SageFs.Features.ReloadBroadcast

let private noopHandler (_: WorkerProtocol.WorkerMessage) : Async<WorkerProtocol.WorkerResponse> =
  async { return WorkerProtocol.WorkerResponse.WorkerShuttingDown }

let private startServer () =
  WorkerHttpTransport.startServer
    noopHandler (ref HotReloadState.empty) []
    (fun () -> WarmupContext.empty)
    (fun () -> fun _ -> async { return Features.LiveTesting.TestResult.NotRun })
    (fun () -> SageFs.HostAgent.AgentAnswered SageFs.HostAgent.NoCoverage)
    0

let private disposeServer (server: WorkerHttpTransport.HttpWorkerServer) =
  (server :> System.IDisposable).Dispose()

[<Tests>]
let hotReloadLastOutcomeHttpTests =
  testSequenced
  <| testList "WorkerHttpTransport./hotreload/last-outcome" [

    testCase "the route is declared GET/ReadOnly and actually mapped" <| fun _ ->
      WorkerHttpTransport.routes
      |> List.tryFind (fun r -> WorkerHttpTransport.WorkerRoute.path r = "/hotreload/last-outcome")
      |> Option.map WorkerHttpTransport.WorkerRoute.access
      |> Expect.equal "declared read-only, like every other worker GET" (Some WorkerHttpTransport.RouteAccess.ReadOnly)

    testTask "GET /hotreload/last-outcome serves DevReload.LastReload.json() verbatim" {
      Broadcast.broadcastOutcome (ReloadOutcome.Patched(1, 2))
      let expected = LastReload.json ()
      let! (server: WorkerHttpTransport.HttpWorkerServer) = startServer ()
      try
        let client = new HttpClient()
        // Explicit annotation: `open SageFs` also brings `RenderRegion`
        // (RenderPipeline.fs) into scope, which has its own `Content: string`
        // field — without pinning the type here, F#'s field-name resolution
        // for `resp.Content` picks the most recently opened record type
        // instead of `HttpResponseMessage`.
        let! (resp: HttpResponseMessage) = client.GetAsync(server.BaseUrl + "/hotreload/last-outcome")
        let! body = resp.Content.ReadAsStringAsync()
        body |> Expect.equal "the HTTP boundary must not re-derive or reshape the payload" expected
        body |> Expect.stringContains "the outcome case travels" "\"outcome\":\"Patched\""
      finally
        disposeServer server
    }

    testTask "the route maps exactly the declared table entry (no bypass, no drift)" {
      let! (server: WorkerHttpTransport.HttpWorkerServer) = startServer ()
      try
        server.MappedRoutes
        |> List.exists (fun (m, p) -> m = "GET" && p = "/hotreload/last-outcome")
        |> Expect.isTrue "the mapped route table must include the declared GET /hotreload/last-outcome"
      finally
        disposeServer server
    }
  ]
