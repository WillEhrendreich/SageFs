namespace SageFs

// Compiled before Settings.fs because a setting carries expressions (a watch tab's rows), and a setting can only hold what is legal.
// It needs only the F# parser, so it has no dependency on the rest of the tweak code.

open Fantomas.FCS.Syntax
open Fantomas.FCS.Text

/// Why a piece of text is not a watch expression.
[<RequireQualifiedAccess>]
type ExpressionError =
  | Empty
  /// A row is one line, so a stored list can be one expression to a line.
  | SeveralLines
  | DoesNotParse of reason: string

/// One expression a watch tab keeps: text the F# parser accepted as a single expression on a single line. The constructor is
/// `WatchExpression.parse`, so an expression that does not parse cannot be stored, listed or sent to a session.
type WatchExpression = private WatchExpression of string

/// What evaluating an expression needs, read off the syntax alone.
[<RequireQualifiedAccess>]
type ExpressionReach =
  /// Only names and field access, as in `player.Lives`: the value can be read from what the session's walk already holds, and none
  /// of the program's code has to run.
  | NamesOnly of path: string list
  /// Anything else (a call, an operator, a lambda, a literal that has to be built): the session has to evaluate it, which runs code.
  | RunsCode of why: string

[<RequireQualifiedAccess>]
module ExpressionError =
  let describe (error: ExpressionError) : string =
    match error with
    | ExpressionError.Empty -> "an expression cannot be empty"
    | ExpressionError.SeveralLines -> "an expression is one line"
    | ExpressionError.DoesNotParse reason -> sprintf "this does not parse as an expression (%s)" reason

[<RequireQualifiedAccess>]
module WatchExpression =

  let text (WatchExpression expression) : string = expression

  /// The parser's message cut to its first line: the rest repeats compiler flags that mean nothing in a watch row.
  let private firstLine (message: string) : string =
    (message.Split('\n') |> Array.head).Trim()

  /// The body of `let _ = <text>` when the parser takes the whole text as that one expression.
  let private bodyOf (text: string) : Result<SynExpr, ExpressionError> =
    // The parser reports bad syntax as diagnostics. If it ever threw on some text, that would be a defect in the parser to see, so it is
    // not caught here.
    let source = sprintf "let _watched =\n  %s\n" text
    let input, diagnostics = Fantomas.FCS.Parse.parseFile false (SourceText.ofString source) []
    match diagnostics |> List.tryFind (fun d -> d.Severity.IsError) with
    | Some error -> Error (ExpressionError.DoesNotParse (firstLine error.Message))
    | None ->
      match input with
      | ParsedInput.ImplFile(ParsedImplFileInput(contents = [ SynModuleOrNamespace(decls = [ SynModuleDecl.Let(bindings = [ SynBinding(expr = body) ]) ]) ])) ->
        Ok body
      | ParsedInput.ImplFile _
      | ParsedInput.SigFile _ -> Error (ExpressionError.DoesNotParse "it is not a single expression")

  /// The only way to make a `WatchExpression`.
  let parse (raw: string) : Result<WatchExpression, ExpressionError> =
    let trimmed = (raw |> Option.ofObj |> Option.defaultValue "").Trim()
    match trimmed with
    | "" -> Error ExpressionError.Empty
    | multi when multi.Contains '\n' || multi.Contains '\r' -> Error ExpressionError.SeveralLines
    | single -> bodyOf single |> Result.map (fun _ -> WatchExpression single)

  let rec private pathOf (expr: SynExpr) : string list option =
    match expr with
    | SynExpr.Ident id -> Some [ id.idText ]
    | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) -> Some (ids |> List.map _.idText)
    | SynExpr.Paren(expr = inner) -> pathOf inner
    | SynExpr.DotGet(expr = target; longDotId = SynLongIdent(id = ids)) ->
      pathOf target |> Option.map (fun head -> head @ (ids |> List.map _.idText))
    | _ -> None

  let private whyItRuns (expr: SynExpr) : string =
    match expr with
    // `a * b` parses as an application of the operator's application to `a`, so the operator is one level down
    | SynExpr.App(isInfix = true)
    | SynExpr.App(funcExpr = SynExpr.App(isInfix = true)) -> "it uses an operator"
    | SynExpr.App _ -> "it calls a function"
    | SynExpr.Lambda _ -> "it is a function"
    | SynExpr.Const _ -> "it is a literal the session has to build"
    | _ -> "it is more than names and fields"

  /// What evaluating the expression needs. An expression is only ever built by `parse`, so the text parses; a text that somehow does not
  /// is held as one that runs code.
  let reach (expression: WatchExpression) : ExpressionReach =
    match bodyOf (text expression) with
    | Error _ -> ExpressionReach.RunsCode "it does not parse"
    | Ok body ->
      match pathOf body with
      | Some path -> ExpressionReach.NamesOnly path
      | None -> ExpressionReach.RunsCode (whyItRuns body)

  /// One expression to a line, the form a setting stores.
  let render (expressions: WatchExpression list) : string =
    expressions |> List.map text |> String.concat "\n"

  /// A stored list: each line that is an expression, and each that is not, with the reason. A stored list edited by hand keeps what is
  /// legal in it.
  let parseLines (stored: string) : WatchExpression list * (string * ExpressionError) list =
    let lines =
      (stored |> Option.ofObj |> Option.defaultValue "").Split('\n')
      |> Array.map _.Trim()
      |> Array.filter (fun line -> line <> "")
      |> Array.toList
    let parsed = lines |> List.map (fun line -> line, parse line)
    let good = parsed |> List.choose (fun (_, r) -> match r with Ok e -> Some e | Error _ -> None)
    let bad = parsed |> List.choose (fun (line, r) -> match r with Error why -> Some (line, why) | Ok _ -> None)
    good, bad
