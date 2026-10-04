/// The text a caller types to name a `TweakAddress`: the module path and the
/// binding, then one `/` step per move down the expression. `Game.Tuning.tuning`
/// addresses the whole right-hand side of `tuning`; `Game.Tuning.tuning/{JumpVelocity}/BinOp.Right`
/// addresses the right operand of the formula in its `JumpVelocity` field. This
/// is the spelling live-tweak-spec.md uses.
///
/// One function turns an address into text and one turns text back, so the two
/// cannot drift: a test round-trips every address `addressesOf` reports.
///
/// Limit, stated plainly: a nested module declared with a dotted name
/// (`module A.B =` inside a file) is ONE path element in `TweakAddress`, and its
/// text is indistinguishable from two nested modules. The parse reads dots as
/// separators, so such an address resolves to nothing and the door refuses it
/// with `BindingRemoved`. It never lands on the wrong binding.
module SageFs.Features.Tweak.NudgeAddress

open System
open SageFs.Features.Tweak.TweakAddress

[<RequireQualifiedAccess>]
type AddressTextRefusal =
  /// Nothing was given.
  | Empty
  /// There is no binding name in the text (it starts with `/`, or ends with a dot).
  | NoBinding of text: string
  /// A step between slashes is not one of the known spellings.
  | UnknownStep of step: string
  /// A step that takes an index (`Tuple.N`, `List.N`, `Arg.N`) has something else after the dot.
  | BadIndex of step: string

/// The one exhaustive spelling of a step.
let formatStep (step: PathStep) : string =
  match step with
  | PathStep.RecordField name -> sprintf "{%s}" name
  | PathStep.TupleItem index -> sprintf "Tuple.%d" index
  | PathStep.ListItem index -> sprintf "List.%d" index
  | PathStep.AppArg index -> sprintf "Arg.%d" index
  | PathStep.IfCond -> "If.Cond"
  | PathStep.IfThen -> "If.Then"
  | PathStep.IfElse -> "If.Else"
  | PathStep.BinOpLeft -> "BinOp.Left"
  | PathStep.BinOpRight -> "BinOp.Right"

/// The address as text.
let format (address: TweakAddress) : string =
  let head = String.concat "." (address.ModulePath @ [ address.BindingName ])
  match address.Path with
  | [] -> head
  | steps -> head + "/" + (steps |> List.map formatStep |> String.concat "/")

/// A step that carries an index: `Tuple.3` is `build 3`. Digits only, so `Tuple.-1` and `Tuple.3x` are refused.
let indexedStep (step: string) (prefix: string) (build: int -> PathStep) : Result<PathStep, AddressTextRefusal> =
  let digits = step.Substring prefix.Length
  match digits.Length > 0 && digits |> Seq.forall Char.IsAsciiDigit with
  | false -> Error(AddressTextRefusal.BadIndex step)
  | true ->
    match Int32.TryParse digits with
    | true, index -> Ok(build index)
    | false, _ -> Error(AddressTextRefusal.BadIndex step)

let tryParseStep (step: string) : Result<PathStep, AddressTextRefusal> =
  match step with
  | "If.Cond" -> Ok PathStep.IfCond
  | "If.Then" -> Ok PathStep.IfThen
  | "If.Else" -> Ok PathStep.IfElse
  | "BinOp.Left" -> Ok PathStep.BinOpLeft
  | "BinOp.Right" -> Ok PathStep.BinOpRight
  | field when field.Length > 2 && field.StartsWith("{", StringComparison.Ordinal) && field.EndsWith("}", StringComparison.Ordinal) ->
    Ok(PathStep.RecordField(field.Substring(1, field.Length - 2)))
  | tuple when tuple.StartsWith("Tuple.", StringComparison.Ordinal) -> indexedStep tuple "Tuple." PathStep.TupleItem
  | list when list.StartsWith("List.", StringComparison.Ordinal) -> indexedStep list "List." PathStep.ListItem
  | app when app.StartsWith("Arg.", StringComparison.Ordinal) -> indexedStep app "Arg." PathStep.AppArg
  | other -> Error(AddressTextRefusal.UnknownStep other)

/// Parse every step, stopping at the first one that is not a step.
let tryParseSteps (steps: string list) : Result<PathStep list, AddressTextRefusal> =
  steps
  |> List.fold
    (fun acc step ->
      match acc with
      | Error _ -> acc
      | Ok parsed -> tryParseStep step |> Result.map (fun s -> parsed @ [ s ]))
    (Ok [])

/// Text back to an address, or the named reason it is not one.
let tryParse (text: string) : Result<TweakAddress, AddressTextRefusal> =
  let trimmed = if isNull text then "" else text.Trim()
  match trimmed with
  | "" -> Error AddressTextRefusal.Empty
  | _ ->
    let parts = trimmed.Split('/') |> List.ofArray
    let head = List.head parts
    let names = head.Split('.') |> List.ofArray
    match names |> List.exists String.IsNullOrEmpty with
    | true -> Error(AddressTextRefusal.NoBinding trimmed)
    | false ->
      let binding = List.last names
      let modulePath = names |> List.take (names.Length - 1)
      tryParseSteps (List.tail parts)
      |> Result.map (fun path -> { ModulePath = modulePath; BindingName = binding; Path = path })
