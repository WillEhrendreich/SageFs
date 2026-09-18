module SageFs.Tests.DstSeedCorpusTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Simulation
open SageFs.Simulation.Scenario
open SageFs.Simulation.Runner
open SageFs.Simulation.Invariants
open SageFs.Simulation.SeedCorpus

/// Brief B4: revision-stamped scenario identity + a saved-seed regression
/// corpus. A `RevisionStamp` identifies a scenario by its CONTENT
/// (Policy + Events), independent of the `Seed` field that happened to
/// generate it — so a finding stays identifiable across re-derivation. The
/// curated `corpus` holds hand-worked, genuinely interesting scenarios that
/// run forever as example tests; `encode`/`tryParse` prove a saved scenario
/// replays to the byte-identical trace, satisfying the contract this brief
/// exists to guarantee.

[<Tests>]
let tests =
  testList "DST seed corpus (revision-stamped identity + regression corpus)" [

    testList "RevisionStamp — determinism + sensitivity" [

      testCase "same scenario stamps identically, twice" <| fun () ->
        for (name, scn) in corpus do
          let a = stamp scn
          let b = stamp scn
          a |> Expect.equal (sprintf "%s stamps deterministically" name) b

      testCase "stamp carries the current harness revision" <| fun () ->
        for (name, scn) in corpus do
          (stamp scn).Revision
          |> Expect.equal (sprintf "%s is stamped with harnessRevision" name) harnessRevision

      testCase "a scenario with one extra event gets a different digest" <| fun () ->
        for (name, scn) in corpus do
          let mutated = { scn with Events = scn.Events @ [ SimEvent.WorkerExitedGracefully ] }
          (stamp scn).Digest
          |> Expect.notEqual (sprintf "%s: appending an event changes the digest" name) (stamp mutated).Digest

      testCase "swapping one event's kind changes the digest" <| fun () ->
        let baseScn = Generators.crashStorm 5
        let mutated =
          { baseScn with
              Events =
                match baseScn.Events with
                | _ :: rest -> SimEvent.WorkerExitedGracefully :: rest
                | [] -> baseScn.Events }
        (stamp baseScn).Digest
        |> Expect.notEqual "swapping the first event's kind changes the digest" (stamp mutated).Digest

      testCase "a policy field change also changes the digest" <| fun () ->
        let baseScn = Generators.spacedCrashes 4 (TimeSpan.FromSeconds 20.0)
        let mutated = { baseScn with Policy = { baseScn.Policy with MaxRestarts = baseScn.Policy.MaxRestarts + 1 } }
        (stamp baseScn).Digest
        |> Expect.notEqual "a changed Policy field changes the digest" (stamp mutated).Digest

      testCase "digest ignores Seed — two scenarios differing only by Seed share a digest" <| fun () ->
        let a = Generators.crashStorm 5
        let b = { a with Seed = a.Seed + 12345 }
        (stamp a).Digest
        |> Expect.equal "Seed is not part of scenario identity" (stamp b).Digest
    ]

    testList "encode / tryParse — round-trip" [

      testCase "every corpus scenario round-trips exactly through encode/tryParse" <| fun () ->
        for (name, scn) in corpus do
          tryParse (encode scn)
          |> Expect.equal (sprintf "%s: tryParse (encode scn) = Some scn" name) (Some scn)

      testCase "a round-tripped scenario replays to the identical trace" <| fun () ->
        for (name, scn) in corpus do
          match tryParse (encode scn) with
          | None -> failtestf "%s: round-trip failed to parse" name
          | Some parsed ->
            (run parsed).Steps
            |> Expect.equal (sprintf "%s: parsed scenario replays to the identical trace" name) (run scn).Steps

      testCase "tryParse rejects garbage text" <| fun () ->
        tryParse "not a scenario at all"
        |> Expect.isNone "malformed text parses to None, not an exception"

      testCase "tryParse rejects a truncated encoding (missing EVENTS field)" <| fun () ->
        let scn = Generators.crashStorm 3
        let truncated =
          (encode scn).Split('\n')
          |> Array.filter (fun l -> not (l.StartsWith "EVENTS="))
          |> String.concat "\n"
        tryParse truncated
        |> Expect.isNone "a missing required field parses to None"

      testCase "encode is deterministic (same scenario, same text, twice)" <| fun () ->
        for (_, scn) in corpus do
          encode scn |> Expect.equal "encode is a pure function of the scenario" (encode scn)
    ]

    testList "corpus stays green" [

      testCase "every corpus scenario's invariants hold under the real core" <| fun () ->
        for (name, scn) in corpus do
          match violations (run scn) with
          | [] -> ()
          | vs ->
            failtestf
              "%s VIOLATES invariants — replay:\n  Seed=%d\n  Policy=%A\n  Events=%A\n  Violations=%A"
              name scn.Seed scn.Policy scn.Events vs

      testCase "corpus is non-empty and every name is unique" <| fun () ->
        corpus
        |> List.isEmpty
        |> Expect.isFalse "the curated corpus has at least one named scenario"
        corpus
        |> List.map fst
        |> List.distinct
        |> List.length
        |> Expect.equal "every corpus entry has a unique name" (List.length corpus)
    ]
  ]
