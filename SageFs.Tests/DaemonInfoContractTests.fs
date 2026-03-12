module SageFs.Tests.DaemonInfoContractTests

open Expecto
open SageFs
open SageFs.Server.DashboardTypes

[<Tests>]
let daemonInfoContractTests =
  testList "DaemonInfoContract" [
    testCase "create derives dashboard port and api version" <| fun () ->
      let contract =
        DaemonInfoContract.create
          4242
          "1.2.3"
          "2026-03-12T00:00:00.0000000Z"
          @"C:\Code\Repos\SageFs"
          37749
          3

      Expect.equal contract.Pid 4242 "pid round-trips"
      Expect.equal contract.DashboardPort 37750 "dashboard port derived from mcp"
      Expect.equal contract.ApiVersion EndpointContracts.apiVersion "apiVersion matches endpoint contract"

    testCase "create preserves working directory and session count" <| fun () ->
      let contract =
        DaemonInfoContract.create
          7
          "0.0.1"
          "2026-03-12T00:00:00.0000000Z"
          @"C:\repo"
          38000
          0

      Expect.equal contract.WorkingDirectory @"C:\repo" "working directory round-trips"
      Expect.equal contract.SessionCount 0 "session count round-trips"

    testCase "create derives dashboard port from a custom smoke MCP port" <| fun () ->
      let contract =
        DaemonInfoContract.create
          11
          "0.0.1"
          "2026-03-12T00:00:00.0000000Z"
          @"C:\repo"
          37851
          1

      Expect.equal contract.McpPort 37851 "custom mcp port round-trips"
      Expect.equal contract.DashboardPort 37852 "dashboard port stays offset from custom mcp port"
  ]
