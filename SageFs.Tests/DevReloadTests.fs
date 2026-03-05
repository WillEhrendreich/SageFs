module SageFs.Tests.DevReloadTests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.AspNetCore.Http
open SageFs.DevReload
open SageFs

// -- Signaling tests (sequential — broadcastReload fires ALL clients) --------

let signalingTests = testSequenced <| testList "DevReload.Signaling" [

  test "registerClient creates a channel that is readable" {
    let reader = registerClient "test-pending-1"
    reader.TryPeek() |> fst |> Expect.isFalse "Channel should be empty after registration"
    unregisterClient "test-pending-1"
  }

  testTask "broadcastReload delivers Reload event to all clients" {
    let r1 = registerClient "trigger-a"
    let r2 = registerClient "trigger-b"
    broadcastReload ()
    let! ok1 = r1.WaitToReadAsync(CancellationToken.None).AsTask()
    let! ok2 = r2.WaitToReadAsync(CancellationToken.None).AsTask()
    ok1 |> Expect.isTrue "r1 should have data"
    ok2 |> Expect.isTrue "r2 should have data"
    let mutable evt1 = DevReloadEvent.Compiling
    r1.TryRead(&evt1) |> ignore
    evt1 |> Expect.equal "should be Reload" DevReloadEvent.Reload
    unregisterClient "trigger-a"
    unregisterClient "trigger-b"
  }

  test "broadcastReload is safe with no clients" {
    broadcastReload ()
  }

  testTask "unregisterClient closes the channel" {
    let reader = registerClient "unregister-me"
    unregisterClient "unregister-me"
    let! completed = reader.Completion
    // If we get here, the channel completed without error
    ignore completed
  }

  testTask "registerClient replaces existing client with same id" {
    let _r1 = registerClient "replace-id"
    let r2 = registerClient "replace-id"
    broadcastReload ()
    let! ok = r2.WaitToReadAsync(CancellationToken.None).AsTask()
    ok |> Expect.isTrue "Replacement channel should receive events"
    unregisterClient "replace-id"
  }

  testTask "broadcastCompiling delivers Compiling event" {
    let reader = registerClient "compile-test"
    broadcastCompiling ()
    let! ok = reader.WaitToReadAsync(CancellationToken.None).AsTask()
    ok |> Expect.isTrue "should have data"
    let mutable evt = DevReloadEvent.Reload
    reader.TryRead(&evt) |> ignore
    evt |> Expect.equal "should be Compiling" DevReloadEvent.Compiling
    unregisterClient "compile-test"
  }

  testTask "events arrive in order: Compiling then Reload" {
    let reader = registerClient "order-test"
    broadcastCompiling ()
    broadcastReload ()
    let mutable evt1 = DevReloadEvent.Reload
    let mutable evt2 = DevReloadEvent.Compiling
    let! _ = reader.WaitToReadAsync(CancellationToken.None).AsTask()
    reader.TryRead(&evt1) |> ignore
    reader.TryRead(&evt2) |> ignore
    evt1 |> Expect.equal "first should be Compiling" DevReloadEvent.Compiling
    evt2 |> Expect.equal "second should be Reload" DevReloadEvent.Reload
    unregisterClient "order-test"
  }

  testTask "multiple reload events can be sent on same channel" {
    let reader = registerClient "multi-reload"
    broadcastReload ()
    broadcastReload ()
    let mutable evt = DevReloadEvent.Compiling
    let! _ = reader.WaitToReadAsync(CancellationToken.None).AsTask()
    reader.TryRead(&evt) |> ignore
    evt |> Expect.equal "first should be Reload" DevReloadEvent.Reload
    reader.TryRead(&evt) |> ignore
    evt |> Expect.equal "second should be Reload" DevReloadEvent.Reload
    unregisterClient "multi-reload"
  }
]

// -- Middleware unit tests (DefaultHttpContext, no TestHost) ------------------

let private runMw (ctx: HttpContext) (responseContentType: string) (responseBody: string) = task {
  let terminal = RequestDelegate(fun ctx -> task {
    ctx.Response.ContentType <- responseContentType
    let bytes = Encoding.UTF8.GetBytes(responseBody)
    do! ctx.Response.Body.WriteAsync(ReadOnlyMemory bytes)
  })
  let mw = DevReloadMiddleware.middleware terminal
  do! mw.Invoke(ctx)
}

let private readBody (ctx: HttpContext) =
  let ms = ctx.Response.Body :?> MemoryStream
  ms.Position <- 0L
  use reader = new StreamReader(ms)
  reader.ReadToEnd()

