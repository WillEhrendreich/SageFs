namespace SageFs.Simulation

open SageFs
open SageFs.Features.LiveTesting
open SageFs.Features.LiveTesting.AttributeDiscovery

/// Deterministic Simulation Testing for the ONE claim live testing makes about a
/// test it has seen twice: "the test you just re-evaluated in the session and the
/// test compiled into your build output are the SAME test."
///
/// WHY THIS EXISTS. That claim is carried entirely by a string: a discovered
/// test's `TestId` is a hash of its normalized `DeclaringType.FullName` plus its
/// method name, and `TestDiscoveryMerge.merge` overrides a compiled entry with a
/// dynamic one only when the two `TestId`s are EQUAL. So one wrong character in
/// the normalizer does not throw, does not log, and does not fail a build — it
/// silently turns "the edited test replaces its compiled twin" into "the panel
/// shows the test twice, one of them permanently stale". The only thing watching
/// that was a real-daemon suite whose observable is a test COUNT
/// (`HttpApiIntegrationTests.fs:1060`), and a count of 2-where-1-was-expected is
/// exactly as loud as a count that is simply late.
///
/// THE MODEL IS TAKEN FROM THE COMPILER SOURCE, not inferred from observed
/// strings (`~/Work/fsharp-compiler-services`, commit cdb9dc5e5):
///
///   * `fsi.fs:2587-2600` — a submission is wrapped in a
///     `SynModuleOrNamespaceKind.NamedModule` named `FSI_NNNN`
///     (`mkFragmentPath`, `FsiDynamicModulePrefix` + `%04d`).
///   * `IlxGen.fs:395-407` (`CompLocForSubModuleOrNamespace`) — a MODULE's name
///     goes into `Enclosing`, a NAMESPACE's into `Namespace`. Because the
///     submission wrapper is a NamedModule, `FSI_NNNN` lands in `Enclosing`, and
///     `IlxGen.fs:437-441` (`NestedTypeRefForCompLoc`) then emits everything
///     under it as NESTED IL types. Reflection spells that with `+`:
///     `FSI_0004+A+B+Tests`.
///   * `ParseAndCheckInputs.fs:92-100` (`PrependPathToInput`) — a `#load`ed input
///     that declares a `namespace` keeps its `Namespace` kind, so the prepended
///     `FSI_NNNN` goes through the `Namespace` branch above and the whole path
///     lands in `Type.Namespace`. Reflection spells THAT with dots:
///     `FSI_0004.A.B.Tests`.
///   * `CheckDeclarations.fs:272-281` (`TryStripPrefixPath`) — the compiler's own
///     rule for undoing it, which `SageFs.FsiNaming` mirrors.
///
/// THE CONSEQUENCE THAT BIT, and the reason this sim exists: the normalizer that
/// shipped was `Regex.Replace(name, "^FSI_\d+\+", "")` — anchored on the NESTED
/// separator. It handled the first spelling and silently did nothing to the
/// second, so every test in a `namespace`-declared file kept its `FSI_0042.`
/// prefix through normalization, hashed to a different `TestId` than its compiled
/// twin, and `merge` appended it as a dynamic-only entry instead of overriding.
///
/// Same design rules as `FsiEmitSim`/`FileReloadRoutingSim`/`CohortLandingSim`:
///   * Chaos is DATA — a `Scenario` is an ordered op list over a tiny test pool.
///     Same ops, same trace, forever. No clock, no IO, no reflection, no sleeps.
///   * The REAL decisions are folded — `AttributeDiscovery.testCaseOfReflectedName`
///     (which is `toTestCase` minus the `MethodInfo`), `TestId.create` inside it,
///     and `TestDiscoveryMerge.merge`. Nothing here reimplements them.
///   * Ground truth comes from an INDEPENDENT bookkeeping fold that never reads
///     a `TestId`: the model knows which LOGICAL test each emitted name is,
///     because it is the thing that emitted the name.
///   * A TWIN reintroduces the shipped regex so the invariants can be shown to
///     have teeth (see `FsiDiscoveryIdentityInvariants`).
module FsiDiscoveryIdentitySim =

  /// How a file declares its contents. This is the ONLY thing that decides which
  /// spelling FSI emits, per `IlxGen.CompLocForSubModuleOrNamespace`.
  [<RequireQualifiedAccess>]
  type FileHeader =
    /// `module A.B` — nested IL types under the submission wrapper.
    | ModuleDeclared
    /// `namespace A.B` in a `#load`ed input — the path lands in `Type.Namespace`.
    | NamespaceDeclared

  /// How the COMPILED build output spells the same type. Both occur in a real
  /// assembly and both reach `ReflectionDiscovery` as `t.FullName`.
  [<RequireQualifiedAccess>]
  type CompiledShape =
    /// A top-level type in a namespace: `A.B.Tests`.
    | TopLevelInNamespace
    /// A type nested in a module: `A.B+Tests`.
    | NestedInModule

  /// One logical test, as the model knows it — independent of any name any
  /// discovery ever produces.
  type LogicalTest =
    { /// Module/namespace path, outermost first.
      Container: string list
      Method: string
      CompiledShape: CompiledShape }

  /// The dotted name the COMPILED assembly's logical identity is, which is what
  /// every spelling must normalize to. Ground truth, computed from the model's
  /// own data and never from a normalizer.
  let logicalName (t: LogicalTest) : string =
    String.concat "." (t.Container @ [ t.Method ])

  [<RequireQualifiedAccess>]
  type Op =
    /// Reflection over the project's build output finds the compiled tests.
    | DiscoverCompiled
    /// One save: FSI evaluates the file as the next submission, and reflection
    /// over the session's dynamic assembly finds the re-evaluated tests.
    | ReEval of header: FileHeader * tests: LogicalTest list

  type Scenario =
    { Tests: LogicalTest list
      Ops: Op list }

  // ── FSI's emit topology, mirrored from the compiler source ────────────────

  /// `fsi.fs:2395-2400` — the submission wrapper's path segment.
  let private wrapper (fragmentId: int) = SageFs.FsiNaming.fragmentPathSegment fragmentId

  /// The `DeclaringType.FullName` reflection reports for a test's COMPILED copy.
  let compiledReflectionName (t: LogicalTest) : string =
    match t.CompiledShape with
    | CompiledShape.TopLevelInNamespace -> String.concat "." t.Container
    | CompiledShape.NestedInModule ->
      // `Outer.Middle+Inner`: the namespace part is dotted, the nesting is not.
      match List.rev t.Container with
      | [] -> ""
      | leaf :: outerRev -> String.concat "." (List.rev outerRev) + "+" + leaf

  /// The `DeclaringType.FullName` reflection reports for a test's copy inside
  /// FSI submission `fragmentId`.
  let fsiReflectionName (fragmentId: int) (header: FileHeader) (t: LogicalTest) : string =
    match header with
    | FileHeader.ModuleDeclared ->
      // IlxGen.CompLocForSubModuleOrNamespace: module names go to `Enclosing`,
      // so the whole chain nests under the wrapper TYPE.
      String.concat "+" (wrapper fragmentId :: t.Container)
    | FileHeader.NamespaceDeclared ->
      // ParseAndCheckInputs.PrependPathToInput keeps the Namespace kind, so the
      // wrapper joins the NAMESPACE and reflection spells it with dots.
      String.concat "." (wrapper fragmentId :: t.Container)

  // ── The trace ─────────────────────────────────────────────────────────────

  /// One discovered entry, paired with the logical test the MODEL knows it is.
  /// The pairing is bookkeeping: it is recorded at emit time, never recovered
  /// from the `TestCase` afterwards.
  type Discovered =
    { Logical: LogicalTest
      Case: TestCase }

  /// The category each side is tagged with, so an invariant can tell WHICH copy
  /// survived a merge without consulting any identity the merge keys on.
  /// `TestDiscoveryMerge.merge` keys on `TestId` alone, so this marker rides
  /// along untouched.
  let compiledMarker = TestCategory.Unit

  let dynamicMarker = TestCategory.Property

  type Observation =
    { /// Everything the merge produced, in its order.
      Merged: TestCase list
      /// Ground truth: the distinct logical tests that have been discovered at
      /// all by this point — tracked by the model, never derived from `Merged`.
      LogicalSoFar: LogicalTest list
      /// Ground truth: the logical tests whose DYNAMIC copy is the current one.
      ReEvaluated: LogicalTest list }

  /// Drive a scenario. `build` produces the `TestCase` for one reflected name;
  /// it is injected ONLY so an invariant can run the historical twin over the
  /// identical trace. `run` below passes the real product function.
  let runWith
    (build: TestCategory -> string -> string -> TestCase)
    (scenario: Scenario)
    : Observation list =
    let mutable compiled : Discovered list = []
    let mutable dynamic : Discovered list = []
    let mutable reEvaluated : LogicalTest list = []
    let mutable nextFragment = 1
    [ for op in scenario.Ops do
        match op with
        | Op.DiscoverCompiled ->
          compiled <-
            scenario.Tests
            |> List.map (fun t ->
              { Logical = t
                Case = build compiledMarker (compiledReflectionName t) t.Method })
        | Op.ReEval(header, tests) ->
          let fragment = nextFragment
          nextFragment <- fragment + 1
          let fresh =
            tests
            |> List.map (fun t ->
              { Logical = t
                Case = build dynamicMarker (fsiReflectionName fragment header t) t.Method })
          // ACCUMULATION, and it is the point. Under `--multiemit-` (which
          // `WorkflowTypes.fs:424` selects) FSI keeps ONE persistent dynamic
          // assembly for the session (`fsi.fs:1818-1830`), so submission 1's
          // copy is still a live, exported, discoverable type when submission 3
          // lands. Reflection over the session therefore returns EVERY copy,
          // each under its own `FSI_NNNN` wrapper. The model reproduces that
          // rather than quietly retiring the old copy, because "an older copy
          // is still there under a different wrapper" is exactly the condition
          // the identity rule has to survive — and the reason a broken rule's
          // damage grows by one phantom test per save instead of staying at
          // one visible mistake.
          dynamic <- dynamic @ fresh
          reEvaluated <-
            (reEvaluated @ tests)
            |> List.distinctBy logicalName

        // THE REAL DECISION under test, folded exactly as the worker's
        // `GetTestDiscovery` composes it.
        let merged =
          TestDiscoveryMerge.merge
            (compiled |> List.map (fun d -> d.Case) |> Array.ofList)
            (dynamic |> List.map (fun d -> d.Case) |> Array.ofList)

        yield
          { Merged = merged |> Array.toList
            LogicalSoFar =
              (compiled @ dynamic)
              |> List.map (fun d -> d.Logical)
              |> List.distinctBy logicalName
            ReEvaluated = reEvaluated } ]

  /// The subject: the REAL identity decision, exactly as `toTestCase` makes it.
  let run (scenario: Scenario) : Observation list =
    runWith
      (fun category declaringName methodName ->
        testCaseOfReflectedName TestFramework.Expecto category declaringName methodName)
      scenario
