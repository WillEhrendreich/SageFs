module SageFs.Vscode.FileAnnotationsListener

open SageFs.Vscode.SafeInterop

// ── Types ───────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type CoverageHealth = AllPassing | SomeFailing | NoCoverage

type CoverageAnnotation = {
  Line: int
  Health: CoverageHealth
}

type InlineFailure = {
  Line: int
  TestName: string
  Presentation: string
}

type FileAnnotations = {
  FilePath: string
  CoverageAnnotations: CoverageAnnotation list
  InlineFailures: InlineFailure list
}

// ── Parsing ─────────────────────────────────────────────────────

let private parseFailurePresentation (f: obj) : string =
  match fieldObj "Failure" f with
  | None -> ""
  | Some failure ->
    let case_ = fieldString "Case" failure
    let fields = fieldArray "Fields" failure |> Option.defaultValue [||]
    match case_, fields.Length with
    | Some "AssertionDiff", n when n >= 2 ->
      sprintf "Expected: %s  Actual: %s" (string fields.[0]) (string fields.[1])
    | Some "ExceptionMessage", n when n >= 1 ->
      string fields.[0]
    | Some "Timeout", n when n >= 1 ->
      sprintf "Timed out after %s" (string fields.[0])
    | Some "RawMessage", n when n >= 1 ->
      string fields.[0]
    | _ -> ""

let parseFileAnnotations (data: obj) : FileAnnotations option =
  match fieldString "FilePath" data with
  | None -> None
  | Some fp ->
    let coverageAnns =
      fieldArray "CoverageAnnotations" data
      |> Option.defaultValue [||]
      |> Array.choose (fun ann ->
        let line = fieldInt "Line" ann
        let health =
          match fieldString "Health" ann with
          | Some "AllPassing" -> CoverageHealth.AllPassing
          | Some "SomeFailing" -> CoverageHealth.SomeFailing
          | _ -> CoverageHealth.NoCoverage
        match line with
        | Some l -> Some { Line = l; Health = health }
        | None -> None)
      |> Array.toList
    let inlineFailures =
      fieldArray "InlineFailures" data
      |> Option.defaultValue [||]
      |> Array.choose (fun f ->
        let line = fieldInt "Line" f
        let testName = fieldString "TestName" f |> Option.defaultValue ""
        let presentation = parseFailurePresentation f
        match line with
        | Some l -> Some { Line = l; TestName = testName; Presentation = presentation }
        | None -> None)
      |> Array.toList
    Some { FilePath = fp; CoverageAnnotations = coverageAnns; InlineFailures = inlineFailures }
