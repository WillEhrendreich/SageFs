module SageFs.Tests.VariantSelectorTests

open Expecto

/// Phase 0 RED: prove there is no version-variant selection today.
///
/// The plan requires: for API-coupled host libraries (Fantomas, Cecil,
/// Harmony), the supervisor selects a version-matched VARIANT assembly from
/// the project's pinned versions. Today: no `VariantSelector` module exists —
/// a project pinning Fantomas 6 while the host ships Fantomas 8 fails at load
/// with 0x80131040 (the collision this whole effort removes).
///
/// These tests reference the planned public API and are compile-RED until
/// Phase 1 creates `SageFs.Core.VariantSelector`.

type LibraryPin = {
  Library: string
  Version: string
}

[<Tests>]
let tests =
  testList "VariantSelector (RED)" [

    testCase "maps a project's pinned Fantomas version to a variant" <| fun _ ->
      // A project whose bin contains Fantomas 6.0.0 must select the
      // Fantomas6 variant, not fail with a load error.
      let pin = { Library = "Fantomas"; Version = "6.0.0" }

      let selected =
        VariantSelector.select
          VariantSelector.selectVariantFromAssemblyIdentity
          pin.Library
          pin.Version

      match selected with
      | Ok (VariantSelector.Variant "Fantomas6") -> ()
      | Ok v -> failtestf "expected Fantomas6 variant, got %A" v
      | Error err -> failtestf "expected selection, got error: %s" err

    testCase "unknown pinned version returns an actionable error" <| fun _ ->
      let pin = { Library = "Fantomas"; Version = "99.0.0" }

      let selected =
        VariantSelector.select
          VariantSelector.selectVariantFromAssemblyIdentity
          pin.Library
          pin.Version

      match selected with
      | Error msg ->
        Expect.stringContains
          msg
          "no variant"
          "error should explain no variant exists"
      | Ok _ -> failtest "unknown version must not silently select a variant"

    testCase "conflicting pins across projects refuse pre-eval" <| fun _ ->
      // Two projects in one solution pinning different Fantomas versions is
      // a genuine conflict → refuse with a message naming both.
      let pins =
        [ { Library = "Fantomas"; Version = "6.0.0" }
          { Library = "Fantomas"; Version = "8.0.0" } ]

      let selected =
        VariantSelector.selectForSolution
          VariantSelector.selectVariantFromAssemblyIdentity
          pins

      match selected with
      | Error msg ->
        Expect.stringContains
          msg
          "conflict"
          "conflicting pins should name the conflict"
      | Ok _ -> failtest "conflicting pins must refuse"
  ]
