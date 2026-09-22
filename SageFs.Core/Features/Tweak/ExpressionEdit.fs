/// Replace a whole expression at an address with new expression text: the
/// "type it" half of tweaking, next to `LiteralEdit`'s "drag it". The new
/// text has to parse as an expression; only the replaced range is
/// reformatted (through Fantomas, so the snippet comes out idiomatic), and
/// everything else in the file is untouched down to the byte.
module SageFs.Features.Tweak.ExpressionEdit

open SageFs.Features.Tweak.TweakAddress

[<RequireQualifiedAccess>]
type ExpressionEditError =
  | Gone of ResolveError
  /// The replacement text doesn't parse as an expression at all.
  | ParseFailed of reason: ResolveError
  /// The expression at `address` isn't the one the caller thinks it is,
  /// its content hash moved since `expectedHash` was captured. Both texts
  /// are carried so a caller can show them side by side; nothing is guessed.
  | HashMismatch of expected: string * actual: string * currentText: string

/// Format `snippet` (a single, already-parsing expression) with Fantomas by
/// wrapping it in a throwaway `let`, formatting THAT, and pulling the
/// right-hand side back out. Only the replaced range is ever touched by
/// this, nothing else in the file goes through the formatter.
///
/// Scope limit, stated plainly: this only normalizes a snippet that formats
/// onto ONE line (every tweak in the pitch, `gravity * 2.0`,
/// `if hardMode then 80 else 100`, does). A snippet Fantomas would spread
/// across several lines falls back to the caller's own text unchanged,
/// rather than guess at re-indentation against surrounding code it never
/// sees; the result still parses, because `setExpression` already checked
/// that before formatting is attempted.
let private formatSnippet (snippet: string) : string =
  let trimmed = snippet.Trim()
  try
    let wrapped = sprintf "let __tweak__ =\n    %s\n" trimmed
    let result =
      Fantomas.Core.CodeFormatter.FormatDocumentAsync(false, wrapped, Fantomas.Core.FormatConfig.Default)
      |> Async.RunSynchronously
    let formatted: string = result.Code
    let marker = "__tweak__ ="
    match formatted.IndexOf marker with
    | -1 -> trimmed
    | idx ->
      let body = formatted.Substring(idx + marker.Length).Replace("\r\n", "\n").Trim('\n')
      match body.Split('\n') |> Array.filter (fun l -> l.Trim() <> "") with
      | [| oneLine |] -> oneLine.Trim()
      | _ -> trimmed
  with _ -> trimmed

/// Replace the expression at `address` with `newExprText`. `expectedHash`,
/// when given, must match the address's CURRENT content hash, a caller
/// that resolved the address earlier and wants to make sure nothing changed
/// underneath it since passes the hash it saw then. Passing `None` skips
/// that check (used by callers, like a fresh literal-to-expression upgrade,
/// that have no earlier hash to compare against).
let setExpression
  (source: string)
  (address: TweakAddress)
  (expectedHash: string option)
  (newExprText: string)
  : Result<string, ExpressionEditError> =
  match resolve source address with
  | Error e -> Error(ExpressionEditError.Gone e)
  | Ok resolved ->
    match expectedHash with
    | Some expected when expected <> resolved.Hash ->
      Error(ExpressionEditError.HashMismatch(expected, resolved.Hash, resolved.Text))
    | _ ->
      match parseExpr newExprText with
      | Error e -> Error(ExpressionEditError.ParseFailed e)
      | Ok _ ->
        let formatted = formatSnippet newExprText
        Ok(replaceRange source resolved.Range formatted)
