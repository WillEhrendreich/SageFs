namespace SageFs.Simulation

open SageFs
open SageFs.Features.LiveTesting
open SageFs.Simulation.FsiDiscoveryIdentitySim

/// Invariants over `FsiDiscoveryIdentitySim`. There is really only one, and
/// every live-testing surface in the product depends on it:
///
///   A test that was discovered compiled and then re-evaluated in the session is
///   ONE test, and the version that survives is the re-evaluated one.
///
/// Violating it does not throw. It shows the user two rows for one test —
/// one of which can never go green again — inflates every count derived from the
/// discovery set (the panel's `11✓`, the affected-test selection, the cohort
/// landing gate's "all tests pass"), and makes a re-eval'd edit look like it did
/// nothing.
module FsiDiscoveryIdentityInvariants =

  type Violation =
    { Index: int
      Why: string
      Detail: string }

  /// THE identity invariant. Ground truth is the model's own count of distinct
  /// LOGICAL tests, which is computed from the scenario's data and never from a
  /// `TestId`, a `FullName` or anything the decision under test produced.
  let oneEntryPerLogicalTest (observations: Observation list) : Violation list =
    observations
    |> List.indexed
    |> List.choose (fun (i, o) ->
      let expected = o.LogicalSoFar |> List.length
      match o.Merged.Length = expected with
      | true -> None
      | false ->
        Some
          { Index = i
            Why =
              "the merge produced a different number of tests than there are distinct tests — a compiled copy and its re-evaluated copy hashed to different TestIds, so merge appended instead of overriding and the panel shows one test twice"
            Detail =
              sprintf
                "expected %d logical test(s), merge produced %d: %s"
                expected
                o.Merged.Length
                (o.Merged |> List.map (fun c -> c.FullName) |> String.concat ", ") })

  /// Every surviving entry must carry the LOGICAL name — i.e. the name the
  /// compiled assembly uses. An entry still wearing FSI's submission wrapper is
  /// a name no compiled artifact, no source map and no user ever produces.
  ///
  /// Checked against the model's own `logicalName`, NOT against a "does it look
  /// like FSI" predicate — the predicate is part of the decision under test.
  let identityIsLogical (observations: Observation list) : Violation list =
    observations
    |> List.indexed
    |> List.collect (fun (i, o) ->
      let expected = o.LogicalSoFar |> List.map logicalName |> Set.ofList
      o.Merged
      |> List.filter (fun c -> not (expected.Contains c.FullName))
      |> List.map (fun c ->
        { Index = i
          Why =
            "a merged test carries a name that is not the logical (compiled) name of any test in the scenario — normalization left FSI's submission wrapper on it"
          Detail =
            sprintf "%s is not one of: %s" c.FullName (expected |> Set.toList |> String.concat ", ") }))

  /// The freshest version wins. A test that has been re-evaluated must survive
  /// the merge as its DYNAMIC copy, because the session's buffer is the truth
  /// about what the test is right now. Read off the model's category marker,
  /// which `TestDiscoveryMerge.merge` never keys on.
  let dynamicWins (observations: Observation list) : Violation list =
    observations
    |> List.indexed
    |> List.collect (fun (i, o) ->
      o.ReEvaluated
      |> List.choose (fun t ->
        let name = logicalName t
        match o.Merged |> List.tryFind (fun c -> c.FullName = name) with
        | None ->
          Some
            { Index = i
              Why = "a re-evaluated test is absent from the merged set entirely"
              Detail = name }
        | Some survivor when survivor.Category <> dynamicMarker ->
          Some
            { Index = i
              Why =
                "the COMPILED copy survived the merge for a test that was re-evaluated — the edited version lost, so the panel keeps showing the pre-edit test"
              Detail = sprintf "%s survived as %A" name survivor.Category }
        | Some _ -> None))

  let all (observations: Observation list) : Violation list =
    oneEntryPerLogicalTest observations
    @ identityIsLogical observations
    @ dynamicWins observations

  /// TWIN WITH TEETH — the normalizer that shipped, so the invariants above can
  /// be PROVEN to catch something rather than merely passing.
  ///
  /// Verbatim shape of the pre-fix
  /// `AttributeDiscovery.normalizeTypeFullName`: strip `FSI_<digits>` ONLY when
  /// it is followed by the nested-type separator, then flatten `+` to `.`.
  /// Against a `module`-declared file that is correct; against a
  /// `namespace`-declared file (`FSI_0042.Foo.Bar.Greeting`) it matches nothing
  /// and the wrapper survives into the `TestId`.
  ///
  /// Deliberately NOT wired into any product path. It exists only so a test can
  /// show the invariants failing under it and passing under the current rule.
  let legacyRegexNormalize (name: string) : string =
    match System.String.IsNullOrEmpty name with
    | true -> ""
    | false ->
      System.Text.RegularExpressions.Regex
        .Replace(name, @"^FSI_\d+\+", "")
        .Replace("+", ".")

  /// Build a `TestCase` the way the product does, but with `normalize` swapped
  /// in. The construction mirrors `AttributeDiscovery.testCaseOfReflectedName`
  /// and the ONLY reason it exists is the twin — `FsiDiscoveryIdentityTests`
  /// pins it against the real function (passing the real normalizer must
  /// reproduce the real function exactly), so it can never quietly become a
  /// second, divergent implementation.
  let buildWithNormalizer
    (normalize: string -> string)
    (category: TestCategory)
    (declaringTypeFullName: string)
    (methodName: string)
    : TestCase =
    let fullName = sprintf "%s.%s" (normalize declaringTypeFullName) methodName
    { Id = TestId.create fullName TestFramework.Expecto
      FullName = fullName
      DisplayName = methodName
      Origin = TestOrigin.ReflectionOnly
      Labels = []
      Framework = TestFramework.Expecto
      Category = category }

  /// The historical run: the identical trace, through the shipped normalizer.
  let runLegacyTwin (scenario: Scenario) : Observation list =
    runWith (buildWithNormalizer legacyRegexNormalize) scenario
