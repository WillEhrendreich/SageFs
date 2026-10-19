/// The endpoint table (`EndpointContracts.all`) is the daemon's API surface as the plugins see it. A table that is only
/// checked against itself cannot notice a route the daemon maps and nobody listed, so this reads the daemon's own route
/// registrations from `SageFs/*.fs` and fails in BOTH directions: a mapped route the table lacks, and a table row nothing
/// maps. The browser UI's routes (`/dashboard...`, `/favicon.ico`) are the page's own and are not part of the table. A
/// registration the scan cannot read as a literal route (a path built at run time) fails its own test, so it cannot hide.
module SageFs.Tests.EndpointContractTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open SageFs.EndpointContracts

/// A route as the daemon registers it, with its path as written.
type MappedRoute = { Method: HttpMethod; Path: string }

module RouteScan =
  /// `app.MapGet("/x"`, `app.MapPost("/x"`, and the Falco helpers `mapGetRaw "/x"` / `mapPostRaw "/x"`.
  let literalRoute =
    Regex(@"(?:\.Map(?<verb>Get|Post)\(\s*|\bmap(?<verb>Get|Post)Raw\s+)""(?<path>[^""]+)""", RegexOptions.Compiled)

  /// Falco's own `yield get "/x"` and `yield post "/x"`.
  let yieldedRoute = Regex(@"\byield\s+(?<verb>get|post)\s+""(?<path>[^""]+)""", RegexOptions.Compiled)

  /// `proxyGetRoute "/x"`, which maps the session-scoped route under a prefix the helper's definition carries.
  let proxiedRoute = Regex(@"\bproxy(?<verb>Get|Post)Route\s+""(?<path>[^""]+)""", RegexOptions.Compiled)

  /// The helper's definition, which is where the prefix lives: `let proxyGetRoute path = ... sprintf "/api/sessions/{sid}%s" path`.
  let proxyDefinition =
    Regex(@"let\s+proxy(?<verb>Get|Post)Route\s+path\s*=.*sprintf\s+""(?<prefix>[^""]*)%s""", RegexOptions.Compiled)

  /// Any text that registers a route. A line with one of these and no literal route is a registration the scan cannot read.
  let registration =
    Regex(
      @"\.Map(?:Get|Post|Put|Delete|Patch|Methods)\(|\bmap(?:Get|Post)Raw\b|\bproxy(?:Get|Post)Route\b|\byield\s+(?:get|post|put|delete|patch|any)\b",
      RegexOptions.Compiled)

  /// A definition of a registration helper (not a use of one).
  let helperDefinition = Regex(@"^\s*let\s+(?:inline\s+)?(?:proxy(?:Get|Post)Route|map(?:Get|Post)Raw)\b", RegexOptions.Compiled)

  /// The browser UI's own routes: the page, its fragments and its actions. They are not the daemon's API.
  let uiPrefixes = [ "/dashboard"; "/favicon.ico" ]

  let isUiRoute (path: string) : bool =
    uiPrefixes |> List.exists (fun prefix -> path.StartsWith(prefix, System.StringComparison.Ordinal))

  let methodOf (verb: string) : HttpMethod =
    match verb.ToLowerInvariant() with
    | "get" -> GET
    | _ -> POST

  let codeLines (text: string) : string list =
    text.Split '\n'
    |> Array.map (fun line -> line.TrimEnd '\r')
    |> Array.filter (fun line -> not (line.TrimStart().StartsWith "//"))
    |> List.ofArray

  let verbOf (m: Match) : string = m.Groups["verb"].Captures[0].Value

  /// The route prefix of each proxy helper, by verb.
  let proxyPrefixes (lines: string list) : Map<HttpMethod, string> =
    lines
    |> List.choose (fun line ->
      match proxyDefinition.Match line with
      | m when m.Success -> Some (methodOf (verbOf m), m.Groups["prefix"].Value)
      | _ -> None)
    |> Map.ofList

  /// Every literal route registered in `text`, in order, with the proxy helpers expanded.
  let routesIn (text: string) : MappedRoute list =
    let lines = codeLines text
    let prefixes = proxyPrefixes lines
    lines
    |> List.collect (fun line ->
      [ for m in literalRoute.Matches line -> { Method = methodOf (verbOf m); Path = m.Groups["path"].Value }
        for m in yieldedRoute.Matches line -> { Method = methodOf (verbOf m); Path = m.Groups["path"].Value }
        for m in proxiedRoute.Matches line do
          let meth = methodOf (verbOf m)
          let prefix = Map.tryFind meth prefixes |> Option.defaultValue ""
          yield { Method = meth; Path = prefix + m.Groups["path"].Value } ])

  /// Lines that register a route but name no literal path: the scan would silently miss these.
  let unreadableIn (text: string) : string list =
    codeLines text
    |> List.filter (fun line ->
      registration.IsMatch line
      && not (helperDefinition.IsMatch line)
      && not (literalRoute.IsMatch line)
      && not (yieldedRoute.IsMatch line)
      && not (proxiedRoute.IsMatch line))
    |> List.map (fun line -> line.Trim())

  let daemonSources () : string list =
    Directory.GetFiles(RepoPaths.repoPath [| "SageFs" |], "*.fs", SearchOption.AllDirectories)
    |> Array.filter (fun path ->
      let normal = path.Replace('\\', '/')
      not (normal.Contains "/obj/") && not (normal.Contains "/bin/"))
    |> List.ofArray

  /// What the daemon maps: the API routes, as (method, normalized path), with the browser UI's routes left out.
  let mapped () : Set<HttpMethod * string> =
    daemonSources ()
    |> List.collect (File.ReadAllText >> routesIn)
    |> List.filter (fun route -> not (isUiRoute route.Path))
    |> List.map (fun route -> route.Method, normalizePath route.Path)
    |> Set.ofList

  /// What the table lists, in the same shape.
  let listed () : Set<HttpMethod * string> =
    all |> List.map (fun ep -> ep.method, normalizePath ep.path) |> Set.ofList

