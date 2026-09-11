module SageFs.Tests.HttpOriginGuardTests

open System
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open SageFs.Server

// The daemon's listeners: MCP and dashboard.
let private mcpPort = 37749
let private dashboardPort = 37750
let private own = HttpOriginGuard.OwnOrigins.ofPorts [ mcpPort; dashboardPort ]

let private request
    (httpMethod: string) (host: string option) (fetchSite: string option) (origin: string option)
    (contentType: string option) (body: HttpOriginGuard.Body)
    : HttpOriginGuard.Request =
  { Method = httpMethod; Host = host; SecFetchSite = fetchSite; Origin = origin
    ContentType = contentType; Body = body }

/// A POST with a JSON body — what Datastar, the editors and the CLI send.
let private jsonPost host fetchSite origin =
  request "POST" host fetchSite origin (Some "application/json") HttpOriginGuard.Body.Present

let private decide = HttpOriginGuard.decide own

let private rejectedWith (expected: HttpOriginGuard.Rejection) (verdict: HttpOriginGuard.Verdict) =
  verdict |> Expect.equal (sprintf "must be rejected: %A" expected) (HttpOriginGuard.Verdict.Reject expected)

let private allowed (because: string) (verdict: HttpOriginGuard.Verdict) =
  verdict |> Expect.equal because HttpOriginGuard.Verdict.Allow

// ─── The decision matrix (roast-4 §4) ───────────────────────────────────────

