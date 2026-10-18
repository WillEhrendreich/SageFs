/// A malformed `{id}` on a mutating dashboard route is refused with a 400 that names the field.
///
/// It used to become `SessionId.newId ()`: the action ran against an id that cannot exist,
/// swapped a "Stopping..." card into the page, and never said the request was wrong.
module SageFs.Tests.DashboardSessionIdRouteTests

open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Falco
open Microsoft.AspNetCore.Http
open SageFs
open SageFs.Server.Dashboard

let private contextFor () =
  let ctx = DefaultHttpContext()
  ctx.Response.Body <- new MemoryStream()
  ctx

let private bodyOf (ctx: HttpContext) =
  let ms = ctx.Response.Body :?> MemoryStream
  ms.Position <- 0L
  use reader = new StreamReader(ms, System.Text.Encoding.UTF8, true, 1024, true)
  reader.ReadToEnd()

/// A handler that records the id it was given, so a test can tell "ran" from "refused".
let private spy () =
  let seen = ref []
  let handler (id: WorkerProtocol.SessionId) : HttpHandler =
    fun _ ->
      seen.Value <- id :: seen.Value
      Task.CompletedTask
  seen, handler

let private malformed =
  [ "", "empty"
    "not-an-id", "words"
    "0A0B0C0D", "upper case hex"
    "0a0b0c0", "seven digits"
    "0a0b0c0de", "nine digits"
    "Infinity", "the float a router can read an id as"
    "../../etc", "a path" ]

[<Tests>]
let tests =
  testList "session id on a route" [
    yield!
      malformed
      |> List.map (fun (raw, what) ->
        testTask (sprintf "WHY — %s (%A) is a 400 and the action never runs, so a bad id cannot act on a session that does not exist" what raw) {
          let seen, handler = spy ()
          let ctx = contextFor ()
          do! SessionIdRoute.handle handler raw ctx
          ctx.Response.StatusCode |> Expect.equal "refused as a bad request" 400
          seen.Value |> Expect.isEmpty "the action was not invoked"
          bodyOf ctx |> Expect.stringContains "the field is named" SessionIdRoute.Field
          bodyOf ctx |> Expect.stringContains "what was expected is said" "8 lowercase hex digits"
        })

    testTask "WHY — a refusal shows the id that was sent, so the caller can see what they typed" {
      let _, handler = spy ()
      let ctx = contextFor ()
      do! SessionIdRoute.handle handler "oops" ctx
      bodyOf ctx |> Expect.stringContains "the received value is echoed" "oops"
    }

    testTask "WHY — a well-formed id reaches the action unchanged and the response is left alone" {
      let seen, handler = spy ()
      let ctx = contextFor ()
      do! SessionIdRoute.handle handler "0a0b0c0d" ctx
      ctx.Response.StatusCode |> Expect.equal "not a refusal" 200
      seen.Value |> List.map WorkerProtocol.SessionId.value |> Expect.equal "the action got that id" [ "0a0b0c0d" ]
    }
  ]
