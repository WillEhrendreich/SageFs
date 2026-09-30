/// What a save tells every client, and when: pending at once, then confirmed
/// or never-entered once the host has answered. The wait for the host is a
/// function, so these tests answer it by hand.
///
/// `LastReload` is process-global, so the whole list runs sequenced.
module SageFs.Tests.PatchAnnouncerTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.ReloadOutcome
open SageFs.Features.PatchConfirmation
open SageFs.Features.PatchAnnouncer
open SageFs.Middleware.EntryProbes

type private Outcome = SageFs.Features.ReloadOutcome.ReloadOutcome

let private lastType () =
  use doc = System.Text.Json.JsonDocument.Parse(DevReload.LastReload.json ())
  doc.RootElement.GetProperty("type").GetString()

let private lastOutcome () =
  use doc = System.Text.Json.JsonDocument.Parse(DevReload.LastReload.json ())
  doc.RootElement.GetProperty("outcome").GetString()

let private begunFor (names: (string * int64 list) list) : Begun =
  let watched = names |> List.map (fun (name, probes) -> { Declaration = name; Probes = probes })
  PatchConfirmation.start watched (Outcome.PatchPending(List.length names, List.length names, []))

let private reading (statuses: (int64 * ProbeStatus) list) : EntryAnswer =
  EntryAnswer.HostSaw { Sightings = statuses |> List.map (fun (probe, status) -> { Probe = probe; Status = status }) }

let private bound = TimeSpan.FromSeconds 10.0

[<Tests>]
let tests =
  testSequenced
  <| testList "PatchAnnouncer" [

    testTask "WHY — the page hears 'pending' the moment the save is applied, before anything has been waited for" {
      DevReload.broadcastCompiling None
      let asked = ref false
      let waiter : EntryWaiter = fun _ _ -> async { asked.Value <- true; return reading [] }
      let rest = PatchAnnouncer.announce waiter bound (begunFor [ "A.f", [ 1L ] ])
      lastType () |> Expect.equal "pending is on the wire already" "pending"
      asked.Value |> Expect.isFalse "nothing has waited yet: the caller decides when the rest runs"
      ignore rest
    }

    testTask "WHY — the host's answer that every function ran confirms the patch" {
      let waiter : EntryWaiter = fun _ _ -> async { return reading [ 1L, ProbeStatus.Entered; 2L, ProbeStatus.Entered ] }
      do! PatchAnnouncer.announce waiter bound (begunFor [ "A.f", [ 1L ]; "A.g", [ 2L ] ]) |> Async.StartAsTask
      lastType () |> Expect.equal "confirmed" "patched"
      lastOutcome () |> Expect.equal "the confirmed case" "Patched"
    }

    testTask "WHY — the host's answer that a function never ran ends in never-entered, naming it" {
      let waiter : EntryWaiter = fun _ _ -> async { return reading [ 1L, ProbeStatus.Entered; 2L, ProbeStatus.NotEntered ] }
      do! PatchAnnouncer.announce waiter bound (begunFor [ "A.f", [ 1L ]; "A.g", [ 2L ] ]) |> Async.StartAsTask
      lastType () |> Expect.equal "never entered" "neverentered"
      DevReload.LastReload.json () |> Expect.stringContains "names the silent function" "A.g"
    }

    testTask "WHY — the wait is asked for exactly the watched probes and the bound it was given" {
      let seenProbes = ref ([]: int64 list)
      let seenBound = ref TimeSpan.Zero
      let waiter : EntryWaiter =
        fun probes b -> async { seenProbes.Value <- probes; seenBound.Value <- b; return reading [] }
      do! PatchAnnouncer.announce waiter (TimeSpan.FromSeconds 7.0) (begunFor [ "A.f", [ 1L; 2L ]; "A.g", [ 3L ] ]) |> Async.StartAsTask
      seenProbes.Value |> List.sort |> Expect.equal "every watched probe" [ 1L; 2L; 3L ]
      seenBound.Value |> Expect.equal "the bound passes through" (TimeSpan.FromSeconds 7.0)
    }

    testTask "WHY — a host that cannot be asked means nothing was observed, so the patch is never-entered, not confirmed" {
      let waiter : EntryWaiter = fun _ _ -> async { return EntryAnswer.HostUnreachable "the host went away" }
      do! PatchAnnouncer.announce waiter bound (begunFor [ "A.f", [ 1L ] ]) |> Async.StartAsTask
      lastType () |> Expect.equal "fail closed" "neverentered"
    }

    testTask "WHY — a function every newer save replaced leaves the newer save's own verdict standing" {
      let waiter : EntryWaiter = fun _ _ -> async { return reading [ 1L, ProbeStatus.Superseded ] }
      do! PatchAnnouncer.announce waiter bound (begunFor [ "A.f", [ 1L ] ]) |> Async.StartAsTask
      lastType () |> Expect.equal "this watch said nothing after pending" "pending"
    }

    testTask "WHY — an outcome that is not a pending patch is broadcast as it is, and nobody is asked to wait" {
      let asked = ref false
      let waiter : EntryWaiter = fun _ _ -> async { asked.Value <- true; return reading [] }
      do! PatchAnnouncer.announce waiter bound (PatchConfirmation.start [] (Outcome.NoEffect(3, []))) |> Async.StartAsTask
      lastType () |> Expect.equal "no effect" "noeffect"
      asked.Value |> Expect.isFalse "there was nothing to confirm"
    }
  ]
