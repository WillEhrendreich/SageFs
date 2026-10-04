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
          "/logs/mcp-server20260930.log"

      contract.Pid |> Expect.equal "pid round-trips" 4242
      contract.LogPath |> Expect.equal "the log path is served, so 'where are the logs' has an answer" "/logs/mcp-server20260930.log"
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
          "/logs/a.log"

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
          "/logs/b.log"

      contract.McpPort |> Expect.equal "custom mcp port round-trips" 37851
      contract.DashboardPort |> Expect.equal "dashboard port stays offset from custom mcp port" 37852
  ]

[<Tests>]
let buildIdentityTests =
  let fullRevision = "b7ad4fdae486dbe9b28f8952c35a4a40de1fc649"
  let short = fullRevision.Substring(0, Features.FrictionTelemetryTypes.SageFsVersion.shortRevisionLength)
  testList "SageFsVersion.display" [
    testCase "a build says which commit it is, in the short form" <| fun () ->
      Features.FrictionTelemetryTypes.SageFsVersion.display ("0.6.892+" + fullRevision)
      |> Expect.equal "version, plus, the short revision" ("0.6.892+" + short)

    testCase "a local prerelease keeps its label in front of the revision" <| fun () ->
      Features.FrictionTelemetryTypes.SageFsVersion.display ("0.6.893-local1+" + fullRevision)
      |> Expect.equal "the prerelease label survives" ("0.6.893-local1+" + short)

    testCase "a version that carries no revision is shown as it is" <| fun () ->
      Features.FrictionTelemetryTypes.SageFsVersion.display "0.6.892"
      |> Expect.equal "nothing to trim" "0.6.892"

    testCase "the daemon reports the same build identity the core does, so the two never disagree" <| fun () ->
      SageFs.Server.DaemonInfo.version
      |> Expect.equal "daemon version is the displayed core version"
           (Features.FrictionTelemetryTypes.SageFsVersion.display (Features.FrictionTelemetryTypes.SageFsVersion.current ()))
  ]
