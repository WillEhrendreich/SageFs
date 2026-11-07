/// Trying a value in the running session without writing the file. The laws are about the statement a push evaluates: a place the
/// program can assign is assigned (so the instance the program holds changes), anything else is rebound, the value is held to the
/// literal's own kind and type exactly as a write to the file is, and the text the walk prints for the new value is the text the row
/// compares against, so a tried value reads `tweaked`. Plus what the walk and the files say about whether a place can be assigned.
module SageFs.Tests.TweakPushTests

open System
open System.Globalization
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features
open SageFs.Features.Tweak
open SageFs.Features.Tweak.Nudge
open SageFs.Features.Tweak.BindingTweak
open SageFs.Features.Tweak.PushCode
open SageFs.Tests.SharedGenerators

type Body = { Mass: float; mutable Hp: int; Name: string }
type Rig = { Inner: Body; mutable Current: Body }

let top (name: string) : RowKey = { Binding = name; Labels = [] }
let part (labels: string list) : RowKey = { Binding = "feel"; Labels = labels }

let allDeclared = [ BindingDeclared.Mutable; BindingDeclared.Immutable; BindingDeclared.NotDeclared ]
let allAssign = [ LiveValueTree.Assignment.NotKnown; LiveValueTree.Assignment.Assignable; LiveValueTree.Assignment.Fixed ]

let isAssigning (via: PushVia) : bool =
  match via with
  | PushVia.AssignBinding
  | PushVia.AssignField
  | PushVia.AssignBindingWithCopy -> true
  | PushVia.RebindBinding
  | PushVia.RebindWithCopy -> false

let codeOf (result: Result<PushPlan, NudgeRefusal>) : string =
  match result with
  | Ok plan -> plan.Code
  | Error refusal -> sprintf "refused: %s" (NudgeRefusal.rule refusal)

/// A statement builder that ignores how the place is reached and always rebinds: the twin the assignment law must fail.
let alwaysRebinds (key: RowKey) (literal: string) : string = sprintf "let %s = %s;;" key.Binding literal

[<Tests>]
let storageTests =
  testList "whether the program can assign the place a row's value lives in" [

    testCase "a binding declared mutable can be assigned whole, so every part of it can change; a plain one cannot; one no file declares says only what the walk saw" <| fun _ ->
      let expected declared field =
        match declared, field with
        | BindingDeclared.Mutable, _ -> Storage.Mutable
        | _, LiveValueTree.Assignment.Assignable -> Storage.Mutable
        | _, LiveValueTree.Assignment.Fixed -> Storage.Fixed
        | BindingDeclared.Immutable, LiveValueTree.Assignment.NotKnown -> Storage.Fixed
        | BindingDeclared.NotDeclared, LiveValueTree.Assignment.NotKnown -> Storage.NotKnown
      for declared in allDeclared do
        for field in allAssign do
          Storage.derive declared field |> Expect.equal (sprintf "%A, %A" declared field) (expected declared field)
      Storage.derive BindingDeclared.NotDeclared LiveValueTree.Assignment.NotKnown |> Expect.equal "unknown stays unknown, never one of the answers" Storage.NotKnown

    testCase "the walk marks a record's fields: a mutable one is assignable, one without is fixed, and nothing else claims either" <| fun _ ->
      let node = LiveValueTree.buildValueNode "b" (box { Mass = 1.0; Hp = 3; Name = "a" })
      node.Children |> List.map (fun c -> c.Label, c.Assign)
      |> Expect.equal "per field" [ "Mass", LiveValueTree.Assignment.Fixed; "Hp", LiveValueTree.Assignment.Assignable; "Name", LiveValueTree.Assignment.Fixed ]
      node.Assign |> Expect.equal "a binding's root is not a field" LiveValueTree.Assignment.NotKnown
      let rig = LiveValueTree.buildValueNode "r" (box { Inner = { Mass = 1.0; Hp = 3; Name = "a" }; Current = { Mass = 2.0; Hp = 4; Name = "b" } })
      rig.Children |> List.map (fun c -> c.Label, c.Assign)
      |> Expect.equal "the outer fields" [ "Inner", LiveValueTree.Assignment.Fixed; "Current", LiveValueTree.Assignment.Assignable ]
      rig.Children.[1].Children |> List.map (fun c -> c.Assign)
      |> Expect.equal "the inner record's own fields are told apart too" [ LiveValueTree.Assignment.Fixed; LiveValueTree.Assignment.Assignable; LiveValueTree.Assignment.Fixed ]
      let list = LiveValueTree.buildValueNode "xs" (box [ 1; 2 ])
      (list :: list.Children) |> List.forall (fun n -> n.Assign = LiveValueTree.Assignment.NotKnown)
      |> Expect.isTrue "a list and its items say nothing about being assigned"

    testCase "the file says which bindings are `let mutable`, at any nesting, and a file that does not parse says none" <| fun _ ->
      let text = "module M\n\nlet plain = 1\nlet mutable counter = 5\n\nmodule Inner =\n  let mutable depth = 2\n  let fixedToo = 3\n"
      TweakAddress.mutableBindings text |> Expect.equal "the two mutable ones" (Ok(Set.ofList [ "counter"; "depth" ]))
      match TweakAddress.mutableBindings "let let let" with
      | Error _ -> ()
      | Ok names -> failtestf "a file that does not parse gave %A" names
  ]

