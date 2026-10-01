/// A patch is reported live only after its new body has been seen running.
/// Until then it is PatchPending, and it becomes NeverEntered if the bound
/// passes without the new body running. These tests pin the pure decision, the
/// outcome cases it produces, and how they read on the wire.
module SageFs.Tests.PatchConfirmationTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Features
open SageFs.Features.ReloadOutcome
open SageFs.Features.ReloadPlanning
open SageFs.Features.PatchConfirmation
open SageFs.Middleware.EntryProbes

type private Outcome = SageFs.Features.ReloadOutcome.ReloadOutcome

let private kept : KeptValue = { Binding = "Counter.count"; KeptValue = "41"; NewInitializer = "0" }

let private decl (name: string) (probes: int64 list) : WatchedDecl = { Declaration = name; Probes = probes }

let private pendingOf (n: int) (m: int) : Outcome = Outcome.PatchPending(n, m, [])

/// Start a watch, failing the test when the outcome was not a pending patch.
let private watchOf (watched: WatchedDecl list) (outcome: Outcome) : PatchWatch =
  match PatchConfirmation.start watched outcome with
  | Begun.Watching(_, watch) -> watch
  | Begun.NothingToWatch o -> failtestf "expected a watch for %A, got NothingToWatch %A" outcome o

let private entered (probe: int64) = WatchEvent.Sighted(probe, ProbeStatus.Entered)
let private superseded (probe: int64) = WatchEvent.Sighted(probe, ProbeStatus.Superseded)

let private fold (watch: PatchWatch) (events: WatchEvent list) : WatchStep =
  events
  |> List.fold
    (fun step event ->
      match step with
      | WatchStep.StillWaiting w -> PatchConfirmation.step w event
      | settled -> settled)
    (WatchStep.StillWaiting watch)

