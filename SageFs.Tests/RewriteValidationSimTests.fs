module SageFs.Tests.RewriteValidationSimTests

/// WHY — translation validation gates a rewrite of real user code, so the
/// property is "never license an unsafe rewrite" folded over the full
/// cross-product of use-site shapes — plus its completeness twin, so a
/// validator that simply refuses everything cannot pass.

open Expecto
open Expecto.Flip
open SageFs.Simulation.RewriteValidationSim
open SageFs.Simulation.RewriteValidationInvariants

let private seeds = [ 1 .. 5000 ]

let private battery = lazy (seeds |> List.map (fun seed -> run (scenarioOf seed)))

let private report label (violations: Violation list) =
  if not (List.isEmpty violations) then
    let first = List.head violations
    failtestf "%s: seed %d — %s (replay: RewriteValidationSim.run (scenarioOf %d))" label first.Seed first.Why first.Seed

[<Tests>]
let tests =
  testList "Translation validation DST" [

    testCase "SAFETY — a rewrite with any unsafe use site is never licensed" <| fun _ ->
      battery.Value
      |> List.iter (fun trace -> trace |> neverLicensesAnUnsafeRewrite |> report "licensed an unsafe rewrite")

    testCase "COMPLETENESS — an all-plain-read rewrite IS licensed, so the gate is not inert" <| fun _ ->
      battery.Value
      |> List.iter (fun trace -> trace |> licensesASafeRewrite |> report "refused a safe rewrite")

    testCase "REPLAY — the same seed always produces the same decision" <| fun _ ->
      (run (scenarioOf 31337)).Licensed
      |> Expect.equal "a seeded scenario must be deterministic" (run (scenarioOf 31337)).Licensed

    testCase "NON-VACUOUS — the battery reaches both licensed and refused" <| fun _ ->
      let licensed = battery.Value |> List.filter (fun t -> t.Licensed)
      let refused = battery.Value |> List.filter (fun t -> not t.Licensed)

      (licensed.Length, 0)
      |> Expect.isGreaterThan "the battery must license something, or completeness is vacuous"

      (refused.Length, 0)
      |> Expect.isGreaterThan "the battery must refuse something, or safety is vacuous"

    testCase "NON-VACUOUS — the battery really reaches multi-site rewrites, not only the easy single-read case" <| fun _ ->
      let multi = battery.Value |> List.filter (fun t -> t.Scenario.SiteCount > 1)
      (multi.Length, 0)
      |> Expect.isGreaterThan "the cross-product must include multi-site rewrites"

    testCase "TWIN — a first-site-only validator that treats unknown as pass breaks the invariant" <| fun _ ->
      let breaking = seeds |> List.filter (fun seed -> firstSiteOnlyTwinBreaksTheRule (scenarioOf seed))
      (breaking.Length, 0)
      |> Expect.isGreaterThan "the twin must break safety on some seed"
  ]
