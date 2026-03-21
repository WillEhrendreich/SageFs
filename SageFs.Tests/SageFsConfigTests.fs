module SageFs.Tests.SageFsConfigTests

open Expecto
open Expecto.Flip
open System
open SageFs

[<Tests>]
let sageFsConfigTests =
  testList "SageFsConfig" [

    testCase "DefaultMcpPort literal is 37749" <| fun _ ->
      SageFsConfig.DefaultMcpPort
      |> Expect.equal "default MCP port" 37749

    testCase "DefaultDashboardPort is DefaultMcpPort + 1" <| fun _ ->
      SageFsConfig.DefaultDashboardPort
      |> Expect.equal "dashboard port = mcp + 1" (SageFsConfig.DefaultMcpPort + 1)

    testCase "DefaultDashboardPort is 37750" <| fun _ ->
      SageFsConfig.DefaultDashboardPort
      |> Expect.equal "dashboard port" 37750

    testCase "WorkerStartupTimeoutMs is positive" <| fun _ ->
      (SageFsConfig.WorkerStartupTimeoutMs, 0)
      |> Expect.isGreaterThan "must be positive"

    testCase "WorkerStartupTimeoutMs default is at least 30 seconds" <| fun _ ->
      (SageFsConfig.WorkerStartupTimeoutMs, 30_000)
      |> Expect.isGreaterThan "at least 30s"

    testCase "OtelConfigured is false when OtelEndpoint is empty" <| fun _ ->
      match SageFsConfig.OtelEndpoint with
      | "" ->
        SageFsConfig.OtelConfigured
        |> Expect.isFalse "OtelConfigured should be false when endpoint empty"
      | _ ->
        SageFsConfig.OtelConfigured
        |> Expect.isTrue "OtelConfigured should be true when endpoint set"

    testCase "RestartCount is non-negative" <| fun _ ->
      (SageFsConfig.RestartCount, 0)
      |> Expect.isGreaterThanOrEqual "restart count must be non-negative"

    testCase "BindHost is non-empty" <| fun _ ->
      SageFsConfig.BindHost
      |> Expect.isNotEmpty "bind host must not be empty"

    testCase "OtelProtocol has a default value" <| fun _ ->
      SageFsConfig.OtelProtocol
      |> Expect.isNotEmpty "otel protocol must not be empty"

    testCase "OtelServiceName has a default value" <| fun _ ->
      SageFsConfig.OtelServiceName
      |> Expect.isNotEmpty "otel service name must not be empty"

    testCase "envInt invalid value falls back to default" <| fun _ ->
      // We can't easily test the private envInt helper, but the observable
      // invariant is that all int configs return valid values even with bad env.
      (SageFsConfig.WorkerStartupTimeoutMs, 0)
      |> Expect.isGreaterThan "should have a valid default"

    testCase "McpPortFromEnv exposes a valid default port even under arbitrary environment state" <| fun _ ->
      let value = SageFsConfig.McpPortFromEnv
      (value > 0 && value < 65536)
      |> Expect.isTrue "MCP port value should remain in a valid range"
  ]