[<Tests>]
let viaTests =
  testList "how a value gets into the session for a row" [

    testCase "a whole binding is assigned when the file declares it mutable and rebound otherwise, whatever the walk says about a field" <| fun _ ->
      for field in allAssign do
        PushVia.derive [] BindingDeclared.Mutable field |> Expect.equal "mutable" PushVia.AssignBinding
        PushVia.derive [] BindingDeclared.Immutable field |> Expect.equal "plain" PushVia.RebindBinding
        PushVia.derive [] BindingDeclared.NotDeclared field |> Expect.equal "undeclared" PushVia.RebindBinding

    testCase "a part is assigned in place when its field is mutable, and otherwise the record is copied: assigned when the binding is mutable, rebound when it is not" <| fun _ ->
      for declared in allDeclared do
        PushVia.derive [ "A"; "B" ] declared LiveValueTree.Assignment.Assignable |> Expect.equal "a mutable field" PushVia.AssignField
      for field in [ LiveValueTree.Assignment.Fixed; LiveValueTree.Assignment.NotKnown ] do
        PushVia.derive [ "A" ] BindingDeclared.Mutable field |> Expect.equal "a fixed field of a mutable binding" PushVia.AssignBindingWithCopy
        PushVia.derive [ "A" ] BindingDeclared.Immutable field |> Expect.equal "a fixed field of a plain binding" PushVia.RebindWithCopy
        PushVia.derive [ "A" ] BindingDeclared.NotDeclared field |> Expect.equal "a fixed field of an undeclared binding" PushVia.RebindWithCopy

    testCase "every way has a distinct token and the token reads back as the same way; anything else is refused with its own words" <| fun _ ->
      PushVia.all |> List.map PushVia.token |> List.distinct |> List.length |> Expect.equal "distinct" PushVia.all.Length
      for via in PushVia.all do
        PushVia.ofToken (PushVia.token via) |> Expect.equal "round trip" (Ok via)
      match PushVia.ofToken "poke" with
      | Error why -> SageFs.ConfigError.describe why |> Expect.stringContains "names the word" "poke"
      | Ok via -> failtestf "'poke' read as %A" via

    testCase "the statements: assign a mutable place where it is, rebind a plain one, and copy a record for a field that cannot be assigned" <| fun _ ->
      statement PushVia.RebindBinding (top "gravity") "9.5" |> Expect.equal "rebind" "let gravity = 9.5;;"
      statement PushVia.AssignBinding (top "hp") "62" |> Expect.equal "assign the binding" "hp <- 62;;"
      statement PushVia.AssignField (part [ "A"; "B" ]) "13.5" |> Expect.equal "assign the field on the instance the program holds" "feel.A.B <- 13.5;;"
      statement PushVia.RebindWithCopy (part [ "A"; "B" ]) "13.5" |> Expect.equal "copy and rebind" "let feel = { feel with A = { feel.A with B = 13.5 } };;"
      statement PushVia.AssignBindingWithCopy (part [ "A" ]) "13.5" |> Expect.equal "copy and assign" "feel <- { feel with A = 13.5 };;"

    testPropertyWithConfig propConfig "WHY — a push that assigns never rebinds, and one that rebinds never assigns, for every way and every depth of field"
      (Prop.forAll (Arb.fromGen (Gen.zip (Gen.elements PushVia.all) (Gen.choose (0, 3)))) (fun (via, depth) ->
        let key = { Binding = "feel"; Labels = List.init depth (fun i -> sprintf "F%d" i) }
        let code = statement via key "1"
        isAssigning via = (code.Contains " <- " && not (code.StartsWith "let "))))

    testCase "the twin that always rebinds fails the assignment law: a mutable field pushed by rebinding never reaches the instance the program holds" <| fun _ ->
      let key = part [ "A"; "B" ]
      statement PushVia.AssignField key "1" |> Expect.stringContains "the real statement assigns" " <- "
      (alwaysRebinds key "1").Contains " <- " |> Expect.isFalse "the twin does not, which is the mistake the law catches"
  ]

