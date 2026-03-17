module SageFs.Vscode.FileAnnotationsListener

open SageFs.Vscode.SafeInterop
module Coverage = SageFs.Vscode.FileAnnotationCoverage

// ── Types ───────────────────────────────────────────────────────

type CoverageHealth = Coverage.CoverageHealth
type BranchCoverage = Coverage.BranchCoverage
type CoverageAnnotation = Coverage.CoverageAnnotation

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

let private tryFieldAny (getField: string -> obj -> 'a option) (names: string list) (data: obj) =
  names |> List.tryPick (fun name -> getField name data)

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

let private parseCoverageHealth (ann: obj) =
  let fromLegacyHealth =
    tryFieldAny fieldString [ "Health"; "health" ] ann
    |> Option.map (function
      | "AllPassing" -> CoverageHealth.AllPassing
      | "SomeFailing" -> CoverageHealth.SomeFailing
      | _ -> CoverageHealth.NoCoverage)

  let fromDetail =
    tryFieldAny fieldObj [ "Detail"; "detail" ] ann
    |> Option.bind (fun detail ->
      match duCase detail with
      | Some "Covered" ->
          duFieldsArray detail
          |> Option.bind (fun fields ->
            match fields.Length >= 2 with
            | true ->
                duCase fields.[1]
                |> Option.map (function
                  | "AllPassing" -> CoverageHealth.AllPassing
                  | "SomeFailing" -> CoverageHealth.SomeFailing
                  | _ -> CoverageHealth.NoCoverage)
            | false -> None)
      | Some "NotCovered"
      | Some "Pending" ->
          Some CoverageHealth.NoCoverage
      | _ -> None)

  fromDetail
  |> Option.orElse fromLegacyHealth
  |> Option.defaultValue CoverageHealth.NoCoverage

let private parseBranchCoverage (ann: obj) =
  match tryFieldAny fieldObj [ "BranchCoverage"; "branchCoverage" ] ann with
  | Some branch ->
      match duCase branch with
      | Some "FullyCovered" -> BranchCoverage.FullyCovered
      | Some "NotCovered" -> BranchCoverage.NotCovered
      | Some "PartiallyCovered" ->
          match duFieldsArray branch with
          | Some fields when fields.Length >= 2 ->
              match tryCastInt fields.[0], tryCastInt fields.[1] with
              | Some covered, Some total -> BranchCoverage.PartiallyCovered (covered, total)
              | _ -> BranchCoverage.Unknown
          | _ -> BranchCoverage.Unknown
      | _ -> BranchCoverage.Unknown
  | None -> BranchCoverage.Unknown

let parseFileAnnotations (data: obj) : FileAnnotations option =
  match tryFieldAny fieldString [ "FilePath"; "filePath" ] data with
  | None -> None
  | Some fp ->
    let coverageAnns : CoverageAnnotation list =
      tryFieldAny fieldArray [ "CoverageAnnotations"; "coverageAnnotations" ] data
      |> Option.defaultValue [||]
      |> Array.choose (fun ann ->
        let line = tryFieldAny fieldInt [ "Line"; "line" ] ann
        match line with
        | Some l ->
            Some
              ({ Line = l
                 Health = parseCoverageHealth ann
                 BranchCoverage = parseBranchCoverage ann } : CoverageAnnotation)
        | None -> None)
      |> Array.toList
    let inlineFailures =
      tryFieldAny fieldArray [ "InlineFailures"; "inlineFailures" ] data
      |> Option.defaultValue [||]
      |> Array.choose (fun f ->
        let line = tryFieldAny fieldInt [ "Line"; "line" ] f
        let testName = tryFieldAny fieldString [ "TestName"; "testName" ] f |> Option.defaultValue ""
        let presentation = parseFailurePresentation f
        match line with
        | Some l -> Some { Line = l; TestName = testName; Presentation = presentation }
        | None -> None)
      |> Array.toList
    Some { FilePath = fp; CoverageAnnotations = coverageAnns; InlineFailures = inlineFailures }
