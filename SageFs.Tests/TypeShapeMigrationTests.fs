module SageFs.Tests.TypeShapeMigrationTests

/// WHY — `type-migration-direction.md` step 3. A migration that silently
/// produces a wrong value is WORSE than the restart SageFs does today, so the
/// whole design is "recognise a decidable subset and refuse everything else".
///
/// These tests pin that rule from both sides: the shapes that must migrate
/// (Tier 0) and the shapes that must refuse, because a refusal that quietly
/// succeeds is the failure mode the rule exists to prevent.
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Holder
// The shape vocabulary lives in its own module, so every field/kind is
// qualified: opening it would shadow the DU cases with the module.
open SageFs.TypeShapeMigration

let private intField (name: string) : Field = { Name = name; Kind = FieldKind.IntField }
let private boolField (name: string) : Field = { Name = name; Kind = FieldKind.BoolField }

/// A record that gained a defaulted field — the Tier 0 shape the design names.
let private v1 : RecordShape =
  { TypeName = "Config"
    Fields = [ intField "Retries"; intField "TimeoutSeconds" ] }

let private v2 : RecordShape =
  { TypeName = "Config"
    Fields = [ intField "Retries"; intField "TimeoutSeconds"; boolField "Trace" ] }

let private traceHasDefault name = name = "Trace"

[<Tests>]
let typeShapeMigrationTests = testList "type-shape migration" [

  testCase "WHY — a record that gained a DEFAULTED field migrates, because the default is the new definition's own" <| fun _ ->
    match TypeShapeMigration.decideRecord v1 v2 traceHasDefault with
    | Migration.Carried(_, into) ->
      into.Fields.Length
      |> Expect.equal "the new shape is what the holder adopts" 3
    | Migration.Refused why -> failtestf "a defaulted field must migrate, but it refused: %s" why

  testCase "WHY — a new field with NO default refuses, because the old record cannot supply a value it never held" <| fun _ ->
    let noDefaults _ = false

    match TypeShapeMigration.decideRecord v1 v2 noDefaults with
    | Migration.Refused why ->
      why
      |> Expect.stringContains "it must name the field that has no default" "Trace"
    | Migration.Carried(_, _) -> failtest "a field with no default must never be invented"

  testCase "WHY — a field whose KIND changed refuses, because the old bytes are not the new meaning" <| fun _ ->
    let retyped = { TypeName = "Config"; Fields = [ intField "Retries"; boolField "TimeoutSeconds" ] }

    match TypeShapeMigration.decideRecord v1 retyped traceHasDefault with
    | Migration.Refused why ->
      why
      |> Expect.stringContains "it must name the kind change" "kind changed"
    | Migration.Carried(_, _) -> failtest "a retyped field must never be carried"

  testCase "WHY — an undecidable field kind refuses the whole migration" <| fun _ ->
    let undecidable =
      { TypeName = "Config"
        Fields = [ intField "Retries"; { Name = "Opaque"; Kind = FieldKind.Undecidable "a byref-like field" } ] }

    match TypeShapeMigration.decideRecord v1 undecidable traceHasDefault with
    | Migration.Refused _ -> ()
    | Migration.Carried(_, _) -> failtest "an undecidable field must never be carried"

  testCase "WHY — a DEFAULT does not rescue an undecidable field, because a default only speaks for a type we understood" <| fun _ ->
    // Found by the DST at seed 27: `Undecidable` sat behind the defaulted path
    // and the whole record was reported as carried. "We cannot tell what this
    // field is" and "we know it, and its default is right" are different
    // claims, and only the second one is safe to act on.
    let undecidableButDefaulted =
      { TypeName = "Config"
        Fields = [ intField "Retries"; { Name = "Opaque"; Kind = FieldKind.Undecidable "a byref-like field" } ] }

    let everyNameHasADefault _ = true

    match TypeShapeMigration.decideRecord v1 undecidableButDefaulted everyNameHasADefault with
    | Migration.Refused why ->
      why
      |> Expect.stringContains "and it must say the kind is undecidable" "undecidable"
    | Migration.Carried(_, _) -> failtest "a default must not rescue an undecidable field"

  testCase "WHY — the per-field trace names every field, so a save can say which moved" <| fun _ ->
    let tiers = TypeShapeMigration.tiersOf v1 v2 traceHasDefault

    let described = tiers |> List.map (fun (name, tier) -> sprintf "%s=%s" name (TypeShapeMigration.describeTier tier))

    // The repo idiom: the value under test leads, the message and the
    // expected substring follow.
    let describedText = String.concat " " described

    Expect.stringContains "an unchanged field is reported as carried" "Retries=carried" describedText

    Expect.stringContains
      "a defaulted field is reported as added"
      "Trace=added from the new definition's default"
      describedText

  testCase "WHY — the closed kind vocabulary cannot be extended without a decision" <| fun _ ->
    // Each kind must be decidable: Carried when equal, Refused otherwise. If a
    // new kind is added, `FieldTier.decide` is the only place that must learn
    // about it, and the equality arm is what carries that guarantee.
    let carried = TypeShapeMigration.decideField (TypeShapeMigration.fieldIndex v1) (intField "Retries") true
    let refused = TypeShapeMigration.decideField (TypeShapeMigration.fieldIndex v1) (boolField "Retries") true

    carried |> Expect.equal "a matching field is carried" FieldTier.Carried

    match refused with
    | FieldTier.Refused why ->
      why |> Expect.stringContains "a mismatched field refuses, and says why" "kind changed"
    | other -> failtestf "a mismatched field must refuse, got %A" other
]
