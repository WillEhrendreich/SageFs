module SageFs.Tests.FalcoTests

open Expecto
open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open SageFs.ActorCreation
open SageFs.AppState
open SageFs.Args
open Falco
open Falco.Markup

let logger =
  { new SageFs.Utils.ILogger with
      member _.LogDebug msg = printfn "[DEBUG] %s" msg
      member _.LogInfo msg = printfn "[INFO] %s" msg
      member _.LogError msg = printfn "[ERROR] %s" msg
      member _.LogWarning msg = printfn "[WARN] %s" msg
  }

let getRandomPort () =
  let listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
  listener.Start()
  let port = (listener.LocalEndpoint :?> System.Net.IPEndPoint).Port
  listener.Stop()
  port

let createTestActor () =
  task {
    // Load the test project which has Falco package references
    let testProjectPath =
      System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "SageFs.Tests.fsproj")

    let fullPath = System.IO.Path.GetFullPath(testProjectPath)
    printfn "Loading test project from: %s" fullPath
    let args = mkCommonActorArgs logger true ignore [ SageFs.Args.Proj fullPath ]
    let! result = createActor args
    return result.Actor
  }

// Shared actor for all Falco tests
let sharedActor = lazy(createTestActor() |> Async.AwaitTask |> Async.RunSynchronously)

let evalCode (actor: AppActor) code =
  task {
    let request = { Code = code; Args = Map.empty }
    let! response = actor.PostAndAsyncReply(fun reply -> Eval(request, CancellationToken.None, reply))
    return response
  }

let testHttpGet (url: string) =
  task {
    use client = new HttpClient()
    client.Timeout <- TimeSpan.FromSeconds(5.0)

    try
      let! response = client.GetAsync(url)
      let! content = response.Content.ReadAsStringAsync()
      return Ok content
    with ex ->
      return Error ex.Message
  }

let testHttpGetWithRetry (url: string) (maxRetries: int) (delayMs: int) =
  task {
    let mutable lastError = ""
    let mutable result: Result<string, string> = Error "no attempts"
    for attempt in 1..maxRetries do
      match result with
      | Ok _ -> ()
      | Error _ ->
          let! r = testHttpGet url
          match r with
          | Ok content -> result <- Ok content
          | Error msg ->
              lastError <- msg
              if attempt < maxRetries then
                do! Task.Delay(delayMs)
    return result
  }

