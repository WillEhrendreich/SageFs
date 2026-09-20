/// Pure decision core for warmup's source-scanned `open` replay (roast-7 F7,
/// roast-8). Kept as its own standalone file — compiled BEFORE AppState.fs —
/// so the reflection/decision logic stays unit-testable over plain data and
/// AppState.fs (already one of the repo's larger files) doesn't grow to hold
/// it; mirrors why `EvalActorDecision.fs` is its own file rather than nested
/// in AppState.fs. AppState.fs's `discoverWarmupReplayPlan` is the only
/// caller: it does the actual reflection (an IO/reflection edge) and passes
/// the results in here for a pure decision.
module SageFs.OpenReplay

open System

/// Extract all `open` namespace/module names from a source file's lines.
/// Returns distinct names preserving first-occurrence order.
/// Ignores commented-out lines and any non-`open` lines.
let extractOpensFromLines (lines: string[]) : string[] =
  lines
  |> Array.choose (fun line ->
    let trimmed = line.Trim()
    match trimmed.StartsWith("open ", System.StringComparison.Ordinal) && not (trimmed.StartsWith("//", System.StringComparison.Ordinal)) with
    | false -> None
    | true ->
      let parts = trimmed.Split([|' '; '\t'|], StringSplitOptions.RemoveEmptyEntries)
      match parts.Length >= 2 with
       | true -> Some (parts.[1].TrimEnd(';'))
       | false -> None)
  |> Array.distinct

/// The full names of the INTERNAL top-level F# modules among `types` (e.g.
/// `SageFs.WarmupReplayCache`). Warmup must not replay a source file's own
/// `open` of such a module: it is legal inside the assembly but fails from the
/// FSI session, which cannot see internal members of a separately-loaded
/// assembly ("namespace not defined") — non-fatal warmup noise (roast-7 F7).
/// A top-level module is a non-nested type carrying the F# Module construct
/// flag; `internal` shows up in reflection as `not IsPublic` on a non-nested
/// type. Pure over the reflected types so it is unit-testable against a real
/// assembly.
let internalTopLevelModuleFullNames (types: System.Type[]) : Set<string> =
  types
  |> Array.filter (fun t ->
    (not t.IsPublic) && (not t.IsNested) && not (isNull t.FullName)
    && (t.GetCustomAttributes(typeof<Microsoft.FSharp.Core.CompilationMappingAttribute>, false)
        |> Array.exists (fun attr ->
          let cma = attr :?> Microsoft.FSharp.Core.CompilationMappingAttribute
          cma.SourceConstructFlags = Microsoft.FSharp.Core.SourceConstructFlags.Module)))
  |> Array.map (fun t -> t.FullName)
  |> Set.ofArray

/// Why a source-scanned `open` cannot be replayed into the FSI session.
/// Never shown to the user as an error — the open was legal exactly where
/// the source wrote it; these are the reasons warmup drops it before ever
/// attempting the replay, kept as evidence (never a bare bool/Option) for a
/// debug-level trace line. Extends roast-7 F7's internal-top-level-module
/// case (`InternalTopLevelModule`) to the shapes it missed: a module nested
/// inside another module/type, whether opened by its bare unqualified name
/// (never resolvable from FSI's top-level scope, public or not — e.g.
/// samples/from-koans/.../AboutModules.fs's `open MushroomKingdom`) or one
/// that is itself, or has an ancestor, not public (inaccessible even fully
/// qualified — e.g. ArchitectureTests.fs's `module private WaitForGraph`).
type DroppedOpenReason =
  | InternalTopLevelModule
  | NestedModuleBareReference of dottedFullName: string
  | NestedModuleNotVisible of dottedFullName: string

module DroppedOpenReason =
  let describe = function
    | InternalTopLevelModule ->
      "internal top-level module — legal in its own assembly, invisible from the separately loaded FSI session"
    | NestedModuleBareReference dottedFullName ->
      sprintf "nested module '%s' referenced by its bare name — only resolvable inside the file that declares its enclosing scope" dottedFullName
    | NestedModuleNotVisible dottedFullName ->
      sprintf "nested module '%s' is not public (or an enclosing module isn't) — inaccessible from outside its assembly even fully qualified" dottedFullName

