module SageFs.Tests.McpServerOversizedRequestTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open Expecto.Flip
open Microsoft.AspNetCore.Http
open SageFs.Server.McpServer

let private maxBodyBytes = 4_194_304L

let private oversizedContext (body: string) =
  let ctx = DefaultHttpContext()
  ctx.Request.ContentType <- "application/json"
  ctx.Request.ContentLength <- Nullable(maxBodyBytes + 1L)
  ctx.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes(body))
  ctx.Response.Body <- new MemoryStream()
  ctx

let private responseBody (ctx: HttpContext) =
  let body = ctx.Response.Body :?> MemoryStream
  body.ToArray() |> Encoding.UTF8.GetString

let private expectTooLargeResponse (ctx: HttpContext) =
  ctx.Response.StatusCode
  |> Expect.equal "should return 413" 413

  ctx.Response.ContentType
  |> Expect.equal "should return JSON content type" "application/json"

  use doc = JsonDocument.Parse(responseBody ctx)
  let root = doc.RootElement

  root.GetProperty("success").GetBoolean()
  |> Expect.isFalse "should mark the request as unsuccessful"

  root.GetProperty("error").GetString()
  |> Expect.equal "should explain the body size failure" "Request body too large"

[<Tests>]
let tests =
  testList "McpServer oversized request handling" [
    testTask "readJsonBody returns a consistent 413 JSON envelope" {
      let ctx = oversizedContext "{}"

      let! rejected =
        task {
          try
            use! _ = readJsonBody ctx
            return false
          with
          | RequestTooLarge -> return true
        }

      rejected |> Expect.isTrue "should reject oversized request bodies"
      expectTooLargeResponse ctx
    }

    testTask "readJsonProp returns a consistent 413 JSON envelope" {
      let ctx = oversizedContext """{"sessionId":"ignored"}"""

      let! rejected =
        task {
          try
            let! _ = readJsonProp ctx "sessionId"
            return false
          with
          | RequestTooLarge -> return true
        }

      rejected |> Expect.isTrue "should reject oversized request bodies"
      expectTooLargeResponse ctx
    }
  ]