[<Tests>]
let tests =
  testList "patch confirmation" [

    testList "starting a watch" [
      testCase "WHY — a pending patch is announced as pending, unchanged, and watched" <| fun _ ->
        let pending = pendingOf 2 3
        match PatchConfirmation.start [ decl "A.f" [ 1L ]; decl "A.g" [ 2L ] ] pending with
        | Begun.Watching(announced, watch) ->
          announced |> Expect.equal "what goes to the page is the pending outcome" pending
          PatchConfirmation.probesOf watch |> List.sort |> Expect.equal "both probes are watched" [ 1L; 2L ]
        | Begun.NothingToWatch o -> failtestf "a pending patch must be watched, got %A" o

      testCase "WHY — an outcome that is not a pending patch has nothing to confirm and passes through untouched" <| fun _ ->
        for o in [ Outcome.NoEffect(2, []); Outcome.RestartRequired []; Outcome.CompileFailed "x"; Outcome.Restarted []; Outcome.Patched(1, 1) ] do
          PatchConfirmation.start [ decl "A.f" [ 1L ] ] o |> Expect.equal (sprintf "%A" o) (Begun.NothingToWatch o)

      testCase "WHY — a pending patch with nothing to watch is not claimed, instead of staying pending forever" <| fun _ ->
        match PatchConfirmation.start [] (pendingOf 1 1) with
        | Begun.NothingToWatch(Outcome.NoEffect(1, [])) -> ()
        | other -> failtestf "a patch that cannot be observed must not be claimed, got %A" other
    ]

    testList "what the sightings decide" [
      testCase "WHY — every watched function seen running confirms the patch with the planner's own counts" <| fun _ ->
        let watch = watchOf [ decl "A.f" [ 1L ]; decl "A.g" [ 2L ] ] (pendingOf 2 3)
        match fold watch [ entered 1L; entered 2L ] with
        | WatchStep.Settled outcome -> outcome |> Expect.equal "confirmed" (Outcome.Patched(2, 3))
        | other -> failtestf "expected Settled, got %A" other

      testCase "WHY — one function seen running is not enough while another is still silent" <| fun _ ->
        let watch = watchOf [ decl "A.f" [ 1L ]; decl "A.g" [ 2L ] ] (pendingOf 2 2)
        match fold watch [ entered 1L ] with
        | WatchStep.StillWaiting _ -> ()
        | other -> failtestf "must keep waiting for A.g, got %A" other

      testCase "WHY — the bound passing with a silent function names it and counts the ones that ran" <| fun _ ->
        let watch = watchOf [ decl "A.f" [ 1L ]; decl "A.g" [ 2L ] ] (pendingOf 2 2)
        match fold watch [ entered 1L; WatchEvent.BoundElapsed ] with
        | WatchStep.Settled outcome -> outcome |> Expect.equal "A.g never ran" (Outcome.NeverEntered("A.g", [], 1, 2, []))
        | other -> failtestf "expected Settled, got %A" other

      testCase "WHY — the bound passing with nothing run reports every function as never entered" <| fun _ ->
        let watch = watchOf [ decl "A.f" [ 1L ]; decl "A.g" [ 2L ] ] (pendingOf 2 2)
        match fold watch [ WatchEvent.BoundElapsed ] with
        | WatchStep.Settled(Outcome.NeverEntered(first, rest, 0, 2, [])) ->
          first :: rest |> List.sort |> Expect.equal "both named" [ "A.f"; "A.g" ]
        | other -> failtestf "expected NeverEntered for both, got %A" other

      testCase "WHY — a function with several probes counts as running when any one of them ran" <| fun _ ->
        let watch = watchOf [ decl "A.Type" [ 1L; 2L; 3L ] ] (pendingOf 1 1)
        match fold watch [ entered 2L ] with
        | WatchStep.Settled outcome -> outcome |> Expect.equal "one member ran" (Outcome.Patched(1, 1))
        | other -> failtestf "expected Settled, got %A" other

      testCase "WHY — a function with no probe can never be confirmed, so the bound leaves it NeverEntered" <| fun _ ->
        let watch = watchOf [ decl "A.f" [] ] (pendingOf 1 1)
        match fold watch [ WatchEvent.BoundElapsed ] with
        | WatchStep.Settled outcome -> outcome |> Expect.equal "unobservable is not confirmed" (Outcome.NeverEntered("A.f", [], 0, 1, []))
        | other -> failtestf "expected Settled, got %A" other

      testCase "WHY — a sighting of a probe nobody watches changes nothing" <| fun _ ->
        let watch = watchOf [ decl "A.f" [ 1L ] ] (pendingOf 1 1)
        match fold watch [ entered 99L ] with
        | WatchStep.StillWaiting _ -> ()
        | other -> failtestf "a stranger's probe must not confirm A.f, got %A" other

      testCase "WHY — a NotEntered sighting is not a sighting" <| fun _ ->
        let watch = watchOf [ decl "A.f" [ 1L ] ] (pendingOf 1 1)
        match fold watch [ WatchEvent.Sighted(1L, ProbeStatus.NotEntered) ] with
        | WatchStep.StillWaiting _ -> ()
        | other -> failtestf "silence must not confirm anything, got %A" other

      testCase "WHY — a function a newer save replaced drops out of this watch instead of being blamed" <| fun _ ->
        let watch = watchOf [ decl "A.f" [ 1L ]; decl "A.g" [ 2L ] ] (pendingOf 2 2)
        match fold watch [ superseded 1L; entered 2L ] with
        | WatchStep.Settled outcome -> outcome |> Expect.equal "only A.g is this save's claim now" (Outcome.Patched(1, 2))
        | other -> failtestf "expected Settled, got %A" other

      testCase "WHY — when every watched function was replaced by newer saves there is nothing left to say" <| fun _ ->
        let watch = watchOf [ decl "A.f" [ 1L ] ] (pendingOf 1 1)
        fold watch [ superseded 1L ] |> Expect.equal "abandoned, silently" WatchStep.Abandoned

      testCase "WHY — kept live state rides through confirmation and through expiry" <| fun _ ->
        let pending = Outcome.PatchPending(1, 2, [ kept ])
        let watch = watchOf [ decl "A.f" [ 1L ] ] pending
        match fold watch [ entered 1L ] with
        | WatchStep.Settled outcome -> outcome |> Expect.equal "kept + patched" (Outcome.KeptLiveState(1, 2, kept, []))
        | other -> failtestf "expected Settled, got %A" other
        match fold watch [ WatchEvent.BoundElapsed ] with
        | WatchStep.Settled outcome -> outcome |> Expect.equal "kept + never entered" (Outcome.NeverEntered("A.f", [], 0, 2, [ kept ]))
        | other -> failtestf "expected Settled, got %A" other

      testCase "WHY — a settled watch is done: settling reads the host's whole answer and then the bound" <| fun _ ->
        let watch = watchOf [ decl "A.f" [ 1L ]; decl "A.g" [ 2L ] ] (pendingOf 2 2)
        let reading : EntryReading =
          { Sightings = [ { Probe = 1L; Status = ProbeStatus.Entered }; { Probe = 2L; Status = ProbeStatus.NotEntered } ] }
        match PatchConfirmation.settle reading watch with
        | WatchStep.Settled outcome -> outcome |> Expect.equal "A.g never ran" (Outcome.NeverEntered("A.g", [], 1, 2, []))
        | other -> failtestf "settle always ends the watch, got %A" other

      testProperty "WHY — Patched is never produced for a function no sighting showed running" <| fun (sightings: (int * bool) list) (bound: bool) ->
        let ids = [ 1L .. 4L ]
        let watch = watchOf (ids |> List.map (fun i -> decl (sprintf "A.f%d" i) [ i ])) (pendingOf 4 4)
        let events =
          (sightings |> List.map (fun (i, isEntered) -> WatchEvent.Sighted(int64 (((i % 6) + 6) % 6), (if isEntered then ProbeStatus.Entered else ProbeStatus.NotEntered))))
          @ (if bound then [ WatchEvent.BoundElapsed ] else [])
        let seen = events |> List.choose (function WatchEvent.Sighted(p, ProbeStatus.Entered) when List.contains p ids -> Some p | _ -> None) |> List.distinct
        match fold watch events with
        | WatchStep.Settled(Outcome.Patched(n, _)) -> n = 4 && List.length seen = 4
        | WatchStep.Settled(Outcome.NeverEntered(_, _, entered', _, _)) -> entered' = List.length seen
        | WatchStep.StillWaiting _ -> List.length seen < 4 && not bound
        | _ -> false
    ]

    testList "what a function is watched by" [
      testCase "WHY — a planner declaration is matched to the probes whose function it names" <| fun _ ->
        let f : SourceDecl =
          { Name = "render"; Kind = DeclKind.FunctionDecl; Access = DeclAccess.Public; Header = "let render x"; Text = "let render x = x"; Container = []; StartLine = 1; EndLine = 1 }
        let probes : EntryProbe list =
          [ { Id = 1L; Declaration = "App.Pages.render" }; { Id = 2L; Declaration = "App.Pages.renderOther" } ]
        PatchConfirmation.watchedOfLanded [ f ] probes
        |> Expect.equal "only the function's own probe" [ decl "render" [ 1L ] ]

      testCase "WHY — an interactive redirect is matched to the probe whose function it re-pointed" <| fun _ ->
        let probes : EntryProbe list = [ { Id = 7L; Declaration = "Demo.greet" } ]
        PatchConfirmation.watchedOfRedirected [ "FSI.Demo.greet" ] probes
        |> Expect.equal "suffix match, since the FSI wrapper is stripped on only one side" [ decl "FSI.Demo.greet" [ 7L ] ]
    ]

    testList "the outcomes" [
      testCase "WHY — the planner's patch count is pending, never Patched, because nothing has been seen running yet" <| fun _ ->
        ReloadOutcome.ofPatchCounts 2 5 [] |> Expect.equal "pending" (Outcome.PatchPending(2, 5, []))
        match ReloadOutcome.ofPatchCounts 0 5 [] with
        | Outcome.NoEffect(5, []) -> ()
        | other -> failtestf "a count of zero is still NoEffect, got %A" other

      testCase "WHY — pending and never-entered say 'unconfirmed, exercise it' and never claim why" <| fun _ ->
        for outcome in [ Outcome.PatchPending(1, 1, []); Outcome.NeverEntered("A.f", [], 0, 1, []) ] do
          let text = ReloadOutcome.describeForUser outcome
          text |> Expect.stringContains (sprintf "%A tells the user to exercise it" outcome) "xercise"
          text.ToLowerInvariant().Contains "inlin" |> Expect.isFalse (sprintf "%A must not guess at inlining" outcome)
          ReloadOutcome.remedy outcome |> Expect.isSome (sprintf "%A leaves something to do" outcome)

      testCase "WHY — the pending message is distinct from the confirmed one, so nobody reads 'applied' as 'live'" <| fun _ ->
        ReloadOutcome.describe (Outcome.PatchPending(1, 1, [])) |> Expect.notEqual "pending" (ReloadOutcome.describe (Outcome.Patched(1, 1)))

      testCase "WHY — NeverEntered names every function that has not run" <| fun _ ->
        ReloadOutcome.describe (Outcome.NeverEntered("A.f", [ "A.g" ], 1, 3, []))
        |> fun text ->
          text |> Expect.stringContains "first" "A.f"
          text |> Expect.stringContains "rest" "A.g"

      testCase "WHY — the browser refreshes on pending (the change may well be live) and on nothing else that patched" <| fun _ ->
        ReloadOutcome.shouldRefreshBrowser (Outcome.PatchPending(1, 1, [])) |> Expect.isTrue "the page must fetch the possibly-new code"
        ReloadOutcome.shouldRefreshBrowser (Outcome.Patched(1, 1)) |> Expect.isFalse "it already refreshed at pending"
        ReloadOutcome.shouldRefreshBrowser (Outcome.NeverEntered("A.f", [], 0, 1, [])) |> Expect.isFalse "nothing new to fetch"

      testCase "WHY — processChanged is true for pending (it may be live) and for a partial confirmation, false when nothing ran" <| fun _ ->
        ReloadOutcome.processChanged (Outcome.PatchPending(1, 1, [])) |> Expect.isTrue "the process was patched"
        ReloadOutcome.processChanged (Outcome.NeverEntered("A.g", [], 1, 2, [])) |> Expect.isTrue "one function ran its new body"
        ReloadOutcome.processChanged (Outcome.NeverEntered("A.g", [], 0, 2, [])) |> Expect.isFalse "no new body was seen running"

      testCase "WHY — kept values fold into a pending patch and into a never-entered one, counts included" <| fun _ ->
        Outcome.PatchPending(1, 1, []) |> ReloadOutcome.withKept [ kept ]
        |> Expect.equal "pending keeps" (Outcome.PatchPending(1, 2, [ kept ]))
        Outcome.NeverEntered("A.f", [], 0, 1, []) |> ReloadOutcome.withKept [ kept ]
        |> Expect.equal "never entered keeps" (Outcome.NeverEntered("A.f", [], 0, 2, [ kept ]))
    ]

    testList "on the wire" [
      testCase "WHY — pending refreshes the page, and the confirmation that follows does not refresh it a second time" <| fun _ ->
        let pending = ReloadBroadcast.eventOf (Outcome.PatchPending(1, 1, []))
        let confirmed = ReloadBroadcast.eventOf (Outcome.Patched(1, 1))
        let neverEntered = ReloadBroadcast.eventOf (Outcome.NeverEntered("A.f", [], 0, 1, []))
        DevReload.DevReloadEvent.refreshes pending |> Expect.isTrue "pending refreshes"
        DevReload.DevReloadEvent.refreshes confirmed |> Expect.isFalse "confirmed does not"
        DevReload.DevReloadEvent.refreshes neverEntered |> Expect.isFalse "never entered does not"

      testCase "WHY — each new case has its own wire type, so a client can branch without parsing prose" <| fun _ ->
        let typeOf outcome =
          use doc = System.Text.Json.JsonDocument.Parse(DevReload.DevReloadEvent.payloadJson (ReloadBroadcast.eventOf outcome))
          doc.RootElement.GetProperty("type").GetString()
        typeOf (Outcome.PatchPending(1, 1, [])) |> Expect.equal "pending" "pending"
        typeOf (Outcome.Patched(1, 1)) |> Expect.equal "confirmed" "patched"
        typeOf (Outcome.NeverEntered("A.f", [], 0, 1, [])) |> Expect.equal "never entered" "neverentered"

      testCase "WHY — the wire's 'patched' count is what has been seen running, so pending says 0 and never-entered says how many ran" <| fun _ ->
        (DevReload.DevReloadEvent.report (ReloadBroadcast.eventOf (Outcome.PatchPending(2, 3, [])))).Patched |> Expect.equal "pending" 0
        (DevReload.DevReloadEvent.report (ReloadBroadcast.eventOf (Outcome.NeverEntered("A.g", [], 1, 3, [])))).Patched |> Expect.equal "one ran" 1
        (DevReload.DevReloadEvent.report (ReloadBroadcast.eventOf (Outcome.PatchPending(2, 3, [])))).Considered |> Expect.equal "considered" 3

      testCase "WHY — the session status carries the new cases under their own tokens, so an agent reading lastReload can tell applied from seen running" <| fun _ ->
        for case, token in [ ReloadCase.PatchPending, "PatchPending"; ReloadCase.NeverEntered, "NeverEntered"; ReloadCase.Patched, "Patched" ] do
          let facts : ReloadFacts = { Case = case; Patched = 0; Considered = 2; Message = "m"; SuggestedAction = "a"; Mechanism = SageFs.Features.ReloadOutcome.PatchMechanism.NoPatch }
          let wire = SessionReload.toWire (SessionReload.Finished facts)
          System.Text.Json.JsonSerializer.Serialize wire
          |> Expect.stringContains (sprintf "%A is reported as %s" case token) (sprintf "\"outcome\":\"%s\"" token)

      testCase "WHY — the daemon reads both new cases back as the case the worker wrote" <| fun _ ->
        for expected, outcome in
          [ ReloadCase.PatchPending, Outcome.PatchPending(2, 3, [])
            ReloadCase.NeverEntered, Outcome.NeverEntered("A.g", [], 1, 3, []) ] do
          match SessionReload.ofPayloadJson (DevReload.DevReloadEvent.payloadJson (ReloadBroadcast.eventOf outcome)) with
          | Result.Ok(SessionReload.Finished facts) ->
            facts.Case |> Expect.equal (sprintf "%A" expected) expected
            facts.Message |> Expect.equal "the worker's wording" (ReloadOutcome.describeForUser outcome)
          | other -> failtestf "%A did not read back: %A" expected other
    ]
  ]
