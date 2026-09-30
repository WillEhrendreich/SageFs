/// The daemon keeps the last thing a worker said a save did. The wire shape is the
/// worker's (`DevReloadEvent.payloadJson`, built from a `ReloadOutcome` by
/// `ReloadBroadcast`), the reading side is `SessionReload.ofPayloadJson`, and they
/// are compiled in different places on purpose, so these tests are the contract
/// between them: every outcome a worker can produce reads back as what it was.
module SageFs.Tests.SessionReloadTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Features
open SageFs.Features.ReloadOutcome

/// The worker's outcome type. The module and the type share a name, so this says which.
type private Outcome = SageFs.Features.ReloadOutcome.ReloadOutcome

let private kept : KeptValue = { Binding = "Counter.count"; KeptValue = "41"; NewInitializer = "0" }

/// One real outcome of every case, with the reasons a worker would give.
let private everyOutcome : (ReloadCase * Outcome) list =
  [ ReloadCase.Patched, Outcome.Patched (3, 5)
    ReloadCase.PatchPending, Outcome.PatchPending (3, 5, [])
    ReloadCase.NeverEntered, Outcome.NeverEntered ("Ticker.renderLine", [], 2, 5, [])
    ReloadCase.Restarted, Outcome.Restarted [ RestartReason.NewDeclaration "Ticker.extra" ]
    ReloadCase.NoEffect, Outcome.NoEffect (4, [ RestartReason.SignatureChanged "Ticker.renderLine" ])
    ReloadCase.RestartRequired, Outcome.RestartRequired [ RestartReason.StartupComputedValue "Program.routes" ]
    ReloadCase.CompileFailed, Outcome.CompileFailed "Ticker.fs(12,3): error FS0039"
    ReloadCase.KeptLiveState, Outcome.KeptLiveState (1, 2, kept, []) ]

[<Tests>]
let tests =
  testList "SessionReload" [
    testCase "WHY — every outcome a worker can produce reads back as the same case with the same facts" <| fun _ ->
      for expectedCase, outcome in everyOutcome do
        let event = ReloadBroadcast.eventOf outcome
        let report = DevReload.DevReloadEvent.report event
        match SessionReload.ofPayloadJson (DevReload.DevReloadEvent.payloadJson event) with
        | Result.Ok (SessionReload.Finished facts) ->
          facts.Case |> Expect.equal (sprintf "%A: the case" expectedCase) expectedCase
          facts.Patched |> Expect.equal (sprintf "%A: patched" expectedCase) report.Patched
          facts.Considered |> Expect.equal (sprintf "%A: considered" expectedCase) report.Considered
          facts.Message |> Expect.equal (sprintf "%A: the worker's own wording" expectedCase) report.Message
          facts.SuggestedAction |> Expect.equal (sprintf "%A: the remedy" expectedCase) report.SuggestedAction
        | other -> failtestf "%A did not read back as a finished reload: %A" expectedCase other

    testCase "WHY — the token the parser reads is the token the worker writes, for every case" <| fun _ ->
      for expectedCase, outcome in everyOutcome do
        (DevReload.DevReloadEvent.report (ReloadBroadcast.eventOf outcome)).Outcome
        |> Expect.equal (sprintf "%A" expectedCase) (ReloadCase.token expectedCase)
      ReloadCase.all |> List.map ReloadCase.token |> List.distinct |> List.length
      |> Expect.equal "one token per case" ReloadCase.all.Length

    testCase "WHY — a save that is still compiling reads as compiling, with the file when there is one" <| fun _ ->
      SessionReload.ofPayloadJson (DevReload.DevReloadEvent.payloadJson (DevReload.Compiling (Some "/src/Ticker.fs")))
      |> Expect.equal "with the file" (Result.Ok (SessionReload.Compiling (Some "/src/Ticker.fs")))
      SessionReload.ofPayloadJson (DevReload.DevReloadEvent.payloadJson (DevReload.Compiling None))
      |> Expect.equal "without" (Result.Ok (SessionReload.Compiling None))

    testCase "WHY — before any save has resolved the worker says none, and that is its own case, not a success" <| fun _ ->
      SessionReload.ofPayloadJson """{"type":"none"}""" |> Expect.equal "none" (Result.Ok SessionReload.NoReloadYet)

    testCase "WHY — a payload that is not what a worker sends is an error that says why, never silently dropped" <| fun _ ->
      let isNotJson = function ReloadPayloadError.NotJson _ -> true | _ -> false
      for payload, expected in
        [ "not json at all", isNotJson
          """{"type":"mystery"}""", (fun e -> e = ReloadPayloadError.UnknownEventType "mystery")
          """{"type":"patched"}""", (fun e -> e = ReloadPayloadError.NoOutcome)
          """{"type":"patched","outcome":"Exploded"}""", (fun e -> e = ReloadPayloadError.UnknownOutcome "Exploded")
          "", isNotJson ] do
        match SessionReload.ofPayloadJson payload with
        | Result.Ok parsed -> failtestf "%s should not parse, got %A" payload parsed
        | Result.Error error ->
          expected error |> Expect.isTrue (sprintf "%s is refused for the right reason, not %A" payload error)
          ReloadPayloadError.describe error |> String.IsNullOrWhiteSpace |> Expect.isFalse (sprintf "%s: the description says why" payload)

    testProperty "WHY — whatever the message says (quotes, newlines, unicode), it survives the wire exactly" <| fun (message: string) ->
      let text = match message with null -> "" | m -> m
      let report : DevReload.ReloadReport =
        { DevReload.ReloadReport.none with Outcome = "Patched"; Patched = 2; Considered = 3; Message = text; SuggestedAction = text }
      match SessionReload.ofPayloadJson (DevReload.DevReloadEvent.payloadJson (DevReload.Patched report)) with
      | Result.Ok (SessionReload.Finished facts) -> facts.Message = text && facts.SuggestedAction = text && facts.Patched = 2
      | _ -> false

    testCase "WHY — describe says what a save did, and says so when nothing has been saved" <| fun _ ->
      SessionReload.describe SessionReload.NoReloadYet |> Expect.stringContains "nothing saved" "No hot reload yet"
      SessionReload.describe (SessionReload.Compiling (Some "/src/Ticker.fs")) |> Expect.stringContains "names the file" "Ticker.fs"
      let facts : ReloadFacts = { Case = ReloadCase.RestartRequired; Patched = 0; Considered = 1; Message = "restart the app to apply this"; SuggestedAction = "restart" }
      SessionReload.describe (SessionReload.Finished facts) |> Expect.equal "the worker's wording" "restart the app to apply this"
  ]
