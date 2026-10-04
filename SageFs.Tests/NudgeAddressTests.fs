/// The text a caller types for a `TweakAddress`. The subject IS the spelling, so
/// these tests pin the spelling live-tweak-spec.md uses, and they round-trip every
/// address the engine can report so the two functions cannot drift apart.
module SageFs.Tests.NudgeAddressTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Microsoft.FSharp.Reflection
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.NudgeAddress
open SageFs.Tests.SharedGenerators

let sampleFile =
  """module Game.Tuning

let gravity = 9.8

let tuning =
  { JumpVelocity = gravity * 2.0
    MaxHealth = 100
    Mode = if hardMode then 80 else 100
    Pair = (1, 2)
    Items = [ 1; 2; 3 ]
    Scaled = scale 3 4.5 }
"""

let tuning path : TweakAddress =
  { ModulePath = [ "Game"; "Tuning" ]; BindingName = "tuning"; Path = path }

let allSteps : PathStep list =
  [ PathStep.RecordField "JumpVelocity"
    PathStep.TupleItem 1
    PathStep.ListItem 2
    PathStep.AppArg 0
    PathStep.IfCond
    PathStep.IfThen
    PathStep.IfElse
    PathStep.BinOpLeft
    PathStep.BinOpRight ]

let identifier = Gen.elements [ "a"; "tuning"; "JumpVelocity"; "x2"; "_hidden"; "Max_Health" ]

let stepGen : Gen<PathStep> =
  Gen.oneof
    [ identifier |> Gen.map PathStep.RecordField
      Gen.choose (0, 9) |> Gen.map PathStep.TupleItem
      Gen.choose (0, 9) |> Gen.map PathStep.ListItem
      Gen.choose (0, 9) |> Gen.map PathStep.AppArg
      Gen.elements [ PathStep.IfCond; PathStep.IfThen; PathStep.IfElse; PathStep.BinOpLeft; PathStep.BinOpRight ] ]

let addressGen : Gen<TweakAddress> =
  gen {
    let! modules = Gen.listOf (Gen.elements [ "Game"; "Tuning"; "Inner" ])
    let! binding = identifier
    let! path = Gen.listOf stepGen
    return { ModulePath = modules; BindingName = binding; Path = path }
  }

[<Tests>]
let nudgeAddressTests =
  testList "NudgeAddress" [

    testCase "WHY - the spelling the spec uses names the right operand of a field's formula" <| fun _ ->
      format (tuning [ PathStep.RecordField "JumpVelocity"; PathStep.BinOpRight ])
      |> Expect.equal "module path, binding, then one slash step per move" "Game.Tuning.tuning/{JumpVelocity}/BinOp.Right"

    testCase "WHY - an address with no steps is the whole binding, spelled as just its name" <| fun _ ->
      format (tuning []) |> Expect.equal "no slash when there is no step" "Game.Tuning.tuning"

    testCase "WHY - a binding in an anonymous module has no module path to spell" <| fun _ ->
      format { ModulePath = []; BindingName = "x"; Path = [] } |> Expect.equal "just the binding" "x"

    testCase "WHY - every step has its own spelling, so two steps never read as one" <| fun _ ->
      allSteps
      |> List.map formatStep
      |> List.distinct
      |> List.length
      |> Expect.equal "the spellings are distinct" allSteps.Length

    testCase "WHY - every address the engine reports for a real file round-trips through its text" <| fun _ ->
      let addresses = addressesOf sampleFile |> Expect.wantOk "the sample parses"
      addresses |> List.isEmpty |> Expect.isFalse "the sample has addresses, so this checks something"
      for address in addresses do
        tryParse (format address)
        |> Expect.equal (sprintf "%s round-trips" (format address)) (Ok address)

    testPropertyWithConfig propConfig "PROPERTY, any address built from plain names round-trips through its text" <| fun () ->
      Prop.forAll (Arb.fromGen addressGen) (fun address -> tryParse (format address) = Ok address)

    testCase "WHY - nothing at all is refused as empty, not read as an address" <| fun _ ->
      tryParse "" |> Expect.equal "empty" (Error AddressTextRefusal.Empty)
      tryParse "   " |> Expect.equal "blank" (Error AddressTextRefusal.Empty)

    testCase "WHY - text with no binding name is refused, so a lone step list never addresses something" <| fun _ ->
      match tryParse "/{Field}" with
      | Error(AddressTextRefusal.NoBinding _) -> ()
      | other -> failtestf "expected NoBinding, got %A" other
      match tryParse "Game.Tuning." with
      | Error(AddressTextRefusal.NoBinding _) -> ()
      | other -> failtestf "expected NoBinding for a trailing dot, got %A" other

    testCase "WHY - a step nobody defined is refused and named, never skipped" <| fun _ ->
      tryParse "Game.Tuning.tuning/Sideways"
      |> Expect.equal "the unknown step is carried" (Error(AddressTextRefusal.UnknownStep "Sideways"))

    testCase "WHY - an index that is not a number is refused and the step is named" <| fun _ ->
      tryParse "Game.Tuning.tuning/Tuple.first"
      |> Expect.equal "the bad step is carried" (Error(AddressTextRefusal.BadIndex "Tuple.first"))
      tryParse "Game.Tuning.tuning/List.-1"
      |> Expect.equal "a negative index is not an index" (Error(AddressTextRefusal.BadIndex "List.-1"))

    testCase "WHY - the refusal cases are a closed list the tests above exercise" <| fun _ ->
      FSharpType.GetUnionCases typeof<AddressTextRefusal>
      |> Array.length
      |> Expect.equal "Empty, NoBinding, UnknownStep, BadIndex" 4
  ]
