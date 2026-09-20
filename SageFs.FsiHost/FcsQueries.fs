/// The compiler-service queries the isolated host answers (diagnostics, symbol references, completion
/// candidates). Compiled ONLY into the host, against the SDK's own FSharp.Compiler.Service, and returning wire
/// types from FsiProtocol: no FCS object ever crosses the process boundary, and SageFs needs no FCS to ask.
module SageFs.FsiHost.FcsQueries

open System
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Interactive.Shell
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.FsiHost.FsiProtocol

let private severityOf (severity: FSharpDiagnosticSeverity) : DiagnosticSeverity =
  match severity with
  | FSharpDiagnosticSeverity.Hidden -> DiagHidden
  | FSharpDiagnosticSeverity.Info -> DiagInfo
  | FSharpDiagnosticSeverity.Warning -> DiagWarning
  | FSharpDiagnosticSeverity.Error -> DiagError

let toWire (d: FSharpDiagnostic) : FsiDiagnostic =
  { Severity = severityOf d.Severity
    ErrorNumber = d.ErrorNumber
    Subcategory = d.Subcategory
    Message = d.Message
    StartLine = d.StartLine
    StartColumn = d.StartColumn
    EndLine = d.EndLine
    EndColumn = d.EndColumn }

/// All diagnostics of a parse + check, in the same order and de-duplicated the same way SageFs reports them in-process.
let private diagnosticsOf (parse: FSharpParseFileResults) (typed: FSharpCheckFileResults) (glob: FSharpCheckProjectResults) =
  parse.Diagnostics
  |> Seq.append typed.Diagnostics
  |> Seq.append glob.Diagnostics
  |> Seq.map toWire
  |> Seq.distinct
  |> Seq.toList

let check (session: FsiEvaluationSession) (text: string) : FsiDiagnostic list =
  let parse, typed, glob = session.ParseAndCheckInteraction text
  diagnosticsOf parse typed glob

/// Diagnostics plus, for error-free code, every symbol definition and use (opens excluded): the live-testing
/// dependency graph's input.
let checkWithSymbols (session: FsiEvaluationSession) (filePath: string) (text: string) : FsiDiagnostic list * WireSymbolRef list =
  let parse, typed, glob = session.ParseAndCheckInteraction text
  let diagnostics = diagnosticsOf parse typed glob
  let hasErrors = diagnostics |> List.exists (fun d -> d.Severity = DiagError)
  let symbols =
    match hasErrors with
    | true -> []
    | false ->
      typed.GetAllUsesOfAllSymbolsInFile()
      |> Seq.choose (fun symbolUse ->
        match symbolUse.IsFromOpenStatement with
        | true -> None
        | false ->
          Some
            { SymbolFullName = symbolUse.Symbol.FullName
              Use = (if symbolUse.IsFromDefinition then WireDefinition else WireUsage)
              FilePath = filePath
              Line = symbolUse.Range.StartLine })
      |> Seq.toList
  diagnostics, symbols

/// Evaluate a config.fsx expression in this session and hand back the DirectoryConfig it builds. The script's
/// `DirectoryConfig.empty` / `LoadStrategy` come from this host's own assembly (ConfigDsl.fs), so the value is the
/// real thing, and the daemon never runs the user's script.
let evalConfig (session: FsiEvaluationSession) (content: string) : ConfigOutcome =
  try
    session.EvalInteractionNonThrowing "open SageFs;;" |> ignore
    let result, diagnostics = session.EvalExpressionNonThrowing content
    let errors = diagnostics |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
    match errors.Length > 0 with
    | true -> ConfigRejected(ConfigDoesNotCompile(errors |> Array.map toWire |> Array.toList))
    | false ->
      match result with
      | Choice1Of2(Some value) ->
        match value.ReflectionValue with
        | :? DirectoryConfig as config -> ConfigEvaluated config
        | null -> ConfigRejected(ConfigWrongType "null")
        | other -> ConfigRejected(ConfigWrongType(other.GetType().Name))
      | Choice1Of2 None -> ConfigRejected ConfigNoValue
      | Choice2Of2 ex -> ConfigRejected(ConfigThrew ex.Message)
  with ex -> ConfigRejected(ConfigThrew ex.Message)

/// The completion candidates at the caret, kept as FCS items so a description can be produced on demand.
let candidates (session: FsiEvaluationSession) (text: string) (caret: int) : DeclarationListItem[] =
  let partialName = QuickParse.GetPartialLongNameEx(text, caret - 1)
  let parse, typed, _ = session.ParseAndCheckInteraction text
  (typed.GetDeclarationListInfo(Some parse, 1, text, partialName)).Items

/// FCS's own name for a glyph, read from the type (no hand-written strings): SageFs maps it back by name.
let private glyphName (glyph: FSharpGlyph) : string =
  let case, _ = FSharpValue.GetUnionFields(box glyph, typeof<FSharpGlyph>)
  case.Name

let toCompletion (item: DeclarationListItem) : WireCompletion =
  { DisplayText = item.NameInList
    ReplacementText = item.NameInCode
    Glyph = glyphName item.Glyph }

/// The plain text of a tooltip's main description. THE ONE API-drift seam of this file: the property's type changed
/// between SDKs (a TaggedText[] on SDK 10, a RichText on SDK 11), and both expose a `Text` — so it is read through
/// that, by shape, and the same source compiles against every SDK's FSharp.Compiler.Service.
let private textOf (description: obj) : string =
  let textProperty (value: obj) =
    match value.GetType().GetProperty "Text" with
    | null -> ""
    | property ->
      match property.GetValue value with
      | :? string as text -> text
      | _ -> ""
  match description with
  | null -> ""
  | :? System.Collections.IEnumerable as parts -> parts |> Seq.cast<obj> |> Seq.map textProperty |> String.concat ""
  | single -> textProperty single

/// The first description group's main text, as plain text.
let describe (item: DeclarationListItem) : string =
  item.Description
  |> (fun (ToolTipText elements) -> elements)
  |> Seq.collect (function
    | ToolTipElement.Group group -> group
    | _ -> [])
  |> Seq.tryHead
  |> Option.map (fun element -> textOf (box element.MainDescription))
  |> Option.defaultValue ""
