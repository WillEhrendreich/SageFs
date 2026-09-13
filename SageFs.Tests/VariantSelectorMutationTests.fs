/// ## VariantSelector Mutation Tests
///
/// Proves the test suite catches mutations in the pure, fail-closed
/// version-variant selection logic. A wrong version-prefix match here would
/// silently load an API-incompatible host library variant and crash at
/// runtime with MissingMethodException — exactly what this module exists to
/// prevent. Each case asserts EXACT equality against the correct value so a
/// mutant that returns any other wrong value is killed too.
module VariantSelectorMutationTests

open Expecto
open Expecto.Flip
open SageFs

let variantSelectorMutationTests = testList "VariantSelector mutations" [

  // ── selectVariantFromAssemblyIdentity: known libraries ──────────────────

  testCase "WHY — fantomas6_matches_major_version_prefix — Fantomas v6.x must select the Fantomas6 variant" <| fun () ->
    VariantSelector.selectVariantFromAssemblyIdentity "Fantomas" "6.0.5"
    |> Expect.equal "Fantomas 6.0.5 must select Variant \"Fantomas6\"" (Ok (VariantSelector.Variant "Fantomas6"))

  testCase "WHY — fantomas8_matches_major_version_prefix — Fantomas v8.x must select the Fantomas8 variant, not Fantomas6" <| fun () ->
    VariantSelector.selectVariantFromAssemblyIdentity "Fantomas" "8.1.0"
    |> Expect.equal "Fantomas 8.1.0 must select Variant \"Fantomas8\", never Fantomas6" (Ok (VariantSelector.Variant "Fantomas8"))

  testCase "WHY — fantomas7_has_no_variant_and_errors — an UNKNOWN major version must fail closed, never fall back to a nearby variant" <| fun () ->
    match VariantSelector.selectVariantFromAssemblyIdentity "Fantomas" "7.0.0" with
    | Error msg ->
      msg |> Expect.stringContains "the error must name the unsupported library and version" "Fantomas"
      msg |> Expect.stringContains "the error must name the unsupported library and version" "7"
    | Ok v -> failwithf "Fantomas 7.0.0 has no known variant and must Error, not select %A" v

  testCase "WHY — cecil_matches_two_component_version_prefix — Mono.Cecil uses a two-part prefix (0.11), not just the major" <| fun () ->
    VariantSelector.selectVariantFromAssemblyIdentity "Mono.Cecil" "0.11.4"
    |> Expect.equal "Mono.Cecil 0.11.4 must select Variant \"Cecil0.11\" via its two-component prefix match"
      (Ok (VariantSelector.Variant "Cecil0.11"))

  testCase "WHY — cecil_wrong_minor_has_no_variant — Mono.Cecil 0.12 must NOT match the 0.11 variant" <| fun () ->
    match VariantSelector.selectVariantFromAssemblyIdentity "Mono.Cecil" "0.12.0" with
    | Error _ -> ()
    | Ok v -> failwithf "Mono.Cecil 0.12.0 must not match the 0.11 variant, got %A" v

  testCase "WHY — harmony_matches_two_component_version_prefix — HarmonyLib 2.3.x must select Harmony2.3" <| fun () ->
    VariantSelector.selectVariantFromAssemblyIdentity "HarmonyLib" "2.3.1"
    |> Expect.equal "HarmonyLib 2.3.1 must select Variant \"Harmony2.3\"" (Ok (VariantSelector.Variant "Harmony2.3"))

  testCase "WHY — unknown_library_errors — a library with no known variants must fail closed" <| fun () ->
    match VariantSelector.selectVariantFromAssemblyIdentity "SomeOtherLib" "1.0.0" with
    | Error _ -> ()
    | Ok v -> failwithf "an unknown library must Error, not select %A" v

  // ── select: delegates to the injected lookup, unmodified ───────────────

  testCase "WHY — select_delegates_to_lookup_verbatim — select must pass args through unchanged and return the lookup's result as-is" <| fun () ->
    let fakeLookup : VariantSelector.VariantLookup =
      fun lib ver -> Ok (VariantSelector.Variant (sprintf "%s-%s" lib ver))
    VariantSelector.select fakeLookup "Foo" "9.9.9"
    |> Expect.equal "select must return exactly what the injected lookup produced for these exact args"
      (Ok (VariantSelector.Variant "Foo-9.9.9"))

  // ── selectForSolution: conflict detection ───────────────────────────────

  testCase "WHY — selectForSolution_no_conflict_selects_all — distinct libraries with single versions each must all resolve" <| fun () ->
    let lookup : VariantSelector.VariantLookup =
      fun lib ver -> Ok (VariantSelector.Variant (sprintf "%s%s" lib ver))
    VariantSelector.selectForSolution lookup [ ("A", "1"); ("B", "2") ]
    |> Expect.equal "two libraries with one version each must both resolve, in order"
      (Ok [ VariantSelector.Variant "A1"; VariantSelector.Variant "B2" ])

  testCase "WHY — selectForSolution_conflicting_versions_of_same_library_errors — two DIFFERENT pinned versions of one library must be a hard conflict" <| fun () ->
    let lookup : VariantSelector.VariantLookup =
      fun lib ver -> Ok (VariantSelector.Variant (sprintf "%s%s" lib ver))
    match VariantSelector.selectForSolution lookup [ ("A", "1"); ("A", "2") ] with
    | Error msg ->
      msg |> Expect.stringContains "the conflict error must name the library" "A"
      msg |> Expect.stringContains "the conflict error must name both versions" "1"
      msg |> Expect.stringContains "the conflict error must name both versions" "2"
    | Ok v -> failwithf "conflicting versions of the same library must Error, not select %A" v

  testCase "WHY — selectForSolution_same_version_twice_is_not_a_conflict — the SAME version pinned twice (e.g. by two projects) is fine" <| fun () ->
    let lookup : VariantSelector.VariantLookup =
      fun lib ver -> Ok (VariantSelector.Variant (sprintf "%s%s" lib ver))
    VariantSelector.selectForSolution lookup [ ("A", "1"); ("A", "1") ]
    |> Expect.equal "the same (library, version) pin repeated must resolve twice, not be treated as a conflict"
      (Ok [ VariantSelector.Variant "A1"; VariantSelector.Variant "A1" ])

  testCase "WHY — selectForSolution_propagates_lookup_error — a lookup failure for any single pin must fail the whole solution" <| fun () ->
    let lookup : VariantSelector.VariantLookup =
      fun lib ver -> match lib with "Bad" -> Error "no variant for Bad" | _ -> Ok (VariantSelector.Variant (sprintf "%s%s" lib ver))
    VariantSelector.selectForSolution lookup [ ("A", "1"); ("Bad", "1") ]
    |> Expect.equal "a single failing lookup must surface as the solution's Error, not be silently dropped"
      (Error "no variant for Bad")

  testCase "WHY — selectForSolution_empty_pins_selects_empty_list — no pins at all is a trivially successful empty selection" <| fun () ->
    let lookup : VariantSelector.VariantLookup = fun _ _ -> Error "should never be called"
    VariantSelector.selectForSolution lookup []
    |> Expect.equal "an empty pin list must resolve to Ok [] without calling the lookup" (Ok [])

  testCase "WHY — selectForSolution_preserves_pin_order — results must come back in the SAME order as the input pins, not reversed" <| fun () ->
    let lookup : VariantSelector.VariantLookup =
      fun lib ver -> Ok (VariantSelector.Variant (sprintf "%s%s" lib ver))
    VariantSelector.selectForSolution lookup [ ("A", "1"); ("B", "2"); ("C", "3") ]
    |> Expect.equal "three distinct libraries must resolve in exactly their input order A,B,C"
      (Ok [ VariantSelector.Variant "A1"; VariantSelector.Variant "B2"; VariantSelector.Variant "C3" ])
]
