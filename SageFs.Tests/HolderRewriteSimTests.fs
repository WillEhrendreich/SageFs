module SageFs.Tests.HolderRewriteSimTests

/// WHY — the classifier gates a source rewrite, so the property is "never
/// rewrite an unsafe binding" folded over the FULL cross-product of
/// conditions, plus its completeness twin so a vacuous pass is impossible.

open Expecto
open Expecto.Flip
open SageFs.Simulation.HolderRewriteSim
open SageFs.Simulation.HolderRewriteInvariants

let private seeds = [ 1 .. 4000 ]

let private battery = lazy (seeds |> List.map (fun seed -> run (scenarioOf seed)))

let private report label (violations: Violation list) =
  if not (List.isEmpty violations) then
    let first = List.head violations
    failtestf "%s: seed %d — %s (replay: HolderRewriteSim.run (scenarioOf %d))" label first.Seed first.Why first.Seed

[<Tests>]
let tests =
  testList "Holder-rewrite side conditions DST" [

    testCase "SAFETY — no unsafe binding is ever rewritten" <| fun _ ->
      battery.Value
      |> List.iter (fun trace -> trace |> neverRewritesAnUnsafeBinding |> report "rewrote an unsafe binding")

    testCase "COMPLETENESS — a fully safe binding IS rewritten, so the feature is not inert" <| fun _ ->
      battery.Value
      |> List.iter (fun trace -> trace |> safeBindingsAreRewritten |> report "refused a safe binding")

    testCase "REPLAY — the same seed always produces the same decision" <| fun _ ->
      (run (scenarioOf 4242)).Reasons
      |> Expect.equal "a seeded scenario must be deterministic" (run (scenarioOf 4242)).Reasons

    testCase "NON-VACUOUS — the battery reaches both rewritten and refused, safe and unsafe" <| fun _ ->
      let rewritten = battery.Value |> List.filter (fun t -> t.Rewritten)
      let refused = battery.Value |> List.filter (fun t -> not t.Rewritten)
      let safeRewritten = battery.Value |> List.filter (fun t -> t.Rewritten && not t.Unsafe)

      (refused.Length, 0)
      |> Expect.isGreaterThan "the battery must refuse, or the safety invariant is vacuous"

      (safeRewritten.Length, 0)
      |> Expect.isGreaterThan "the battery must safely rewrite, or the completeness invariant is vacuous"

      (rewritten.Length, 0)
      |> Expect.isGreaterThan "the battery must rewrite something at all"

    testCase "TWIN — a partial check rewrites an unsafe binding, so the safety invariant has teeth" <| fun _ ->
      let breaking = seeds |> List.filter (fun seed -> partialCheckBreaksTheRule (scenarioOf seed))

      (breaking.Length, 0)
      |> Expect.isGreaterThan "the partial-check twin must break the invariant on some seed"
  ]