[<Tests>]
let planTests =
  testList "what a typed value becomes: held to the literal's own kind and type, as a write to the file is" [

    testCase "each kind of literal takes a value of its own kind and keeps the file's spelling" <| fun _ ->
      codeOf (plan PushVia.RebindBinding (top "gravity") "9.8" "9.5") |> Expect.equal "a real" "let gravity = 9.5;;"
      codeOf (plan PushVia.AssignBinding (top "hp") "100" "62") |> Expect.equal "an integer" "hp <- 62;;"
      codeOf (plan PushVia.RebindBinding (top "mask") "0xFF" "16") |> Expect.stringContains "the file's hex spelling is kept" "0x"
      codeOf (plan PushVia.RebindBinding (top "title") "\"Nudge\"" "Nudge2") |> Expect.equal "a string keeps its quotes" "let title = \"Nudge2\";;"
      codeOf (plan PushVia.RebindBinding (top "mode") "Easy" "Hard") |> Expect.equal "a union case" "let mode = Hard;;"

    testCase "the text the walk will print for the pushed value is what the row compares, and the statement is ready to evaluate" <| fun _ ->
      match plan PushVia.RebindBinding (top "title") "\"Nudge\"" "Nudge2" with
      | Ok planned ->
        planned.Printed |> Expect.equal "the walk prints a string with quotes" "\"Nudge2\""
        planned.Effect |> Expect.equal "a different value is a tweak" PushEffect.StartsTweak
      | Error refusal -> failtestf "refused: %A" refusal

    testCase "a typed value that is the file's own value is not a tweak: the row has nothing to say about it" <| fun _ ->
      match plan PushVia.RebindBinding (top "gravity") "9.8" "9.8" with
      | Ok planned -> planned.Effect |> Expect.equal "it returns to the file" PushEffect.MatchesFile
      | Error refusal -> failtestf "refused: %A" refusal

    testCase "a value of the wrong kind or type is refused with the door's own rule, and a formula is not a value to try" <| fun _ ->
      codeOf (plan PushVia.RebindBinding (top "gravity") "9.8" "fast") |> Expect.stringContains "a real wanted" "refused: "
      (match plan PushVia.RebindBinding (top "b") "false" "maybe" with
       | Error(NudgeRefusal.LiteralNotReadable(kind, given)) ->
         kind |> Expect.equal "a boolean was wanted" LiteralKindName.Boolean
         given |> Expect.equal "what was typed" "maybe"
       | other -> failtestf "expected the door's refusal, got %A" other)
      (match plan PushVia.RebindBinding (top "jump") "gravity * 2.0" "3" with
       | Error(NudgeRefusal.NotALiteral shown) -> shown |> Expect.equal "the formula" "gravity * 2.0"
       | other -> failtestf "expected NotALiteral, got %A" other)
      (match plan PushVia.RebindBinding (top "small") "5uy" "300" with
       | Error(NudgeRefusal.ValueOutOfRange _) -> ()
       | other -> failtestf "a byte cannot hold 300: %A" other)

    testPropertyWithConfig propConfig "WHY — the text a push records is exactly what the live-values walk prints for that value, so a tried value reads as tweaked and never as something else"
      (Prop.forAll (Arb.fromGen (Gen.choose (-100000, 100000) |> Gen.map (fun n -> float n / 100.0))) (fun d ->
        let typed = d.ToString("R", CultureInfo.InvariantCulture)
        match plan PushVia.RebindBinding (top "gravity") "9.8" typed with
        | Ok planned -> planned.Printed = (LiveValueTree.buildValueNode "v" (box d)).Preview
        | Error _ -> false))

    testPropertyWithConfig propConfig "an integer pushed prints as the walk prints it"
      (Prop.forAll (Arb.fromGen (Gen.choose (-100000, 100000))) (fun n ->
        match plan PushVia.RebindBinding (top "hp") "100" (string n) with
        | Ok planned -> planned.Printed = (LiveValueTree.buildValueNode "v" (box (int64 n))).Preview
        | Error _ -> false))

    testCase "a revert puts the file's own literal back, spelled as the file spells it, and ends the tweak" <| fun _ ->
      match revert PushVia.AssignBinding (top "hp") " 100 " with
      | Ok planned ->
        planned.Code |> Expect.equal "the file's text, trimmed" "hp <- 100;;"
        planned.Effect |> Expect.equal "nothing is tried any more" PushEffect.MatchesFile
      | Error refusal -> failtestf "refused: %A" refusal
      match revert PushVia.RebindBinding (top "jump") "gravity * 2.0" with
      | Error(NudgeRefusal.NotALiteral _) -> ()
      | other -> failtestf "a formula is not reverted by value: %A" other

    testCase "several statements are one interaction: only the last carries the `;;`, and a statement without one is left as it is" <| fun _ ->
      oneInteraction [ "let gravity = 9.5;;"; "lives <- 3;;" ] |> Expect.equal "two statements" "let gravity = 9.5\nlives <- 3;;"
      oneInteraction [ "lives <- 3;;" ] |> Expect.equal "one statement" "lives <- 3;;"
      oneInteraction [ "lives <- 3 ;; "; "x <- 1" ] |> Expect.equal "trailing spaces and a missing terminator" "lives <- 3\nx <- 1;;"

    testPropertyWithConfig propConfig "WHY — a batch of any statements a push can make is exactly one `;;`, at the end, because the dashboard's eval runs only the first interaction of what it is given"
      (Prop.forAll (Arb.fromGen (Gen.listOfLength 3 (Gen.zip (Gen.elements PushVia.all) (Gen.choose (0, 2))))) (fun parts ->
        let statements =
          parts |> List.mapi (fun i (via, depth) -> statement via { Binding = sprintf "b%d" i; Labels = List.init depth (fun d -> sprintf "F%d" d) } "1")
        let one = oneInteraction statements
        one.EndsWith ";;" && one.Split([| ";;" |], StringSplitOptions.None).Length = 2))

    testCase "a write takes the value, not its spelling: a string and a character lose their quotes, the rest are as printed" <| fun _ ->
      doorText "\"Nudge\"" "\"Nudge2\"" |> Expect.equal "string" "Nudge2"
      doorText "'a'" "'b'" |> Expect.equal "char" "b"
      doorText "9.8" "9.5" |> Expect.equal "real" "9.5"
      doorText "false" "true" |> Expect.equal "bool" "true"
      doorText "Easy" "Hard" |> Expect.equal "case" "Hard"
  ]
