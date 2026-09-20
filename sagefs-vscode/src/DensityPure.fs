/// What `sagefs.density` actually gates.
///
/// WHY — the setting ships three values with three `enumDescriptions` that read
/// like a contract, and exactly ONE surface honoured it
/// (sagefs-ux-roast.md §4.3): the eval CodeLens, plus the cell highlight.
/// `TestCodeLensProvider`, `CoverageViewCodeLensProvider`, `TestDecorations`
/// and the binding ghost text contained no density check at all. So in
/// `minimal` — documented as *"Only inline results on eval, nothing
/// persistent"* — a user still got test gutter signs, coverage gutters, two
/// CodeLens families and persistent binding ghost text. The setting described
/// a product that did not exist.
///
/// The descriptions in package.json are the specification, quoted here so the
/// contract test can compare this table against them:
///
///   full    — "All decorations: inline results, code lens, cell highlight,
///              test signs, stale markers"
///   normal  — "Inline results and test signs, no code lens or cell highlight"
///   minimal — "Only inline results on eval, nothing persistent"
///
/// No Fable dependency; tested under `dotnet fsi`
/// (tests/DensityContractTests.fsx).
module SageFs.Vscode.DensityPure

/// The visual-annotation preset. Named exactly as the setting's enum values.
[<RequireQualifiedAccess>]
type Density =
  | Full
  | Normal
  | Minimal

/// Every visual annotation the extension can draw. Adding one forces a
/// decision here rather than letting it default to "always on", which is how
/// four surfaces ended up ignoring the setting.
[<RequireQualifiedAccess>]
type AnnotationSurface =
  /// The result rendered next to a block right after you evaluate it.
  /// The one thing `minimal` explicitly keeps.
  | InlineEvalResult
  /// "▶ Eval" above each block.
  | EvalCodeLens
  /// The per-test outcome CodeLens.
  | TestCodeLens
  /// The per-function coverage badge CodeLens.
  | CoverageCodeLens
  /// Pass/fail gutter signs on test lines.
  | TestSigns
  /// Coverage gutter decorations.
  | CoverageGutters
  /// The "this result is stale" marker on a previously-evaluated block.
  | StaleMarkers
  /// The highlight around the block the caret is in.
  | CellHighlight
  /// Persistent ghost text showing bound values.
  | BindingGhostText

module Density =

  let ofString (s: string) : Density =
    match (s |> Option.ofObj |> Option.defaultValue "").Trim().ToLowerInvariant() with
    | "normal" -> Density.Normal
    | "minimal" -> Density.Minimal
    | _ -> Density.Full

  let toString = function
    | Density.Full -> "full"
    | Density.Normal -> "normal"
    | Density.Minimal -> "minimal"

  let label = function
    | Density.Full -> "Full"
    | Density.Normal -> "Normal"
    | Density.Minimal -> "Minimal"

  /// The cycle order the `sagefs.cycleDensity` command walks.
  let next = function
    | Density.Full -> Density.Normal
    | Density.Normal -> Density.Minimal
    | Density.Minimal -> Density.Full

/// The one table. Total over both DUs, so a new surface or a new preset is a
/// compile error rather than a silently-always-on annotation.
let shows (density: Density) (surface: AnnotationSurface) : bool =
  match surface with
  // "Only inline results on eval" — the one thing every preset keeps.
  | AnnotationSurface.InlineEvalResult -> true
  // "no code lens" at normal; "nothing persistent" at minimal.
  | AnnotationSurface.EvalCodeLens
  | AnnotationSurface.TestCodeLens
  | AnnotationSurface.CoverageCodeLens ->
    match density with
    | Density.Full -> true
    | Density.Normal | Density.Minimal -> false
  // "Inline results and test signs" — normal keeps the gutter.
  | AnnotationSurface.TestSigns
  | AnnotationSurface.CoverageGutters ->
    match density with
    | Density.Full | Density.Normal -> true
    | Density.Minimal -> false
  // Named only in `full`'s description.
  | AnnotationSurface.StaleMarkers
  | AnnotationSurface.CellHighlight
  | AnnotationSurface.BindingGhostText ->
    match density with
    | Density.Full -> true
    | Density.Normal | Density.Minimal -> false

/// Every surface, for the contract test's exhaustiveness sweep.
let allSurfaces: AnnotationSurface list = [
  AnnotationSurface.InlineEvalResult
  AnnotationSurface.EvalCodeLens
  AnnotationSurface.TestCodeLens
  AnnotationSurface.CoverageCodeLens
  AnnotationSurface.TestSigns
  AnnotationSurface.CoverageGutters
  AnnotationSurface.StaleMarkers
  AnnotationSurface.CellHighlight
  AnnotationSurface.BindingGhostText
]

let allDensities: Density list = [ Density.Full; Density.Normal; Density.Minimal ]
