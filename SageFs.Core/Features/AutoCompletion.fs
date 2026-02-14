module SageFs.Features.AutoCompletion

open FSharp.Compiler.EditorServices
open FSharp.Compiler.Interactive
open FSharp.Compiler.Text
open FuzzySharp

open FSharpPlus

[<RequireQualifiedAccess>]
type CompletionKind =
  | Class
  | Constant
  | Delegate
  | Enum
  | EnumMember
  | Event
  | Exception
  | Field
  | Interface
  | Method
  | OverriddenMethod
  | Module
  | Namespace
  | Property
  | Struct
  | Typedef
  | Type
  | Union
  | Variable
  | ExtensionMethod
  | TypeParameter
  | Keyword
  | Folder
  | File

module CompletionKind =
  let label = function
    | CompletionKind.Class -> "Class"
    | CompletionKind.Constant -> "Constant"
    | CompletionKind.Delegate -> "Delegate"
    | CompletionKind.Enum -> "Enum"
    | CompletionKind.EnumMember -> "EnumMember"
    | CompletionKind.Event -> "Event"
    | CompletionKind.Exception -> "Exception"
    | CompletionKind.Field -> "Field"
    | CompletionKind.Interface -> "Interface"
    | CompletionKind.Method -> "Method"
    | CompletionKind.OverriddenMethod -> "OverriddenMethod"
    | CompletionKind.Module -> "Module"
    | CompletionKind.Namespace -> "Namespace"
    | CompletionKind.Property -> "Property"
    | CompletionKind.Struct -> "Struct"
    | CompletionKind.Typedef -> "Typedef"
    | CompletionKind.Type -> "Type"
    | CompletionKind.Union -> "Union"
    | CompletionKind.Variable -> "Variable"
    | CompletionKind.ExtensionMethod -> "ExtensionMethod"
    | CompletionKind.TypeParameter -> "TypeParameter"
    | CompletionKind.Keyword -> "Keyword"
    | CompletionKind.Folder -> "Folder"
    | CompletionKind.File -> "File"

  let ofGlyph (glyph: FSharpGlyph) : CompletionKind =
    match glyph with
    | FSharpGlyph.Class -> CompletionKind.Class
    | FSharpGlyph.Constant -> CompletionKind.Constant
    | FSharpGlyph.Delegate -> CompletionKind.Delegate
    | FSharpGlyph.Enum -> CompletionKind.Enum
    | FSharpGlyph.EnumMember -> CompletionKind.EnumMember
    | FSharpGlyph.Event -> CompletionKind.Event
    | FSharpGlyph.Exception -> CompletionKind.Exception
    | FSharpGlyph.Field -> CompletionKind.Field
    | FSharpGlyph.Interface -> CompletionKind.Interface
    | FSharpGlyph.Method -> CompletionKind.Method
    | FSharpGlyph.OverridenMethod -> CompletionKind.OverriddenMethod
    | FSharpGlyph.Module -> CompletionKind.Module
    | FSharpGlyph.NameSpace -> CompletionKind.Namespace
    | FSharpGlyph.Property -> CompletionKind.Property
    | FSharpGlyph.Struct -> CompletionKind.Struct
    | FSharpGlyph.Typedef -> CompletionKind.Typedef
    | FSharpGlyph.Type -> CompletionKind.Type
    | FSharpGlyph.Union -> CompletionKind.Union
    | FSharpGlyph.Variable -> CompletionKind.Variable
    | FSharpGlyph.ExtensionMethod -> CompletionKind.ExtensionMethod
    | FSharpGlyph.Error -> CompletionKind.Type
    | FSharpGlyph.TypeParameter -> CompletionKind.TypeParameter

type CompletionItem = {
  DisplayText: string
  ReplacementText: string
  Kind: CompletionKind
  GetDescription: (unit -> TaggedText array) option
}

