namespace SageFs.Features

open System

/// The pure decision for a signature-at-caret query. The FCS interaction, the file read and the
/// session lookup all happen at the edge; everything here is a function of a request and the answer
/// the compiler gave, so it can be replayed in a test with no session. A caret may sit one past the
/// last character of a line (an editor's end-of-line position), which the boundary test pins.
module SignatureQuery =

  /// Every way a query can be refused before or during the compiler lookup, with the rule and the
  /// next action in one place.
  [<RequireQualifiedAccess>]
  type Refusal =
    | NoFilePath
    | FileUnreadable of path: string
    | LineBeforeOne of line: int
    | ColumnBeforeOne of column: int
    | LinePastEnd of line: int * count: int
    | ColumnPastEnd of column: int * length: int
    | NoWordAtCaret
    | NoMatchingCandidate of word: string
    | EmptySignature

  module Refusal =
    let describe (r: Refusal) =
      match r with
      | Refusal.NoFilePath -> "signature: file_path is required. Pass the source file the caret is in."
      | Refusal.FileUnreadable p -> sprintf "signature: the source file '%s' could not be read. Check it exists, is a file, and is readable by this process." p
      | Refusal.LineBeforeOne l -> sprintf "signature: line must be 1-based, got %d. Pass the declaration's first line." l
      | Refusal.ColumnBeforeOne c -> sprintf "signature: column must be 1-based, got %d. Pass the caret's column." c
      | Refusal.LinePastEnd(l, n) -> sprintf "signature: line %d is past the file's %d lines." l n
      | Refusal.ColumnPastEnd(c, n) -> sprintf "signature: column %d is past the line's %d characters." c n
      | Refusal.NoWordAtCaret -> "signature: the caret is not on an identifier. Move it onto a declaration or member name."
      | Refusal.NoMatchingCandidate word -> sprintf "signature: no completion candidate matched '%s' at the caret." word
      | Refusal.EmptySignature -> "signature: the compiler gave no signature for that position. Move the caret onto a declaration or member name."

  /// A caret address, exactly as the caller sent it.
  type Request =
    { File: string
      Line: int
      Column: int }

  /// The already-extracted text of one completion candidate. Named fields so two strings cannot be
  /// swapped, and named to say what it is: the compiler's tooltip and kind label, not an FCS object.
  type CandidateText =
    { Name: string
      Tooltip: string
      Kind: string }

  /// The signature shown to the caller. The compiler's first tooltip line already carries the full inferred
  /// type, so there is no separate full name or definition address to fill from the completion path.
  type View =
    { Name: string
      Signature: string
      Kind: string }

  let plan (r: Request) : Result<Request, Refusal> =
    if String.IsNullOrWhiteSpace r.File then Error Refusal.NoFilePath
    elif r.Line < 1 then Error(Refusal.LineBeforeOne r.Line)
    elif r.Column < 1 then Error(Refusal.ColumnBeforeOne r.Column)
    else Ok r

  /// Map a 1-based (line, column) to a 0-based character offset, refusing an address outside the file.
  /// Column may be length + 1: that is the end-of-line caret an editor really produces.
  let caretOf (lines: string[]) (r: Request) : Result<int, Refusal> =
    if r.Line > lines.Length then Error(Refusal.LinePastEnd(r.Line, lines.Length))
    elif r.Column > lines.[r.Line - 1].Length + 1 then Error(Refusal.ColumnPastEnd(r.Column, lines.[r.Line - 1].Length))
    else
      let before = lines |> Array.take (r.Line - 1) |> Array.sumBy (fun l -> l.Length + 1)
      Ok(before + r.Column - 1)

  /// The identifier immediately BEFORE a 1-based editor caret. An editor caret sits between characters:
  /// column N is before character N-1 (0-based), and column length+1 is end of line. FCS reads the partial
  /// name as `GetPartialLongNameEx(text, cursorPosition - 1)`, so the word is the identifier ending at
  /// `column - 1` — an exclusive end. Both this and `caretOf` must use that convention or the word we look
  /// up and the candidates the compiler returns drift apart (proved at every column in the REPL).
  let wordAt (line: string) (column: int) : string =
    if isNull line || line.Length = 0 then ""
    else
      let idx = min (max 0 (column - 1)) line.Length
      let prefix = line.Substring(0, idx)
      let ident c = Char.IsLetterOrDigit c || c = '_'
      prefix |> Seq.rev |> Seq.takeWhile ident |> Seq.rev |> Seq.toArray |> String

  /// Which completion candidate is the word under the caret. An exact match only: a prefix like `doub`
  /// must not silently resolve to `double`. The named reason travels with the failure; the edge never
  /// reconstructs it from a bare `None`.
  let pickIndex (word: string) (candidates: string[]) : Result<int, Refusal> =
    if String.IsNullOrEmpty word then Error Refusal.NoWordAtCaret
    else
      match candidates |> Array.tryFindIndex (fun c -> c = word) with
      | Some index -> Ok index
      | None -> Error(Refusal.NoMatchingCandidate word)

  /// The signature is the first line of the compiler's tooltip, trimmed. An empty first line means
  /// the compiler had nothing to say at that position, which is a refusal rather than a blank result.
  let render (a: CandidateText) : Result<View, Refusal> =
    let firstLine =
      a.Tooltip.Split('\n')
      |> Array.tryHead
      |> Option.defaultValue ""
      |> fun s -> s.Trim()

    if String.IsNullOrWhiteSpace firstLine then Error Refusal.EmptySignature
    else
      Ok
        { Name = a.Name
          Signature = firstLine
          Kind = a.Kind }
