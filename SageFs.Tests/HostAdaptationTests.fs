module SageFs.Tests.HostAdaptationTests

open Expecto
open Expecto.Flip
open SageFs

/// GREEN tests for the automatic version-adaptation plan.
///
/// The guarantee: no code path where project code silently runs against the
/// wrong version of a host library — it loads the project's version, swaps in
/// a variant built for it, or refuses before any eval.
[<Tests>]
let tests =
  testList "HostAdaptation" [

    testCase "API-compatible libs load the project's pinned version" <| fun _ ->
      let plan =
        HostAdaptation.plan [ ("FSharp.Core", "10.0.0"); ("FSharp.SystemTextJson", "1.2.0") ]

      plan |> Expect.contains "FSharp.Core pin loads the project's version" (HostAdaptation.LoadProjectVersion("FSharp.Core", "10.0.0"))
      plan |> Expect.contains "SystemTextJson pin loads the project's version" (HostAdaptation.LoadProjectVersion("FSharp.SystemTextJson", "1.2.0"))

    testCase "API-coupled libs select the version-matched variant" <| fun _ ->
      let plan = HostAdaptation.plan [ ("Fantomas", "6.0.0") ]

      plan |> Expect.contains "Fantomas 6 selects the Fantomas6 variant" (HostAdaptation.LoadVariant("Fantomas", "6.0.0", "Fantomas6"))

    testCase "unsupported API-coupled pin refuses pre-eval" <| fun _ ->
      let plan = HostAdaptation.plan [ ("Fantomas", "99.0.0") ]

      (HostAdaptation.hasRefusal plan) |> Expect.isTrue "unsupported pin must refuse"
      (HostAdaptation.refusalReasons plan) |> Expect.isNonEmpty "refusal must carry an actionable reason"

    testCase "project-only libs are no conflict" <| fun _ ->
      let plan = HostAdaptation.plan [ ("Marten", "7.0.0"); ("Falco", "6.0.0") ]

      (HostAdaptation.hasRefusal plan) |> Expect.isFalse "project-only libs never refuse"
      (plan |> List.forall (function HostAdaptation.NoConflict _ -> true | _ -> false)) |> Expect.isTrue "all decisions are NoConflict"

    testCase "requiredVariants collects the variant assemblies" <| fun _ ->
      let plan = HostAdaptation.plan [ ("Fantomas", "8.0.0"); ("Mono.Cecil", "0.11.4") ]

      let variants = HostAdaptation.requiredVariants plan

      variants |> Expect.contains "collects the Fantomas8 variant" "Fantomas8"
      variants |> Expect.contains "collects the Cecil variant" "Cecil0.11"
  ]
