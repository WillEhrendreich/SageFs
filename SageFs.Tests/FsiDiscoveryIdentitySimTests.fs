module SageFs.Tests.FsiDiscoveryIdentitySimTests

/// DST over FSI's emit topology as LIVE TESTING sees it, modelled from the
/// COMPILER SOURCE (~/Work/fsharp-compiler-services @ cdb9dc5e5), against the
/// REAL `AttributeDiscovery.testCaseOfReflectedName` (hence the real
/// `TestId.create`) and the REAL `TestDiscoveryMerge.merge`.
///
/// This exists because the identity rule that decides whether a re-evaluated
/// test REPLACES its compiled twin or is APPENDED beside it had no test of any
/// kind. The only thing that could have noticed it was wrong is a real-daemon
/// suite whose observable is a test count — and a count that is 2 instead of 1
/// looks exactly like a count that is merely late.

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.Simulation
open SageFs.Simulation.FsiDiscoveryIdentitySim

/// A test in a `module`-declared file: reflection spells the re-eval'd copy
/// `FSI_0001+Acme+Suite+Tests`.
let private nestedTest =
  { Container = [ "Acme"; "Suite"; "Tests" ]
    Method = "addsUp"
    CompiledShape = CompiledShape.NestedInModule }

/// A test whose compiled copy is a top-level type in a namespace. This is the
/// shape a `namespace`-declared file produces, and the one the shipped regex
/// normalizer could not see.
let private namespacedTest =
  { Container = [ "Acme"; "Suite"; "Tests" ]
    Method = "addsUp"
    CompiledShape = CompiledShape.TopLevelInNamespace }

