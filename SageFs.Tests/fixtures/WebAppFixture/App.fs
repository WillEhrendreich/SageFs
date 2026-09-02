namespace WebAppFixture

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http

module App =
  /// Start the web app on the given port and return IMMEDIATELY (the server
  /// runs on a background task). FSI evals bind the returned unit — awaiting
  /// the server task here would hang the eval forever (Kestrel never returns
  /// until shutdown). The route calls WebAppFixture.Greeting.greeting at
  /// request time so a Harmony detour of that function changes what the
  /// running app serves — without restart.
  let run (port: int) =
    let builder = WebApplication.CreateBuilder()
    let app = builder.Build()
    app.MapGet("/", Func<_, _>(fun (ctx: HttpContext) ->
      ctx.Response.ContentType <- "text/plain"
      ctx.Response.WriteAsync(sprintf "<h1>%s</h1>" (Greeting.greeting ())))
    ) |> ignore
    let _serverTask =
      app.RunAsync(sprintf "http://127.0.0.1:%d" port)
      |> Async.AwaitTask
      |> Async.Start
    ()
