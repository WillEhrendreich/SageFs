module SageFs.Features.AutoCompletion

open SageFs
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Interactive
open FSharp.Compiler.Text
open FuzzySharp

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
  /// The kind as the ladder's shared contract (LadderContract.fs) names it. Exhaustive on purpose: a new
  /// `CompletionKind` breaks the build here until someone decides what the extension and the daemon call it
  /// and which side of the ladder it sits on.
  let toLadder (kind: CompletionKind) : LadderKind =
    match kind with
    | CompletionKind.Class -> LadderKind.Class
    | CompletionKind.Constant -> LadderKind.Constant
    | CompletionKind.Delegate -> LadderKind.Delegate
    | CompletionKind.Enum -> LadderKind.Enum
    | CompletionKind.EnumMember -> LadderKind.EnumMember
    | CompletionKind.Event -> LadderKind.Event
    | CompletionKind.Exception -> LadderKind.Exception
    | CompletionKind.Field -> LadderKind.Field
    | CompletionKind.Interface -> LadderKind.Interface
    | CompletionKind.Method -> LadderKind.Method
    | CompletionKind.OverriddenMethod -> LadderKind.OverriddenMethod
    | CompletionKind.Module -> LadderKind.Module
    | CompletionKind.Namespace -> LadderKind.Namespace
    | CompletionKind.Property -> LadderKind.Property
    | CompletionKind.Struct -> LadderKind.Struct
    | CompletionKind.Typedef -> LadderKind.Typedef
    | CompletionKind.Type -> LadderKind.Type
    | CompletionKind.Union -> LadderKind.Union
    | CompletionKind.Variable -> LadderKind.Variable
    | CompletionKind.ExtensionMethod -> LadderKind.ExtensionMethod
    | CompletionKind.TypeParameter -> LadderKind.TypeParameter
    | CompletionKind.Keyword -> LadderKind.Keyword
    | CompletionKind.Folder -> LadderKind.Folder
    | CompletionKind.File -> LadderKind.File

  /// The wire word. One home: `LadderKind.label`, which the extension compiles too.
  let label (kind: CompletionKind) : string = kind |> toLadder |> LadderKind.label

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

/// How many candidates a caller wants back. An editor wants the few best; the symbol ladder
/// (`list_members`) wants every member there is.
[<RequireQualifiedAccess>]
type CompletionLimit =
  | UpTo of int
  | EveryMember

/// What a reply knows about the size of the set it drew from. `Exact` is the true count. `AtLeast`
/// is a floor: the reply stopped at a bound and does not vouch for what lies past it.
[<RequireQualifiedAccess>]
type CompletionTotal =
  | Exact of int
  | AtLeast of int

/// Why no list could be produced. An empty list means "nothing is there"; each of these means "nobody
/// could look", and a caller must be able to tell the two apart (a static class with members and a
/// session that was not ready both used to read as `0 total`).
[<RequireQualifiedAccess>]
type CompletionUnavailable =
  /// The session is warming up, rebuilding or stopped, so its compiler could not be asked.
  | SessionNotActive
  /// FCS recovered from an error while resolving the name and handed back nothing.
  | CompilerError of message: string
  /// The isolated FSI host is gone.
  | HostLost of reason: string
  /// The compiler threw while resolving the name.
  | CheckThrew of message: string
  /// No session could be chosen to ask.
  | NoSession of why: string
  /// The daemon could not reach the session's worker.
  | WorkerUnreachable of why: string
  /// This front end has no completion source.
  | NotOffered of why: string

/// One reply: the candidates with the size they were drawn from, or the reason there are none to give.
/// `'item` is a `CompletionItem` inside a process and a `CompletionEntry` on the worker wire.
[<RequireQualifiedAccess>]
type CompletionAnswer<'item> =
  | Answered of items: 'item list * total: CompletionTotal
  | Unavailable of CompletionUnavailable

/// A candidate as the worker sends it: the text and the real kind FCS gave it, without the lazy
/// description closure a `CompletionItem` carries (a closure cannot cross the wire).
type CompletionEntry = {
  DisplayText: string
  ReplacementText: string
  Kind: CompletionKind
}

module CompletionUnavailable =
  /// The one place the closed set becomes text: what is true, and what to do about it.
  let describe (reason: CompletionUnavailable) : string =
    match reason with
    | CompletionUnavailable.SessionNotActive ->
      "the session is not active (warming up, rebuilding or stopped); wait for it to be Ready and ask again"
    | CompletionUnavailable.CompilerError message ->
      sprintf "the compiler reported an error resolving the name (%s); check the name is spelled right and its namespace is open or loaded" message
    | CompletionUnavailable.HostLost reason ->
      sprintf "the session's F# host is gone (%s); check get_session_status, then hard reset the session" reason
    | CompletionUnavailable.CheckThrew message ->
      sprintf "the compiler threw while resolving the name (%s); ask again, and report it if it repeats" message
    | CompletionUnavailable.NoSession why -> why
    | CompletionUnavailable.WorkerUnreachable why -> why
    | CompletionUnavailable.NotOffered why -> why

  /// A short stable word for each reason, for wire payloads and logs.
  let label (reason: CompletionUnavailable) : string =
    match reason with
    | CompletionUnavailable.SessionNotActive -> "SessionNotActive"
    | CompletionUnavailable.CompilerError _ -> "CompilerError"
    | CompletionUnavailable.HostLost _ -> "HostLost"
    | CompletionUnavailable.CheckThrew _ -> "CheckThrew"
    | CompletionUnavailable.NoSession _ -> "NoSession"
    | CompletionUnavailable.WorkerUnreachable _ -> "WorkerUnreachable"
    | CompletionUnavailable.NotOffered _ -> "NotOffered"