let middlewareTests = testList "DevReload.Middleware" [

  testTask "injects script into HTML responses" {
    let ctx = DefaultHttpContext()
    ctx.Request.Path <- PathString("/")
    ctx.Request.Headers["Accept"] <- "text/html"
    ctx.Response.Body <- new MemoryStream()
    do! runMw ctx "text/html" "<html><body><h1>Hello</h1></body></html>"
    let body = readBody ctx
    body |> Expect.stringContains "should have script attr" "data-sagefs-injected"
    body |> Expect.stringContains "should have EventSource" "EventSource"
    body |> Expect.stringContains "should close body" "</body>"
  }

  testTask "injects visual indicator div" {
    let ctx = DefaultHttpContext()
    ctx.Request.Path <- PathString("/")
    ctx.Request.Headers["Accept"] <- "text/html"
    ctx.Response.Body <- new MemoryStream()
    do! runMw ctx "text/html" "<html><body><h1>Hello</h1></body></html>"
    let body = readBody ctx
    body |> Expect.stringContains "should have indicator" "sagefs-reload-indicator"
    body |> Expect.stringContains "should parse JSON" "JSON.parse"
    body |> Expect.stringContains "should handle compiling" "compiling"
    body |> Expect.stringContains "should handle reload" "reload"
  }

  testTask "does NOT inject into JSON responses" {
    let ctx = DefaultHttpContext()
    ctx.Request.Path <- PathString("/api")
    ctx.Request.Headers["Accept"] <- "application/json"
    ctx.Response.Body <- new MemoryStream()
    do! runMw ctx "application/json" """{"ok":true}"""
    let body = readBody ctx
    body |> Expect.equal "should be raw JSON" """{"ok":true}"""
  }

  testTask "does NOT inject when Accept is image/*" {
    let ctx = DefaultHttpContext()
    ctx.Request.Path <- PathString("/logo.png")
    ctx.Request.Headers["Accept"] <- "image/*"
    ctx.Response.Body <- new MemoryStream()
    do! runMw ctx "image/png" "BINARYDATA"
    let body = readBody ctx
    body |> Expect.equal "should pass through binary" "BINARYDATA"
  }

  testTask "idempotency guard prevents double injection" {
    let ctx = DefaultHttpContext()
    ctx.Request.Path <- PathString("/")
    ctx.Request.Headers["Accept"] <- "text/html"
    ctx.Response.Body <- new MemoryStream()
    let terminal = RequestDelegate(fun ctx -> task {
      ctx.Response.ContentType <- "text/html"
      do! ctx.Response.Body.WriteAsync(ReadOnlyMemory(Encoding.UTF8.GetBytes("<html><body></body></html>")))
    })
    let inner = DevReloadMiddleware.middleware terminal
    let outer = DevReloadMiddleware.middleware inner
    do! outer.Invoke(ctx)
    let body = readBody ctx
    let mutable c = 0
    let marker = "<script data-sagefs-injected"
    let mutable idx = body.IndexOf(marker, 0)
    while idx >= 0 do
      c <- c + 1
      idx <- body.IndexOf(marker, idx + 1)
    c |> Expect.equal "should inject exactly once" 1
  }

  testTask "injects before closing body tag" {
    let ctx = DefaultHttpContext()
    ctx.Request.Path <- PathString("/")
    ctx.Request.Headers["Accept"] <- "text/html"
    ctx.Response.Body <- new MemoryStream()
    do! runMw ctx "text/html" "<html><body><p>Content</p></body></html>"
    let body = readBody ctx
    let scriptIdx = body.IndexOf("data-sagefs-injected")
    let bodyCloseIdx = body.IndexOf("</body>")
    (scriptIdx, bodyCloseIdx) |> Expect.isLessThan "script should precede </body>"
  }

  testTask "appends script when no body close tag" {
    let ctx = DefaultHttpContext()
    ctx.Request.Path <- PathString("/")
    ctx.Request.Headers["Accept"] <- "text/html"
    ctx.Response.Body <- new MemoryStream()
    do! runMw ctx "text/html" "<html><p>No body tag</p>"
    let body = readBody ctx
    body |> Expect.stringContains "should still have script" "data-sagefs-injected"
  }

  testTask "script uses cross-origin URL when port specified" {
    let script = DevReloadMiddleware.reloadScript 12345
    script |> Expect.stringContains "should have 127.0.0.1:12345" "http://127.0.0.1:12345/__sagefs__/reload"
  }

  testTask "script uses relative URL when port is 0" {
    let script = DevReloadMiddleware.reloadScript 0
    script |> Expect.stringContains "should have relative path" "/__sagefs__/reload"
    (script.Contains("127.0.0.1")) |> Expect.isFalse "should not have absolute URL"
  }
]

// -- Kill switch tests -------------------------------------------------------

let killSwitchTests = testSequenced <| testList "DevReload.KillSwitch" [

  test "SAGEFS_DEVRELOAD=false disables injection" {
    let original = Environment.GetEnvironmentVariable("SAGEFS_DEVRELOAD")
    try
      Environment.SetEnvironmentVariable("SAGEFS_DEVRELOAD", "false")
      let disabled =
        match Environment.GetEnvironmentVariable("SAGEFS_DEVRELOAD") with
        | "false" | "0" -> true
        | _ -> false
      disabled |> Expect.isTrue "kill switch should be respected"
    finally
      Environment.SetEnvironmentVariable("SAGEFS_DEVRELOAD", original)
  }

  test "SAGEFS_DEVRELOAD=0 disables injection" {
    let original = Environment.GetEnvironmentVariable("SAGEFS_DEVRELOAD")
    try
      Environment.SetEnvironmentVariable("SAGEFS_DEVRELOAD", "0")
      let disabled =
        match Environment.GetEnvironmentVariable("SAGEFS_DEVRELOAD") with
        | "false" | "0" -> true
        | _ -> false
      disabled |> Expect.isTrue "kill switch with 0 should be respected"
    finally
      Environment.SetEnvironmentVariable("SAGEFS_DEVRELOAD", original)
  }

  test "SAGEFS_DEVRELOAD unset means enabled" {
    let original = Environment.GetEnvironmentVariable("SAGEFS_DEVRELOAD")
    try
      Environment.SetEnvironmentVariable("SAGEFS_DEVRELOAD", null)
      let disabled =
        match Environment.GetEnvironmentVariable("SAGEFS_DEVRELOAD") with
        | null | "" | "true" | "1" -> false
        | "false" | "0" -> true
        | _ -> false
      disabled |> Expect.isFalse "unset should mean enabled"
    finally
      Environment.SetEnvironmentVariable("SAGEFS_DEVRELOAD", original)
  }
]

// -- Combined test list -------------------------------------------------------

[<Tests>]
let devReloadTests = testList "DevReload" [
  signalingTests
  middlewareTests
  killSwitchTests
]
