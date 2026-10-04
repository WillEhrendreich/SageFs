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
let formatStep (step: PathStep) : string = failwith "not built yet"

/// The address as text.
let format (address: TweakAddress) : string = failwith "not built yet"

/// Text back to an address, or the named reason it is not one.
let tryParse (text: string) : Result<TweakAddress, AddressTextRefusal> = failwith "not built yet"
