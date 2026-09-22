/// A structural address for one expression inside an F# file: module path,
/// then the binding it lives in, then a path down through the expression
/// tree. The point of addressing this way instead of by line number is that
/// reformatting the file, or adding a comment above the binding, never moves
/// the target. Only editing the expression itself moves it, and that shows
/// up as a hash change, not a missing address.
///
/// Parses with the same Fantomas.FCS parser ReloadPlanning already uses
/// (Fantomas vendors the real FCS syntax tree for its own formatter, so this
/// is the same untyped AST FCS itself would hand back). No typed info here on
/// purpose: an address only needs to know where things sit in the tree, not
/// what they mean.
module SageFs.Features.Tweak.TweakAddress

open System
open System.Security.Cryptography
open System.Text
open Fantomas.FCS.Syntax
open Fantomas.FCS.Text

/// One step down through an expression's shape. Kept to the handful of
/// constructs a tuning record actually uses: field access into a record
/// literal, positions inside a tuple/list, the two sides of `if`, the two
/// sides of a binary operator, and curried call arguments. Anything else
/// (`for`, `match` with many clauses, lambdas, objects...) is left opaque,
/// conservative, same as the rest of this feature: an address never points
/// somewhere the walker can't also explain how it got there.
[<RequireQualifiedAccess>]
type PathStep =
  | RecordField of name: string
  | TupleItem of index: int
  | ListItem of index: int
  | AppArg of index: int
  | IfCond
  | IfThen
  | IfElse
  | BinOpLeft
  | BinOpRight

/// Module path, then the binding's own name, then a path through its
/// expression. `Path = []` addresses the binding's whole right-hand side.
type TweakAddress =
  { ModulePath: string list
    BindingName: string
    Path: PathStep list }

[<RequireQualifiedAccess>]
type ResolveError =
  /// The file no longer has a binding by this name (in this module path).
  | BindingRemoved of address: TweakAddress
  /// The binding is there, but the path inside its expression doesn't lead
  /// anywhere any more (the shape of the expression changed under it).
  | PathGone of address: TweakAddress
  /// The file doesn't parse at all, so no address in it can resolve.
  | ParseFailed of message: string

/// What `resolve` hands back: the exact range and text of the addressed
/// expression, plus a content hash of that text so a caller can tell "the
/// expression itself changed under you" from "everything around it moved".
type ResolvedTweak =
  { Address: TweakAddress
    Range: range
    Text: string
    Hash: string }

// ── source text plumbing ──
//
// Positions from the parser are 1-based lines / 0-based columns. Everything
// below works in absolute character OFFSETS into the exact original string
// (never a re-joined copy), so a file's real line endings are never touched
// by a replace outside the target range, CRLF stays CRLF, LF stays LF,
// mixed stays mixed, because nothing ever gets normalized and rebuilt.

let private lineStartOffsets (source: string) : int array =
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
  offsets.ToArray()

let private offsetOf (starts: int array) (line: int) (col: int) : int =
  let idx = max 0 (min (starts.Length - 1) (line - 1))
  max 0 (min (starts.[idx] + col) 0x7FFFFFFF)

/// The exact original bytes a range covers, never a copy rebuilt from
/// normalized lines.
let rangeText (source: string) (r: range) : string =
  let starts = lineStartOffsets source
  let a = max 0 (min source.Length (offsetOf starts r.StartLine r.StartColumn))
  let b = max a (min source.Length (offsetOf starts r.EndLine r.EndColumn))
  source.Substring(a, b - a)

/// Replace exactly the bytes `r` covers with `replacement`; every byte
/// outside the range is untouched, including whatever line endings the file
/// already used.
let replaceRange (source: string) (r: range) (replacement: string) : string =
  let starts = lineStartOffsets source
  let a = max 0 (min source.Length (offsetOf starts r.StartLine r.StartColumn))
  let b = max a (min source.Length (offsetOf starts r.EndLine r.EndColumn))
  source.Substring(0, a) + replacement + source.Substring(b)

/// Sha256 hex of the exact bytes of a snippet. Used only to detect "this
/// changed", never to reconstruct anything, so any stable hash would do.
let contentHash (text: string) : string =
  use sha = SHA256.Create()
  sha.ComputeHash(Encoding.UTF8.GetBytes text)
  |> Array.map (sprintf "%02x")
  |> String.concat ""

