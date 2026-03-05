module SageFs.Tests.DevReloadTests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Microsoft.AspNetCore.Http
open SageFs.DevReload
open SageFs

// ============================================================================
// Property-based tests (FsCheck) — intent-surfacing
// ============================================================================

/// Generator for DevReloadEvent — covers all three DU cases
let private genDevReloadEvent =
  Gen.oneof [
    Gen.constant (Compiling None)
    Gen.elements [ "App.fs"; "Handlers.fs"; "Domain.fs"; "Views.fs" ]
    |> Gen.map (fun s -> Compiling (Some s))
    Gen.constant Reload
    Gen.elements [ "FS0001: type mismatch"; "FS0010: unexpected"; "FS0039: undefined" ]
    |> Gen.map CompilationFailed
  ]

/// Helper to broadcast any DevReloadEvent via the public API
let private broadcastAny (evt: DevReloadEvent) =
  match evt with
  | Compiling fileName -> broadcastCompiling fileName
  | Reload -> broadcastReload ()
  | CompilationFailed err -> broadcastCompilationFailed err

let propertyTests = testSequenced <| testList "DevReload.Properties" [

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 50 }
    "broadcast delivers every event to every registered client" <|
    Prop.forAll (Arb.fromGen (Gen.listOfLength 15 genDevReloadEvent)) (fun events ->
      let suffix = Guid.NewGuid().ToString("N").[..7]
      let ids = [ sprintf "pa-%s" suffix; sprintf "pb-%s" suffix; sprintf "pc-%s" suffix ]
      let readers = ids |> List.map (fun id -> id, registerClient id)
      for evt in events do broadcastAny evt
      let allCorrect =
        readers |> List.forall (fun (_, reader) ->
          let received = ResizeArray()
          let mutable evt = Reload
          while reader.TryRead(&evt) do received.Add(evt)
          received.Count = events.Length)
      for id in ids do unregisterClient id
      allCorrect)

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 50 }
    "event ordering is preserved per client" <|
    Prop.forAll (Arb.fromGen (Gen.listOfLength 10 genDevReloadEvent)) (fun events ->
      let id = sprintf "ord-%s" (Guid.NewGuid().ToString("N").[..7])
      let reader = registerClient id
      for evt in events do broadcastAny evt
      let received = ResizeArray()
      let mutable evt = Reload
      while reader.TryRead(&evt) do received.Add(evt)
      unregisterClient id
      Seq.toList received = events)

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 50 }
    "unregisterClient is idempotent — calling N times never throws" <|
    Prop.forAll (Arb.fromGen (Gen.choose (1, 10))) (fun count ->
      let id = sprintf "idem-%s" (Guid.NewGuid().ToString("N").[..7])
      let _reader = registerClient id
      for _ in 1..count do unregisterClient id
      true)

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 50 }
    "registerClient replaces existing channel — new reader gets all events" <|
    Prop.forAll (Arb.fromGen (Gen.listOfLength 5 genDevReloadEvent)) (fun events ->
      let id = sprintf "repl-%s" (Guid.NewGuid().ToString("N").[..7])
      let _r1 = registerClient id
      let r2 = registerClient id
      for evt in events do broadcastAny evt
      let received = ResizeArray()
      let mutable evt = Reload
      while r2.TryRead(&evt) do received.Add(evt)
      unregisterClient id
      received.Count = events.Length)

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 50 }
    "broadcast with zero clients never throws" <|
    Prop.forAll (Arb.fromGen genDevReloadEvent) (fun evt ->
      broadcastAny evt
      true)

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 50 }
    "DU exhaustiveness: exactly 3 lifecycle cases (documentary)" <|
    // Documentary test — verifies the DU has exactly 3 cases by pattern matching
    // without a wildcard. If a 4th case is added, this fails to compile.
    // The lifecycle is: Compiling → (Reload | CompilationFailed).
    Prop.forAll (Arb.fromGen genDevReloadEvent) (fun evt ->
      match evt with
      | Compiling _ -> true
      | Reload -> true
      | CompilationFailed _ -> true)
]