let scoreCandidate (enteredWord: string) (candidate: string) =
  seq {
    if candidate.StartsWith enteredWord then
      yield 200

    yield Fuzz.Ratio(enteredWord, candidate)
    yield int (100.0 / (float candidate.Length + 1.0))
  }
  |> Seq.sum

module FsCompletions =
  let mkDeclInfo (fsiSession: Shell.FsiEvaluationSession) text caret =
    let l = QuickParse.GetPartialLongNameEx(text, caret - 1)
    let parse, typed, _ = fsiSession.ParseAndCheckInteraction text
    let declList = typed.GetDeclarationListInfo(Some parse, 1, text, l)
    declList.Items

  let getFsCompletions (fsiSession: Shell.FsiEvaluationSession) text caret word =
    let declItems = mkDeclInfo fsiSession text caret

    let mkCompletionItem (declInfo: DeclarationListItem) =
      let getDocs () =
        declInfo.Description
        |> (fun (ToolTipText elems) -> elems)
        |> Seq.collect (function
          | ToolTipElement.Group e -> e
          | _ -> [])
        |> Seq.tryHead
        |>> _.MainDescription
        |> Option.defaultValue [||]

      {
        CompletionItem.DisplayText = declInfo.NameInList
        ReplacementText = declInfo.NameInCode
        Kind = CompletionKind.ofGlyph declInfo.Glyph
        GetDescription = Some getDocs
      }

    declItems |> Seq.map mkCompletionItem

module DirectiveCompletions =
  let directives =
    Set [ "reference"; "include"; "load"; "time"; "help"; "clear"; "quit"; "open"; "exec"; "e"; "pwd"; "cd" ]

  open System.IO

  let mkReplacement wordToReplace entry =
    let maxVal = min (String.length entry) (String.length wordToReplace)
    let mutable i = 0

    while i < maxVal && wordToReplace[i] = entry[i] do
      i <- i + 1

    String.drop i entry

  let commandCompletions (text: string) carret (wordToReplace: string) =
    if not <| String.contains ' ' text then
      directives
      |> Seq.map (fun keyword -> {
        CompletionItem.DisplayText = keyword
        ReplacementText = mkReplacement wordToReplace keyword
        Kind = CompletionKind.Keyword
        GetDescription = None
      })
    else
      let currentWord =
        let textList = String.toList text

        let firstPart =
          textList
          |> Seq.rev
          |> Seq.skip (text.Length - carret)
          |> Seq.takeWhile (fun c -> c <> ' ')

        let lastPart = textList |> Seq.skip carret |> Seq.takeWhile (fun c -> c <> ' ')
        Seq.append (Seq.rev firstPart) lastPart |> String.ofSeq

      let isDirectory =
        text |> String.contains Path.DirectorySeparatorChar
        || text.StartsWith "#open"
        || text.StartsWith ":open"

      if isDirectory then
        let currentDir =
          [| Directory.GetCurrentDirectory(); Path.GetDirectoryName currentWord |]
          |> Path.Combine

        if Directory.Exists currentDir then
          Directory.EnumerateFiles currentDir
          |> Seq.append (Directory.EnumerateDirectories currentDir |> Seq.map (fun d -> d + "/"))
          |> Seq.map (fun e -> Path.GetRelativePath(currentDir, e))
          //|> Seq.map (mkReplacement currentWord)
          |> Seq.map (fun fsEntry -> {
            CompletionItem.DisplayText = fsEntry
            ReplacementText = fsEntry
            Kind = if fsEntry.EndsWith "/" then CompletionKind.Folder else CompletionKind.File
            GetDescription = None
          })
        else
          []
      else
        []

let getCompletions session text carret word =
  let sortCompletions =
    Seq.sortByDescending (fun c -> scoreCandidate word c.ReplacementText)
    >> Seq.truncate 50
    >> Seq.toList

  match String.tryHead text with
  | Some ':'
  | Some '#' -> DirectiveCompletions.commandCompletions text carret word |> sortCompletions
  | Some _ -> FsCompletions.getFsCompletions session text carret word |> sortCompletions
  | None -> []
