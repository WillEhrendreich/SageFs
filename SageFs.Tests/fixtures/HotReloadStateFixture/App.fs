namespace StateFixture

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http

module App =
  /// Starts the app on `port` and returns straight away, so an FSI eval of
  /// `StateFixture.App.run 1234` doesn't hang on Kestrel. The table is read
  /// ONCE, here, and each route keeps the closure it got.
  let run (port: int) =
    let builder = WebApplication.CreateBuilder()
    let app = builder.Build()
    for name, handler in State.handlers do
      app.MapGet("/" + name, Func<_, _>(fun (ctx: HttpContext) ->
        ctx.Response.ContentType <- "text/plain"
        ctx.Response.WriteAsync(handler ()))) |> ignore
    app.RunAsync(sprintf "http://127.0.0.1:%d" port)
    |> Async.AwaitTask
    |> Async.Start