// ── walking an expression for tweakable points ──

/// Does this constant count as a tweakable leaf? Everything a knob could
/// reasonably drag: numbers, bool, char, string, and a measure-annotated
/// number. `Unit`, byte blobs and the exotic constant kinds are left alone.
let private isTweakableConst (c: SynConst) =
  match c with
  | SynConst.Unit
  | SynConst.Bytes _
  | SynConst.UInt16s _
  | SynConst.SourceIdentifier _ -> false
  | _ -> true

/// A bare, unqualified, uppercase-first identifier used as a whole
/// expression (never a function's own argument list) is the closest a
/// parse-only walker can get to "this is a DU case written as a literal",
/// like `Difficulty.Hard` shortened to `Hard` in scope, or `Easy` on its
/// own. Deliberately narrow: a QUALIFIED name (`Constants.Gravity`) is left
/// alone, because at parse level there is no way to tell a nullary case
/// from a plain value reference once it's qualified, and a false positive
/// here would let `setLiteral` try to reformat something that was never a
/// literal to begin with.
let private isDuCaseLike (name: string) =
  name.Length > 0 && Char.IsUpper name.[0]

/// Peel a curried application (`f a b c` = `App(App(App(f,a),b),c)`) down to
/// its base function and the list of arguments, in call order.
let rec private flattenApp (expr: SynExpr) : SynExpr * SynExpr list =
  match expr with
  | SynExpr.App(isInfix = false; funcExpr = inner; argExpr = arg) ->
    let baseFunc, args = flattenApp inner
    baseFunc, args @ [ arg ]
  | _ -> expr, []

let private fieldName (name: RecordFieldName) : string =
  let (SynLongIdent(id = ids)), _ = name
  ids |> List.map _.idText |> String.concat "."

/// A semicolon-separated list/array literal (`[ 1; 2; 3 ]`) doesn't parse as
/// a flat `SynExpr.ArrayOrList` with an `exprs` list, it parses as
/// `ArrayOrListComputed` wrapping a right-nested chain of `Sequential`
/// nodes, one per `;`. Flatten that chain back into the list of items it
/// spells; a single-item list (no `;` at all) is just the bare item.
let rec private flattenSequential (expr: SynExpr) : SynExpr list =
  match expr with
  | SynExpr.Sequential(expr1 = e1; expr2 = e2) -> flattenSequential e1 @ flattenSequential e2
  | other -> [ other ]

/// Every tweakable point reachable from `expr`, each paired with the
/// sub-expression it targets. Transparent through `Paren`/`Typed`, those
/// exist only for the writer's syntax, not for the tree's shape.
let rec private collect (path: PathStep list) (expr: SynExpr) : (PathStep list * SynExpr) list =
  match expr with
  | SynExpr.Paren(expr = inner)
  | SynExpr.Typed(expr = inner) -> collect path inner

  | SynExpr.Const(constant = c) when isTweakableConst c -> [ path, expr ]
  | SynExpr.Const _ -> []

  | SynExpr.Ident id when isDuCaseLike id.idText -> [ path, expr ]
  | SynExpr.Ident _ -> []

  | SynExpr.Record(recordFields = fields) ->
    fields
    |> List.collect (function
      | SynExprRecordField(fieldName = name; expr = Some fieldExpr) ->
        let fieldPath = path @ [ PathStep.RecordField(fieldName name) ]
        (fieldPath, fieldExpr) :: collect fieldPath fieldExpr
      | _ -> [])

  | SynExpr.Tuple(exprs = exprs) ->
    exprs |> List.indexed |> List.collect (fun (i, e) -> collect (path @ [ PathStep.TupleItem i ]) e)

  | SynExpr.ArrayOrList(exprs = exprs) ->
    exprs |> List.indexed |> List.collect (fun (i, e) -> collect (path @ [ PathStep.ListItem i ]) e)

  | SynExpr.ArrayOrListComputed(expr = inner) ->
    flattenSequential inner |> List.indexed |> List.collect (fun (i, e) -> collect (path @ [ PathStep.ListItem i ]) e)

  | SynExpr.IfThenElse(ifExpr = cond; thenExpr = thenExpr; elseExpr = elseExpr) ->
    collect (path @ [ PathStep.IfCond ]) cond
    @ collect (path @ [ PathStep.IfThen ]) thenExpr
    @ (elseExpr |> Option.map (collect (path @ [ PathStep.IfElse ])) |> Option.defaultValue [])

  // `a + b` is App(isInfix=true, App(_, _, opExpr, a), b) in the untyped
  // tree: the outer application carries the right operand, the inner one
  // (whose own isInfix flag we don't rely on) carries the operator and the
  // left operand.
  | SynExpr.App(isInfix = false; funcExpr = SynExpr.App(isInfix = true; argExpr = left); argExpr = right) ->
    collect (path @ [ PathStep.BinOpLeft ]) left @ collect (path @ [ PathStep.BinOpRight ]) right

  | SynExpr.App _ ->
    let _, args = flattenApp expr
    args |> List.indexed |> List.collect (fun (i, a) -> collect (path @ [ PathStep.AppArg i ]) a)

  | _ -> []

