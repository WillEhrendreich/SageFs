module SageFs.Tests.WarmupProgressContractTests

open System.Collections.Generic
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WarmUp
open SageFs.Server.DaemonMode

let private dispatchedWarmupEvents progress =
  let messages = ResizeArray<SageFsMsg>()
  handleWarmupProgress messages.Add "session-1" progress
  messages |> Seq.toList

[<Tests>]
let warmupProgressContractTests =
  testList "WarmupProgressContract" [
    testCase "legacy line shape is preserved for valid progress" <| fun _ ->
      WarmupProgressLine.tryFormatLine 2 4 "Opening namespaces"
      |> Expect.equal
        "valid progress should keep the legacy WARMUP_PROGRESS line shape"
        (Some "WARMUP_PROGRESS=2/4 Opening namespaces")

    testCase "full line round-trips through shared contract" <| fun _ ->
      WarmupProgressLine.tryParseLine "WARMUP_PROGRESS=2/4 Opening namespaces"
      |> Expect.equal
        "valid worker line should round-trip through the shared contract"
        (Some (2, 4, "Opening namespaces"))

    testCase "valid payload still parses" <| fun _ ->
      tryParseWarmupProgress "2/4 Opening namespaces"
      |> Expect.equal "valid payload should parse" (Some (2, 4, "Opening namespaces"))

    testCase "parser rejects impossible step and total counts" <| fun _ ->
      tryParseWarmupProgress "0/4 Opening namespaces"
      |> Expect.isNone "step must be positive"

      tryParseWarmupProgress "5/4 Opening namespaces"
      |> Expect.isNone "step cannot exceed total"

      tryParseWarmupProgress "1/0 Opening namespaces"
      |> Expect.isNone "total must be positive"

    testCase "handleWarmupProgress ignores impossible payloads" <| fun _ ->
      dispatchedWarmupEvents "5/4 Opening namespaces"
      |> Expect.isEmpty "invalid payload should not dispatch an Elm warmup event"
  ]
