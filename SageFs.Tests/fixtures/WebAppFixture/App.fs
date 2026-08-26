namespace WebAppFixture

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http

/// The hot-reloaded greeting. The hot-reload verification edits this value
/// (via the FSI session) and asserts the running app serves the new value.
module Greeting =
  let mutable text = "hello from sagefs"

module App =
  /// Start the web app on the given port and return IMMEDIATELY (the server
  /// runs on a background task). FSI evals bind the returned unit — awaiting
  /// the server task here would hang the eval forever (Kestrel never returns
  /// until shutdown).
  let run (port: int) =
    let builder = WebApplication.CreateBuilder()
    let app = builder.Build()
    app.MapGet("/", Func<_, _>(fun (ctx: HttpContext) ->
      ctx.Response.ContentType <- "text/plain"
      ctx.Response.WriteAsync(sprintf "<h1>%s</h1>" Greeting.text))
    ) |> ignore
    let _serverTask =
      app.RunAsync(sprintf "http://127.0.0.1:%d" port)
      |> Async.AwaitTask
      |> Async.Start
    ()
