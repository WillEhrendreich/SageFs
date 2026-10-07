/// Write an eval back to the file it came from: the DECISION, pure. Given the eval text, the file
/// path and block start line the call supplied and the file's current source, answer either with
/// the exact splice target (the declaration the eval redefines, its line range, the replacement
/// text) or with a `NotPersisted` reason that says what to do — specific and actionable, never a
/// silent skip. No IO, no locks, no daemon: the door that journals and renames the file is
/// `SageFs/EvalWriteBackWire.fs`, which calls this and reuses the tweak door's crash-safe path so
/// `undo` keeps covering these writes.
module SageFs.Features.Tweak.EvalWriteBack

open System
open SageFs.Features.ReloadPlanning

/// The verdict a caller sees on the tool result — both outcomes in one type, so a caller never
/// has to correlate two channels. `NotPersisted` is what `decide` refuses with; `Written` is only
/// ever built by the writer, after the journal record and the rename have both landed.
[<RequireQualifiedAccess>]
type WriteBack =
  | Written of file: string * line: int
  | NotPersisted of reason: string

[<RequireQualifiedAccess>]
module WriteBack =
  /// The exact line the tool result carries for this verdict. Two shapes, no third: either the
  /// eval landed in a file and says which file and line, or it did not and says why.
  let describe (writeBack: WriteBack) : string =
    match writeBack with
    | WriteBack.Written (file, line) -> sprintf "written back to %s:%d" file line
    | WriteBack.NotPersisted reason -> sprintf "not persisted: %s" reason

/// Where the eval replaces in the file: the declaration the eval redefines (its `Name`, `Text`,
/// `StartLine`/`EndLine` in the file's current source), the file's module path (so the writer can
/// build the same address the tweak engine resolves), and the replacement text — the eval exactly
/// as submitted, minus the interactive terminator.
type Splice =
  { Declaration: SourceDecl
    ModulePath: string list
    Replacement: string }

/// Why the eval is not persisted when the call named no file. Shared by `decide` and by the
/// writer, so the most common case there is can never drift between the two.
let noFilePathReason =
  "the call supplied no file_path, so there is no file this eval could be written back to — pass file_path, the source file the code came from"

/// Why the eval is not persisted when the call named a file but no line.
let noBlockStartLineReason =
  "the call supplied no block_start_line, so the eval cannot be located in the file — pass block_start_line, the 1-based line where the block starts"

/// The eval as the file would receive it: `;;` is the interactive terminator, never part of a
/// declaration, and trailing whitespace is not what a splice writes back.
let private submission (code: string) : string =
  let trimmed = if isNull code then "" else code.Trim()
  match trimmed.EndsWith(";;", StringComparison.Ordinal) with
  | true -> trimmed.Substring(0, trimmed.Length - 2).TrimEnd()
  | false -> trimmed

/// The char offset each line starts at, 1-based, plus one slot holding the source length so "the
/// end of line n" is always `starts.[n]`. The same rule the tweak engine's offsets use, so CRLF,
/// CR and LF all keep their bytes: nothing is ever re-joined from split lines.
let private lineStarts (source: string) : int array =
  let offsets = ResizeArray [ 0 ]
  let mutable i = 0
  while i < source.Length do
    match source.[i] with
    | '\r' when i + 1 < source.Length && source.[i + 1] = '\n' ->
      offsets.Add(i + 2)
      i <- i + 2
    | '\r'
    | '\n' ->
      offsets.Add(i + 1)
      i <- i + 1
    | _ -> i <- i + 1
  offsets.Add(source.Length)
  offsets.ToArray()

/// Replace the splice's declaration in `source` with its replacement text: the declaration's own
/// text, found UNIQUELY inside its line range, is what moves — bytes before it (an indentation, a
/// sibling declaration sharing the line) and after it are untouched. Refuses rather than guesses
/// when the text is gone from that range or appears twice in it.
let applySplice (source: string) (splice: Splice) : Result<string, WriteBack> =
  let decl = splice.Declaration
  let starts = lineStarts source
  let lineCount = starts.Length - 1
  if decl.StartLine < 1 || decl.EndLine < decl.StartLine || decl.EndLine > lineCount then
    Error(
      WriteBack.NotPersisted(
        sprintf "the declaration's lines %d-%d are not in the file any more — it moved under the eval; run the eval again" decl.StartLine decl.EndLine
      )
    )
  else
    let regionStart = starts.[decl.StartLine - 1]
    let regionEnd = starts.[decl.EndLine]
    let region = source.Substring(regionStart, regionEnd - regionStart)
    let found = region.IndexOf(decl.Text, StringComparison.Ordinal)
    if found < 0 then
      Error(
        WriteBack.NotPersisted(
          sprintf "the declaration's own text is not in lines %d-%d any more — the file moved under the eval; run the eval again" decl.StartLine decl.EndLine
        )
      )
    elif region.IndexOf(decl.Text, found + 1, StringComparison.Ordinal) >= 0 then
      Error(
        WriteBack.NotPersisted(
          sprintf "the declaration's text appears twice inside lines %d-%d, so where to splice would be a guess — nothing was written" decl.StartLine decl.EndLine
        )
      )
    else
      let at = regionStart + found
      Ok(source.Substring(0, at) + splice.Replacement + source.Substring(at + decl.Text.Length))

