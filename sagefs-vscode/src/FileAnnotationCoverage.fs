module SageFs.Vscode.FileAnnotationCoverage

[<RequireQualifiedAccess>]
type CoverageHealth =
  | AllPassing
  | SomeFailing
  | NoCoverage

[<RequireQualifiedAccess>]
type BranchCoverage =
  | FullyCovered
  | PartiallyCovered of covered: int * total: int
  | NotCovered
  | Unknown

type CoverageAnnotation = {
  Line: int
  Health: CoverageHealth
  BranchCoverage: BranchCoverage
}

[<RequireQualifiedAccess>]
type CoverageDecorationKind =
  | LinePassing
  | LineFailing
  | LineNone
  | BranchFull
  | BranchPartial of covered: int * total: int
  | BranchNone

module CoverageAnnotation =
  let decorationKind (annotation: CoverageAnnotation) =
    match annotation.BranchCoverage with
    | BranchCoverage.FullyCovered -> CoverageDecorationKind.BranchFull
    | BranchCoverage.PartiallyCovered (covered, total) -> CoverageDecorationKind.BranchPartial (covered, total)
    | BranchCoverage.NotCovered -> CoverageDecorationKind.BranchNone
    | BranchCoverage.Unknown ->
        match annotation.Health with
        | CoverageHealth.AllPassing -> CoverageDecorationKind.LinePassing
        | CoverageHealth.SomeFailing -> CoverageDecorationKind.LineFailing
        | CoverageHealth.NoCoverage -> CoverageDecorationKind.LineNone

  let hoverMessage (annotation: CoverageAnnotation) =
    match decorationKind annotation with
    | CoverageDecorationKind.LinePassing -> "Coverage: all tests passing"
    | CoverageDecorationKind.LineFailing -> "Coverage: some tests failing"
    | CoverageDecorationKind.LineNone -> "No coverage"
    | CoverageDecorationKind.BranchFull -> "Branch coverage: all branches covered"
    | CoverageDecorationKind.BranchPartial (covered, total) ->
        sprintf "Branch coverage: %d/%d branches covered" covered total
    | CoverageDecorationKind.BranchNone -> "Branch coverage: no branches covered"