let routeScanTests =
  testList "Route scan" [
    test "MapGet, MapPost, mapGetRaw, mapPostRaw and Falco yield routes are read, comments are not" {
      let text =
        String.concat "\n"
          [ """  app.MapGet("/health", fun ctx -> task { () }) |> ignore"""
            """  app.MapPost(  "/exec", handler) |> ignore"""
            """  Dashboard.mapPostRaw "/api/sessions/{sid}/live-values/mode" read handler"""
            """  yield get "/api/state" (handler)"""
            """  // app.MapGet("/commented", handler)""" ]
      RouteScan.routesIn text
      |> Expect.equal "four routes, in order"
        [ { Method = GET; Path = "/health" }
          { Method = POST; Path = "/exec" }
          { Method = POST; Path = "/api/sessions/{sid}/live-values/mode" }
          { Method = GET; Path = "/api/state" } ]
    }

    test "a proxied route is expanded with the prefix its helper carries, per verb" {
      let text =
        String.concat "\n"
          [ """  let proxyGetRoute path = Dashboard.mapGetRaw (sprintf "/api/sessions/{sid}%s" path) extractSid handler"""
            """  let proxyPostRoute path = Dashboard.mapPostRaw (sprintf "/api/sessions/{sid}%s" path) extractSid handler"""
            """    proxyGetRoute "/hotreload" """
            """    proxyPostRoute "/hotreload/toggle" """ ]
      RouteScan.routesIn text
      |> Expect.equal "both expanded"
        [ { Method = GET; Path = "/api/sessions/{sid}/hotreload" }
          { Method = POST; Path = "/api/sessions/{sid}/hotreload/toggle" } ]
    }

    test "a registration whose path is not a literal is reported, a helper definition is not" {
      let text =
        String.concat "\n"
          [ """  app.MapGet(computedPath, handler) |> ignore"""
            """  let mapGetRaw (route: string) (read: HttpContext -> 'T) = get route handler"""
            """  app.MapGet("/ok", handler) |> ignore""" ]
      RouteScan.unreadableIn text
      |> Expect.equal "only the computed path" [ "app.MapGet(computedPath, handler) |> ignore" ]
    }

    test "UI routes are told apart from API routes" {
      RouteScan.isUiRoute "/dashboard/stream/{clientId}" |> Expect.isTrue "dashboard fragment"
      RouteScan.isUiRoute "/favicon.ico" |> Expect.isTrue "favicon"
      RouteScan.isUiRoute "/api/state" |> Expect.isFalse "an API route"
    }

    test "the daemon's sources hold registrations the scan can read, and none it cannot" {
      let sources = RouteScan.daemonSources ()
      (sources.Length, 0) |> Expect.isGreaterThan "the daemon's source files were found"
      let unreadable = sources |> List.collect (File.ReadAllText >> RouteScan.unreadableIn)
      unreadable |> Expect.isEmpty "every registration names a literal path (a computed one would hide from the contract)"
      (RouteScan.mapped().Count, 40) |> Expect.isGreaterThan "the scan reads the real route registrations"
    }
  ]

