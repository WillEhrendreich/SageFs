namespace SageFs

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open SageFs.WorkerProtocol

module WorkerHttpTransport =

  /// Opaque server handle — exposes BaseUrl and Dispose.
  type HttpWorkerServer internal (baseUrl: string, app: WebApplication) =
    member _.BaseUrl = baseUrl
    interface IAsyncDisposable with
      member _.DisposeAsync() = app.StopAsync() |> ValueTask
    interface IDisposable with
      member _.Dispose() =
        app.StopAsync().GetAwaiter().GetResult()

  /// Map WorkerMessage → (httpMethod, path, bodyJson option).
  /// Delegates to HttpWorkerClient in SageFs.Core.
  let toRoute = HttpWorkerClient.toRoute

  let readBody (ctx: HttpContext) : Task<string> = task {
    use reader = new StreamReader(ctx.Request.Body)
    return! reader.ReadToEndAsync()
  }

  let jsonProp (doc: JsonDocument) (name: string) =
    doc.RootElement.GetProperty(name)

  let respond
    (handler: WorkerMessage -> Async<WorkerResponse>)
    (ctx: HttpContext)
    (msg: WorkerMessage)
    = task {
    let! resp = handler msg |> Async.StartAsTask
    ctx.Response.ContentType <- "application/json"
    do! ctx.Response.WriteAsync(Serialization.serialize resp)
  }

  /// Start a Kestrel HTTP server dispatching to the given handler.
  /// Pass port=0 for OS-assigned dynamic port.
  let startServer
    (handler: WorkerMessage -> Async<WorkerResponse>)
    (hotReloadStateRef: HotReloadState.T ref)
    (projectFiles: string list)
    (getWarmupContext: unit -> WarmupContext)
    (port: int)
    : Task<HttpWorkerServer> =
    task {
      let builder = WebApplication.CreateBuilder([||])
      builder.WebHost.UseUrls(sprintf "http://127.0.0.1:%d" port) |> ignore
      builder.Logging.ClearProviders() |> ignore
      let app = builder.Build()

      let inline respond' ctx msg = respond handler ctx msg

      app.MapGet("/status", Func<HttpContext, Task>(fun ctx -> task {
        let rid = ctx.Request.Query["replyId"].ToString()
        return! respond' ctx (WorkerMessage.GetStatus rid)
      })) |> ignore

      app.MapPost("/eval", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let code = (jsonProp doc "code").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.EvalCode(code, rid))
      })) |> ignore

      app.MapPost("/check", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let code = (jsonProp doc "code").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.CheckCode(code, rid))
      })) |> ignore

      app.MapPost("/completions", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let code = (jsonProp doc "code").GetString()
        let cursorPos = (jsonProp doc "cursorPos").GetInt32()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.GetCompletions(code, cursorPos, rid))
      })) |> ignore

      app.MapPost("/cancel", Func<HttpContext, Task>(fun ctx ->
        respond' ctx WorkerMessage.CancelEval)) |> ignore

      app.MapPost("/load-script", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let filePath = (jsonProp doc "filePath").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.LoadScript(filePath, rid))
      })) |> ignore

      app.MapPost("/reset", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.ResetSession rid)
      })) |> ignore

      app.MapPost("/hard-reset", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let rebuild = (jsonProp doc "rebuild").GetBoolean()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.HardResetSession(rebuild, rid))
      })) |> ignore

      app.MapPost("/shutdown", Func<HttpContext, Task>(fun ctx ->
        respond' ctx WorkerMessage.Shutdown)) |> ignore

      // Session context endpoint
      app.MapGet("/warmup-context", Func<HttpContext, Task>(fun ctx -> task {
        let wCtx = getWarmupContext ()
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize wCtx)
      })) |> ignore

      // Hot-reload state endpoints
      app.MapGet("/hotreload", Func<HttpContext, Task>(fun ctx -> task {
        let state = !hotReloadStateRef
        let files =
          projectFiles
          |> List.map (fun f -> {| path = f; watched = HotReloadState.isWatched f state |})
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| files = files; watchedCount = HotReloadState.watchedCount state |})
      })) |> ignore

      app.MapPost("/hotreload/toggle", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let path = (jsonProp doc "path").GetString()
        hotReloadStateRef.Value <- HotReloadState.toggle path !hotReloadStateRef
        let isNowWatched = HotReloadState.isWatched path !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| path = path; watched = isNowWatched |})
      })) |> ignore

      app.MapPost("/hotreload/watch-all", Func<HttpContext, Task>(fun ctx -> task {
        hotReloadStateRef.Value <- HotReloadState.watchAll projectFiles !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| watchedCount = HotReloadState.watchedCount !hotReloadStateRef |})
      })) |> ignore

      app.MapPost("/hotreload/unwatch-all", Func<HttpContext, Task>(fun ctx -> task {
        hotReloadStateRef.Value <- HotReloadState.unwatchAll !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| watchedCount = 0 |})
      })) |> ignore

      app.MapPost("/hotreload/watch-project", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let project = (jsonProp doc "project").GetString()
        hotReloadStateRef.Value <- HotReloadState.watchByDirectory project projectFiles !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| project = project; watchedCount = HotReloadState.watchedCount !hotReloadStateRef |})
      })) |> ignore

      app.MapPost("/hotreload/unwatch-project", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let project = (jsonProp doc "project").GetString()
        hotReloadStateRef.Value <- HotReloadState.unwatchByDirectory project !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| project = project; watchedCount = HotReloadState.watchedCount !hotReloadStateRef |})
      })) |> ignore

      app.MapPost("/hotreload/watch-directory", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let dir = (jsonProp doc "directory").GetString()
        hotReloadStateRef.Value <- HotReloadState.watchByDirectory dir projectFiles !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        let watched = HotReloadState.watchedInDirectory dir !hotReloadStateRef
        do! ctx.Response.WriteAsync(Serialization.serialize {| directory = dir; watchedCount = List.length watched |})
      })) |> ignore

      app.MapPost("/hotreload/unwatch-directory", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let dir = (jsonProp doc "directory").GetString()
        hotReloadStateRef.Value <- HotReloadState.unwatchByDirectory dir !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| directory = dir; watchedCount = HotReloadState.watchedCount !hotReloadStateRef |})
      })) |> ignore

      do! app.StartAsync()

      let server = app.Services.GetRequiredService<IServer>()
      let addresses = server.Features.Get<IServerAddressesFeature>().Addresses
      let actualUrl = addresses |> Seq.head
      return new HttpWorkerServer(actualUrl, app)
    }

  /// Create a SessionProxy backed by HTTP to the given base URL.
  /// Delegates to HttpWorkerClient in SageFs.Core.
  let httpProxy = HttpWorkerClient.httpProxy
