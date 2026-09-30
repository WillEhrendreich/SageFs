module SageFs.Tests.UnexpectedErrorLoggingTests

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open SageFs.Server

/// One entry a captured logger saw.
type private Captured = { Level: LogLevel; Message: string; Error: exn | null }

/// An ILoggerProvider that records every entry, so a test can assert on what
/// the daemon's own log pipeline was handed rather than on a file.
type private CapturingProvider(sink: ConcurrentQueue<Captured>) =
  interface ILoggerProvider with
    member _.CreateLogger(_category: string) : ILogger =
      { new ILogger with
          member _.BeginScope<'S>(_state: 'S) : IDisposable | null = null
          member _.IsEnabled(_level: LogLevel) = true
          member _.Log<'S>(level: LogLevel, _eventId: EventId, state: 'S, error: exn | null, formatter: Func<'S, exn | null, string>) =
            sink.Enqueue { Level = level; Message = formatter.Invoke(state, error); Error = error } }
    member _.Dispose() = ()

let private client = new HttpClient()

let private boom = InvalidOperationException("the handler blew up")

/// The daemon's real error middleware in front of a route that throws.
let private startThrowingHost (sink: ConcurrentQueue<Captured>) : Task<WebApplication> = task {
  let builder = WebApplication.CreateBuilder([||])
  builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
  builder.Logging.ClearProviders().AddProvider(new CapturingProvider(sink)) |> ignore
  let app = builder.Build()
  app.Use(Func<HttpContext, Func<Task>, Task>(fun ctx next ->
    McpServer.errorHandlingMiddleware ctx next :> Task)) |> ignore
  app.MapGet("/exec", RequestDelegate(fun _ -> raise boom)) |> ignore
  do! app.StartAsync()
  return app
}

/// Hit the throwing route once; return the HTTP status, the body, and what the
/// daemon's log pipeline was handed.
let private hitThrowingRoute () : Task<int * string * Captured list> = task {
  let sink = ConcurrentQueue<Captured>()
  let! app = startThrowingHost sink
  let! response = client.GetAsync(Seq.head app.Urls + "/exec")
  let! body = response.Content.ReadAsStringAsync()
  do! app.StopAsync()
  return int response.StatusCode, body, Seq.toList sink
}

let private requestIdOf (body: string) : string =
  use json = JsonDocument.Parse(body)
  json.RootElement.GetProperty("requestId").GetString() |> Option.ofObj |> Option.defaultValue ""

[<Tests>]
let unexpectedErrorLoggingTests =
  testList "Unexpected HTTP errors are logged and carry a request id" [
    testTask "WHY — a 500 must leave the exception in the daemon log, or a user can only shrug at 'Unexpected error'" {
      let! result = hitThrowingRoute ()
      let (status: int), (body: string), (captured: Captured list) = result
      status |> Expect.equal "the middleware still answers 500" 500
      let requestId = requestIdOf body
      (requestId.Length > 0)
      |> Expect.isTrue "a short id a user can quote, not empty"
      (requestId.Length <= 12)
      |> Expect.isTrue "short enough to read out loud"
      body |> Expect.stringContains "the error text names the id too, for clients that print only it" requestId

      let errors = captured |> List.filter (fun c -> c.Level = LogLevel.Error)
      errors |> List.isEmpty |> Expect.isFalse "an Error entry reached the log"
      errors
      |> List.exists (fun c -> obj.ReferenceEquals(c.Error, boom))
      |> Expect.isTrue "the entry carries the real exception, so the stack trace is in the file"
      errors
      |> List.exists (fun c -> c.Message.Contains requestId)
      |> Expect.isTrue "the log line carries the same id the response gave the user"
    }
  ]
