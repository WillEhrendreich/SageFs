module BrowserAssets.Host

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http

[<EntryPoint>]
let main args =
  let builder = WebApplication.CreateBuilder(args)
  let app = builder.Build()
  app.UseDefaultFiles() |> ignore
  app.UseStaticFiles(StaticFileOptions(OnPrepareResponse = fun context ->
    context.Context.Response.Headers.CacheControl <- "no-store")) |> ignore
  app.MapGet("/host-pid", Func<IResult>(fun () -> Results.Text(string Environment.ProcessId))) |> ignore
  app.Run()
  0
