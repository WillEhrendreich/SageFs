#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/DaemonDiscovery.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.DaemonDiscovery

let tests =
  testList "VS Code daemon discovery contract" [
    testCase "configured ports derive dashboard from mcp" <| fun _ ->
      let ports = normalizeConfiguredPorts 38123 39000
      ports.McpPort |> Expect.equal "mcp port should round-trip" 38123
      ports.DashboardPort |> Expect.equal "dashboard port should follow the discovery contract" 38124

    testCase "discovered ports override stale client ports" <| fun _ ->
      let current : PortSnapshot =
        { McpPort = 37749
          DashboardPort = 37750 }

      let discovered : DiscoveredPorts =
        { McpPort = Some 38123
          DashboardPort = Some 38124 }

      let resolved = resolveDiscoveredPorts current discovered
      resolved.McpPort |> Expect.equal "daemon-reported mcp port should win" 38123
      resolved.DashboardPort |> Expect.equal "daemon-reported dashboard port should win" 38124

    testCase "discovered ports preserve the dashboard contract when the daemon reports stale data" <| fun _ ->
      let current : PortSnapshot =
        { McpPort = 37749
          DashboardPort = 37750 }

      let discovered : DiscoveredPorts =
        { McpPort = Some 38123
          DashboardPort = Some 39000 }

      let resolved = resolveDiscoveredPorts current discovered
      resolved.McpPort |> Expect.equal "daemon-reported mcp port should win" 38123
      resolved.DashboardPort |> Expect.equal "dashboard should still derive from the authoritative mcp port" 38124

    testCase "discovered mcp port derives dashboard port when omitted" <| fun _ ->
      let current : PortSnapshot =
        { McpPort = 37749
          DashboardPort = 37750 }

      let discovered : DiscoveredPorts =
        { McpPort = Some 38123
          DashboardPort = None }

      let resolved = resolveDiscoveredPorts current discovered
      resolved.DashboardPort |> Expect.equal "dashboard port should derive from discovered mcp port" 38124

    testCase "daemon json candidates prioritize persisted port before configured and default" <| fun _ ->
      candidateMcpPorts 39000 (Some 38123)
      |> Expect.equal "candidate order should prefer persisted state" [ 38123; 39000; defaultMcpPort ]

    testCase "daemon json parser reads url and port payloads" <| fun _ ->
      let parsedFromUrl =
        tryParseDaemonJsonMcpPort """{"Url":"http://localhost:38123"}"""

      let parsedFromPort =
        tryParseDaemonJsonMcpPort """{"mcpPort":38124,"dashboardPort":38125}"""

      parsedFromUrl |> Expect.equal "url payload should yield mcp port" (Some 38123)
      parsedFromPort |> Expect.equal "mcpPort payload should yield mcp port" (Some 38124)

    testCase "daemon start arguments carry the authoritative mcp port" <| fun _ ->
      buildDaemonStartArgs @"C:\repo\App.slnx" 38123
      |> Expect.equal "start arguments should include the configured mcp port" [| "--sln"; @"C:\repo\App.slnx"; "--mcp-port"; "38123" |]
  ]

Expecto.Tests.runTestsWithCLIArgs [] [||] tests
