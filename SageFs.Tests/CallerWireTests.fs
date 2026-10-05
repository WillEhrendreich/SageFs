module SageFs.Tests.CallerWireTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.CallerState
open SageFs.Features.ReloadOutcome

/// The callers state rides every reload report: the worker's SSE payload, the daemon's `lastReload` on the session, and
/// the words a client that only shows `message` shows. These cases pin that it arrives, whole, and that a report which
/// carries no word on callers is not read as "all is well".

type private Outcome = SageFs.Features.ReloadOutcome.ReloadOutcome

let private edit : SignatureEdit = { Declaration = "Shop.Tags.stamp"; Cause = SignatureCause.ReSigned; File = "/p/Tags.fs" }

let private site : CallSite = { File = "/p/Pages.fs"; Line = 12; Caller = "Shop.Pages.render"; Evidence = SiteEvidence.ResolvedByCompiler }

let private pending : CallersState =
  CallerLedger.empty
  |> CallerLedger.apply (LedgerEvent.Checked [ edit, CallersCheck.Callers(site, []) ])
  |> CallerLedger.stateOf

let private facts (payload: string) : ReloadFacts =
  match SessionReload.ofPayloadJson payload with
  | Result.Ok(SessionReload.Finished facts) -> facts
  | other -> failtestf "should parse as a finished reload: %A" other

[<Tests>]
let tests =
  testList "the callers state on every reload report" [

    testCase "WHY — a patched save with callers still on the old method says so in its message, and the next action is the remedy" <| fun _ ->
      let report = ReloadBroadcast.reportWith pending (Outcome.Patched(1, 1))
      report.Message |> Expect.stringContains "names the caller's file" "Pages.fs"
      report.Message |> Expect.stringContains "says what to do" "Save Pages.fs"
      report.SuggestedAction |> Expect.stringContains "a Patched outcome has no remedy of its own, so the callers' is the action" "Save Pages.fs"
      CallersState.ofJson report.Callers |> Expect.equal "the structure rides along" (Result.Ok pending)

    testCase "WHY — a pending patch's own remedy is to exercise the code, but a caller on the old method is what to do first, so that is the action" <| fun _ ->
      let report = ReloadBroadcast.reportWith pending (Outcome.PatchPending(1, 1, []))
      report.SuggestedAction |> Expect.stringContains "the callers' remedy leads" "Save Pages.fs"
      report.Message |> Expect.stringContains "the outcome's own words are still in the message" "Exercise the changed code"

    testCase "WHY — with nothing pending the report's words are exactly what they were before this state existed" <| fun _ ->
      let outcome = Outcome.Patched(1, 1)
      let report = ReloadBroadcast.reportWith CallersState.CallersCurrent outcome
      report.Message |> Expect.equal "unchanged" (ReloadOutcome.ReloadOutcome.describeForUser outcome)
      report.SuggestedAction |> Expect.equal "unchanged" ""
      CallersState.ofJson report.Callers |> Expect.equal "and says Current, so a client can tell it from silence" (Result.Ok CallersState.CallersCurrent)

    testCase "WHY — an outcome with a remedy of its own keeps it as the suggested action, and the callers are in the message too" <| fun _ ->
      let outcome = Outcome.RestartRequired [ RestartReason.StartupComputedValue "routes" ]
      let report = ReloadBroadcast.reportWith pending outcome
      report.SuggestedAction |> Expect.equal "the outcome's remedy" (ReloadOutcome.ReloadOutcome.remedy outcome |> Option.get)
      report.Message |> Expect.stringContains "the callers are still told" "Pages.fs"

    testCase "WHY — the default report carries Current, not an absent field, and the unchanged-save event carries it too" <| fun _ ->
      let report = ReloadBroadcast.reportOf (Outcome.Patched(1, 1))
      CallersState.ofJson report.Callers |> Expect.equal "Current" (Result.Ok CallersState.CallersCurrent)
      let unchanged = DevReload.DevReloadEvent.report (ReloadBroadcast.unchanged "Pages.fs")
      CallersState.ofJson unchanged.Callers |> Expect.equal "a save that changed nothing still says where callers stand" (Result.Ok CallersState.CallersCurrent)

    testCase "WHY — the worker's payload carries the callers object, and the daemon reads back the same state from it" <| fun _ ->
      let event = ReloadBroadcast.eventWith pending (Outcome.PatchPending(1, 1, []))
      let payload = DevReload.DevReloadEvent.payloadJson event
      payload |> Expect.stringContains "the field is in the payload" "\"callers\":{"
      (facts payload).Callers |> Expect.equal "same state on the other side" pending

    testCase "WHY — a payload with no callers field is NotReported, never Current" <| fun _ ->
      (facts """{"type":"patched","outcome":"Patched","patched":1,"considered":1}""").Callers
      |> Expect.equal "a worker that said nothing has not said all is well" CallersState.CallersNotReported

    testCase "WHY — a callers object the daemon cannot read is a named error, not a silent default" <| fun _ ->
      match SessionReload.ofPayloadJson """{"type":"patched","outcome":"Patched","callers":{"state":"CallersMystery"}}""" with
      | Result.Error(ReloadPayloadError.BadCallers detail) -> detail |> Expect.stringContains "says what it could not read" "CallersMystery"
      | other -> failtestf "expected BadCallers, got %A" other

    testCase "WHY — the daemon's status object carries the callers state under lastReload, structure and words" <| fun _ ->
      let finished =
        SessionReload.Finished
          { Case = ReloadCase.Patched; Patched = 1; Considered = 1; Message = "m"; SuggestedAction = ""
            Mechanism = PatchMechanism.Detour; Declarations = []; Callers = pending }
      let wire = System.Text.Json.JsonSerializer.Serialize(SessionReload.toWire finished)
      use doc = System.Text.Json.JsonDocument.Parse wire
      let callers = doc.RootElement.GetProperty "callers"
      callers.GetProperty("state").GetString() |> Expect.equal "the token" "CallersPending"
      callers.GetProperty("suggestedAction").GetString() |> Expect.stringContains "the action" "Save Pages.fs"
      callers.GetProperty("pending").[0].GetProperty("sites").[0].GetProperty("line").GetInt32() |> Expect.equal "the line" 12
  ]
