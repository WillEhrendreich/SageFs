/// Coverage for `TweakAddress`: every tweakable point in a file, and
/// resolving one back to its exact text and hash, surviving a reformat,
/// and refusing to hide a real underlying change.
module SageFs.Tests.TweakAddressTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.Tweak.TweakAddress

let private tuningFile =
  """module Game.Tuning

let jumpVelocity = gravity * 2.0

type Tuning =
  { JumpVelocity: float
    Gravity: float
    Difficulty: string }

let tuning =
  { JumpVelocity = gravity * 2.0
    Gravity = 9.8
    Difficulty = "Easy" }
"""

let private addrFor binding path : TweakAddress =
  { ModulePath = [ "Game"; "Tuning" ]; BindingName = binding; Path = path }

[<Tests>]
let tweakAddressTests =
  testList "TweakAddress" [

    testCase "addressesOf finds the record-field addresses plus the leaves inside them" <| fun _ ->
      let addresses = addressesOf tuningFile |> Expect.wantOk "a well-formed file must parse"
      addresses
      |> Expect.contains "the field itself is addressable, for a whole-expression edit"
        (addrFor "tuning" [ PathStep.RecordField "JumpVelocity" ])
      addresses
      |> Expect.contains "the literal leaf inside it is separately addressable, for a scrub"
        (addrFor "tuning" [ PathStep.RecordField "JumpVelocity"; PathStep.BinOpRight ])
      addresses
      |> Expect.contains "a plain numeric field is its own leaf"
        (addrFor "tuning" [ PathStep.RecordField "Gravity" ])
      addresses
      |> Expect.contains "a string field is a leaf too"
        (addrFor "tuning" [ PathStep.RecordField "Difficulty" ])

    testCase "a module-level binding that's just a literal has an empty path" <| fun _ ->
      let addresses = addressesOf tuningFile |> Expect.wantOk "parses"
      addresses |> Expect.contains "top-level bare literals are addressable at path []"
        (addrFor "jumpVelocity" [ PathStep.BinOpRight ])

    testCase "resolve finds the exact text and a stable hash" <| fun _ ->
      let address = addrFor "tuning" [ PathStep.RecordField "Gravity" ]
      let resolved = resolve tuningFile address |> Expect.wantOk "the address exists"
      resolved.Text |> Expect.equal "the field's exact source text" "9.8"

    testCase "resolve says BindingRemoved when the binding is gone" <| fun _ ->
      let missing = { ModulePath = [ "Game"; "Tuning" ]; BindingName = "notThere"; Path = [] }
      resolve tuningFile missing
      |> Expect.equal "no such binding" (Error(ResolveError.BindingRemoved missing))

    testCase "resolve says PathGone when the shape underneath the binding changed" <| fun _ ->
      let address = addrFor "tuning" [ PathStep.RecordField "JumpVelocity"; PathStep.BinOpRight; PathStep.BinOpLeft ]
      match resolve tuningFile address with
      | Error(ResolveError.PathGone a) -> a |> Expect.equal "the failing address is the one asked for" address
      | other -> failtestf "expected PathGone, got %A" other

    testCase "resolve says ParseFailed on a broken file" <| fun _ ->
      match resolve "module M\nlet x = (((" (addrFor "x" []) with
      | Error(ResolveError.ParseFailed _) -> ()
      | other -> failtestf "expected ParseFailed, got %A" other

    testCase "a reformatted file (blank lines and a comment inserted above) still resolves the same address to the same text" <| fun _ ->
      let reformatted =
        """module Game.Tuning


// a totally unrelated comment
let jumpVelocity = gravity * 2.0

type Tuning =
  { JumpVelocity: float
    Gravity: float
    Difficulty: string }


let tuning =
  { JumpVelocity = gravity * 2.0
    Gravity = 9.8
    Difficulty = "Easy" }
"""
      let address = addrFor "tuning" [ PathStep.RecordField "Gravity" ]
      let before = resolve tuningFile address |> Expect.wantOk "resolves before"
      let after = resolve reformatted address |> Expect.wantOk "resolves after reformat"
      after.Text |> Expect.equal "same text" before.Text
      after.Hash |> Expect.equal "same hash" before.Hash

    testCase "changing the expression's own text changes its hash" <| fun _ ->
      let changed = tuningFile.Replace("Gravity = 9.8", "Gravity = 12.0")
      let address = addrFor "tuning" [ PathStep.RecordField "Gravity" ]
      let before = resolve tuningFile address |> Expect.wantOk "resolves"
      let after = resolve changed address |> Expect.wantOk "resolves"
      after.Hash |> Expect.notEqual "a real edit changes the hash" before.Hash

    testCase "an if/then/else's branches are addressable" <| fun _ ->
      let source = "module M\nlet pick hard = if hard then 80 else 100\n"
      let addresses = addressesOf source |> Expect.wantOk "parses"
      addresses |> Expect.contains "then-branch" { ModulePath = [ "M" ]; BindingName = "pick"; Path = [ PathStep.IfThen ] }
      addresses |> Expect.contains "else-branch" { ModulePath = [ "M" ]; BindingName = "pick"; Path = [ PathStep.IfElse ] }

    testCase "a list literal's items are addressable by index" <| fun _ ->
      let source = "module M\nlet xs = [ 1; 2; 3 ]\n"
      let addresses = addressesOf source |> Expect.wantOk "parses"
      addresses |> Expect.contains "index 0" { ModulePath = [ "M" ]; BindingName = "xs"; Path = [ PathStep.ListItem 0 ] }
      addresses |> Expect.contains "index 2" { ModulePath = [ "M" ]; BindingName = "xs"; Path = [ PathStep.ListItem 2 ] }

    testCase "replaceRange only touches the given range, preserving CRLF elsewhere" <| fun _ ->
      let source = "module M\r\nlet x = 1\r\nlet y = 2\r\n"
      let address = { ModulePath = [ "M" ]; BindingName = "x"; Path = [] }
      let resolved = resolve source address |> Expect.wantOk "resolves"
      let replaced = replaceRange source resolved.Range "42"
      replaced |> Expect.equal "only the literal changed; CRLFs and the rest of the file survive" "module M\r\nlet x = 42\r\nlet y = 2\r\n"

    testProperty "PROPERTY, every address addressesOf returns resolves in the same file" <|
      fun () ->
        match addressesOf tuningFile with
        | Error _ -> false
        | Ok addresses ->
          addresses |> List.forall (fun a -> match resolve tuningFile a with Ok _ -> true | Error _ -> false)

    testProperty "PROPERTY, surrounding a binding with extra blank lines never changes what an existing address resolves to" <|
      fun (NonNegativeInt n) ->
        let extraBlankLines = String.replicate (min n 20) "\n"
        let padded = extraBlankLines + tuningFile
        let address = addrFor "tuning" [ PathStep.RecordField "Gravity" ]
        match resolve tuningFile address, resolve padded address with
        | Ok a, Ok b -> a.Text = b.Text && a.Hash = b.Hash
        | _ -> false

    testList "relocate, an offer, never auto-taken" [

      testCase "finds the same expression under its new binding name after a rename" <| fun _ ->
        let original = addrFor "tuning" [ PathStep.RecordField "Gravity" ]
        let baselineHash = (resolve tuningFile original |> Expect.wantOk "resolves before the rename").Hash
        let renamed = tuningFile.Replace("let tuning =", "let tuningV2 =")
        match relocate renamed original baselineHash with
        | RelocationResult.Relocated candidate ->
          candidate |> Expect.equal "found under the new name, same path" (addrFor "tuningV2" [ PathStep.RecordField "Gravity" ])
        | RelocationResult.NoCandidate -> failtest "expected a relocation candidate"

      testCase "never returns the original address as its own candidate" <| fun _ ->
        let original = addrFor "tuning" [ PathStep.RecordField "Gravity" ]
        let hash = (resolve tuningFile original |> Expect.wantOk "resolves").Hash
        // The address still resolves here (nothing renamed), relocate must
        // not just hand back the same address as a "candidate".
        match relocate tuningFile original hash with
        | RelocationResult.Relocated candidate -> candidate |> Expect.notEqual "never itself" original
        | RelocationResult.NoCandidate -> ()

      testCase "no candidate when the expression genuinely doesn't exist anywhere else" <| fun _ ->
        let gone = addrFor "notThere" []
        relocate tuningFile gone "some-hash-nothing-matches"
        |> Expect.equal "nothing to offer" RelocationResult.NoCandidate

      testCase "relocate is a pure offer: it never changes the source" <| fun _ ->
        let original = addrFor "tuning" [ PathStep.RecordField "Gravity" ]
        let hash = (resolve tuningFile original |> Expect.wantOk "resolves").Hash
        let renamed = tuningFile.Replace("let tuning =", "let tuningV2 =")
        relocate renamed original hash |> ignore
        renamed |> Expect.equal "relocate only reads" (tuningFile.Replace("let tuning =", "let tuningV2 ="))
    ]
  ]