[<Tests>]
let matrixTests =
  testList "HttpOriginGuard.matrix" [

    testCase "local tooling with no browser headers passes" <| fun _ ->
      decide (jsonPost None None None) |> allowed "curl/CLI/editor must pass"
      decide (jsonPost (Some "localhost:37749") None None) |> allowed "a loopback Host must pass"
      decide (jsonPost (Some "127.0.0.1:37749") None None) |> allowed "127.0.0.1 Host must pass"

    testCase "IPv6 loopback Host [::1]:port passes" <| fun _ ->
      decide (jsonPost (Some "[::1]:37749") None None) |> allowed "[::1] is loopback"

    testCase "a Datastar-shaped same-origin POST from the dashboard passes" <| fun _ ->
      decide (jsonPost (Some "localhost:37750") (Some "same-origin") (Some "http://localhost:37750"))
      |> allowed "the dashboard's own page must reach its own endpoints"

    testCase "the dashboard page posting to the MCP port (same-site, own origin) passes" <| fun _ ->
      decide (jsonPost (Some "127.0.0.1:37749") (Some "same-site") (Some "http://127.0.0.1:37750"))
      |> allowed "both listeners are the daemon's own origins"
      decide (jsonPost (Some "[::1]:37749") (Some "same-site") (Some "http://[::1]:37750"))
      |> allowed "the IPv6 form of an own origin is own"

    testCase "a page on another localhost port is refused even though it is same-site" <| fun _ ->
      decide (jsonPost (Some "localhost:37749") (Some "same-site") (Some "http://localhost:5173"))
      |> rejectedWith (HttpOriginGuard.Rejection.ForeignOrigin "http://localhost:5173")

    testCase "a same-site text/plain POST from another port is refused" <| fun _ ->
      request "POST" (Some "localhost:37749") (Some "same-site") (Some "http://localhost:8888")
        (Some "text/plain;charset=UTF-8") HttpOriginGuard.Body.Present
      |> decide
      |> rejectedWith (HttpOriginGuard.Rejection.ForeignOrigin "http://localhost:8888")

    testCase "http://localhost.evil.com is a foreign origin" <| fun _ ->
      HttpOriginGuard.isLoopbackOrigin "http://localhost.evil.com"
      |> Expect.isFalse "a DNS name starting with localhost is not loopback"
      decide (jsonPost (Some "localhost:37749") (Some "cross-site") (Some "http://localhost.evil.com"))
      |> rejectedWith (HttpOriginGuard.Rejection.ForeignOrigin "http://localhost.evil.com")

    testCase "origins with userinfo, a path, https or no port are not own" <| fun _ ->
      for origin in [ "http://localhost:37749@evil.com"; "http://localhost:37749/"; "http://localhost:37749/x"
                      "https://localhost:37749"; "http://localhost"; "null"; "localhost:37749" ] do
        decide (jsonPost (Some "localhost:37749") (Some "same-origin") (Some origin))
        |> rejectedWith (HttpOriginGuard.Rejection.ForeignOrigin origin)

    testCase "a same-site or cross-site POST without an Origin is refused" <| fun _ ->
      decide (jsonPost (Some "localhost:37749") (Some "same-site") None)
      |> rejectedWith (HttpOriginGuard.Rejection.CrossSite "same-site")
      decide (jsonPost (Some "localhost:37749") (Some "cross-site") None)
      |> rejectedWith (HttpOriginGuard.Rejection.CrossSite "cross-site")

    testCase "reads: same-site GET passes, cross-site GET is refused" <| fun _ ->
      request "GET" (Some "localhost:37750") (Some "same-site") None None HttpOriginGuard.Body.Empty
      |> decide |> allowed "the same-origin policy hides a same-site read's response"
      request "GET" (Some "localhost:37750") (Some "cross-site") None None HttpOriginGuard.Body.Empty
      |> decide |> rejectedWith (HttpOriginGuard.Rejection.CrossSite "cross-site")

    testCase "an unknown Sec-Fetch-Site value is refused" <| fun _ ->
      decide (jsonPost (Some "localhost:37749") (Some "sideways") None)
      |> rejectedWith (HttpOriginGuard.Rejection.UnknownFetchSite "sideways")

    testCase "a non-loopback Host is refused (DNS rebinding)" <| fun _ ->
      for host in [ "sagefs.evil.com:37749"; "localhost.evil.com:37749"; "localhost:37749@evil.com"; "192.168.1.20:37749" ] do
        decide (jsonPost (Some host) None None)
        |> rejectedWith (HttpOriginGuard.Rejection.ForeignHost host)

    testCase "a body must be labelled JSON; a bodyless POST needs no Content-Type" <| fun _ ->
      request "POST" (Some "localhost:37749") None None (Some "text/plain; charset=utf-8") HttpOriginGuard.Body.Present
      |> decide |> rejectedWith (HttpOriginGuard.Rejection.NonJsonBody "text/plain; charset=utf-8")
      request "POST" (Some "localhost:37749") None None None HttpOriginGuard.Body.Present
      |> decide |> rejectedWith (HttpOriginGuard.Rejection.NonJsonBody "(none)")
      request "POST" (Some "localhost:37749") None None (Some "application/json; charset=utf-8") HttpOriginGuard.Body.Present
      |> decide |> allowed "charset parameters are fine"
      request "POST" (Some "localhost:37750") None None (Some "text/plain; charset=utf-8") HttpOriginGuard.Body.Empty
      |> decide |> allowed "an empty POST (e.g. /api/shutdown, Visual Studio's reset) carries nothing to mislabel"

    testCase "rejections map to 403, a mislabelled body to 415" <| fun _ ->
      HttpOriginGuard.Rejection.statusCode (HttpOriginGuard.Rejection.NonJsonBody "text/plain")
      |> Expect.equal "a wrong media type is 415" 415
      HttpOriginGuard.Rejection.statusCode (HttpOriginGuard.Rejection.ForeignOrigin "http://localhost:5173")
      |> Expect.equal "a foreign page is 403" 403
  ]

// ─── Properties ─────────────────────────────────────────────────────────────

let private genHostForm = Gen.elements HttpOriginGuard.OwnOrigins.loopbackHostForms
let private genForeignPort =
  Gen.choose (1, 65535) |> Gen.filter (fun p -> p <> mcpPort && p <> dashboardPort)
let private genOwnPort = Gen.elements [ mcpPort; dashboardPort ]
let private genUnsafeMethod = Gen.elements [ "POST"; "PUT"; "PATCH"; "DELETE"; "post" ]
let private genAnyMethod = Gen.elements [ "GET"; "HEAD"; "OPTIONS"; "POST"; "PUT"; "PATCH"; "DELETE" ]
let private genSimpleContentType =
  Gen.elements [ "text/plain"; "text/plain;charset=UTF-8"; "application/x-www-form-urlencoded"
                 "multipart/form-data; boundary=----x" ]
let private genLabel =
  Gen.elements ([ 'a' .. 'z' ] @ [ '0' .. '9' ])
  |> Gen.nonEmptyListOf
  |> Gen.map (fun cs -> String(List.toArray cs))

