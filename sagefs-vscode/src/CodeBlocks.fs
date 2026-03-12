module SageFs.Vscode.CodeBlocks

open Vscode

/// Check if the file uses ;; delimiters anywhere.
let hasSemiSemiDelimiters (doc: TextDocument) =
  let lineCount = int doc.lineCount
  let mutable found = false
  let mutable i = 0
  while not found && i < lineCount do
    if doc.lineAt(float i).text.TrimEnd().EndsWith(";;") then found <- true
    i <- i + 1
  found

/// Find code block boundaries around a given line in the document.
/// Returns (startLine, endLine).
let getBlockBounds (doc: TextDocument) (curLine: int) =
  let lineCount = int doc.lineCount
  let isBlank (n: int) = doc.lineAt(float n).text.Trim() = ""
  let endsWithSS (n: int) = doc.lineAt(float n).text.TrimEnd().EndsWith(";;")
  match hasSemiSemiDelimiters doc with
  | true ->
    let mutable s = curLine
    while s > 0 && not (endsWithSS (s - 1)) do s <- s - 1
    let mutable e = curLine
    while e < lineCount - 1 && not (endsWithSS e) do e <- e + 1
    s, e
  | false ->
    let mutable s = curLine
    while s > 0 && not (isBlank (s - 1)) do s <- s - 1
    let mutable e = curLine
    while e < lineCount - 1 && not (isBlank (e + 1)) do e <- e + 1
    s, e

/// Find the code block boundaries around the cursor.
/// Returns (text, startLine, endLine).
let getCodeBlock (editor: TextEditor) =
  let doc = editor.document
  let curLine = int editor.selection.active.line
  let startLine, endLine = getBlockBounds doc curLine
  let range = newRange startLine 0 endLine (int (doc.lineAt(float endLine).text.Length))
  doc.getTextRange range, startLine, endLine

/// Collect all code block line ranges in document order.
let getAllBlockRanges (doc: TextDocument) =
  let lineCount = int doc.lineCount
  let blocks = ResizeArray<int * int>()
  match hasSemiSemiDelimiters doc with
  | true ->
    let mutable blockStart = 0
    for i in 0 .. lineCount - 1 do
      if doc.lineAt(float i).text.TrimEnd().EndsWith(";;") then
        blocks.Add(blockStart, i)
        blockStart <- i + 1
  | false ->
    let mutable inBlock = false
    let mutable blockStart = 0
    for i in 0 .. lineCount - 1 do
      let empty = doc.lineAt(float i).text.Trim() = ""
      match empty, inBlock with
      | false, false ->
        blockStart <- i
        inBlock <- true
      | true, true ->
        blocks.Add(blockStart, i - 1)
        inBlock <- false
      | _ -> ()
    if inBlock then blocks.Add(blockStart, lineCount - 1)
  blocks.ToArray()
