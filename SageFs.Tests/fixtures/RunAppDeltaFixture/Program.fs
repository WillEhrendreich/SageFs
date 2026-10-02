/// The app the run_app delta rows start. It is an executable, so SageFs's own `run_app` runs it in the
/// worker process, and it serves what `Handlers` computes, one route per row.
module RunAppDeltaFixture.Program

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http

let private text (body: unit -> string) : Func<HttpContext, Task> =
  Func<HttpContext, Task>(fun ctx ->
    ctx.Response.ContentType <- "text/plain"
    ctx.Response.WriteAsync(body ()))

let private textTask (body: unit -> Task<string>) : Func<HttpContext, Task> =
  Func<HttpContext, Task>(fun ctx ->
    ctx.Response.ContentType <- "text/plain"
    task {
      let! value = body ()
      do! ctx.Response.WriteAsync value
    } :> Task)

[<EntryPoint>]
let main args =
  let builder = WebApplication.CreateBuilder args
  let app = builder.Build()
  app.MapGet("/ready", text (fun () -> "ready")) |> ignore
  app.MapGet("/pid", text (fun () -> string Environment.ProcessId)) |> ignore
  // The app runs in the worker, so this is the worker's own answer to whether a debugger is attached to it.
  app.MapGet("/debugger", text (fun () -> string System.Diagnostics.Debugger.IsAttached)) |> ignore
  app.MapGet("/generic", text Handlers.generic) |> ignore
  app.MapGet("/genericLate", text Handlers.genericLate) |> ignore
  app.MapGet("/closure", text Handlers.closure) |> ignore
  app.MapGet("/instance", text Handlers.instance) |> ignore
  app.MapGet("/taskBody", textTask Handlers.taskBody) |> ignore
  app.MapGet("/addedMethod", text Handlers.addedCaller) |> ignore
  app.MapGet("/spin", text Handlers.spin) |> ignore
  app.MapGet("/rudeVirtual", text Handlers.rudeVirtual) |> ignore
  app.MapGet("/rudeStruct", text Handlers.rudeStruct) |> ignore
  app.Run()
  0