/// One F# module discovered via reflection, reduced to exactly what
/// `resolveWarmupOpens` needs to decide whether a scraped `open` of it could
/// ever resolve from a separately loaded FSI session. Pure data — computed
/// once per assembly at the reflection edge (`reflectedModuleFacts`),
/// consumed here with no further reflection calls, so the decision itself
/// is unit-testable against plain records.
type ReflectedModuleFact = {
  /// The module's own simple name, in F#-source form: the CLR "Module"
  /// suffix the compiler adds on a name clash is stripped, matching the
  /// convention the top-level module scan below already applies.
  BareName: string
  /// Fully-qualified F#-source form — the CLR FullName with nested
  /// separators ('+') rewritten to '.', the form a fully-qualified `open`
  /// would use.
  DottedFullName: string
  IsNested: bool
  /// True only when the module AND every enclosing module/type is public —
  /// genuinely reachable, by its fully-qualified name, from outside its own
  /// assembly. `System.Type.IsVisible` already encodes exactly this for
  /// both top-level and nested types.
  IsVisibleOutsideAssembly: bool
}

/// Reduces reflected types to the F# module facts `resolveWarmupOpens`
/// needs — real F# modules only (`CompilationMapping.Module`), skipping
/// compiler-generated/closure junk the same way the top-level module scan
/// below already does.
let reflectedModuleFacts (types: System.Type[]) : ReflectedModuleFact list =
  types
  |> Array.filter (fun t ->
    not (isNull t.FullName)
    && not (
      t.Name.StartsWith("<", System.StringComparison.Ordinal)
      || t.Name.StartsWith("$", System.StringComparison.Ordinal)
      || t.Name.Contains("@")
      || t.Name.Contains("+"))
    && (t.GetCustomAttributes(typeof<Microsoft.FSharp.Core.CompilationMappingAttribute>, false)
        |> Array.exists (fun attr ->
          let cma = attr :?> Microsoft.FSharp.Core.CompilationMappingAttribute
          cma.SourceConstructFlags = Microsoft.FSharp.Core.SourceConstructFlags.Module)))
  |> Array.map (fun t ->
    let hasModuleSuffix = t.Name.EndsWith("Module", System.StringComparison.Ordinal)
    let stripSuffix (s: string) =
      match hasModuleSuffix with
      | true -> s.Substring(0, s.Length - 6)
      | false -> s
    { BareName = stripSuffix t.Name
      DottedFullName = stripSuffix (t.FullName.Replace('+', '.'))
      IsNested = t.IsNested
      IsVisibleOutsideAssembly = t.IsVisible })
  |> Array.toList

/// Decides which source-scanned `open` names can safely be replayed into
/// the FSI session and which cannot possibly resolve there — folding
/// roast-7 F7's internal-top-level-module rule and the nested/private-module
/// rule (both reported live, roast-8) into ONE decision instead of two
/// parallel filters. A scraped name this function has no evidence against —
/// e.g. a BCL or NuGet-package namespace, since `reflectedModuleFacts` only
/// ever reflects the solution's OWN project assemblies — is kept: dropping
/// is always a positive finding from real reflection evidence, never a
/// default, so this can only ever make warmup MORE permissive of names it
/// can't evaluate, never silently reject something it hasn't actually
/// proven unresolvable.
let resolveWarmupOpens
  (scrapedNames: string seq)
  (moduleFacts: ReflectedModuleFact list)
  : {| Replayable: string list; Dropped: (string * DroppedOpenReason) list |} =
  let bareNestedNames =
    moduleFacts
    |> List.filter (fun m -> m.IsNested)
    |> List.map (fun m -> m.BareName, m.DottedFullName)
    |> Map.ofList
  let notVisibleByDottedName =
    moduleFacts
    |> List.filter (fun m -> not m.IsVisibleOutsideAssembly)
    |> List.map (fun m -> m.DottedFullName, m.IsNested)
    |> Map.ofList
  let classify (name: string) =
    match name.Contains('.') with
    | false ->
      match Map.tryFind name bareNestedNames with
      | Some dottedFullName -> Choice2Of2 (name, NestedModuleBareReference dottedFullName)
      | None ->
        match Map.tryFind name notVisibleByDottedName with
        | Some _ -> Choice2Of2 (name, InternalTopLevelModule)
        | None -> Choice1Of2 name
    | true ->
      match Map.tryFind name notVisibleByDottedName with
      | Some true -> Choice2Of2 (name, NestedModuleNotVisible name)
      | Some false -> Choice2Of2 (name, InternalTopLevelModule)
      | None -> Choice1Of2 name
  let results = scrapedNames |> Seq.map classify |> Seq.toList
  {|
    Replayable = results |> List.choose (function Choice1Of2 n -> Some n | _ -> None)
    Dropped = results |> List.choose (function Choice2Of2 (n, r) -> Some (n, r) | _ -> None)
  |}