/// Every declaration in a parsed file, module path included, regardless of
/// nesting. Only `let`/`let mutable` bindings at a level produce entries,
/// this module only ever addresses bindings, never types or members.
type private ModuleBinding =
  { ModulePath: string list
    Name: string
    Expr: SynExpr }

let private identText (ids: Ident list) = ids |> List.map _.idText |> String.concat "."

let rec private patName (pat: SynPat) : string option =
  match pat with
  | SynPat.LongIdent(longDotId = SynLongIdent(id = ids)) -> Some(identText ids)
  | SynPat.Named(ident = SynIdent(ident, _)) -> Some ident.idText
  | SynPat.Typed(pat = inner)
  | SynPat.Paren(pat = inner)
  | SynPat.Attrib(pat = inner) -> patName inner
  | _ -> None

let rec private bindingsIn (modulePath: string list) (decls: SynModuleDecl list) : ModuleBinding list =
  decls
  |> List.collect (function
    | SynModuleDecl.Let(bindings = bindings) ->
      bindings
      |> List.choose (fun (SynBinding(headPat = pat; expr = expr)) ->
        patName pat |> Option.map (fun name -> { ModulePath = modulePath; Name = name; Expr = expr }))
    | SynModuleDecl.NestedModule(moduleInfo = SynComponentInfo(longId = ids); decls = inner) ->
      bindingsIn (modulePath @ [ identText ids ]) inner
    | _ -> [])

let private parse (source: string) : Result<string list * ModuleBinding list, string> =
  try
    let input, diagnostics = Fantomas.FCS.Parse.parseFile false (SourceText.ofString source) []
    match diagnostics |> List.tryFind (fun d -> d.Severity.IsError), input with
    | Some error, _ ->
      let line = error.Range |> Option.map (fun r -> string r.StartLine) |> Option.defaultValue "?"
      Error(sprintf "the file does not parse (line %s: %s)" line error.Message)
    | None, ParsedInput.ImplFile(ParsedImplFileInput(contents = [ SynModuleOrNamespace(longId = ids; kind = kind; decls = decls) ])) ->
      let modulePath =
        match kind with
        | SynModuleOrNamespaceKind.AnonModule -> []
        | _ -> ids |> List.map _.idText
      Ok(modulePath, bindingsIn modulePath decls)
    | None, ParsedInput.ImplFile _ -> Error "the file declares several namespaces or modules at the top level"
    | None, ParsedInput.SigFile _ -> Error "signature files have no tweakable expressions"
  with ex -> Error(sprintf "the file could not be parsed: %s" ex.Message)

/// Every tweakable leaf (numeric, bool, char, string, DU-case-like
/// identifier) plus every record-field expression, for every binding in the
/// file. A record field's own address is included even when its expression
/// is not itself a leaf (a binop, an if), because that address is exactly
/// what `ExpressionEdit` targets to replace the whole field's formula.
let addressesOf (source: string) : Result<TweakAddress list, string> =
  parse source
  |> Result.map (fun (_, bindings) ->
    bindings
    |> List.collect (fun b ->
      // `collect` already reports a bare leaf binding's own (empty) path as
      // well as every record field and nested leaf, so nothing extra is
      // needed here, just distinct, since a record field whose value is
      // ITSELF a leaf (`Gravity = 9.8`) is otherwise reported twice: once as
      // "the field", once as "the leaf value", both at the same path.
      collect [] b.Expr
      |> List.map (fun (path, _) -> { ModulePath = b.ModulePath; BindingName = b.Name; Path = path })
      |> List.distinct))

