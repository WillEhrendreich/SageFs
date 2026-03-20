module SageFs.Tests.FrictionTelemetryTypesTests

open Expecto
open Expecto.Flip
open SageFs.Features.FrictionTelemetryTypes

[<Tests>]
let tests =
  testList "Friction telemetry types" [
    testCase "tool names reject empty input because telemetry without a tool is meaningless" <| fun _ ->
      ToolName.create "  "
      |> Expect.equal "empty tool names should be rejected" (Error "Tool name cannot be empty.")

    testCase "session refs reject empty input because friction must stay attributable" <| fun _ ->
      SessionRef.create ""
      |> Expect.equal "empty session refs should be rejected" (Error "Session reference cannot be empty.")

    testCase "durations reject negative values because time cannot run backward" <| fun _ ->
      DurationMs.create -1
      |> Expect.equal "negative durations should be rejected" (Error "Duration cannot be negative.")
  ]
