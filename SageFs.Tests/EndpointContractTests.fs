module SageFs.Tests.EndpointContractTests

open Expecto
open Expecto.Flip
open SageFs.EndpointContracts

[<Tests>]
let endpointContractTests = testList "EndpointContracts" [

  testList "Neovim contract" [
    test "all Neovim contract endpoints exist in daemon" {
      let missing = missingEndpoints neovimContract
      missing
      |> Expect.isEmpty
        (sprintf "Neovim contract has endpoints missing from daemon: %A" missing)
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

    test "VS Code contract has expected count" {
      vscodeContract
      |> List.length
      |> Expect.equal "should have 13 endpoints" 13
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
      |> Expect.equal "apiVersion should be 2 for current contract" 2
    }

    test "VS Code extension expectedApiVersion matches daemon apiVersion" {
      // Cross-boundary contract test: reads the Fable source to extract the
      // literal expectedApiVersion and asserts it matches the daemon's value.
      // This prevents the exact mismatch bug reported as "apiVersion=2 is
      // incompatible with this extension (requires v1)".
      let clientFs =
        System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "sagefs-vscode", "src", "SageFsClient.fs")
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

    test "endpoint count pinned — update contracts when adding endpoints" {
      // CI GATE: This count is pinned. When you add a daemon endpoint:
      // 1. Add it to the appropriate list in EndpointContracts.fs
      // 2. Decide which plugin contracts need it (neovimContract, vscodeContract)
      // 3. Bump apiVersion if it's a breaking change
      // 4. Update this count
      all
      |> List.length
      |> Expect.equal
        "endpoint count changed — update contracts and bump this number"
        34
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

    testProperty "missingEndpoints returns empty for subset of all" <| fun (idx: int) ->
      let safeIdx = abs idx % (max 1 (all.Length))
      let ep = all.[safeIdx]
      let contract = [(ep.method, ep.path)]
      missingEndpoints contract |> List.isEmpty
  ]
]