[<Tests>]
let propertyTests =
  testList "HttpOriginGuard.properties" [

    testProperty "a page on any other loopback port is refused for every unsafe request" <|
      Prop.forAll
        (Arb.fromGen (gen {
          let! host = genHostForm
          let! port = genForeignPort
          let! site = Gen.elements [ None; Some "same-site"; Some "cross-site"; Some "same-origin" ]
          let! httpMethod = genUnsafeMethod
          let! contentType = Gen.oneof [ Gen.constant "application/json"; genSimpleContentType ]
          return host, port, site, httpMethod, contentType }))
        (fun (host, port, site, httpMethod, contentType) ->
          let origin = sprintf "http://%s:%d" host port
          request httpMethod (Some (sprintf "%s:%d" host mcpPort)) site (Some origin) (Some contentType) HttpOriginGuard.Body.Present
          |> decide = HttpOriginGuard.Verdict.Reject (HttpOriginGuard.Rejection.ForeignOrigin origin))

    testProperty "every own origin passes a JSON request in any host form" <|
      Prop.forAll
        (Arb.fromGen (gen {
          let! host = genHostForm
          let! originHost = genHostForm
          let! port = genOwnPort
          let! site = Gen.elements [ None; Some "same-origin"; Some "same-site"; Some "none" ]
          let! httpMethod = genAnyMethod
          return host, originHost, port, site, httpMethod }))
        (fun (host, originHost, port, site, httpMethod) ->
          request httpMethod (Some (sprintf "%s:%d" host port)) site (Some (sprintf "http://%s:%d" originHost port))
            (Some "application/json") HttpOriginGuard.Body.Present
          |> decide = HttpOriginGuard.Verdict.Allow)

    testProperty "a CORS-simple body is refused on every unsafe request, browser or not" <|
      Prop.forAll
        (Arb.fromGen (gen {
          let! httpMethod = genUnsafeMethod
          let! contentType = genSimpleContentType
          let! origin = Gen.elements [ None; Some "http://localhost:37750" ]
          return httpMethod, contentType, origin }))
        (fun (httpMethod, contentType, origin) ->
          request httpMethod (Some "localhost:37749") None origin (Some contentType) HttpOriginGuard.Body.Present
          |> decide = HttpOriginGuard.Verdict.Reject (HttpOriginGuard.Rejection.NonJsonBody contentType))

    testProperty "a DNS name that merely starts with localhost is never loopback" <|
      Prop.forAll
        (Arb.fromGen (gen {
          let! label = genLabel
          let! dotted = Gen.elements [ true; false ]
          let! port = Gen.choose (1, 65535)
          return label, dotted, port }))
        (fun (label, dotted, port) ->
          let name = match dotted with | true -> "localhost." + label | false -> "localhost" + label
          not (HttpOriginGuard.isLoopbackOrigin (sprintf "http://%s:%d" name port))
          && not (HttpOriginGuard.isLoopbackHost (sprintf "%s:%d" name port)))

    testProperty "every loopback host form is loopback, with or without a port" <|
      Prop.forAll
        (Arb.fromGen (gen {
          let! host = genHostForm
          let! port = Gen.choose (1, 65535)
          return host, port }))
        (fun (host, port) ->
          HttpOriginGuard.isLoopbackHost (sprintf "%s:%d" host port)
          && HttpOriginGuard.isLoopbackHost host
          && HttpOriginGuard.isLoopbackOrigin (sprintf "http://%s:%d" host port))

    testProperty "local tooling (no browser headers) passes on loopback for any method" <|
      Prop.forAll
        (Arb.fromGen (gen {
          let! host = genHostForm
          let! port = Gen.choose (1, 65535)
          let! httpMethod = genAnyMethod
          let! body = Gen.elements [ HttpOriginGuard.Body.Empty; HttpOriginGuard.Body.Present ]
          return host, port, httpMethod, body }))
        (fun (host, port, httpMethod, body) ->
          request httpMethod (Some (sprintf "%s:%d" host port)) None None (Some "application/json") body
          |> decide = HttpOriginGuard.Verdict.Allow)
  ]

// ─── HTTP level: the daemon middleware in front of a real Kestrel host ──────

let private freeLoopbackPort () =
  let listener = new TcpListener(IPAddress.Loopback, 0)
  listener.Start()
  let port = (listener.LocalEndpoint :?> IPEndPoint).Port
  listener.Stop()
  port

