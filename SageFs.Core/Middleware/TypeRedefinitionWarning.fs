module SageFs.Middleware.TypeRedefinitionWarning

open System.Text.RegularExpressions
open SageFs.AppState

/// Key used in AppState.Custom to track previously defined type names.
[<Literal>]
let TypeTrackingKey = "typeRedefinitionTracker"

/// State stored in AppState.Custom: the set of type names defined so far.
type TrackedTypes = { DefinedTypes: Set<string> }

module TrackedTypes =
  let empty = { DefinedTypes = Set.empty }

  let get (st: AppState) : TrackedTypes =
    AppStateCustom.tryGetFeature<TrackedTypes> TypeTrackingKey st
    |> Option.defaultValue empty

  let set (tracked: TrackedTypes) (st: AppState) : AppState =
    AppStateCustom.set TypeTrackingKey tracked st

/// Regex to detect `type <Name> =` at the start of a line (not inside strings).
/// Matches: type Foo =, type Bar<'T> =, etc.
/// Skips lines that start inside a string by requiring line-start anchor.
let private typeDefPattern =
  Regex(
    @"(?m)^\s*type\s+([A-Z][\w']*)\b",
    RegexOptions.Compiled)

/// Strip triple-quoted and regular string literals so that
/// `type Foo` inside a string doesn't cause false positives.
let private stripStrings (code: string) =
  // Strip triple-quoted strings first, then regular double-quoted strings
  let tripleQuoted = Regex.Replace(code, "\"\"\"[\\s\\S]*?\"\"\"", "\"\"")
  Regex.Replace(tripleQuoted, "\"(?:[^\"\\\\]|\\\\.)*\"", "\"\"")

/// Extract type names from submitted code.
let extractTypeNames (code: string) : string list =
  let cleaned = stripStrings code
  typeDefPattern.Matches cleaned
  |> Seq.cast<Match>
  |> Seq.map (fun m -> m.Groups.[1].Value)
  |> Seq.distinct
  |> List.ofSeq

/// Format a warning message for redefined types.
let formatWarning (redefined: string list) : string =
  let typeList = redefined |> String.concat ", "
  sprintf
    "⚠ Type redefinition warning: %s was previously defined in an earlier submission. Redefining a type in a separate ;; block can cause TypeLoadException when the old and new definitions coexist. Consider combining the type definition and its usage into a single ;; block."
    typeList

/// Pre-eval middleware: detects type redefinitions and prepends a warning.
let typeRedefinitionWarningMiddleware next (request, st: AppState) =
  let currentNames = extractTypeNames request.Code
  let tracked = TrackedTypes.get st

  let redefined =
    currentNames
    |> List.filter (fun name -> Set.contains name tracked.DefinedTypes)

  // Track all newly defined types (union with existing)
  let updatedTracked =
    { DefinedTypes =
        currentNames
        |> Set.ofList
        |> Set.union tracked.DefinedTypes }
  let st' = TrackedTypes.set updatedTracked st

  // Run the downstream pipeline
  let (response, finalSt) = next (request, st')

  // Prepend warning to the response if redefinitions were found
  match redefined with
  | [] -> (response, finalSt)
  | _ ->
    let warning = formatWarning redefined
    let enrichedResult =
      match response.EvaluationResult with
      | Ok text -> Ok (sprintf "%s\n%s" warning text)
      | err -> err
    ({ response with EvaluationResult = enrichedResult }, finalSt)
