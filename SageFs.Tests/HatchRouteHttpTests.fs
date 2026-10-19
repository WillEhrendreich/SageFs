module SageFs.Tests.HatchRouteHttpTests

// Issue #160: POST /api/hatch/record is where the sagefs-repl-guard hook reports a use of the escape hatch. The route
// hands each report to the function it is mapped with (the daemon passes the live ledger's `HatchWatch.record`). This
// stands up only SageFs.Server.McpServer.mapHatchRoutes on a bare Kestrel host on an ephemeral loopback port, the
// in-process pattern WorkflowRouteHttpTests uses, with a recording function, so no daemon is spawned and the process's
// own ledger is not touched.

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Expecto
open Expecto.Flip
open SageFs

let private startTestServer (recorded: ConcurrentQueue<HatchUse>) = task {
  let builder = WebApplication.CreateBuilder([||])
  builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
  builder.Logging.ClearProviders() |> ignore
  let app = builder.Build()
  SageFs.Server.McpServer.mapHatchRoutes recorded.Enqueue app
  do! app.StartAsync()
  let server = app.Services.GetRequiredService<IServer>()
  let baseUrl = server.Features.Get<IServerAddressesFeature>().Addresses |> Seq.head
  return app, baseUrl
}

let private httpClient = new HttpClient()

let private post (url: string) (body: string) : Task<int * string> = task {
  use content = new StringContent(body, Encoding.UTF8, "application/json")
  let! resp = httpClient.PostAsync(url, content)
  let! text = resp.Content.ReadAsStringAsync()
  return int resp.StatusCode, text
}

let private declaredReady = { Declaration = GateDeclaration.DeclaredGate; Repl = ReplAtTheTime.Ready }

[<Tests>]
let hatchRouteHttpTests =
  testList "Hatch record HTTP route" [

    testTask "WHY — a report with both tokens is recorded as that kind and answers 200" {
      let recorded = ConcurrentQueue<HatchUse>()
      let! (app: WebApplication), baseUrl = startTestServer recorded
      try
        let! (status: int), (body: string) = post (baseUrl + "/api/hatch/record") """{"declaration":"declared","repl":"ready"}"""
        status |> Expect.equal "the report is accepted" 200
        JsonDocument.Parse(body).RootElement.GetProperty("success").GetBoolean() |> Expect.isTrue "and says so"
        recorded |> List.ofSeq |> Expect.equal "exactly that kind, once" [ declaredReady ]
      finally (app :> IDisposable).Dispose()
    }

    testTask "WHY — every kind the hook can name is accepted and recorded as itself, because the hook compiles the same closed type" {
      let recorded = ConcurrentQueue<HatchUse>()
      let! (app: WebApplication), baseUrl = startTestServer recorded
      try
        for hatch in HatchUse.all do
          let declaration, repl = HatchUse.toTokens hatch
          let! (status: int), (_body: string) =
            post (baseUrl + "/api/hatch/record") (JsonSerializer.Serialize {| declaration = declaration; repl = repl |})
          status |> Expect.equal (sprintf "%s/%s is accepted" declaration repl) 200
        recorded |> List.ofSeq |> Expect.equal "all six, in the order sent" HatchUse.all
      finally (app :> IDisposable).Dispose()
    }

    testTask "WHY — a token the daemon does not know is a 400 that names it and the accepted ones, and nothing is recorded" {
      let recorded = ConcurrentQueue<HatchUse>()
      let! (app: WebApplication), baseUrl = startTestServer recorded
      try
        let! (status: int), (body: string) = post (baseUrl + "/api/hatch/record") """{"declaration":"final-gate","repl":"ready"}"""
        status |> Expect.equal "refused" 400
        let doc = JsonDocument.Parse body
        doc.RootElement.GetProperty("success").GetBoolean() |> Expect.isFalse "not a success"
        doc.RootElement.GetProperty("error").GetString()
        |> Expect.stringContains "names the bad token and what is accepted" "\"final-gate\": it must be one of declared, undeclared"
        recorded.Count |> Expect.equal "the refused report counted for nothing" 0
      finally (app :> IDisposable).Dispose()
    }

    testTask "WHY — a report with nothing in it is a 400, not a default kind" {
      let recorded = ConcurrentQueue<HatchUse>()
      let! (app: WebApplication), baseUrl = startTestServer recorded
      try
        let! (status: int), (body: string) = post (baseUrl + "/api/hatch/record") "{}"
        status |> Expect.equal "refused" 400
        JsonDocument.Parse(body).RootElement.GetProperty("error").GetString()
        |> Expect.stringContains "says what is wrong" "unknown 'declaration'"
        recorded.Count |> Expect.equal "nothing recorded" 0
      finally (app :> IDisposable).Dispose()
    }

    testTask "WHY — a half-filled report is refused for the half that is wrong" {
      let recorded = ConcurrentQueue<HatchUse>()
      let! (app: WebApplication), baseUrl = startTestServer recorded
      try
        let! (status: int), (body: string) = post (baseUrl + "/api/hatch/record") """{"declaration":"declared","repl":"broken"}"""
        status |> Expect.equal "refused" 400
        JsonDocument.Parse(body).RootElement.GetProperty("error").GetString()
        |> Expect.stringContains "names the REPL state" "unknown 'repl' \"broken\""
        recorded.Count |> Expect.equal "nothing recorded" 0
      finally (app :> IDisposable).Dispose()
    }
  ]
