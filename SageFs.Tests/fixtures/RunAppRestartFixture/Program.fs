module RunAppRestartFixture.Program

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http

/// A web app as `dotnet run` would start it: the address comes from ASPNETCORE_URLS, which `run_app`
/// sets (a free loopback port the first time, the same address on a restart).
[<EntryPoint>]
let main args =
  let app = WebApplication.CreateBuilder(args).Build()
  app.MapGet("/", Func<_, _>(fun (ctx: HttpContext) ->
    ctx.Response.ContentType <- "text/plain"
    ctx.Response.WriteAsync(Page.body ()))) |> ignore
  app.Run()
  0
