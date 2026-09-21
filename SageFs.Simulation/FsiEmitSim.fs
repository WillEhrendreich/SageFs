namespace SageFs.Simulation

open SageFs.Features.ReloadPlanning
open SageFs.Features.ReloadOutcome

/// Deterministic Simulation Testing for the ONE claim hot reload makes to a
/// user: "Hot reloaded N of M changed definition(s)" means the running process
/// now executes the new code.
///
/// WHY THIS EXISTS. That claim was false and the only thing watching it was a
/// 15-second real-host integration test with no observability: the worker sent
/// `{"outcome":"Patched","patched":1,"considered":1}` while the app kept
/// serving the pre-edit value, and narrowing it down cost repeated full host
/// spawns to learn one bit at a time. The decision that produces the claim
/// (`ReloadPlanning.confirmPatchAsOutcome`) is PURE, so it can be driven
/// against a model of FSI's emit topology in microseconds instead.
///
/// THE MODEL IS TAKEN FROM THE COMPILER SOURCE, not inferred from behaviour
/// (`~/Work/fsharp-compiler-services`):
///
///   * `fsi.fs:1818-1830` — with `--multiemit-` (`fsiMultiAssemblyEmit` false,
///     which `WorkflowTypes.fs:424` selects for HotReload) FSI builds ONE
///     persistent dynamic assembly via `SingleRefEmitAssembly` and keeps it for
///     the session. It ACCUMULATES: every prior eval's types stay reachable by
///     reflection, so each new eval faces a growing pile of older copies.
///     Default (`CompilerConfig.fs:835`) is multiemit TRUE — a fresh in-memory
///     assembly per submission under `FSI-ASSEMBLY-MULTI`.
///   * `fsi.fs:2396-2400` — every submission is wrapped in a type named
///     `FSI_NNNN` (`FsiDynamicModulePrefix`, `PrettyNaming.fs:1097`).
///   * `CheckDeclarations.fs:740` — the compiler itself strips that prefix when
///     computing logical paths, and `HotReloadCore.getAllMethods`/`seedPath`
///     strip it too so both sides register the same qualified name.
///
/// THE CONSEQUENCE THAT BITES, and the reason this sim exists: once `FSI_NNNN`
/// is stripped, the COMPILED copy of `M.f` and EVERY prior eval's copy of `M.f`
/// share one identical `FullName`. `reloadedMethods` is a list of those names.
/// So "a method with this name was re-pointed" CANNOT distinguish
///   (a) the compiled entry point an app running from the project's build
///       output actually calls, from
///   (b) a previous eval's copy that nothing outside that eval ever calls.
/// Both produce the same string, and `confirmPatchAsOutcome` counts either as
/// landed.
///
/// Same design rules as `FileReloadRoutingSim`/`CohortLandingSim`:
///   * Chaos is DATA — a `Scenario` is an ordered op list over a tiny decl
///     pool. Same ops, same trace, forever. No clock, no IO, no reflection.
///   * The REAL decision is folded (`confirmPatchAsOutcome`,
///     `ReloadOutcome.processChanged`), never a reimplemented "correct" copy.
///   * Ground truth is tracked by an INDEPENDENT bookkeeping fold that never
///     reads the decision under test: the model knows which copy the app holds
///     and whether that copy was re-pointed.
///   * A TWIN reintroduces the historical bug so the invariants can be shown to
///     have teeth (see `FsiEmitInvariants`).
module FsiEmitSim =

  /// Which copy of a declaration this is. Distinct in the CLR; identical as a
  /// string once `FSI_NNNN` is stripped — that collision is the subject.
  [<RequireQualifiedAccess>]
  type Copy =
    /// The project's compiled build output. An app started from compiled code
    /// captures this one.
    | Compiled
    /// `FSI_NNNN` in the session's dynamic assembly, N = eval ordinal.
    | Fragment of eval: int

  /// How the re-eval was emitted — the choice `CompilationContext.emitStableIdentity`
  /// makes, and the one that decides whether the compiled copy can be paired.
  [<RequireQualifiedAccess>]
  type EmitStyle =
    /// Declarations re-emitted inside their own module path with
    /// `open global.<path>`, so a type the file declares resolves to the
    /// COMPILED type and the compiled method's signature still matches.
    | NestedWithGlobalOpen
    /// Declarations flattened into the top-level module. A file-local type is
    /// re-declared as a fresh FSI type, so the re-evaluated signature mentions
    /// a DIFFERENT type than the compiled one and `compatibleForDetour`'s
    /// parameter comparison rejects the compiled pair — leaving only
    /// FSI-copy-to-FSI-copy pairings to succeed.
    | Flattened

  /// A declaration's shape, insofar as it changes pairing.
  type Decl =
    { Name: string
      /// True when the signature mentions a type declared in the same file.
      /// This is the shape the shape-matrix fixture calls `localType`, and the
      /// one that "broke every real web app".
      MentionsFileLocalType: bool }

  [<RequireQualifiedAccess>]
  type Op =
    /// The app starts and captures whichever copy of each decl is current.
    | StartApp
    /// One save: re-evaluate `decls`, emitted in `style`.
    | Save of decls: Decl list * style: EmitStyle

  type Scenario =
    { Decls: Decl list
      Ops: Op list }

  /// What the model knows, tracked independently of the decision under test.
  type ModelState =
    { /// Every copy that exists, newest first, per decl.
      Copies: Map<string, Copy list>
      /// The copy the running app actually calls, if it has started.
      AppHolds: Map<string, Copy>
      NextEval: int }

  let initial (decls: Decl list) : ModelState =
    { Copies = decls |> List.map (fun d -> d.Name, [ Copy.Compiled ]) |> Map.ofList
      AppHolds = Map.empty
      NextEval = 1 }

  /// Can the detour matcher pair this OLDER copy with the newly emitted one?
  ///
  /// Mirrors `HotReloadCore.compatibleForDetour`, whose decisive clause is a
  /// parameter/return TYPE comparison. A flattened re-emit of a decl whose
  /// signature mentions a file-local type re-declares that type in FSI, so the
  /// compiled copy's parameter type is the project assembly's and the new one's
  /// is FSI's — not equal, no pair. Fragment copies re-declared the same way
  /// share the FSI type of their own generation, so they still pair with each
  /// other, which is exactly how a save can report success having moved
  /// nothing the app calls.
  let private pairs (style: EmitStyle) (decl: Decl) (older: Copy) : bool =
    match style, decl.MentionsFileLocalType, older with
    | EmitStyle.NestedWithGlobalOpen, _, _ -> true
    | EmitStyle.Flattened, false, _ -> true
    | EmitStyle.Flattened, true, Copy.Compiled -> false
    | EmitStyle.Flattened, true, Copy.Fragment _ -> true

  /// One save. Returns the new state, the names `reloadedMethods` would carry
  /// (indistinguishable strings, exactly as the real report does), and the
  /// ground truth: which decls the RUNNING APP now executes anew.
  let step (state: ModelState) (decls: Decl list) (style: EmitStyle) =
    let evalId = state.NextEval
    let newCopy = Copy.Fragment evalId

    let redirectedOrigins =
      decls
      |> List.map (fun d ->
        let older = state.Copies |> Map.tryFind d.Name |> Option.defaultValue []
        d.Name, older |> List.filter (pairs style d))
      |> Map.ofList

    // `reloadedMethods` is a NAME list: a decl appears iff ANY older copy was
    // re-pointed, with no record of which. That erasure is the defect this sim
    // exists to pin, so the model reproduces it faithfully rather than
    // smuggling the origin through.
    let reloadedMethods =
      decls
      |> List.filter (fun d -> redirectedOrigins |> Map.find d.Name |> List.isEmpty |> not)
      |> List.map (fun d -> d.Name)

    // The evidence a NAME cannot carry: of those redirects, the ones whose old
    // entry point lived in a COMPILED assembly. `HotReloadCore` reads this off
    // `MethodInfo.DeclaringType.Assembly.IsDynamic`; the model reads it off the
    // copy's origin, which is the same fact.
    let reachedRunningProcess =
      decls
      |> List.filter (fun d ->
        redirectedOrigins |> Map.find d.Name |> List.contains Copy.Compiled)
      |> List.map (fun d -> d.Name)

    // GROUND TRUTH, tracked without consulting the decision under test: the app
    // executes new code for a decl iff the copy IT holds was among the
    // re-pointed ones.
    let appChanged =
      decls
      |> List.filter (fun d ->
        match state.AppHolds |> Map.tryFind d.Name with
        | None -> false
        | Some held -> redirectedOrigins |> Map.find d.Name |> List.contains held)
      |> List.map (fun d -> d.Name)
      |> Set.ofList

    let copies =
      decls
      |> List.fold
        (fun acc d ->
          let older = acc |> Map.tryFind d.Name |> Option.defaultValue []
          acc |> Map.add d.Name (newCopy :: older))
        state.Copies

    { state with Copies = copies; NextEval = evalId + 1 }, reloadedMethods, reachedRunningProcess, appChanged

  /// The app captures whatever copy is current for each decl.
  let startApp (state: ModelState) : ModelState =
    let held =
      state.Copies
      |> Map.map (fun _ copies -> copies |> List.tryHead |> Option.defaultValue Copy.Compiled)
    { state with AppHolds = held }

  /// `FileDecls`/`SourceDecl` shaped for the real `confirmPatchAsOutcome`.
  /// Only the fields that function reads are populated; nothing here is a
  /// parallel reimplementation of it.
  let private sourceDecl (name: string) : SourceDecl =
    { Name = name
      Kind = DeclKind.FunctionDecl
      Access = DeclAccess.Public
      Header = sprintf "let %s x" name
      Text = sprintf "let %s x = x" name
      Container = []
      StartLine = 1
      EndLine = 1 }

  let private fileDecls (decls: Decl list) : FileDecls =
    { ModulePath = [ "M" ]
      Opens = []
      Decls = decls |> List.map (fun d -> sourceDecl d.Name)
      RawSource = None }

  /// One scenario's trace: for each save, what the REAL decision reported and
  /// what the app actually does.
  type Observation =
    { Reported: ReloadOutcome
      /// Did the decision claim the running process changed?
      ClaimedChange: bool
      /// Did it actually, per the independent model?
      ActualChange: bool }

  /// Drive a scenario through the REAL `confirmPatchAsOutcome`.
  /// `confirm` is injected so an invariant can run the historical TWIN against
  /// the identical trace and show the difference.
  let runWith
    (confirm: FileDecls -> SourceDecl list -> string list -> string list -> ReloadOutcome)
    (scenario: Scenario)
    : Observation list =
    let mutable state = initial scenario.Decls
    [ for op in scenario.Ops do
        match op with
        | Op.StartApp ->
          state <- startApp state
        | Op.Save(decls, style) ->
          let before = fileDecls scenario.Decls
          let next, reloaded, reached, appChanged = step state decls style
          state <- next
          let outcome = confirm before (decls |> List.map (fun d -> sourceDecl d.Name)) reloaded reached
          yield
            { Reported = outcome
              ClaimedChange = ReloadOutcome.processChanged outcome
              ActualChange = not (Set.isEmpty appChanged) } ]

  /// The REAL decision wired exactly as `WorkerMain` wires it TODAY.
  ///
  /// The worker cannot tell which copy the running app holds — measured: a
  /// `#load`ed file's app holds an FSI copy even when a compiled copy also
  /// exists, so neither "a compiled entry point was re-pointed" nor "...or no
  /// compiled copy exists" is a sound proxy; both turned working reloads into
  /// reported no-ops against a real host. So it passes the redirect-set as the
  /// reached-set, and this over-claims. That is the gap, pinned.
  let run (scenario: Scenario) : Observation list =
    runWith
      (fun before patched reloaded _ -> confirmPatchAsOutcome before patched reloaded reloaded)
      scenario

  /// The same REAL decision GIVEN the evidence it now accepts — what the
  /// product reports once the app's captured copy is recorded where the handler
  /// table is BUILT rather than inferred at detour time.
  ///
  /// Kept beside `run` so the sim proves the designed fix is correct while the
  /// product still shows the gap: only the evidence is missing, not the logic.
  let runWithEvidence (scenario: Scenario) : Observation list =
    runWith confirmPatchAsOutcome scenario
