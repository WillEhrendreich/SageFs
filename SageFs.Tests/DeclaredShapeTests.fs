module SageFs.Tests.DeclaredShapeTests

/// WHY — the migration's NEW side can only come from source text, because the
/// app is still running the OLD assembly when a type changes and the new one has
/// no compiled type yet. A field's KIND is not readable from that text
/// (measured: `SynField` exposes `fieldType` but has no nameable case field), so
/// the question these answer is whether NAMES are enough to decide.
///
/// They are, and that is not obvious until it is stated: a kind we cannot read
/// is `Undecidable`, and two `Undecidable` kinds are EQUAL, so the name
/// comparison decides the carried fields rather than refusing all of them. The
/// consequence — that a field whose kind actually CHANGED is not caught here —
/// is stated in `MigrationPlan.DeclaredShape` and pinned below, because a test
/// suite that only asserts the happy path would let it be discovered in
/// production instead.

open Expecto
open Expecto.Flip
open SageFs
open SageFs.MigrationPlan

let private oldOrder = { DeclaredShape.TypeName = "Order"; FieldNames = [ "Id"; "Name" ] }
let private newOrderSame = { DeclaredShape.TypeName = "Order"; FieldNames = [ "Id"; "Name" ] }
let private newOrderPlus = { DeclaredShape.TypeName = "Order"; FieldNames = [ "Id"; "Name"; "Note" ] }
let private newOrderRenamed = { DeclaredShape.TypeName = "Order"; FieldNames = [ "Id"; "Title" ] }
let private noDefaults _ = false
let private noteDefaults name = name = "Note"

let private isCarried result =
  match result with
  | SageFs.Holder.Migration.Carried _ -> true
  | SageFs.Holder.Migration.Refused _ -> false

let private refusalOf result =
  match result with
  | SageFs.Holder.Migration.Refused why -> why
  | SageFs.Holder.Migration.Carried _ -> "<carried>"

[<Tests>]
let declaredShapeTests =
  testList "a type's shape, compared from its declared names" [

    testCase "WHY — an UNCHANGED type carries, which is the whole point of having the old shape at all" <| fun _ ->
      decideFromNames oldOrder newOrderSame noDefaults
      |> isCarried
      |> Expect.isTrue "nothing changed, so the value is carried"

    testCase "WHY — a field ONLY in the new shape is refused when nothing defaults it, rather than invented" <| fun _ ->
      let why = decideFromNames oldOrder newOrderPlus noDefaults |> refusalOf
      (why.Contains "Note")
      |> Expect.isTrue "and the refusal names the field that blocked it"

    testCase "WHY — an added field whose NAME establishes no type still refuses, even when defaulted" <| fun _ ->
      // This looked like a bug in the product and was not. Relaxing
      // `decideField` so an undecidable-but-defaulted NEW field could be added
      // broke two existing tests — "a default does not rescue an undecidable
      // field" and the DST invariant "no migration ever claims a carry over
      // something undecidable" — and both were right. A default says what to put
      // in a field we UNDERSTAND; it does not tell us the type. So the refusal
      // stands, and `Note` is simply a name this tier cannot type.
      let why = decideFromNames oldOrder newOrderPlus noteDefaults |> refusalOf
      (why.Contains "Note")
      |> Expect.isTrue "the added field is refused and named, and the default does not rescue it"

    testCase "WHY — a RENAMED field is a new field, so it is refused unless the new one defaults" <| fun _ ->
      // `Name` became `Title`. A rename is a drop plus an add, and the two must
      // not be mistaken for a carried field — that would carry a string into a
      // value whose field means something else.
      let why = decideFromNames oldOrder newOrderRenamed noDefaults |> refusalOf
      (why.Contains "Title")
      |> Expect.isTrue "the added side of a rename is refused, and named"

    testCase "WHY — a field REMOVED by the edit is not a refusal: the new shape has no such field to carry into" <| fun _ ->
      // The one direction that is genuinely safe, and the reason a removed field
      // must not be treated like an added one.
      let newShape = { DeclaredShape.TypeName = "Order"; FieldNames = [ "Id" ] }
      decideFromNames oldOrder newShape noDefaults
      |> isCarried
      |> Expect.isTrue "dropping a field loses nothing the new shape needs"

    testCase "WHY — the NAME-ONLY tier cannot see a changed KIND, and that limit is pinned rather than assumed" <| fun _ ->
      // `Id: int` becoming `Id: string` keeps the NAME, so the name comparison
      // carries it. The compiled tier catches this when a live value supplies
      // both types; the source tier cannot, and pretending otherwise would make
      // this test suite claim a guarantee it does not give.
      let newShape = { DeclaredShape.TypeName = "Order"; FieldNames = [ "Id"; "Name" ] }
      decideFromNames oldOrder newShape noDefaults
      |> isCarried
      |> Expect.isTrue "a changed kind is INVISIBLE here — stated, not hidden"

    testCase "WHY — the refusal carries EVERY blocked field, so a user is told what to fix" <| fun _ ->
      let newShape = { DeclaredShape.TypeName = "Order"; FieldNames = [ "A"; "B"; "C" ] }
      let why = decideFromNames oldOrder newShape noDefaults |> refusalOf
      (why.Contains "'A'")
      |> Expect.isTrue "the first blocked field is named"
      (why.Contains "'C'")
      |> Expect.isTrue "and so is the last, not just the first"  ]
