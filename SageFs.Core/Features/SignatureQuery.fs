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
    | LineBeforeOne of line: int
    | ColumnBeforeOne of column: int
    | LinePastEnd of line: int * count: int
    | ColumnPastEnd of column: int * length: int
    | EmptySignature

  module Refusal =
    let describe (r: Refusal) =
      match r with
      | Refusal.NoFilePath -> "signature: file_path is required. Pass the source file the caret is in."
      | Refusal.LineBeforeOne l -> sprintf "signature: line must be 1-based, got %d. Pass the declaration's first line." l
      | Refusal.ColumnBeforeOne c -> sprintf "signature: column must be 1-based, got %d. Pass the caret's column." c
      | Refusal.LinePastEnd(l, n) -> sprintf "signature: line %d is past the file's %d lines." l n
      | Refusal.ColumnPastEnd(c, n) -> sprintf "signature: column %d is past the line's %d characters." c n
      | Refusal.EmptySignature -> "signature: the compiler gave no signature for that position. Move the caret onto a declaration or member name."

  /// A caret address, exactly as the caller sent it.
  type Request =
    { File: string
      Line: int
      Column: int }

  /// What the FCS seam reports. Named fields so two strings cannot be swapped across the wire.
  type FcsAnswer =
    { Name: string
      FullName: string
      Tooltip: string
      Kind: string
      DefinitionFile: string option
      DefinitionLine: int option }

  /// The signature shown to the caller.
  type View =
    { Name: string
      FullName: string
      Signature: string
      Kind: string
      DefinitionFile: string option
      DefinitionLine: int option }

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

  /// The signature is the first line of the compiler's tooltip, trimmed. An empty first line means
  /// the compiler had nothing to say at that position, which is a refusal rather than a blank result.
  let render (a: FcsAnswer) : Result<View, Refusal> =
    let firstLine =
      a.Tooltip.Split('\n')
      |> Array.tryHead
      |> Option.defaultValue ""
      |> fun s -> s.Trim()

    if String.IsNullOrWhiteSpace firstLine then Error Refusal.EmptySignature
    else
      Ok
        { Name = a.Name
          FullName = a.FullName
          Signature = firstLine
          Kind = a.Kind
          DefinitionFile = a.DefinitionFile
          DefinitionLine = a.DefinitionLine }