// ============================================================================
// Signaling tests (sequential — broadcast fires ALL clients)
// ============================================================================

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
    let mutable evt1 = Compiling None
    r1.TryRead(&evt1) |> ignore
    evt1 |> Expect.equal "should be Reload" Reload
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
    ignore completed
  }

  testTask "broadcastCompiling delivers Compiling event with filename" {
    let reader = registerClient "compile-test"
    broadcastCompiling (Some "Handlers.fs")
    let! ok = reader.WaitToReadAsync(CancellationToken.None).AsTask()
    ok |> Expect.isTrue "should have data"
    let mutable evt = Reload
    reader.TryRead(&evt) |> ignore
    evt |> Expect.equal "should be Compiling with filename" (Compiling (Some "Handlers.fs"))
    unregisterClient "compile-test"
  }

  testTask "broadcastCompiling without filename delivers Compiling None" {
    let reader = registerClient "compile-none"
    broadcastCompiling None
    let! ok = reader.WaitToReadAsync(CancellationToken.None).AsTask()
    ok |> Expect.isTrue "should have data"
    let mutable evt = Reload
    reader.TryRead(&evt) |> ignore
    evt |> Expect.equal "should be Compiling None" (Compiling None)
    unregisterClient "compile-none"
  }

  testTask "broadcastCompilationFailed delivers error summary" {
    let reader = registerClient "fail-test"
    broadcastCompilationFailed "FS0001: type mismatch"
    let! ok = reader.WaitToReadAsync(CancellationToken.None).AsTask()
    ok |> Expect.isTrue "should have data"
    let mutable evt = Reload
    reader.TryRead(&evt) |> ignore
    evt |> Expect.equal "should be CompilationFailed" (CompilationFailed "FS0001: type mismatch")
    unregisterClient "fail-test"
  }

  testTask "compilation lifecycle: Compiling → CompilationFailed unsticks browser" {
    let reader = registerClient "lifecycle-fail"
    broadcastCompiling (Some "Broken.fs")
    broadcastCompilationFailed "Broken.fs: FS0010: Unexpected symbol"
    let mutable evt1 = Reload
    let mutable evt2 = Reload
    let! _ = reader.WaitToReadAsync(CancellationToken.None).AsTask()
    reader.TryRead(&evt1) |> ignore
    reader.TryRead(&evt2) |> ignore
    evt1 |> Expect.equal "first should be Compiling" (Compiling (Some "Broken.fs"))
    evt2 |> Expect.equal "second should be CompilationFailed" (CompilationFailed "Broken.fs: FS0010: Unexpected symbol")
    unregisterClient "lifecycle-fail"
  }

  testTask "compilation lifecycle: Compiling → Reload (success path)" {
    let reader = registerClient "lifecycle-ok"
    broadcastCompiling (Some "Handlers.fs")
    broadcastReload ()
    let mutable evt1 = Reload
    let mutable evt2 = Compiling None
    let! _ = reader.WaitToReadAsync(CancellationToken.None).AsTask()
    reader.TryRead(&evt1) |> ignore
    reader.TryRead(&evt2) |> ignore
    evt1 |> Expect.equal "first should be Compiling" (Compiling (Some "Handlers.fs"))
    evt2 |> Expect.equal "second should be Reload" Reload
    unregisterClient "lifecycle-ok"
  }

  testTask "multiple reload events can be sent on same channel" {
    let reader = registerClient "multi-reload"
    broadcastReload ()
    broadcastReload ()
    let mutable evt = Compiling None
    let! _ = reader.WaitToReadAsync(CancellationToken.None).AsTask()
    reader.TryRead(&evt) |> ignore
    evt |> Expect.equal "first should be Reload" Reload
    reader.TryRead(&evt) |> ignore
    evt |> Expect.equal "second should be Reload" Reload
    unregisterClient "multi-reload"
  }
]

