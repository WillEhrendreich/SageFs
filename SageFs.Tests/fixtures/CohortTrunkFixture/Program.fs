/// The trunk app. Every route that answers with a handler's message also counts the request, and the count is held in memory.
module CohortTrunkFixture.Program

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http

let private text (body: unit -> string) : Func<HttpContext, Task> =
  Func<HttpContext, Task>(fun ctx ->
    ctx.Response.ContentType <- "text/plain"
    ctx.Response.WriteAsync(body ()))

/// A handler's message and the count of requests so far, so one read says what is served and whether the process kept its state.
let private counted (message: unit -> string) : Func<HttpContext, Task> =
  text (fun () -> message () + "#" + string (Counter.hits.Next()))

[<EntryPoint>]
let main args =
  let builder = WebApplication.CreateBuilder args
  let app = builder.Build()
  app.MapGet("/ready", text (fun () -> "ready")) |> ignore
  app.MapGet("/pid", text (fun () -> string Environment.ProcessId)) |> ignore
  app.MapGet("/alice", counted Alice.aliceMessage) |> ignore
  app.MapGet("/bob", counted Bob.bobMessage) |> ignore
  app.MapGet("/rude", counted Rude.rudeMessage) |> ignore
  app.Run()
  0