/// The five conditions, in the order a caller fixes things in: the call named a file, the call
/// named a line, the submission is one statement that parses, the file parses and a declaration in
/// it contains that line, and the declaration's name is what the eval redefines — so the splice
/// lands over the thing the eval actually replaced. Anything else answers `NotPersisted` with the
/// specific reason, never a guess.
let decide
  (code: string)
  (filePath: string option)
  (blockStartLine: int option)
  (source: string)
  : Result<Splice, WriteBack> =
  let refuse reason = Error(WriteBack.NotPersisted reason)
  match filePath with
  | None -> refuse noFilePathReason
  | Some file ->
    match blockStartLine with
    | None -> refuse noBlockStartLineReason
    | Some blockLine ->
      let text = submission code
      match extractDecls text with
      | Error e -> refuse (sprintf "the submission cannot be written back as it stands: %s" e)
      | Ok evalFile ->
        match evalFile.Decls with
        | [] ->
          refuse "the submission declares nothing (an open, a directive, or a bare expression), so there is nothing in the file for it to replace"
        | _ :: _ :: _ as several ->
          refuse (sprintf "the submission is %d declarations; write back one declaration at a time" several.Length)
        | [ evalDecl ] ->
          match evalDecl.Kind with
          | DeclKind.StartupCode -> refuse "the submission is a bare expression; it redefines no declaration, so there is nothing in the file to splice over"
          | _ ->
            match extractDecls source with
            | Error e -> refuse (sprintf "%s cannot be written back as it stands: %s" file e)
            | Ok fileDecls ->
              match fileDecls.Decls |> List.tryFind (fun d -> blockLine >= d.StartLine && blockLine <= d.EndLine) with
              | None -> refuse (sprintf "no declaration in %s contains line %d — block_start_line may not point into this file" file blockLine)
              | Some decl ->
                match decl.Kind with
                | DeclKind.StartupCode ->
                  refuse (sprintf "the declaration at line %d is top-level code run at startup, which no eval redefines — edit the file directly" decl.StartLine)
                | _ when evalDecl.Name <> decl.Name ->
                  refuse (
                    sprintf
                      "the declaration at line %d is %s, but the submission redefines %s — write-back replaces only what the eval redefined"
                      decl.StartLine
                      decl.Name
                      evalDecl.Name
                  )
                | _ ->
                  // The names match: this IS the thing the eval redefined. What is left is what
                  // the undo journal can ADDRESS — a value or function binding, whose right-hand
                  // side a TweakAddress resolves. A type or a module abbreviation would land in
                  // the file with no way back, and "undo keeps covering these writes" is the rule
                  // the write path is allowed under, so those are refused by name, not guessed.
                  let addressable (d: SourceDecl) =
                    match d.Kind with
                    | DeclKind.ValueDecl
                    | DeclKind.FunctionDecl
                    | DeclKind.MutableValueDecl
                    | DeclKind.EntryPointDecl -> true
                    | DeclKind.ValueClosures
                    | DeclKind.TypeDecl
                    | DeclKind.NestedModuleDecl
                    | DeclKind.StartupCode -> false
                  match addressable evalDecl, addressable decl with
                  | true, true -> Ok { Declaration = decl; ModulePath = fileDecls.ModulePath; Replacement = text }
                  | false, _ ->
                    refuse (
                      sprintf
                        "the submission redefines %s, which the undo journal cannot address — nothing is written back; edit the file directly"
                        evalDecl.Name
                    )
                  | _, false ->
                    refuse (
                      sprintf
                        "the declaration at line %d is %s, which the undo journal cannot address — nothing is written back; edit the file directly"
                        decl.StartLine
                        decl.Name
                    )