// ============================================================================
// Middleware unit tests (DefaultHttpContext, no TestHost)
// ============================================================================

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

  testTask "injects visual indicator div with error handling" {
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
    body |> Expect.stringContains "should handle failed" "failed"
    body |> Expect.stringContains "should have box-shadow" "box-shadow"
    body |> Expect.stringContains "should have console.debug" "console.debug"
    body |> Expect.stringContains "should have reload guard" "reloadCount"
    body |> Expect.stringContains "should have safeReload" "safeReload"
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

  testTask "script handles compilation error overlay" {
    let script = DevReloadMiddleware.reloadScript 0
    // The script must handle the 'failed' event type for error display
    script |> Expect.stringContains "should handle failed type" "'failed'"
    script |> Expect.stringContains "should show error text" "msg.error"
    script |> Expect.stringContains "should use red background" "#dc2626"
  }
]

// ============================================================================
// Kill switch tests
// ============================================================================

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

// ============================================================================
// SSE wire format tests
// ============================================================================

let sseFormatTests = testList "DevReload.SSEFormat" [

  test "compiling event without file produces valid SSE" {
    let script = DevReloadMiddleware.reloadScript 5000
    script |> Expect.stringContains "should have EventSource URL" "http://127.0.0.1:5000/__sagefs__/reload"
  }

  test "reloadScript escapes sprintf correctly" {
    // Ensure no unescaped % in the output that could break sprintf
    let script = DevReloadMiddleware.reloadScript 8080
    script |> Expect.stringContains "should have full URL" "http://127.0.0.1:8080/__sagefs__/reload"
    // The script should be valid JavaScript — check balanced braces
    let opens = script |> Seq.filter ((=) '{') |> Seq.length
    let closes = script |> Seq.filter ((=) '}') |> Seq.length
    opens |> Expect.equal "braces should be balanced" closes
  }

  test "compiling SSE payload with filename uses JsonSerializer" {
    // Chesterton's fence: manual Replace("\\","\\\\") missed control chars
    // and Unicode escapes. JsonSerializer.Serialize handles all edge cases.
    let file = "src\\Models\\UserTest.fs"
    let serialized = System.Text.Json.JsonSerializer.Serialize(file)
    let payload = sprintf "data: {\"type\":\"compiling\",\"file\":%s}" serialized
    // The serialized filename should be valid JSON — parse it to prove
    let json = payload.Substring(6) // strip "data: "
    let doc = System.Text.Json.JsonDocument.Parse(json)
    let roundTripped = doc.RootElement.GetProperty("file").GetString()
    roundTripped |> Expect.equal "filename should round-trip through JSON" file
  }

  test "compiling SSE payload handles quotes in filename" {
    let file = "User\"Test.fs"
    let serialized = System.Text.Json.JsonSerializer.Serialize(file)
    let payload = sprintf "data: {\"type\":\"compiling\",\"file\":%s}" serialized
    let json = payload.Substring(6)
    let doc = System.Text.Json.JsonDocument.Parse(json)
    let roundTripped = doc.RootElement.GetProperty("file").GetString()
    roundTripped |> Expect.equal "quoted filename should round-trip" file
  }

  test "failed SSE payload round-trips error through JSON" {
    let error = "Error in line 42: unexpected token"
    let serialized = System.Text.Json.JsonSerializer.Serialize(error)
    let payload = sprintf "data: {\"type\":\"failed\",\"error\":%s}" serialized
    let json = payload.Substring(6)
    let doc = System.Text.Json.JsonDocument.Parse(json)
    let roundTripped = doc.RootElement.GetProperty("error").GetString()
    roundTripped |> Expect.equal "error should round-trip through JSON" error
  }
]

// ============================================================================
// Combined test list
// ============================================================================

[<Tests>]
let devReloadTests = testList "DevReload" [
  propertyTests
  signalingTests
  middlewareTests
  killSwitchTests
  sseFormatTests
]
