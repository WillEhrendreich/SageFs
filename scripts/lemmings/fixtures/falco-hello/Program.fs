module FalcoHello.Program

open Falco
open Falco.Routing
open Microsoft.AspNetCore.Builder

let hello : HttpHandler = Response.ofPlainText "Hello from Falco"

let health : HttpHandler = Response.ofPlainText "ok"

let routes =
  [ get "/hello" hello
    get "/health" health ]

[<EntryPoint>]
let main args =
  let app = WebApplication.CreateBuilder(args).Build()
  app.UseRouting().UseFalco(routes) |> ignore
  app.Run()
  0
