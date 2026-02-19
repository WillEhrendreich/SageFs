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

      do! app.StartAsync()

      let server = app.Services.GetRequiredService<IServer>()
      let addresses = server.Features.Get<IServerAddressesFeature>().Addresses
      let actualUrl = addresses |> Seq.head
      return new HttpWorkerServer(actualUrl, app)
    }

  /// Create a SessionProxy backed by HTTP to the given base URL.
  /// Delegates to HttpWorkerClient in SageFs.Core.
  let httpProxy = HttpWorkerClient.httpProxy
