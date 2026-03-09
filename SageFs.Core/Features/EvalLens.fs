namespace SageFs.Features

/// Purity classification for a pipeline stage.
type LensClassification =
  | Pure
  | Effectful
  | Unknown

/// A single stage in a decomposed pipeline.
type PipelineStage = {
  StageIndex: int
  Code: string
}

/// Result of lens inspection for a pipeline stage.
type LensResult = {
  StageIndex: int
  Code: string
  TypeSig: string option
  Value: string option
}

module EvalLens =

  let private pureModules =
    set [ "List"; "Array"; "Seq"; "Map"; "Set"; "Option"; "Result"
          "String"; "Char"; "Int32"; "Int64"; "Float"; "Decimal"
          "Tuple"; "Choice"; "Async" ]

  let private purePrefixes =
    [ "List."; "Array."; "Seq."; "Map."; "Set."; "Option."; "Result."
      "String."; "Char."; "sprintf"; "string "; "int "; "float " ]

  let private effectfulPrefixes =
    [ "Async.RunSynchronously"; "Async.Start"; "Async.AwaitTask"
      "File."; "Directory."; "Console."; "IO."; "Http"; "Net."
      "Process."; "Environment."; "Stream." ]

  let private purePatterns =
    [ "+"; "-"; "*"; "/"; "%"; "="; "<"; ">"; "&&"; "||"; "not "; "fst"; "snd" ]

  /// Split a pipeline expression into stages, respecting parenthesized groups.
  let decomposePipeline (code: string) : PipelineStage list =
    let mutable depth = 0
    let mutable stages = []
    let mutable current = System.Text.StringBuilder()
    let chars = code.ToCharArray()
    let len = chars.Length
    let mutable i = 0
    while i < len do
      match chars.[i] with
      | '(' -> depth <- depth + 1; current.Append('(') |> ignore; i <- i + 1
      | ')' -> depth <- depth - 1; current.Append(')') |> ignore; i <- i + 1
      | '|' when depth = 0 && i + 1 < len && chars.[i + 1] = '>' ->
        stages <- current.ToString().Trim() :: stages
        current <- System.Text.StringBuilder()
        i <- i + 2
      | c -> current.Append(c) |> ignore; i <- i + 1
    let last = current.ToString().Trim()
    match last with
    | "" -> ()
    | _ -> stages <- last :: stages
    stages
    |> List.rev
    |> List.mapi (fun idx code -> { StageIndex = idx; Code = code })

  /// Classify a pipeline stage as Pure, Effectful, or Unknown.
  let classifyStage (code: string) : LensClassification =
    let trimmed = code.Trim()
    match effectfulPrefixes |> List.exists (fun p -> trimmed.StartsWith(p) || trimmed.Contains(p)) with
    | true -> Effectful
    | false ->
      let isPure =
        purePrefixes |> List.exists (fun p -> trimmed.StartsWith(p) || trimmed.Contains(p))
        || purePatterns |> List.exists (fun p -> trimmed.Contains(p))
      match isPure with
      | true -> Pure
      | false -> Unknown

  /// Format a lens result as a human-readable string.
  let formatLensResult (r: LensResult) : string =
    match r.TypeSig, r.Value with
    | Some t, Some v -> sprintf "[%d] %s : %s = %s" r.StageIndex r.Code t v
    | Some t, None -> sprintf "[%d] %s : %s" r.StageIndex r.Code t
    | None, Some v -> sprintf "[%d] %s = %s" r.StageIndex r.Code v
    | None, None -> sprintf "[%d] %s" r.StageIndex r.Code

  /// Create unannotated lens results for each pipeline stage (types filled by runtime).
  let annotatePipeline (stages: PipelineStage list) : LensResult list =
    stages
    |> List.map (fun s ->
      { StageIndex = s.StageIndex; Code = s.Code; TypeSig = None; Value = None })