module CompletionTotal =
  /// The wire word for each case, matching `CompletionTotal.count`.
  let label (total: CompletionTotal) : string =
    match total with
    | CompletionTotal.Exact _ -> LadderSizeKind.label LadderSizeKind.Exact
    | CompletionTotal.AtLeast _ -> LadderSizeKind.label LadderSizeKind.AtLeast

  let count (total: CompletionTotal) : int =
    match total with
    | CompletionTotal.Exact n
    | CompletionTotal.AtLeast n -> n

  /// "212" for a count, "at least 2000" for a floor: the phrase that follows "of" in "showing 50 of ...".
  let describe (total: CompletionTotal) : string =
    match total with
    | CompletionTotal.Exact n -> string n
    | CompletionTotal.AtLeast n -> sprintf "at least %d" n

module CompletionEntry =
  let ofItem (item: CompletionItem) : CompletionEntry =
    { DisplayText = item.DisplayText; ReplacementText = item.ReplacementText; Kind = item.Kind }

  /// An entry back as an item. The description is gone: only the process that held the compiler can make one.
  let toItem (entry: CompletionEntry) : CompletionItem =
    { DisplayText = entry.DisplayText
      ReplacementText = entry.ReplacementText
      Kind = entry.Kind
      GetDescription = None }

module CompletionAnswer =
  let map (f: 'a -> 'b) (answer: CompletionAnswer<'a>) : CompletionAnswer<'b> =
    match answer with
    | CompletionAnswer.Answered(items, total) -> CompletionAnswer.Answered(List.map f items, total)
    | CompletionAnswer.Unavailable reason -> CompletionAnswer.Unavailable reason

  /// The items of an answer, or none when there was no answer. For callers with nowhere to say why (a dropdown).
  let itemsOrEmpty (answer: CompletionAnswer<'a>) : 'a list =
    match answer with
    | CompletionAnswer.Answered(items, _) -> items
    | CompletionAnswer.Unavailable _ -> []

/// The most candidates one reply carries however many were asked for. A set past it is cut here and the
/// reply says `AtLeast`, so no caller can read the cut as the size of the set. The largest real sets are a
/// few hundred names (`System`, `Microsoft.FSharp.Core.Operators`); this leaves room for them and bounds the
/// wire for the partial-identifier case, where FCS lists everything in scope.
[<Literal>]
let WireCeiling = 2000

/// What an editor's popup or a tree view asks for: the few best candidates. The reply states the size of the
/// set they came from, so an editor can say "50 of 212" instead of presenting 50 as everything.
[<Literal>]
let EditorCandidates = 50

/// Cut ranked candidates to what was asked for, and say how large the set was. `UpTo` ranks all of
/// them already, so its total is the true count. `EveryMember` stops at `WireCeiling`.
let select (limit: CompletionLimit) (ranked: 'item list) : CompletionAnswer<'item> =
  let known = List.length ranked
  match limit with
  | CompletionLimit.UpTo n ->
    CompletionAnswer.Answered(List.truncate (max 0 n) ranked, CompletionTotal.Exact known)
  | CompletionLimit.EveryMember ->
    match known > WireCeiling with
    | true -> CompletionAnswer.Answered(List.truncate WireCeiling ranked, CompletionTotal.AtLeast WireCeiling)
    | false -> CompletionAnswer.Answered(ranked, CompletionTotal.Exact known)

let scoreCandidate (enteredWord: string) (candidate: string) =
  seq {
    match candidate.StartsWith(enteredWord, System.StringComparison.Ordinal) with
    | true -> yield 200
    | false -> ()

    yield Fuzz.Ratio(enteredWord, candidate)
    yield int (100.0 / (float candidate.Length + 1.0))
  }
  |> Seq.sum

module FsCompletions =
  let mkDeclInfo (fsiSession: Shell.FsiEvaluationSession) text caret =
    let l = QuickParse.GetPartialLongNameEx(text, caret - 1)
    let parse, typed, _ = fsiSession.ParseAndCheckInteraction text
    typed.GetDeclarationListInfo(Some parse, 1, text, l)

  /// What FCS says when it recovers from an error: the list holds one `<Note>` item, flagged `IsError`.
  /// Read that as "could not list", never as a member named `<Note>`.
  let compilerErrorMessage = "FCS recovered from an error in the declaration list"

  let candidatesOf (declItems: DeclarationListItem[]) : CompletionItem[] =
    let mkCompletionItem (declInfo: DeclarationListItem) =
      let getDocs () =
        declInfo.Description
        |> (fun (ToolTipText elems) -> elems)
        |> Seq.collect (function
          | ToolTipElement.Group e -> e
          | _ -> [])
        |> Seq.tryHead
        |> Option.map _.MainDescription
        |> Option.defaultValue [||]

      {
        CompletionItem.DisplayText = declInfo.NameInList
        ReplacementText = declInfo.NameInCode
        Kind = CompletionKind.ofGlyph declInfo.Glyph
        GetDescription = Some getDocs
      }

    declItems |> Array.map mkCompletionItem

  let getFsCompletions (fsiSession: Shell.FsiEvaluationSession) text caret word : Result<CompletionItem seq, CompletionUnavailable> =
    try
      let declList = mkDeclInfo fsiSession text caret
      match declList.IsError with
      | true -> Result.Error(CompletionUnavailable.CompilerError compilerErrorMessage)
      | false -> Result.Ok(Seq.ofArray (candidatesOf declList.Items))
    with ex ->
      Result.Error(CompletionUnavailable.CheckThrew ex.Message)

module DirectiveCompletions =
  let directives =
    Set [ "reference"; "include"; "load"; "time"; "help"; "clear"; "quit"; "open"; "exec"; "e"; "pwd"; "cd" ]

  open System.IO

  let mkReplacement wordToReplace entry =
    let maxVal = min (String.length entry) (String.length wordToReplace)
    let mutable i = 0

    while i < maxVal && wordToReplace[i] = entry[i] do
      i <- i + 1

    entry.Substring(i)

  let commandCompletions (text: string) carret (wordToReplace: string) =
    match text.Contains(' ') with
    | false ->
      directives
      |> Seq.map (fun keyword -> {
        CompletionItem.DisplayText = keyword
        ReplacementText = mkReplacement wordToReplace keyword
        Kind = CompletionKind.Keyword
        GetDescription = None
      })
    | true ->
      let currentWord =
        let textList = text |> Seq.toList

        let firstPart =
          textList
          |> Seq.rev
          |> Seq.skip (text.Length - carret)
          |> Seq.takeWhile (fun c -> c <> ' ')

        let lastPart = textList |> Seq.skip carret |> Seq.takeWhile (fun c -> c <> ' ')
        Seq.append (Seq.rev firstPart) lastPart |> Seq.toArray |> System.String

      let isDirectory =
        text.Contains(Path.DirectorySeparatorChar)
        || text.StartsWith("#open", System.StringComparison.Ordinal)
        || text.StartsWith(":open", System.StringComparison.Ordinal)

      match isDirectory with
      | true ->
        let currentDir =
          [| Directory.GetCurrentDirectory(); Path.GetDirectoryName currentWord |]
          |> Path.Combine

        match Directory.Exists currentDir with
        | true ->
          Directory.EnumerateFiles currentDir
          |> Seq.append (Directory.EnumerateDirectories currentDir |> Seq.map (fun d -> d + "/"))
          |> Seq.map (fun e -> Path.GetRelativePath(currentDir, e))
          //|> Seq.map (mkReplacement currentWord)
          |> Seq.map (fun fsEntry -> {
            CompletionItem.DisplayText = fsEntry
            ReplacementText = fsEntry
            Kind = match fsEntry.EndsWith("/", System.StringComparison.Ordinal) with | true -> CompletionKind.Folder | false -> CompletionKind.File
            GetDescription = None
          })
        | false ->
          []
      | false ->
        []

/// Rank and truncate candidates, dispatching `:`/`#` input to the directive completions and everything else to
/// `fsCompletions` (text -> caret -> word -> candidates). The F# candidates come from wherever the session lives:
/// in this process (`getCompletions`) or an isolated host (RemoteFsiSession); the ranking is identical either way.
let getCompletionsWith
  (fsCompletions: string -> int -> string -> Result<CompletionItem seq, CompletionUnavailable>)
  (limit: CompletionLimit)
  text
  carret
  word
  : CompletionAnswer<CompletionItem> =
  let ranked (candidates: CompletionItem seq) : CompletionItem list =
    candidates |> Seq.sortByDescending (fun c -> scoreCandidate word c.ReplacementText) |> Seq.toList

  match text |> Seq.tryHead with
  | Some ':'
  | Some '#' -> DirectiveCompletions.commandCompletions text carret word |> ranked |> select limit
  | Some _ ->
    match fsCompletions text carret word with
    | Result.Ok candidates -> candidates |> ranked |> select limit
    | Result.Error reason -> CompletionAnswer.Unavailable reason
  | None -> CompletionAnswer.Answered([], CompletionTotal.Exact 0)

let getCompletions session limit text carret word =
  getCompletionsWith (FsCompletions.getFsCompletions session) limit text carret word