[<Tests>]
let fsiDiscoveryIdentitySimTests =
  testList "FSI discovery identity (DST)" [

    testCase "a module-declared file's re-eval REPLACES its compiled twin" <| fun _ ->
      // The spelling the shipped regex handled: `FSI_0001+Acme+Suite+Tests`.
      let scenario =
        { Tests = [ nestedTest ]
          Ops = [ Op.DiscoverCompiled; Op.ReEval(FileHeader.ModuleDeclared, [ nestedTest ]) ] }
      let observations = run scenario
      observations
      |> List.map (fun o -> o.Merged.Length)
      |> Expect.equal "one test compiled, one test after the re-eval — never two" [ 1; 1 ]
      observations
      |> FsiDiscoveryIdentityInvariants.all
      |> Expect.isEmpty "the re-eval'd copy must win, under the compiled name"

    // ── The one that pins the shipped bug ────────────────────────────────
    testCase "a NAMESPACE-declared file's re-eval also replaces its twin, instead of doubling the test" <| fun _ ->
      // `PrependPathToInput` keeps the `Namespace` kind for a `#load`ed input, so
      // `IlxGen.CompLocForSubModuleOrNamespace` routes the whole path — wrapper
      // included — into `Type.Namespace`. Reflection therefore reports
      // `FSI_0001.Acme.Suite.Tests`: DOTTED, not `+`.
      let scenario =
        { Tests = [ namespacedTest ]
          Ops = [ Op.DiscoverCompiled; Op.ReEval(FileHeader.NamespaceDeclared, [ namespacedTest ]) ] }
      let observations = run scenario

      // Ground truth, tracked by the model without consulting any TestId.
      observations
      |> List.map (fun o -> o.LogicalSoFar.Length)
      |> Expect.equal "there is exactly ONE logical test in this scenario throughout" [ 1; 1 ]

      // FIXED: normalization now uses the compiler's own rule over BOTH
      // separators, so the wrapper comes off the dotted spelling too and the two
      // copies hash to one TestId.
      observations
      |> FsiDiscoveryIdentityInvariants.all
      |> Expect.isEmpty
        "a namespace-declared file's re-eval must merge with its compiled twin, not append beside it"

      // TWIN WITH TEETH, kept so the invariants are provably not vacuous: the
      // shipped normalizer was anchored on `^FSI_\d+\+` and matched nothing
      // here. Run the IDENTICAL trace through it and the violations reappear.
      // If this ever comes back empty, the invariants have stopped proving
      // anything.
      let twinViolations =
        FsiDiscoveryIdentityInvariants.runLegacyTwin scenario
        |> FsiDiscoveryIdentityInvariants.all
      twinViolations
      |> Expect.isNonEmpty
        "TWIN: the `^FSI_\\d+\\+` normalizer must still be caught doubling the test on this exact trace"
      twinViolations
      |> List.map (fun v -> v.Why)
      |> List.exists (fun why -> why.Contains "shows one test twice")
      |> Expect.isTrue
        "and the twin's violation is specifically the double-count, not some incidental difference"

    testCase "the twin harness is not a divergent copy — with the REAL normalizer it reproduces the real decision exactly" <| fun _ ->
      // `buildWithNormalizer` exists only so the twin can swap the normalizer.
      // Pinning it against the product function is what stops it drifting into
      // a second implementation that could make the twin's failure meaningless.
      let scenario =
        { Tests = [ namespacedTest; nestedTest ]
          Ops =
            [ Op.DiscoverCompiled
              Op.ReEval(FileHeader.NamespaceDeclared, [ namespacedTest ])
              Op.ReEval(FileHeader.ModuleDeclared, [ nestedTest ]) ] }
      let real = run scenario
      let viaHarness =
        runWith
          (FsiDiscoveryIdentityInvariants.buildWithNormalizer
            SageFs.Features.LiveTesting.AttributeDiscovery.normalizeTypeFullName)
          scenario
      (real |> List.map (fun o -> o.Merged |> List.map (fun c -> c.FullName, TestId.value c.Id)))
      |> Expect.equal
        "the harness with the real normalizer must produce byte-identical identities to the product path"
        (viaHarness |> List.map (fun o -> o.Merged |> List.map (fun c -> c.FullName, TestId.value c.Id)))

    testCase "repeated saves of the same file never accumulate copies" <| fun _ ->
      // Under `--multiemit-` the single FSI assembly ACCUMULATES every eval
      // (fsi.fs:1818-1830), so submissions 1, 2 and 3 are all still live,
      // exported, discoverable types. Each wears a different wrapper
      // (`FSI_0001`/`FSI_0002`/`FSI_0003`), so if the wrapper ever survives
      // normalization the count grows once per save — and correct normalization
      // has to collapse all of them onto the compiled twin, not just the newest.
      let scenario =
        { Tests = [ namespacedTest ]
          Ops =
            [ Op.DiscoverCompiled
              Op.ReEval(FileHeader.NamespaceDeclared, [ namespacedTest ])
              Op.ReEval(FileHeader.NamespaceDeclared, [ namespacedTest ])
              Op.ReEval(FileHeader.NamespaceDeclared, [ namespacedTest ]) ] }
      let observations = run scenario
      observations
      |> List.map (fun o -> o.Merged.Length)
      |> Expect.equal "one test, however many times it is saved" [ 1; 1; 1; 1 ]
      observations
      |> FsiDiscoveryIdentityInvariants.all
      |> Expect.isEmpty "and every one of them reports under the compiled name"

      // The twin's damage is cumulative, which is what made it look like a
      // flaky count rather than a broken rule.
      FsiDiscoveryIdentityInvariants.runLegacyTwin scenario
      |> List.map (fun o -> o.Merged.Length)
      |> Expect.equal "TWIN: one extra phantom test per save" [ 1; 2; 3; 4 ]

    testCase "a file whose tests are re-eval'd alongside an untouched sibling leaves the sibling alone" <| fun _ ->
      let sibling =
        { Container = [ "Acme"; "Other"; "Tests" ]
          Method = "alsoAddsUp"
          CompiledShape = CompiledShape.TopLevelInNamespace }
      let scenario =
        { Tests = [ namespacedTest; sibling ]
          Ops = [ Op.DiscoverCompiled; Op.ReEval(FileHeader.NamespaceDeclared, [ namespacedTest ]) ] }
      let observations = run scenario
      observations
      |> List.map (fun o -> o.Merged.Length)
      |> Expect.equal "two tests before and after — the untouched sibling is neither dropped nor duplicated" [ 2; 2 ]
      observations
      |> FsiDiscoveryIdentityInvariants.all
      |> Expect.isEmpty "only the re-eval'd test switches to its dynamic copy"

    testCase "a test discovered ONLY in the session (never compiled) still reports under a compiled-shaped name" <| fun _ ->
      // A test written in the buffer and never saved: there is no compiled copy
      // to merge with, so the identity must stand on its own — and it must
      // still be the name the file WOULD compile to, or the moment the user
      // saves, the same test appears twice.
      let scenario =
        { Tests = [ namespacedTest ]
          Ops = [ Op.ReEval(FileHeader.NamespaceDeclared, [ namespacedTest ]) ] }
      let observations = run scenario
      observations
      |> List.map (fun o -> o.Merged |> List.map (fun c -> c.FullName))
      |> Expect.equal "the dynamic-only entry carries the compiled name" [ [ "Acme.Suite.Tests.addsUp" ] ]
      observations
      |> FsiDiscoveryIdentityInvariants.all
      |> Expect.isEmpty "and no invariant is violated by a dynamic-only discovery"
  ]
