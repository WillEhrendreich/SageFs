/// Classifies an expression for live re-evaluation while scrubbing: `Live`
/// (safe to re-run many times a second) or `OnRelease` (re-run only when the
/// user lets go of the knob). Parse-level only, on purpose, no typed
/// checker in this module. Conservative is the rule stated in the spec: when
/// unsure, `OnRelease`. A false "Live" would re-run a side effect on every
/// drag tick; a false "OnRelease" only costs a little responsiveness.
module SageFs.Features.Tweak.SideEffectClass

open Fantomas.FCS.Syntax
open SageFs.Features.Tweak.TweakAddress

[<RequireQualifiedAccess>]
type SideEffectClass =
  | Live
  | OnRelease of reason: string

/// The exact functions this module trusts to be pure enough to re-run every
/// scrub tick, kept as DATA (not scattered `if`s) so the allow-list can be
/// audited and tested on its own. Each entry is the qualified name FCS
/// resolves a `LongIdent`/`Ident` call target to at parse level, i.e. the
/// dotted spelling as written, since there is no typed checker here to
/// resolve an open namespace back to its full name.
let knownPureCalls : Set<string> =
  set [
    "not"; "abs"; "min"; "max"; "sqrt"; "float"; "float32"; "int"; "int64"; "int32"
    "int16"; "byte"; "sbyte"; "uint"; "uint32"; "uint64"; "uint16"; "decimal"
    "char"; "string"
    "Math.Abs"; "Math.Min"; "Math.Max"; "Math.Sqrt"; "Math.Pow"; "Math.Floor"
    "Math.Ceiling"; "Math.Round"; "Math.Sign"; "Math.Clamp"; "Math.Log"
    "Math.Log2"; "Math.Log10"; "Math.Exp"; "Math.Sin"; "Math.Cos"; "Math.Tan"
    "System.Math.Abs"; "System.Math.Min"; "System.Math.Max"; "System.Math.Sqrt"
    "System.Math.Pow"; "System.Math.Clamp"
  ]

let private identText (ids: Ident list) = ids |> List.map _.idText |> String.concat "."

/// The dotted name a call's function expression spells, when it's plain
/// enough to name at all (an `Ident`/`LongIdent`, not a computed function
/// value, a lambda, or a member chain off a non-identifier receiver).
let rec private calledName (expr: SynExpr) : string option =
  match expr with
  | SynExpr.Ident id -> Some id.idText
  | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) -> Some(identText ids)
  | SynExpr.Paren(expr = inner)
  | SynExpr.Typed(expr = inner) -> calledName inner
  | SynExpr.DotGet(expr = target; longDotId = SynLongIdent(id = ids)) ->
    calledName target |> Option.map (fun t -> t + "." + identText ids)
  | _ -> None

let rec private flattenApp (expr: SynExpr) : SynExpr * SynExpr list =
  match expr with
  | SynExpr.App(isInfix = false; funcExpr = inner; argExpr = arg) ->
    let baseFunc, args = flattenApp inner
    baseFunc, args @ [ arg ]
  | _ -> expr, []

/// Classify one expression. Total over the untyped tree: every construct
/// this module doesn't explicitly recognize as safe falls through to
/// `OnRelease "an expression shape this classifier doesn't know is pure"`,
/// there is no silent default to `Live`.
let rec classify (expr: SynExpr) : SideEffectClass =
  match expr with
  | SynExpr.Paren(expr = inner)
  | SynExpr.Typed(expr = inner) -> classify inner

  | SynExpr.Const _
  | SynExpr.Ident _ -> SideEffectClass.Live

  | SynExpr.LongIdent _ -> SideEffectClass.Live // a read of a (possibly qualified) binding

  | SynExpr.Tuple(exprs = exprs) -> classifyAll exprs "a tuple element"
  | SynExpr.ArrayOrList(exprs = exprs) -> classifyAll exprs "a list/array element"
  | SynExpr.Record(recordFields = fields) ->
    fields
    |> List.choose (function
      | SynExprRecordField(expr = Some e) -> Some e
      | _ -> None)
    |> fun es -> classifyAll es "a record field"

  | SynExpr.IfThenElse(ifExpr = cond; thenExpr = thenExpr; elseExpr = elseExpr) ->
    let branches = cond :: thenExpr :: (elseExpr |> Option.toList)
    classifyAll branches "an if/then/else branch"

  | SynExpr.Match(expr = scrutinee; clauses = clauses) ->
    let bodies = clauses |> List.map (fun (SynMatchClause(resultExpr = r)) -> r)
    classifyAll (scrutinee :: bodies) "a match branch"

  // A binary/unary operator application: the operator itself is trusted
  // (F#'s built-in arithmetic/comparison/boolean operators), and only the
  // operands are walked.
  | SynExpr.App(isInfix = false; funcExpr = SynExpr.App(isInfix = true; funcExpr = op; argExpr = left); argExpr = right) ->
    match calledName op |> Option.map isKnownOperator with
    | Some true -> classifyAll [ left; right ] "an operator operand"
    | _ -> SideEffectClass.OnRelease "an infix application this classifier doesn't recognize as a built-in operator"

  | SynExpr.App _ as app ->
    let func, args = flattenApp app
    match calledName func with
    | Some name when knownPureCalls.Contains name -> classifyAll args "a call argument"
    | Some name -> SideEffectClass.OnRelease(sprintf "a call to '%s', which isn't on the known-pure allow-list" name)
    | None -> SideEffectClass.OnRelease "a call whose target isn't a plain name this classifier can check against the allow-list"

  | _ -> SideEffectClass.OnRelease "an expression shape this classifier doesn't know is pure"

and private classifyAll (exprs: SynExpr list) (positionLabel: string) : SideEffectClass =
  exprs
  |> List.map classify
  |> List.tryPick (function
    | SideEffectClass.OnRelease _ as r -> Some r
    | SideEffectClass.Live -> None)
  |> Option.defaultValue SideEffectClass.Live
  |> function
    | SideEffectClass.OnRelease reason -> SideEffectClass.OnRelease(sprintf "%s: %s" positionLabel reason)
    | live -> live

/// F#'s own arithmetic/comparison/boolean symbolic operators, spelled the
/// way FCS names them as an identifier (`op_Addition`, and the bare
/// symbolic form the parser can also hand back depending on how the
/// operator was written).
and private isKnownOperator (name: string) : bool =
  let symbolic =
    set [ "+"; "-"; "*"; "/"; "%"; "**"
          "="; "<>"; "<"; ">"; "<="; ">="
          "&&"; "||"
          "min"; "max" ]
  let compiled =
    set [ "op_Addition"; "op_Subtraction"; "op_Multiply"; "op_Division"; "op_Modulus"
          "op_Exponentiation"; "op_Equality"; "op_Inequality"; "op_LessThan"
          "op_GreaterThan"; "op_LessThanOrEqual"; "op_GreaterThanOrEqual"
          "op_BooleanAnd"; "op_BooleanOr"; "op_UnaryNegation" ]
  symbolic.Contains name || compiled.Contains name

/// Classify the expression at `address`. `Gone` when the address doesn't
/// resolve any more, a caller has to decide what "safe to re-run" means
/// for something that no longer exists, this module only answers for
/// something that does.
let classifyAt (source: string) (address: TweakAddress) : Result<SideEffectClass, ResolveError> =
  resolve source address
  |> Result.bind (fun resolved -> parseExpr resolved.Text |> Result.map classify |> Result.mapError (fun _ -> ResolveError.PathGone address))