/// A loopback Kestrel host whose own origins are its port and port + 1 (the
/// daemon's MCP + dashboard pair), running the daemon's origin guard in front
/// of an /exec that parses its body the way the real /exec does (readJsonBody).
/// `executed` counts requests that reached the handler.
let private startGuardedExec (executed: int ref) : Task<WebApplication * int> = task {
  let port = freeLoopbackPort ()
  let builder = WebApplication.CreateBuilder([||])
  builder.WebHost.UseUrls(sprintf "http://127.0.0.1:%d" port) |> ignore
  let app = builder.Build()
  McpServer.useOriginGuard (HttpOriginGuard.OwnOrigins.ofPorts [ port; port + 1 ]) app
  app.MapPost("/exec", RequestDelegate(fun ctx ->
    task {
      use! json = McpServer.readJsonBody ctx
      json.RootElement.GetProperty("code").GetString() |> ignore
      executed.Value <- executed.Value + 1
      ctx.Response.StatusCode <- 200
      do! ctx.Response.WriteAsync("""{"success":true}""")
    } :> Task)) |> ignore
  do! app.StartAsync()
  return app, port
}

let private guardClient = new HttpClient()

let private post
    (port: int) (path: string) (contentType: string) (headers: (string * string) list) (body: string)
    : Task<HttpResponseMessage> =
  task {
    use req = new HttpRequestMessage(HttpMethod.Post, sprintf "http://127.0.0.1:%d%s" port path)
    let content = new ByteArrayContent(Encoding.UTF8.GetBytes body)
    content.Headers.TryAddWithoutValidation("Content-Type", contentType) |> ignore
    req.Content <- content
    for (name, value) in headers do
      req.Headers.TryAddWithoutValidation(name, value) |> ignore
    use cts = new Threading.CancellationTokenSource(TimeSpan.FromSeconds 20.0)
    return! guardClient.SendAsync(req, cts.Token)
  }

/// Run `body` against a fresh guarded host, then stop the host (awaited, never
/// blocked on) whether or not the body threw.
let private withGuardedExec (body: int -> int ref -> Task<unit>) : Task<unit> = task {
  let executed = ref 0
  let! (app, port) = startGuardedExec executed
  let! outcome = task {
    try
      do! body port executed
      return Ok ()
    with ex -> return Error ex
  }
  do! app.StopAsync()
  do! app.DisposeAsync()
  match outcome with
  | Ok () -> ()
  | Error ex -> Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
}

[<Tests>]
let httpTests =
  testList "HttpOriginGuard.http" [

    testTask "a cross-port same-site text/plain POST to /exec is rejected and never executes" {
      do! withGuardedExec (fun port executed -> task {
        use! resp =
          post port "/exec" "text/plain;charset=UTF-8"
            [ "Origin", "http://localhost:5173"; "Sec-Fetch-Site", "same-site" ]
            """{"code":"System.IO.File.Delete \"x\""}"""
        int resp.StatusCode |> Expect.equal "a page on another localhost port must be refused" 403
        executed.Value |> Expect.equal "the refused eval must never reach /exec" 0
      })
    }

    testTask "a Datastar-shaped same-origin JSON POST succeeds" {
      do! withGuardedExec (fun port executed -> task {
        use! resp =
          post port "/exec" "application/json"
            [ "Origin", sprintf "http://127.0.0.1:%d" port; "Sec-Fetch-Site", "same-origin"; "Datastar-Request", "true" ]
            """{"code":"1 + 1"}"""
        int resp.StatusCode |> Expect.equal "the daemon's own page must be served" 200
        executed.Value |> Expect.equal "the allowed eval must reach /exec" 1
      })
    }

    testTask "the dashboard origin (the sibling listener) may POST JSON same-site" {
      do! withGuardedExec (fun port executed -> task {
        use! resp =
          post port "/exec" "application/json"
            [ "Origin", sprintf "http://localhost:%d" (port + 1); "Sec-Fetch-Site", "same-site" ]
            """{"code":"1 + 1"}"""
        int resp.StatusCode |> Expect.equal "the dashboard is one of the daemon's own origins" 200
        executed.Value |> Expect.equal "the allowed eval must reach /exec" 1
      })
    }

    testTask "a text/plain body without browser headers is refused 415 and never executes" {
      do! withGuardedExec (fun port executed -> task {
        use! resp = post port "/exec" "text/plain" [] """{"code":"1 + 1"}"""
        int resp.StatusCode |> Expect.equal "an unlabelled JSON body is refused" 415
        executed.Value |> Expect.equal "the refused eval must never reach /exec" 0
      })
    }
  ]
