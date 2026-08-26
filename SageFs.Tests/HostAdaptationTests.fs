module SageFs.Tests.HostAdaptationTests

open Expecto
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

      Expect.contains
        plan
        (HostAdaptation.LoadProjectVersion("FSharp.Core", "10.0.0"))
        "FSharp.Core pin loads the project's version"
      Expect.contains
        plan
        (HostAdaptation.LoadProjectVersion("FSharp.SystemTextJson", "1.2.0"))
        "SystemTextJson pin loads the project's version"

    testCase "API-coupled libs select the version-matched variant" <| fun _ ->
      let plan = HostAdaptation.plan [ ("Fantomas", "6.0.0") ]

      Expect.contains
        plan
        (HostAdaptation.LoadVariant("Fantomas", "6.0.0", "Fantomas6"))
        "Fantomas 6 selects the Fantomas6 variant"

    testCase "unsupported API-coupled pin refuses pre-eval" <| fun _ ->
      let plan = HostAdaptation.plan [ ("Fantomas", "99.0.0") ]

      Expect.isTrue
        (HostAdaptation.hasRefusal plan)
        "unsupported pin must refuse"
      Expect.isNonEmpty
        (HostAdaptation.refusalReasons plan)
        "refusal must carry an actionable reason"

    testCase "project-only libs are no conflict" <| fun _ ->
      let plan = HostAdaptation.plan [ ("Marten", "7.0.0"); ("Falco", "6.0.0") ]

      Expect.isFalse
        (HostAdaptation.hasRefusal plan)
        "project-only libs never refuse"
      Expect.isTrue
        (plan |> List.forall (function HostAdaptation.NoConflict _ -> true | _ -> false))
        "all decisions are NoConflict"

    testCase "requiredVariants collects the variant assemblies" <| fun _ ->
      let plan = HostAdaptation.plan [ ("Fantomas", "8.0.0"); ("Mono.Cecil", "0.11.4") ]

      let variants = HostAdaptation.requiredVariants plan

      Expect.contains variants "Fantomas8" "collects the Fantomas8 variant"
      Expect.contains variants "Cecil0.11" "collects the Cecil variant"
  ]