[<Tests>]
let endpointContractTests = testList "EndpointContracts" [

  testList "Neovim contract" [
    test "all Neovim contract endpoints exist in daemon" {
      let missing = missingEndpoints neovimContract
      missing
      |> Expect.isEmpty
        (sprintf "Neovim contract has endpoints missing from daemon: %A" missing)
    }

    test "all Neovim contract endpoints are routes the daemon really maps" {
      let mapped = RouteScan.mapped ()
      let unmapped = neovimContract |> List.filter (fun (m, p) -> not (mapped.Contains (m, normalizePath p)))
      unmapped |> Expect.isEmpty (sprintf "Neovim contract names routes the daemon does not map: %A" unmapped)
    }

    test "Neovim contract has expected count" {
      neovimContract
      |> List.length
      |> Expect.equal "should have 19 endpoints" 19
    }

    test "Neovim contract includes SSE events endpoint" {
      neovimContract
      |> List.exists (fun (m, p) -> m = GET && p = "/events")
      |> Expect.isTrue "SSE /events must be in Neovim contract"
    }

    test "Neovim contract includes /exec" {
      neovimContract
      |> List.exists (fun (m, p) -> m = POST && p = "/exec")
      |> Expect.isTrue "/exec must be in Neovim contract"
    }

    test "Neovim contract includes session management" {
      let sessionPaths =
        neovimContract
        |> List.filter (fun (_, p) -> p.Contains("/api/sessions"))
        |> List.length
      (sessionPaths, 4)
      |> Expect.isGreaterThanOrEqual "at least 4 session endpoints"
    }

    test "Neovim contract includes live testing" {
      let testPaths =
        neovimContract
        |> List.filter (fun (_, p) -> p.Contains("/api/live-testing"))
        |> List.length
      (testPaths, 3)
      |> Expect.isGreaterThanOrEqual "at least 3 testing endpoints"
    }
  ]

  testList "VS Code contract" [
    test "all VS Code contract endpoints exist in daemon" {
      let missing = missingEndpoints vscodeContract
      missing
      |> Expect.isEmpty
        (sprintf "VS Code contract has endpoints missing from daemon: %A" missing)
    }

    test "all VS Code contract endpoints are routes the daemon really maps" {
      let mapped = RouteScan.mapped ()
      let unmapped = vscodeContract |> List.filter (fun (m, p) -> not (mapped.Contains (m, normalizePath p)))
      unmapped |> Expect.isEmpty (sprintf "VS Code contract names routes the daemon does not map: %A" unmapped)
    }

    test "VS Code contract has expected count" {
      vscodeContract
      |> List.length
      |> Expect.equal "should have 17 endpoints" 17
    }
  ]

  testList "API version" [
    test "apiVersion is a positive integer" {
      (apiVersion, 1)
      |> Expect.isGreaterThanOrEqual "apiVersion must be at least 1"
    }

    test "apiVersion matches current contract shape" {
      // Pin the version so any contract change forces a conscious version bump
      apiVersion
      |> Expect.equal "apiVersion should be 3 for current contract" 3
    }

    test "VS Code extension expectedApiVersion matches daemon apiVersion" {
      // Cross-boundary contract test: reads the Fable source to extract the
      // literal expectedApiVersion and asserts it matches the daemon's value.
      // This prevents the exact mismatch bug reported as "apiVersion=2 is
      // incompatible with this extension (requires v1)".
      let clientFs =
        RepoPaths.repoPath [| "sagefs-vscode"; "src"; "SageFsClient.fs" |]
        |> System.IO.File.ReadAllText
      let m =
        System.Text.RegularExpressions.Regex.Match(
          clientFs,
          @"let\s+\[<Literal>\]\s+expectedApiVersion\s*=\s*(\d+)")
      m.Success
      |> Expect.isTrue "should find expectedApiVersion literal in SageFsClient.fs"
      let extensionVersion = int m.Groups.[1].Value
      extensionVersion
      |> Expect.equal
        "VS Code extension expectedApiVersion must match EndpointContracts.apiVersion"
        apiVersion
    }
  ]

  testList "Endpoint registry" [
    test "all endpoints have non-empty path" {
      all
      |> List.filter (fun ep -> System.String.IsNullOrEmpty(ep.path))
      |> Expect.isEmpty "no endpoint should have empty path"
    }

    test "all endpoints have non-empty description" {
      all
      |> List.filter (fun ep -> System.String.IsNullOrEmpty(ep.description))
      |> Expect.isEmpty "no endpoint should have empty description"
    }

    test "all endpoint paths start with /" {
      all
      |> List.filter (fun ep -> not (ep.path.StartsWith("/")))
      |> Expect.isEmpty "all paths must start with /"
    }

    test "no duplicate endpoint definitions" {
      let dupes =
        all
        |> List.groupBy (fun ep -> (ep.method, normalizePath ep.path))
        |> List.filter (fun (_, group) -> group.Length > 1)
        |> List.map fst
      dupes
      |> Expect.isEmpty (sprintf "duplicate endpoints: %A" dupes)
    }

    test "session-scoped buffer changed endpoint exists" {
      all
      |> List.exists (fun ep -> ep.method = POST && ep.path = "/api/sessions/{sid}/buffer-changed")
      |> Expect.isTrue "daemon must expose a session-scoped buffer changed endpoint for unsaved editor content"
    }

    test "every API route the daemon maps is in the table" {
      // When you add a daemon route: list it in EndpointContracts.fs, decide which plugin contracts need it
      // (neovimContract, vscodeContract), and bump apiVersion if the change is breaking. The commit that touches
      // the wire surface carries a Plugin: trailer, and an unlisted route is a surface that check cannot see.
      let unlisted =
        Set.difference (RouteScan.mapped ()) (RouteScan.listed ())
        |> Set.toList
      unlisted |> Expect.isEmpty (sprintf "the daemon maps routes EndpointContracts.all does not list: %A" unlisted)
    }

    test "every row of the table is a route the daemon maps" {
      let unmapped =
        Set.difference (RouteScan.listed ()) (RouteScan.mapped ())
        |> Set.toList
      unmapped |> Expect.isEmpty (sprintf "EndpointContracts.all lists routes nothing maps: %A" unmapped)
    }
  ]

  testList "Contract validation" [
    test "normalizePath replaces all placeholders" {
      normalizePath "/api/sessions/{sid}/hotreload"
      |> Expect.equal "should normalize" "/api/sessions/{id}/hotreload"
    }

    test "normalizePath handles multiple placeholders" {
      normalizePath "/api/{a}/foo/{b}/bar"
      |> Expect.equal "should normalize both" "/api/{id}/foo/{id}/bar"
    }

    test "normalizePath leaves paths without placeholders unchanged" {
      normalizePath "/api/sessions"
      |> Expect.equal "should be unchanged" "/api/sessions"
    }

    test "uncovered endpoints returns daemon endpoints not in any contract" {
      let uncovered = uncoveredEndpoints [neovimContract; vscodeContract]
      // There should be some uncovered endpoints (dashboard, diagnostics, etc.)
      (uncovered |> List.length, 0)
      |> Expect.isGreaterThan "some endpoints are uncovered"
    }
  ]

  routeScanTests
]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant endpointContractTests |> ignore