/// Walk `expr` down `path`, or say exactly where the path stopped meaning
/// anything.
let rec private walkPath (expr: SynExpr) (path: PathStep list) : SynExpr option =
  match path with
  | [] -> Some expr
  | step :: rest ->
    let strip =
      match expr with
      | SynExpr.Paren(expr = inner)
      | SynExpr.Typed(expr = inner) -> inner
      | e -> e
    match step, strip with
    | PathStep.RecordField name, SynExpr.Record(recordFields = fields) ->
      fields
      |> List.tryPick (function
        | SynExprRecordField(fieldName = fn; expr = Some fieldExpr) when fieldName fn = name -> Some fieldExpr
        | _ -> None)
      |> Option.bind (fun e -> walkPath e rest)
    | PathStep.TupleItem i, SynExpr.Tuple(exprs = exprs) ->
      exprs |> List.tryItem i |> Option.bind (fun e -> walkPath e rest)
    | PathStep.ListItem i, SynExpr.ArrayOrList(exprs = exprs) ->
      exprs |> List.tryItem i |> Option.bind (fun e -> walkPath e rest)
    | PathStep.ListItem i, SynExpr.ArrayOrListComputed(expr = inner) ->
      flattenSequential inner |> List.tryItem i |> Option.bind (fun e -> walkPath e rest)
    | PathStep.IfCond, SynExpr.IfThenElse(ifExpr = cond) -> walkPath cond rest
    | PathStep.IfThen, SynExpr.IfThenElse(thenExpr = thenExpr) -> walkPath thenExpr rest
    | PathStep.IfElse, SynExpr.IfThenElse(elseExpr = Some elseExpr) -> walkPath elseExpr rest
    | PathStep.BinOpLeft, SynExpr.App(isInfix = false; funcExpr = SynExpr.App(isInfix = true; argExpr = left)) -> walkPath left rest
    | PathStep.BinOpRight, SynExpr.App(isInfix = false; funcExpr = SynExpr.App(isInfix = true); argExpr = right) -> walkPath right rest
    | PathStep.AppArg i, (SynExpr.App _ as app) ->
      let _, args = flattenApp app
      args |> List.tryItem i |> Option.bind (fun e -> walkPath e rest)
    | _ -> None

/// Resolve an address against `source`: the exact range, text and content
/// hash of the expression it names right now.
let resolve (source: string) (address: TweakAddress) : Result<ResolvedTweak, ResolveError> =
  match parse source with
  | Error msg -> Error(ResolveError.ParseFailed msg)
  | Ok(_, bindings) ->
    match bindings |> List.tryFind (fun b -> b.ModulePath = address.ModulePath && b.Name = address.BindingName) with
    | None -> Error(ResolveError.BindingRemoved address)
    | Some b ->
      match walkPath b.Expr address.Path with
      | None -> Error(ResolveError.PathGone address)
      | Some target ->
        let text = rangeText source target.Range
        Ok { Address = address; Range = target.Range; Text = text; Hash = contentHash text }

/// Parse a standalone expression snippet (possibly multi-line) the same way
/// `LiteralEdit`/`ExpressionEdit` need to: wrapped just enough to be legal
/// top-level F#, offside-safe by indenting every line under the wrapper's
/// `let`. Shared here so both modules parse a snippet identically.
let parseExpr (text: string) : Result<SynExpr, string> =
  let indented =
    text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')
    |> Array.map (fun line -> "    " + line)
    |> String.concat "\n"
  let wrapped = sprintf "module __TweakProbe__\nlet __v__ =\n%s\n" indented
  try
    let input, diagnostics = Fantomas.FCS.Parse.parseFile false (SourceText.ofString wrapped) []
    match diagnostics |> List.tryFind (fun d -> d.Severity.IsError) with
    | Some err -> Error err.Message
    | None ->
      match input with
      | ParsedInput.ImplFile(ParsedImplFileInput(contents = [ SynModuleOrNamespace(decls = [ SynModuleDecl.Let(bindings = [ SynBinding(expr = expr) ]) ]) ])) ->
        Ok expr
      | _ -> Error "expected exactly one expression"
  with ex -> Error ex.Message
