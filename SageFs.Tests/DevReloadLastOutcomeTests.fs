/// `DevReload.LastReload` — carrying `ReloadOutcome` past the server
/// boundary for a client that cannot hold the `/__sagefs__/reload` SSE
/// stream open (sagefs-ux-roast.md §11, Island C). Every `broadcastXxx`
/// funnels through the same `broadcast` choke point that live SSE
/// subscribers see, so a client that only polls plain HTTP gets the exact
/// same payload — `ReloadOutcome.describeForUser` rendered once, server
/// side, never re-derived.
///
/// `LastReload`'s state is process-global (an AppDomain-shared slot, same
/// technique as the SSE client registry), so this whole list runs
/// sequenced: interleaving with anything else that calls `broadcastXxx`
/// would make the "what was last recorded" assertions meaningless.
module SageFs.Tests.DevReloadLastOutcomeTests

open Expecto
open Expecto.Flip
open SageFs.DevReload
open SageFs.Features.ReloadOutcome

// Same abbreviation discipline as ReloadBroadcastTests.fs: `SageFs.Features`
// is deliberately NOT opened, so the bare name `ReloadOutcome` keeps
// resolving to the (RequireQualifiedAccess) TYPE rather than the sibling
// companion module.
module Outcome = SageFs.Features.ReloadOutcome.ReloadOutcome
module Broadcast = SageFs.Features.ReloadBroadcast

[<Tests>]
let devReloadLastOutcomeTests =
  testSequenced
  <| testList "DevReload.LastReload — past the server boundary" [

    // WHY — a client with no standing SSE connection must be able to poll
    // for the exact same verdict a live subscriber would have received.
    test "WHY — a poll after a save sees the same payload an SSE subscriber would have" {
      let outcome = ReloadOutcome.Patched(2, 3)
      Broadcast.broadcastOutcome outcome
      LastReload.json ()
      |> Expect.equal
           "the poll payload matches what the SSE frame would have carried"
           (DevReloadEvent.payloadJson (Broadcast.eventOf outcome))
    }

    // WHY — the whole point of the wire extension: a client that only reads
    // this poll must be able to render `describeForUser` verbatim, with the
    // remedy under the name SageFs's structured errors already use.
    test "WHY — the payload carries outcome/patched/considered/message/suggestedAction/reasons, not just a path" {
      let outcome = ReloadOutcome.NoEffect(448, [ RestartReason.StartupComputedValue "routes" ])
      Broadcast.broadcastOutcome outcome
      let json = System.Text.Json.JsonDocument.Parse(LastReload.json ()).RootElement
      json.GetProperty("outcome").GetString() |> Expect.equal "the case name is on the wire" "NoEffect"
      json.GetProperty("patched").GetInt32() |> Expect.equal "the numerator is on the wire" 0
      json.GetProperty("considered").GetInt32() |> Expect.equal "the denominator is on the wire" 448
      json.GetProperty("message").GetString()
      |> Expect.equal "rendered once, server-side, so every client words it identically" (Outcome.describeForUser outcome)
      json.GetProperty("suggestedAction").GetString()
      |> Expect.stringContains "under the name SageFs's structured errors already use" "Restart the app"
      let reasons = json.GetProperty("reasons")
      reasons.GetArrayLength() |> Expect.equal "the refusals travel too" 1
      reasons.[0].GetProperty("case").GetString()
      |> Expect.equal "each refusal carries a stable token" "StartupComputedValue"
    }

    // WHY — a poller must never observe a phase that is guaranteed to
    // resolve into a terminal one. Recording Compiling right after a
    // terminal event must not blank or overwrite it.
    test "WHY — Compiling is never published to a poller; the last terminal verdict survives it" {
      Broadcast.broadcastOutcome (ReloadOutcome.Restarted [ RestartReason.SignatureChanged "Program.handle" ])
      let beforeCompiling = LastReload.json ()
      broadcastCompiling (Some "Handlers.fs")
      LastReload.json ()
      |> Expect.equal "a poller must still see the last RESOLVED outcome, not a stuck compiling frame" beforeCompiling
      LastReload.json ()
      |> Expect.stringContains "and it must still be the terminal event, not a bare compiling frame" "\"type\":\"restarted\""
    }

    // WHY — each new terminal event replaces the last one; a poller always
    // sees the MOST RECENT save's verdict, never a merge of the two.
    test "WHY — a later terminal event replaces the earlier one for a poller" {
      Broadcast.broadcastOutcome (ReloadOutcome.NoEffect(4, []))
      LastReload.json () |> Expect.stringContains "sees the first verdict" "\"type\":\"noeffect\""
      Broadcast.broadcastOutcome (ReloadOutcome.Patched(1, 1))
      LastReload.json () |> Expect.stringContains "and then the newer one, not a merge of both" "\"type\":\"reload\""
    }

    // WHY — a save that reached nothing must poll the same as it pushes: a
    // client that only polls must never be told to refresh into
    // byte-identical code either.
    test "WHY — a poll never reads a refresh cue for a save that patched nothing" {
      Broadcast.broadcastOutcome (ReloadOutcome.NoEffect(5, []))
      LastReload.json ()
      |> Expect.stringContains "the wire type for a no-effect save is never the refresh cue" "\"type\":\"noeffect\""
    }

    // WHY — the refactor that split `sseData` into `payloadJson` + framing
    // must not change a single byte the wire already carries.
    testCase "WHY — sseData is always exactly \"data: \" + payloadJson + \"\\n\\n\"" <| fun _ ->
      let events =
        [ DevReloadEvent.Compiling None
          DevReloadEvent.Compiling(Some "A.fs")
          Broadcast.eventOf (ReloadOutcome.Patched(1, 2))
          Broadcast.eventOf (ReloadOutcome.Restarted [])
          Broadcast.eventOf (ReloadOutcome.NoEffect(3, []))
          Broadcast.eventOf (ReloadOutcome.CompileFailed "boom") ]
      for evt in events do
        DevReloadEvent.sseData evt
        |> Expect.equal
             (sprintf "%A must be exactly \"data: \" + payloadJson + \"\\n\\n\"" evt)
             ("data: " + DevReloadEvent.payloadJson evt + "\n\n")
  ]
