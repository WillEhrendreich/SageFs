module SageFs.Tests.CohortLandingScopeTests

/// Which affected tests the landing gate asks the integration session to run (finding C-failing of the cohort run). Two landings of
/// pure additions were each Blocked by the same hundred `[Integration]` tests, which cannot pass inside a session: each ended
/// as `Skipped "Timed out after 5s"` and the verifier counts anything but a Passed as failing. The gate leaves them to the
/// release gate's integration tiers, names each one, and keeps reading every test it does run fail-closed.
open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features
open SageFs.Features.LiveTesting
open SageFs.Features.CohortLandingScope

let private names : Map<string, string> =
  Map.ofList
    [ "a1", "SageFs.Tests.Unit.adds"
      "b2", "[Integration] SageFs.Tests.Spawn.real process"
      "c3", "SageFs.Tests.Unit.subtracts"
      "d4", "SageFs.Tests.Suite/[Integration] daemon on a port/starts" ]

[<Tests>]
let scopeTests =
  testList "CohortLandingScope.scope: what the session runs and what is named as left out" [

    testCase "WHY — a test carrying the registry's tag is left to the release gate, and an ordinary test is run" <| fun _ ->
      let scoped = scope (fun id -> Map.tryFind id names) [ "a1"; "b2"; "c3" ]
      scoped.Run |> Expect.equal "the ordinary tests run, in order" [ "a1"; "c3" ]
      scoped.NotRun |> List.map (fun e -> e.Id) |> Expect.equal "the tagged one is left out" [ "b2" ]

    testCase "WHY — the tag counts anywhere along the name's list path, because a suite is tagged at its list" <| fun _ ->
      let scoped = scope (fun id -> Map.tryFind id names) [ "d4" ]
      scoped.Run |> Expect.equal "nothing to run" []
      scoped.NotRun |> List.map (fun e -> e.Name) |> Expect.equal "named in full" [ names.["d4"] ]

    testCase "WHY — an id the session cannot name is run, never skipped, so the gate fails closed on what it cannot read" <| fun _ ->
      let scoped = scope (fun id -> Map.tryFind id names) [ "zz"; "b2" ]
      scoped.Run |> Expect.equal "unknown runs" [ "zz" ]

    testCase "WHY — the tests left out are listed by name so the report is stable however they arrived" <| fun _ ->
      let a = scope (fun id -> Map.tryFind id names) [ "d4"; "b2" ]
      let b = scope (fun id -> Map.tryFind id names) [ "b2"; "d4" ]
      b.NotRun |> Expect.equal "same order" a.NotRun
      a.NotRun |> List.map (fun e -> e.Id) |> Expect.equal "by name" [ "b2"; "d4" ]

    testCase "WHY — an empty affected set stays empty on both sides" <| fun _ ->
      let scoped = scope (fun _ -> None) []
      scoped.Run |> Expect.equal "none to run" []
      scoped.NotRun |> Expect.equal "none left out" []

    testProperty "WHY — no test is lost: whatever the names, every id is either run or named as left out, once" <| fun (ids: string list) (tagged: bool list) ->
      let distinct = ids |> List.filter (fun id -> not (String.IsNullOrEmpty id)) |> List.distinct
      let nameOf id =
        let index = distinct |> List.findIndex ((=) id)
        match tagged |> List.tryItem index with
        | Some true -> Some ("[Integration] " + id)
        | Some false -> Some id
        | None -> None
      let scoped = scope nameOf distinct
      let accounted = scoped.Run @ (scoped.NotRun |> List.map (fun e -> e.Id))
      (List.sort accounted = List.sort distinct)
      && (scoped.NotRun |> List.forall (fun e -> isRegisteredIntegration e.Name))

    testCase "WHY — the tag is matched exactly, so a name that only mentions integration is still run" <| fun _ ->
      isRegisteredIntegration "[Integration] x" |> Expect.isTrue "tagged"
      isRegisteredIntegration "a suite [Integration] case" |> Expect.isTrue "tagged mid-name"
      isRegisteredIntegration "[integration] x" |> Expect.isFalse "case matters"
      isRegisteredIntegration "Integration tests of x" |> Expect.isFalse "a word is not the tag"
      isRegisteredIntegration "" |> Expect.isFalse "empty"
  ]

[<Tests>]
let couldNotRunTests =
  testList "CohortLandingScope.couldNotRun: a test that did not pass because it could not run" [

    testCase "WHY — a per-test timeout, Skipped 'Timed out after 5s', is could not run in the executor's words, lower-cased" <| fun _ ->
      couldNotRun (Some (TestResult.Skipped "Timed out after 5s")) |> Expect.equal "timed out" (Some "timed out after 5s")

    testCase "WHY — a pass has no reason, and neither does a test that failed its assertions or threw" <| fun _ ->
      couldNotRun (Some (TestResult.Passed TimeSpan.Zero)) |> Expect.isNone "passed"
      couldNotRun (Some (TestResult.Failed (TestFailure.AssertionFailed "expected 1", TimeSpan.Zero))) |> Expect.isNone "assertion"
      couldNotRun (Some (TestResult.Failed (TestFailure.ExceptionThrown ("boom", "at x"), TimeSpan.Zero))) |> Expect.isNone "exception"

    testCase "WHY — a test that timed out as a failure, never ran, or never reported could not run, each with its reason" <| fun _ ->
      couldNotRun (Some (TestResult.Failed (TestFailure.TimedOut (TimeSpan.FromSeconds 5.0), TimeSpan.Zero)))
      |> Expect.equal "timed out failure" (Some "timed out after 5s")
      couldNotRun (Some TestResult.NotRun) |> Expect.equal "not run" (Some "it was not run")
      couldNotRun (Some (TestResult.NoResult NoResultReason.StreamEnded))
      |> Expect.equal "no result" (Some (NoResultReason.describe NoResultReason.StreamEnded))
      couldNotRun None |> Expect.equal "nothing recorded" (Some "no result was reported for it")

    testCase "WHY — a skip for any other reason is also could not run, so the reason text is the executor's own" <| fun _ ->
      couldNotRun (Some (TestResult.Skipped "Cancelled")) |> Expect.equal "cancelled" (Some "cancelled")
  ]
