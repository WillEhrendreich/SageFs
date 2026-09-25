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

    // WHY — the daemon REFUSES to start when given --proj/--sln (it exits 2 with
    // "accepted for recognition but not implemented"). It has not loaded a project at
    // startup for a long time: it always starts bare and a session is created
    // afterwards. Emitting those flags here therefore meant "Start Daemon" could not
    // start a daemon at all. Only flags the daemon actually accepts may appear.
    testCase "daemon start arguments carry the mcp port and a valid bounded lifetime" <| fun _ ->
      buildDaemonStartArgs @"C:\repo\App.slnx" 38123
      |> Expect.equal "port plus TTL" [| "--mcp-port"; "38123"; "--ttl"; "4h" |]

    testCase "a project path is never passed to the daemon, because it refuses --proj" <| fun _ ->
      let args = buildDaemonStartArgs @"C:\repo\App.fsproj" 38124
      Expect.isFalse "no --proj" (args |> Array.contains "--proj")
      Expect.isFalse "no --sln" (args |> Array.contains "--sln")
      Expect.isFalse "and not the path either" (args |> Array.exists (fun a -> a.Contains "App.fsproj"))
  ]

Expecto.Tests.runTestsWithCLIArgs [] [||] tests
