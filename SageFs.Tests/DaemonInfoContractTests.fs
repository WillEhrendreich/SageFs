module SageFs.Tests.DaemonInfoContractTests

open Expecto
open Expecto.Flip
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

      contract.Pid |> Expect.equal "pid round-trips" 4242
      contract.DashboardPort |> Expect.equal "dashboard port derived from mcp" 37750
      contract.ApiVersion |> Expect.equal "apiVersion matches endpoint contract" EndpointContracts.apiVersion

    testCase "create preserves working directory and session count" <| fun () ->
      let contract =
        DaemonInfoContract.create
          7
          "0.0.1"
          "2026-03-12T00:00:00.0000000Z"
          @"C:\repo"
          38000
          0

      contract.WorkingDirectory |> Expect.equal "working directory round-trips" @"C:\repo"
      contract.SessionCount |> Expect.equal "session count round-trips" 0

    testCase "create derives dashboard port from a custom smoke MCP port" <| fun () ->
      let contract =
        DaemonInfoContract.create
          11
          "0.0.1"
          "2026-03-12T00:00:00.0000000Z"
          @"C:\repo"
          37851
          1

      contract.McpPort |> Expect.equal "custom mcp port round-trips" 37851
      contract.DashboardPort |> Expect.equal "dashboard port stays offset from custom mcp port" 37852
  ]
