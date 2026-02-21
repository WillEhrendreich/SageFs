module SageFs.Tests.HotReloadBrowserTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Playwright
open SageFs.AppState

let private evalCode (actor: AppActor) code =
  task {
    let request = { Code = code; Args = Map.empty }
    let! response = actor.PostAndAsyncReply(fun reply -> Eval(request, CancellationToken.None, reply))
    return response
  }

let private evalHotReload (actor: AppActor) code =
  task {
    let request = { Code = code; Args = Map.ofList [ "hotReload", box true ] }
    let! response = actor.PostAndAsyncReply(fun reply -> Eval(request, CancellationToken.None, reply))
    return response
  }

let private waitForServer (port: int) =
  task {
    use client = new System.Net.Http.HttpClient()
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable ready = false
    let mutable lastErr = ""
    while not ready && sw.ElapsedMilliseconds < 15000L do
      try
        let! _ = client.GetStringAsync(sprintf "http://localhost:%d/" port)
        ready <- true
      with ex ->
        lastErr <- ex.Message
        do! Task.Delay(500)
    if not ready then failtestf "Server did not start within 15s (last error: %s)" lastErr
  }

[<Tests>]
let tests =
  testSequenced
  <| testList "[Integration] Hot reload browser tests" [

    testCase "browser shows code change and output update when handler is hot-patched"
    <| fun _ ->
      task {
        let actor = FalcoTests.sharedActor.Value
        let port = FalcoTests.getRandomPort ()
        let screenshotDir =
          System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hot-reload-demo")
        System.IO.Directory.CreateDirectory(screenshotDir) |> ignore

        // --- Step 1: Start demo Falco app ---
        let initialCode =
          sprintf
            """
let demoGreeting () = "Hello from Falco! v1"
let greetingSource () = "v1 source"

let demoHandler : HttpHandler =
  fun ctx ->
    Response.ofHtml (
      _html [] [
        _head [] [ _title [] [ _text "Hot Reload Demo" ] ]
        _body [] [
          _div [] [
            _h2 [] [ _text "source" ]
            _pre [ _id_ "source" ] [ _text (greetingSource ()) ]
          ]
          _div [] [
            _h1 [ _id_ "greeting" ] [ _text (demoGreeting ()) ]
          ]
        ]
      ]
    ) ctx

let hrDemoBuilder = WebApplication.CreateBuilder()
HostingAbstractionsWebHostBuilderExtensions.UseUrls(hrDemoBuilder.WebHost, "http://localhost:%d") |> ignore
let hrDemoApp = hrDemoBuilder.Build()
hrDemoApp.MapGet("/", demoHandler) |> ignore
let hrDemoStartTask = hrDemoApp.StartAsync()
printfn "Hot reload demo started on port %d"
"""
            port
            port

        let! r1 = evalCode actor initialCode
        match r1.EvaluationResult with
        | Error ex -> failtestf "Failed to create demo app: %s" ex.Message
        | Ok _ -> printfn "Demo app eval succeeded, waiting for server on port %d..." port
        if r1.Diagnostics.Length > 0 then
          printfn "Diagnostics: %A" r1.Diagnostics

        do! waitForServer port

        // --- Step 2: Launch headless Chromium ---
        let! pw = Playwright.CreateAsync()
        let! browser = pw.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
        let! page =
          browser.NewPageAsync(
            BrowserNewPageOptions(ViewportSize = ViewportSize(Width = 900, Height = 600)))

        try
          // --- Step 3: Navigate and verify initial state ---
          let! _ = page.GotoAsync(sprintf "http://localhost:%d" port)
          let! sourceText = page.InnerTextAsync("#source")
          let! greetingText = page.InnerTextAsync("#greeting")
          Expect.stringContains sourceText "v1 source" "initial source panel"
          Expect.stringContains greetingText "Hello from Falco! v1" "initial greeting"

          let! _ =
            page.ScreenshotAsync(
              PageScreenshotOptions(Path = IO.Path.Combine(screenshotDir, "01-before-reload.png")))

          // --- Step 4: Hot-reload BOTH functions ---
          let hotCode =
            "let demoGreeting () = \"Hot reloaded!\"\n" +
            "let greetingSource () = \"v2 source\""

          let! r2 = evalHotReload actor hotCode
          match r2.EvaluationResult with
          | Error ex -> failtestf "Hot reload eval failed: %s" ex.Message
          | Ok _ -> ()

          // Verify Harmony patched the methods
          Expect.isNonEmpty
            (r2.Metadata |> Map.tryFind "reloadedMethods" |> Option.map (fun v -> v :?> string list) |> Option.defaultValue [])
            "should have reloadedMethods in metadata"

          // Verify server-side HTTP response has new content
          use httpClient = new System.Net.Http.HttpClient()
          let! html = httpClient.GetStringAsync(sprintf "http://localhost:%d/" port)
          Expect.stringContains html "Hot reloaded!" "server responds with patched content"

          // --- Step 5: Navigate again to see updated content in browser ---
          let! _ = page.GotoAsync(sprintf "http://localhost:%d" port)
          let! newSource = page.InnerTextAsync("#source")
          let! newGreeting = page.InnerTextAsync("#greeting")
          Expect.stringContains newSource "v2 source" "code panel shows new source"
          Expect.stringContains newGreeting "Hot reloaded!" "output shows new greeting"

          let! _ =
            page.ScreenshotAsync(
              PageScreenshotOptions(Path = IO.Path.Combine(screenshotDir, "02-after-reload.png")))

          printfn "✅ Screenshots saved to: %s" screenshotDir
        finally
          browser.CloseAsync().GetAwaiter().GetResult()
          pw.Dispose()
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

  ]
