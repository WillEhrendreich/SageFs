module LemScore.Tests.CatalogTests

open System
open System.IO
open Expecto
open Expecto.Flip
open LemScore
open LemScore.Types
open LemScore.Tests.Samples

let private catalog () = Catalog.parse (readSample "list-models.txt")

/// The free models in the live catalog the samples were captured from (2026-10-01).
let private expectedFree =
  [ "poolside/laguna-s-2.1-free"
    "inclusionai/ling-3.0-flash-sante:free"
    "inclusionai/ling-3.1-flash:free"
    "stealth/space-bunny-alpha" ]

[<Tests>]
let catalogTests =
  testList "Catalog" [
    testCase "the real catalog has its free models, and only those" <| fun _ ->
      Catalog.freeModels (catalog ())
      |> Expect.equal "free list" expectedFree

    testCase "every free model is accepted" <| fun _ ->
      for model in expectedFree do
        Catalog.checkFree (catalog ()) model
        |> Result.isOk
        |> Expect.isTrue (sprintf "%s should be accepted" model)

    testCase "a paid model is refused with the catalog's own description" <| fun _ ->
      match Catalog.checkFree (catalog ()) "claude-sonnet-5-5" with
      | Error why -> why |> Expect.stringContains "says why" "not marked FREE"
      | Ok _ -> failtest "a paid model was accepted"

    testCase "a model that is not in the catalog is refused and the free ones are named" <| fun _ ->
      match Catalog.checkFree (catalog ()) "stealth/pixel-canary" with
      | Error why ->
        why |> Expect.stringContains "names the miss" "stealth/pixel-canary"
        why |> Expect.stringContains "names the free ones" "stealth/space-bunny-alpha"
      | Ok _ -> failtest "an unlisted model was accepted"

    testCase "a blurb that merely says 'free' does not make a model free" <| fun _ ->
      Catalog.checkFree (catalog ()) "typesafe/jev"
      |> Result.isError
      |> Expect.isTrue "typesafe/jev says 'everything else free' but is not marked FREE"

    testCase "an empty catalog confirms nothing" <| fun _ ->
      Catalog.checkFree [] "stealth/space-bunny-alpha"
      |> Result.isError
      |> Expect.isTrue "no catalog, no free model"

    testCase "section headings and the banner are not models" <| fun _ ->
      let ids = catalog () |> List.map _.Id
      for heading in [ "Open"; "Stealth"; "Anthropic"; "Available" ] do
        ids |> List.contains heading |> Expect.isFalse (sprintf "%s is a heading" heading)

    testCase "short names drop the provider and the free suffixes" <| fun _ ->
      Catalog.shortName "stealth/space-bunny-alpha" |> Expect.equal "space-bunny" "space-bunny"
      Catalog.shortName "inclusionai/ling-3.1-flash:free" |> Expect.equal "ling" "ling-3.1-flash"
      Catalog.shortName "poolside/laguna-s-2.1-free" |> Expect.equal "laguna" "laguna-s-2.1"
      Catalog.shortName "inclusionai/ling-3.0-flash-sante:free" |> Expect.equal "sante" "ling-3.0-flash-sante"

    testCase "the next run id is the first unused number" <| fun _ ->
      let root = Path.Combine(Path.GetTempPath(), "lemscore-test-" + Guid.NewGuid().ToString "N")
      try
        Directory.CreateDirectory(Path.Combine(root, "space-bunny-parse-seed-01")) |> ignore
        Directory.CreateDirectory(Path.Combine(root, "space-bunny-parse-seed-02")) |> ignore
        Catalog.nextRunId root "stealth/space-bunny-alpha" "parse-seed"
        |> Expect.equal "third" "space-bunny-parse-seed-03"
        Catalog.nextRunId root "stealth/space-bunny-alpha" "smoke"
        |> Expect.equal "other task starts at one" "space-bunny-smoke-01"
      finally
        if Directory.Exists root then Directory.Delete(root, true)
  ]

[<Tests>]
let closedSetTests =
  testList "Closed sets" [
    testCase "the outcome set is exactly the eight the harness promises" <| fun _ ->
      Outcome.all
      |> List.map Outcome.toString
      |> Expect.equal "outcomes"
           [ "Pass"; "PassWithRecovery"; "Fail"; "Blocked"; "Incomplete"; "ProviderQuota"; "MaxTurns"; "HarnessError" ]

    testCase "every outcome round-trips through its string" <| fun _ ->
      for o in Outcome.all do
        Outcome.tryParse (Outcome.toString o) |> Expect.equal "round trip" (Ok o)

    testCase "a string outside the set is refused with the set named" <| fun _ ->
      match Outcome.tryParse "Mostly" with
      | Error why -> why |> Expect.stringContains "names the set" "PassWithRecovery"
      | Ok _ -> failtest "an unknown outcome parsed"

    testCase "stages and harnesses round-trip" <| fun _ ->
      Stage.tryParse "SessionWarmup" |> Expect.equal "stage" (Ok SessionWarmup)
      Stage.tryParse "Nonsense" |> Result.isError |> Expect.isTrue "unknown stage refused"
      Harness.tryParse "cmdc-nvim" |> Expect.equal "harness" (Ok CmdcNvim)
      Harness.tryParse "claude" |> Result.isError |> Expect.isTrue "claude is not a lemming harness"
  ]