[<Tests>]
let tests =
  testSequenced <| testList "[Integration] Falco web application tests" [

    testCase "create and start basic Falco web app"
    <| fun _ ->
      task {
        printfn "Starting test: create and start basic Falco web app"
        let actor = sharedActor.Value
        let port = getRandomPort ()

        // Create a basic Falco web app
        let initialCode =
          $"""
open Falco
open Falco.Markup
open Falco.Markup.Elem
open Falco.Markup.Attr
open Falco.Markup.Text
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Hosting

let basicBuilder = WebApplication.CreateBuilder()
HostingAbstractionsWebHostBuilderExtensions.UseUrls(basicBuilder.WebHost, "http://localhost:{port}") |> ignore
let basicApp = basicBuilder.Build()

let basicHandler = 
    Response.ofHtml (
        _html [] [
            _head [] [ _title [] [ _text "Test Page" ] ]
            _body [] [
                _h1 [ _id_ "title" ] [ _text "Hello from SageFs!" ]
                _p [] [ _text "This is the initial page." ]
            ]
        ]
    )

basicApp.MapGet("/", basicHandler) |> ignore
let basicStartTask = basicApp.StartAsync()
printfn "Web app started on port {port}"
"""

        printfn "Evaluating initial Falco app code on port %d..." port
        let! response = evalCode actor initialCode

        printfn "Evaluation result: %A" response.EvaluationResult
        printfn "Diagnostics: %A" response.Diagnostics

        match response.EvaluationResult with
        | Error ex ->
          printfn "Error creating app: %s" ex.Message
          failtestf "Failed to create Falco app: %s\nDiagnostics: %A" ex.Message response.Diagnostics
        | Ok _ -> printfn "App created successfully"

        // Give the server time to start
        do! Task.Delay(500)

        // Test the endpoint with retries
        printfn "Testing HTTP endpoint..."
        let! result = testHttpGetWithRetry $"http://localhost:{port}/" 10 200

        match result with
        | Ok content ->
          printfn "Response received: %s" (content.Substring(0, min 200 content.Length))
          Expect.stringContains content "Hello from SageFs!" "Should contain initial greeting"
          Expect.stringContains content "This is the initial page." "Should contain initial text"
        | Error msg -> failtestf "Failed to get response: %s" msg

        printfn "Test completed successfully"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "hot reload Falco markup"
    <| fun _ ->
      task {
        printfn "Starting test: hot reload Falco markup"
        let actor = sharedActor.Value
        let port = getRandomPort ()

        // Create initial app
        let initialCode =
          $"""
open Falco
open Falco.Markup
open Falco.Markup.Elem
open Falco.Markup.Attr
open Falco.Markup.Text
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Hosting

let mutable hotReloadHandler = 
    Response.ofHtml (
        _html [] [
            _head [] [ _title [] [ _text "Hot Reload Test" ] ]
            _body [] [
                _h1 [] [ _text "Original Content" ]
                _p [ _id_ "content" ] [ _text "This is the original version." ]
            ]
        ]
    )

let hotReloadBuilder = WebApplication.CreateBuilder()
HostingAbstractionsWebHostBuilderExtensions.UseUrls(hotReloadBuilder.WebHost, "http://localhost:{port}") |> ignore
let hotReloadApp = hotReloadBuilder.Build()
hotReloadApp.MapGet("/", fun ctx -> hotReloadHandler ctx) |> ignore
let hotReloadStartTask = hotReloadApp.StartAsync()
printfn "Initial app started on port {port}"
"""

        printfn "Creating initial app on port %d..." port
        let! response1 = evalCode actor initialCode

        printfn "Diagnostics: %A" response1.Diagnostics

        match response1.EvaluationResult with
        | Error ex -> failtestf "Failed to create initial app: %s\nDiagnostics: %A" ex.Message response1.Diagnostics
        | Ok _ -> printfn "Initial app created"

        do! Task.Delay(500)

        // Test initial content with retries
        printfn "Testing initial content..."
        let! result1 = testHttpGetWithRetry $"http://localhost:{port}/" 10 200

        match result1 with
        | Ok content ->
          printfn "Initial response: %s" (content.Substring(0, min 200 content.Length))
          Expect.stringContains content "Original Content" "Should have original content"
        | Error msg -> failtestf "Failed initial request: %s" msg

        // Update the markup
        let updatedCode =
          """
hotReloadHandler <- 
    Response.ofHtml (
        _html [] [
            _head [] [ _title [] [ _text "Hot Reload Test" ] ]
            _body [] [
                _h1 [] [ _text "Updated Content!" ]
                _p [ _id_ "content" ] [ _text "This page was hot reloaded successfully!" ]
                _strong [] [ _text "SageFs rocks!" ]
            ]
        ]
    )
printfn "Handler updated"
"""

        printfn "Updating markup..."
        let! response2 = evalCode actor updatedCode

        match response2.EvaluationResult with
        | Error ex -> failtestf "Failed to update markup: %s" ex.Message
        | Ok _ -> printfn "Markup updated"

        do! Task.Delay(500)

        // Test updated content with retries
        printfn "Testing updated content..."
        let! result2 = testHttpGetWithRetry $"http://localhost:{port}/" 10 200

        match result2 with
        | Ok content ->
          printfn "Updated response: %s" (content.Substring(0, min 300 content.Length))
          Expect.stringContains content "Updated Content!" "Should have updated heading"
          Expect.stringContains content "hot reloaded successfully" "Should have updated text"
          Expect.stringContains content "SageFs rocks!" "Should have new strong text"
          Expect.isFalse (content.Contains "Original Content") "Should not have original content"
        | Error msg -> failtestf "Failed updated request: %s" msg

        printfn "Hot reload test completed successfully"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously
  ]

