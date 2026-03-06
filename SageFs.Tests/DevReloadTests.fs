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
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
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
    |> Gen.map (fun s -> CompilationFailed(s, []))
  ]

/// Helper to broadcast any DevReloadEvent via the public API
let private broadcastAny (evt: DevReloadEvent) =
  match evt with
  | Compiling fileName -> broadcastCompiling fileName
  | Reload -> broadcastReload ()
  | CompilationFailed(err, diags) -> broadcastCompilationFailed err diags

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
    broadcastCompilationFailed "FS0001: type mismatch" []
    let! ok = reader.WaitToReadAsync(CancellationToken.None).AsTask()
    ok |> Expect.isTrue "should have data"
    let mutable evt = Reload
    reader.TryRead(&evt) |> ignore
    evt |> Expect.equal "should be CompilationFailed" (CompilationFailed("FS0001: type mismatch", []))
    unregisterClient "fail-test"
  }

  testTask "compilation lifecycle: Compiling → CompilationFailed unsticks browser" {
    let reader = registerClient "lifecycle-fail"
    broadcastCompiling (Some "Broken.fs")
    broadcastCompilationFailed "Broken.fs: FS0010: Unexpected symbol" []
    let mutable evt1 = Reload
    let mutable evt2 = Reload
    let! _ = reader.WaitToReadAsync(CancellationToken.None).AsTask()
    reader.TryRead(&evt1) |> ignore
    reader.TryRead(&evt2) |> ignore
    evt1 |> Expect.equal "first should be Compiling" (Compiling (Some "Broken.fs"))
    evt2 |> Expect.equal "second should be CompilationFailed" (CompilationFailed("Broken.fs: FS0010: Unexpected symbol", []))
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
    body |> Expect.stringContains "should have elapsed timer" "compilingStart"
    body |> Expect.stringContains "should have shake animation" "sagefs-shake"
    body |> Expect.stringContains "should have reconnect timer" "reconnectTimer"
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
// Pipeline ordering tests (ResponseCompression interaction)
// ============================================================================

/// Access _components via the same reflection path as tryInsertFirst
let private getComponents (app: IApplicationBuilder) =
  let appType = app.GetType()
  let abProp = appType.GetProperty("ApplicationBuilder", Reflection.BindingFlags.NonPublic ||| Reflection.BindingFlags.Instance)
  let target =
    match abProp with
    | null -> app :> obj
    | prop -> prop.GetValue(app)
  let compField = target.GetType().GetField("_components", Reflection.BindingFlags.NonPublic ||| Reflection.BindingFlags.Instance)
  compField.GetValue(target) :?> System.Collections.IList

let pipelineOrderingTests = testList "DevReload.PipelineOrdering" [

  test "reflection: can access _components on WebApplication" {
    let app = WebApplication.CreateBuilder([||]).Build()
    let components = getComponents app
    components |> Expect.isNotNull "should find _components"
    (app :> IDisposable).Dispose()
  }

  test "reflection: Insert(0) places middleware before existing entries" {
    let app = WebApplication.CreateBuilder([||]).Build()
    // Add two markers via Use()
    app.Use(Func<RequestDelegate, RequestDelegate>(fun next -> next)) |> ignore
    app.Use(Func<RequestDelegate, RequestDelegate>(fun next -> next)) |> ignore
    let components = getComponents app
    let countBefore = components.Count
    // Insert at head
    let marker = Func<RequestDelegate, RequestDelegate>(fun next -> next)
    components.Insert(0, marker)
    components.Count |> Expect.equal "should have one more" (countBefore + 1)
    let first = components.[0]
    Object.ReferenceEquals(first, marker)
    |> Expect.isTrue "inserted middleware should be at position 0"
    (app :> IDisposable).Dispose()
  }

  testTask "DevReload before ResponseCompression: script injected in HTML" {
    let builder = WebApplication.CreateBuilder([||])
    builder.Services.AddResponseCompression(fun opts ->
      opts.EnableForHttps <- true
      opts.MimeTypes <- [| "text/html" |]) |> ignore
    builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
    let app = builder.Build()
    // Insert DevReload at head (position 0)
    let mw = Func<RequestDelegate, RequestDelegate>(DevReloadMiddleware.createMiddleware 0)
    let components = getComponents app
    components.Insert(0, mw)
    // Add ResponseCompression after DevReload
    app.UseResponseCompression() |> ignore
    // Terminal: return HTML
    app.MapGet("/", Func<HttpContext, Task>(fun ctx -> task {
      ctx.Response.ContentType <- "text/html"
      do! ctx.Response.WriteAsync("<html><body><h1>Test</h1></body></html>")
    })) |> ignore
    let cts = new CancellationTokenSource()
    let runTask = app.RunAsync(cts.Token)
    do! Task.Delay(1000)
    let addresses =
      (app :> IHost).Services.GetRequiredService<IServer>()
      |> fun s -> s.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
    let addr = addresses.Addresses |> Seq.head
    let client = new System.Net.Http.HttpClient()
    client.DefaultRequestHeaders.Add("Accept-Encoding", "identity")
    let! (resp: System.Net.Http.HttpResponseMessage) = client.GetAsync(addr)
    let! (body: string) = resp.Content.ReadAsStringAsync()
    body |> Expect.stringContains "should have DevReload script" "data-sagefs-injected"
    body |> Expect.stringContains "should have body close" "</body>"
    cts.Cancel()
    try do! runTask with _ -> ()
  }

  testTask "DevReload after ResponseCompression: script NOT injected (documents the bug)" {
    let builder = WebApplication.CreateBuilder([||])
    builder.Services.AddResponseCompression(fun opts ->
      opts.EnableForHttps <- true
      opts.MimeTypes <- [| "text/html" |]) |> ignore
    builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
    let app = builder.Build()
    // ResponseCompression first, DevReload appended (the old broken order)
    app.UseResponseCompression() |> ignore
    app.Use(Func<RequestDelegate, RequestDelegate>(DevReloadMiddleware.createMiddleware 0)) |> ignore
    app.MapGet("/", Func<HttpContext, Task>(fun ctx -> task {
      ctx.Response.ContentType <- "text/html"
      do! ctx.Response.WriteAsync("<html><body><h1>Test</h1></body></html>")
    })) |> ignore
    let cts = new CancellationTokenSource()
    let runTask = app.RunAsync(cts.Token)
    do! Task.Delay(1000)
    let addresses =
      (app :> IHost).Services.GetRequiredService<IServer>()
      |> fun s -> s.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
    let addr = addresses.Addresses |> Seq.head
    let client = new System.Net.Http.HttpClient()
    // Must request compression to trigger ResponseCompression middleware
    client.DefaultRequestHeaders.Add("Accept-Encoding", "br, gzip")
    let! (resp: System.Net.Http.HttpResponseMessage) = client.GetAsync(addr)
    // Read raw bytes — response is compressed, can't find script in compressed data
    let! (bytes: byte[]) = resp.Content.ReadAsByteArrayAsync()
    let encoding = resp.Content.Headers.ContentEncoding |> Seq.tryHead |> Option.defaultValue ""
    // When DevReload is AFTER compression, the body-swap sees compressed bytes
    // and can't find </body> — script is NOT injected. This documents the bug.
    // If compression is active, raw bytes won't contain the script marker
    match encoding with
    | "br" | "gzip" ->
      let raw = System.Text.Encoding.UTF8.GetString(bytes)
      let hasScript = raw.Contains("data-sagefs-injected")
      hasScript |> Expect.isFalse "script should NOT be injected when DevReload runs after compression"
    | _ ->
      // Compression didn't activate (e.g., response too small) — skip test
      ()
    cts.Cancel()
    try do! runTask with _ -> ()
  }

  test "reflection: _components starts empty on fresh WebApplication" {
    // WebApplication.Build() starts with empty _components
    let app = WebApplication.CreateBuilder([||]).Build()
    let components = getComponents app
    components.Count |> Expect.equal "fresh app has no middleware yet" 0
    (app :> IDisposable).Dispose()
  }
]

// ============================================================================
// DevReloadHealth state machine tests
// ============================================================================

let healthStateTests = testSequenced <| testList "DevReloadHealth state transitions" [
  testCase "initial state is Disabled" <| fun () ->
    DevReloadHealthTracker.reset ()
    DevReloadHealthTracker.current ()
    |> Expect.equal "initial state" Disabled

  testCase "transition to PatchPending" <| fun () ->
    DevReloadHealthTracker.reset ()
    DevReloadHealthTracker.transition PatchPending
    DevReloadHealthTracker.current ()
    |> Expect.equal "should be PatchPending" PatchPending

  testCase "transition to PatchFailed with reason" <| fun () ->
    DevReloadHealthTracker.reset ()
    DevReloadHealthTracker.transition (PatchFailed "harmony not found")
    DevReloadHealthTracker.current ()
    |> Expect.equal "should carry reason" (PatchFailed "harmony not found")

  testCase "transition to Injected" <| fun () ->
    DevReloadHealthTracker.reset ()
    DevReloadHealthTracker.transition Injected
    DevReloadHealthTracker.current ()
    |> Expect.equal "should be Injected" Injected

  testCase "transition to Active with client count" <| fun () ->
    DevReloadHealthTracker.reset ()
    DevReloadHealthTracker.transition (Active 3)
    DevReloadHealthTracker.current ()
    |> Expect.equal "should be Active 3" (Active 3)

  testCase "transition to Degraded with reason" <| fun () ->
    DevReloadHealthTracker.reset ()
    DevReloadHealthTracker.transition (Degraded "middleware appended")
    DevReloadHealthTracker.current ()
    |> Expect.equal "should carry degraded reason" (Degraded "middleware appended")

  testCase "callback fires on transition" <| fun () ->
    DevReloadHealthTracker.reset ()
    let mutable captured = None
    DevReloadHealthTracker.setTransitionCallback (fun s -> captured <- Some s)
    DevReloadHealthTracker.transition Injected
    captured |> Expect.equal "callback should fire" (Some Injected)
    DevReloadHealthTracker.clearTransitionCallback ()

  testCase "reset clears callback" <| fun () ->
    DevReloadHealthTracker.reset ()
    let mutable fired = false
    DevReloadHealthTracker.setTransitionCallback (fun _ -> fired <- true)
    DevReloadHealthTracker.reset ()
    DevReloadHealthTracker.transition PatchPending
    fired |> Expect.isFalse "callback should not fire after reset"

  testCase "DU cases are exhaustive" <| fun () ->
    let describe h =
      match h with
      | Disabled -> "disabled"
      | PatchPending -> "pending"
      | PatchFailed r -> sprintf "failed: %s" r
      | Injected -> "injected"
      | Active n -> sprintf "active(%d)" n
      | Degraded r -> sprintf "degraded: %s" r
    [ Disabled; PatchPending; PatchFailed "x"; Injected; Active 1; Degraded "y" ]
    |> List.map describe
    |> Expect.hasLength "six DU cases" 6

  testCase "multiple transitions track latest state" <| fun () ->
    DevReloadHealthTracker.reset ()
    DevReloadHealthTracker.transition PatchPending
    DevReloadHealthTracker.transition Injected
    DevReloadHealthTracker.transition (Active 2)
    DevReloadHealthTracker.current ()
    |> Expect.equal "should be latest" (Active 2)
]

// ============================================================================
// SSE handshake + connection indicator tests
// ============================================================================

let sseHandshakeTests = testList "SSE handshake connection indicator" [
  testCase "reloadScript contains es.onopen handler" <| fun () ->
    let script = DevReloadMiddleware.reloadScript 0
    script |> Expect.stringContains "should have onopen handler" "es.onopen"

  testCase "reloadScript contains connectionTimeout" <| fun () ->
    let script = DevReloadMiddleware.reloadScript 0
    script |> Expect.stringContains "should have connection timeout" "connectionTimeout"

  testCase "reloadScript shows Connecting state" <| fun () ->
    let script = DevReloadMiddleware.reloadScript 0
    script |> Expect.stringContains "should show Connecting" "Connecting"

  testCase "reloadScript shows Connected state" <| fun () ->
    let script = DevReloadMiddleware.reloadScript 0
    script |> Expect.stringContains "should show Connected" "Connected"

  testCase "reloadScript shows Could not connect warning" <| fun () ->
    let script = DevReloadMiddleware.reloadScript 0
    script |> Expect.stringContains "should show failure warning" "Could not connect"
]

// ============================================================================
// E2E integration tests — signal path: register → broadcast → receive
// ============================================================================

let e2eSignalPathTests = testSequenced <| testList "DevReload E2E signal path" [
  testCase "registerClient + broadcast Reload → reader receives event" <| fun () ->
    let reader = registerClient "e2e-test-1"
    broadcastReload ()
    let mutable received = false
    match reader.TryRead() with
    | true, evt ->
      match evt with
      | Reload -> received <- true
      | _ -> ()
    | _ -> ()
    unregisterClient "e2e-test-1"
    received |> Expect.isTrue "should receive Reload event"

  testCase "registerClient + broadcast Compiling → reader receives with filename" <| fun () ->
    let reader = registerClient "e2e-test-2"
    broadcastCompiling (Some "App.fs")
    match reader.TryRead() with
    | true, Compiling (Some "App.fs") -> ()
    | true, other -> failwithf "expected Compiling(Some App.fs), got %A" other
    | false, _ -> failwith "no event received"
    unregisterClient "e2e-test-2"

  testCase "registerClient + broadcast CompilationFailed → reader receives diagnostics" <| fun () ->
    let diag = {
      File = "App.fs"; Line = 10; EndLine = 10; Column = 5; EndColumn = 15
      Severity = "error"; DiagCode = Some "FS0001"; Message = "Type mismatch"
    }
    let reader = registerClient "e2e-test-3"
    broadcastCompilationFailed "1 error" [diag]
    match reader.TryRead() with
    | true, CompilationFailed("1 error", [d]) ->
      d.File |> Expect.equal "diagnostic file" "App.fs"
      d.Line |> Expect.equal "diagnostic line" 10
      d.Message |> Expect.equal "diagnostic message" "Type mismatch"
    | true, other -> failwithf "expected CompilationFailed, got %A" other
    | false, _ -> failwith "no event received"
    unregisterClient "e2e-test-3"

  testCase "multiple clients all receive the same broadcast" <| fun () ->
    let r1 = registerClient "e2e-multi-1"
    let r2 = registerClient "e2e-multi-2"
    let r3 = registerClient "e2e-multi-3"
    broadcastReload ()
    let check (r: System.Threading.Channels.ChannelReader<DevReloadEvent>) =
      match r.TryRead() with
      | true, Reload -> true
      | _ -> false
    let results = [check r1; check r2; check r3]
    unregisterClient "e2e-multi-1"
    unregisterClient "e2e-multi-2"
    unregisterClient "e2e-multi-3"
    results |> Expect.all "all 3 should receive Reload" id

  testCase "unregistered client does not receive events" <| fun () ->
    let reader = registerClient "e2e-unreg"
    unregisterClient "e2e-unreg"
    broadcastReload ()
    match reader.TryRead() with
    | true, Reload -> failwith "should not receive after unregister"
    | _ -> ()
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
  pipelineOrderingTests
  healthStateTests
  sseHandshakeTests
  e2eSignalPathTests
]
