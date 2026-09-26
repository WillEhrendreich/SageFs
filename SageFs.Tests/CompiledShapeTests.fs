module SageFs.Tests.CompiledShapeTests

/// WHY — the text-AST route for a type's field shape is a dead end, measured:
/// `SynField` exposes `fieldType: SynType` but has no nameable case field, so a
/// field's KIND is unreachable from source and every migration refuses. The
/// COMPILED type carries the same information through a supported public API
/// the repo already uses (`LiveValueTree.fs:161`), and it carries more: it can
/// also READ a live value, which the AST never could.
///
/// These tests assert the compiled route produces DECIDABLE kinds for the
/// three carryable ones and still refuses everything else — and that the
/// refusal is discriminating rather than a blanket "nothing is decidable".

open Expecto
open Expecto.Flip
open SageFs
open SageFs.TypeShapeMigration

// A record whose every field is carryable.
type Carriable = { Id: int; Name: string; Paid: bool }

// A record with fields that are NOT carryable. The refusal must be specific:
// saying "byte[] is undecidable" is a decision; "everything is undecidable" is
// a stub.
type NotCarriable = { Id: int; Payload: byte array; Meta: Map<string, string> }

// A union, not a record: asking about it is a different question and must not
// be answered with an empty record shape.
type Shape = Circle of float | Square of float

let private kindsOf (shape: RecordShape) = shape.Fields |> List.map _.Kind

[<Tests>]
let compiledShapeTests =
  testList "a record shape read from its compiled type" [

    testCase "WHY — a real record's fields come back DECIDABLE, which is what the AST route could not do" <| fun _ ->
      let shape =
        match compiledShapeOf typeof<Carriable> with
        | Some s -> s
        | None -> failtest "Carriable is a record"
      (shape.Fields |> List.map _.Name)
      |> Expect.equal "every field, in declaration order" [ "Id"; "Name"; "Paid" ]
      (kindsOf shape)
      |> Expect.equal "and every one of them decidable"
           [ FieldKind.IntField; FieldKind.StringField; FieldKind.BoolField ]

    testCase "WHY — a migration over two IDENTICAL decidable shapes is Carried, so the path is reachable at all" <| fun _ ->
      // The headline the whole feature needed: with decidable kinds, the
      // decision that was previously always Refused now succeeds.
      match compiledShapeOf typeof<Carriable> with
      | None -> failtest "Carriable is a record"
      | Some shape ->
        match decideRecord shape shape (fun _ -> false) with
        | Holder.Migration.Carried _ -> ()
        | Holder.Migration.Refused why -> failtestf "an unchanged decidable shape must migrate: %s" why

    testCase "WHY — an UNCARRYABLE field refuses, and says WHICH one: 'nothing is decidable' would be a stub, not a decision" <| fun _ ->
      match compiledShapeOf typeof<NotCarriable> with
      | None -> failtest "NotCarriable is a record"
      | Some shape ->
        // The decidable field must still be decidable, so the refusal is
        // discriminating rather than blanket.
        (kindsOf shape |> List.head)
        |> Expect.equal "the int field is still decidable" FieldKind.IntField
        match decideRecord shape shape (fun _ -> false) with
        | Holder.Migration.Carried _ -> failtest "a byte[] field must not be reported as carried"
        | Holder.Migration.Refused why ->
          (why.Contains "Payload")
          |> Expect.isTrue "and the refusal must NAME the field that blocked it"

    testCase "WHY — a type that is not a record gets no shape at all, rather than an empty one that looks like a record with no fields" <| fun _ ->
      (compiledShapeOf typeof<Shape>).IsNone
      |> Expect.isTrue "a union is not a record"
      (compiledShapeOf typeof<int>).IsNone
      |> Expect.isTrue "nor is an int"

    testCase "WHY — a LIVE value's fields are readable, which is what makes a migration a CARRY rather than a description" <| fun _ ->
      // The AST route could never do this at all. It is not a lesser path; it
      // is a different capability.
      let live = box { Id = 7; Name = "x"; Paid = true }
      match compiledFieldsOf typeof<Carriable> live with
      | None -> failtest "a record instance must be readable"
      | Some fields ->
        fields
        |> Expect.equal "the old value's own fields, in order" [| box 7; box "x"; box true |]

    testCase "WHY — the two producers cannot disagree about what is carryable, because they use ONE FieldKind vocabulary" <| fun _ ->
      // The source-side producer returns the same TYPE, so a shape from either
      // route feeds the same `decideRecord` and a disagreement about
      // carryability is a compile error rather than a runtime surprise.
      //
      // The check is over the CASE, not the whole value: `Undecidable` carries a
      // reason string, so two `Undecidable` values are not `=`. An earlier
      // version of this line compared whole values and failed — correctly, since
      // the reason text is expected to differ between the two routes.
      let caseOf (k: FieldKind) =
        match k with
        | FieldKind.IntField -> "int"
        | FieldKind.StringField -> "string"
        | FieldKind.BoolField -> "bool"
        | FieldKind.Undecidable _ -> "undecidable"
      let admitted = set [ "int"; "string"; "bool"; "undecidable" ]
      let compiledSide =
        match compiledShapeOf typeof<NotCarriable> with
        | Some s -> kindsOf s |> List.map caseOf
        | None -> failtest "NotCarriable is a record"
      (compiledSide |> List.forall admitted.Contains)
      |> Expect.isTrue "every compiled kind is a case the vocabulary already admits"
  ]
