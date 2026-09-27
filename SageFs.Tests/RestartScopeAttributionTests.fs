module SageFs.Tests.RestartScopeAttributionTests

/// WHY — this is the seam that decides whether the type-migration work is
/// reachable AT ALL. Five modules (granular restart, holder, derived
/// migration, rewrite side conditions, translation validation) are built and
/// tested, and until this file exists none of them can narrow anything: every
/// `TypeChanged` becomes `TypeShapeChanged(name, RestartScope.Everything)`, so
/// a type change restarts the whole app exactly as it did before.
////
/// Proven in the live REPL on the real function, not just asserted here:
////
///   ReloadChange.restartReason (TypeChanged "Order")
///     = TypeShapeChanged ("Order", Everything)
////
/// even though `RestartScope.infer` takes a type NAME and would scope it. The
/// name is right there in the change and is thrown away.
////
/// These tests are the acceptance gate for that gap closing: if the planner
/// cannot attribute, it MUST stay at Everything. The safety direction matters
/// more than the win — a wrongly-narrowed restart leaves a half-restarted app
/// holding a value laid out by the old type.

open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.ReloadOutcome
open SageFs.Features.ReloadPlanning

let private orderUnits =
  [ { Name = "order-store"; DeclaresType = "Order"; DeclaresFields = None }
    { Name = "cart"; DeclaresType = "Cart"; DeclaresFields = None } ]

[<Tests>]
let restartScopeAttributionTests =
  testList "type-change restart scope attribution" [

    testCase "WHY — a type an owner declares narrows to that owner, instead of restarting everything" <| fun _ ->
      let reason = ReloadChange.restartReasonAttributed orderUnits (ReloadChange.TypeChanged "Order")
      reason
      |> Expect.equal "scoped to the unit that declares it" (RestartReason.TypeShapeChanged("Order", RestartScope.Scoped "order-store"))

    testCase "WHY — a type NO unit declares stays Everything, because attribution is never guessed" <| fun _ ->
      let reason = ReloadChange.restartReasonAttributed orderUnits (ReloadChange.TypeChanged "Todo")
      reason
      |> Expect.equal "unattributable is the safe direction" (RestartReason.TypeShapeChanged("Todo", RestartScope.Everything))

    testCase "WHY — with no units registered, EVERYTHING is the answer: a default that narrowed would be a lie" <| fun _ ->
      let reason = ReloadChange.restartReasonAttributed [] (ReloadChange.TypeChanged "Order")
      reason
      |> Expect.equal "no attribution available" (RestartReason.TypeShapeChanged("Order", RestartScope.Everything))

    testCase "WHY — a unit that does not declare the type does not capture it" <| fun _ ->
      // The sharp case: 'order-store' exists, but it declares Order, not Todo.
      // A name-substring or first-match implementation would scope this wrongly.
      let reason = ReloadChange.restartReasonAttributed orderUnits (ReloadChange.TypeChanged "Todo")
      reason
      |> Expect.equal "presence of other units must not capture an unrelated type" (RestartReason.TypeShapeChanged("Todo", RestartScope.Everything))

    testCase "WHY — attribution changes NOTHING about a non-type change" <| fun _ ->
      // A mutable's remedy is its own decision; scoping must not leak into it.
      let withUnits = ReloadChange.restartReasonAttributed orderUnits (ReloadChange.MutableStateChanged "counter")
      let withoutUnits = ReloadChange.restartReason (ReloadChange.MutableStateChanged "counter")
      withUnits
      |> Expect.equal "attribution is irrelevant to a mutable" withoutUnits

    testCase "WHY — the unattributed translation is still correct on its own, so existing callers are unharmed" <| fun _ ->
      ReloadChange.restartReason (ReloadChange.TypeChanged "Order")
      |> Expect.equal "the old entry point still means Everything" (RestartReason.TypeShapeChanged("Order", RestartScope.Everything))

    testCase "WHY — every declared unit is reachable, not just the first" <| fun _ ->
      [ "Order"; "Cart" ]
      |> List.iter (fun t ->
        let r = ReloadChange.restartReasonAttributed orderUnits (ReloadChange.TypeChanged t)
        match r with
        | RestartReason.TypeShapeChanged(_, RestartScope.Scoped _) -> ()
        | _ -> failtestf "'%s' should be attributable" t)

    testCase "WHY — RestartScope.infer is total: no type name ever throws or produces an invalid scope" <| fun _ ->
      [ "Order"; "Cart"; "Todo"; ""; "order-store" ]
      |> List.iter (fun t ->
        let s = RestartScope.infer orderUnits t
        (s = RestartScope.Scoped "cart" || s = RestartScope.Everything || s = RestartScope.Scoped "order-store")
        |> Expect.isTrue (sprintf "infer returned a valid scope for '%s'" t))
  ]

/// WHY — a value migration needs the OLD shape of a type, and nothing retained
/// it across an edit. `recordShapeOf` is the producer, and it reads the field
/// NAMES from the type's own parsed source rather than from reflection, because
/// reflection describes the NEW assembly and the whole question is what the
/// shape was BEFORE.
///
/// Every field it produces is `Undecidable` today, and that is the CORRECT
/// answer rather than a stub: the AST binding that carries a field's name does
/// not carry its type, and a guessed kind would carry a wrongly-typed value
/// into a live object. The undecidable answer refuses the migration, which is
/// the direction we already rebuild in.
[<Tests>]
let recordShapeTests =
  testList "a type's field shape, read from its own source" [

    testCase "WHY — the fields of a real record are read from real parsed source" <| fun _ ->
      let source = "module Shop\ntype Order = { Id: int; Name: string; Paid: bool }\n"
      let shapeOpt =
        match extractDecls source with
        | Error e -> failtestf "the fixture must parse: %s" e
        | Ok decls ->
          decls.Decls
          |> List.filter (fun d -> d.Name = "Order")
          |> List.map recordShapeOf
          |> List.tryHead
      match shapeOpt with
      | None -> failtest "the fixture must declare Order"
      | Some shape ->
        (shape.Fields |> List.map _.Name)
        |> Expect.equal "all three fields, in source order" [ "Id"; "Name"; "Paid" ]

    testCase "WHY — every field is Undecidable, which REFUSES the migration rather than guessing a kind" <| fun _ ->
      // The negative control that makes the refusal meaningful: if this passed
      // with a real IntField, a migration would carry an int into a value whose
      // kind we never established.
      let source = "module Shop\ntype Order = { Id: int; Name: string }\n"
      match extractDecls source with
      | Error e -> failtestf "the fixture must parse: %s" e
      | Ok decls ->
        let kindsOpt =
          decls.Decls |> List.filter (fun d -> d.Name = "Order")
          |> List.map recordShapeOf |> List.tryHead
          |> Option.map (fun s -> s.Fields |> List.map _.Kind)
        match kindsOpt with
        | None -> failtest "the fixture must declare Order"
        | Some fields ->
          fields
          |> List.iter (fun k ->
            match k with
            | SageFs.TypeShapeMigration.FieldKind.Undecidable _ -> ()
            | other -> failtestf "a field must not claim a kind we cannot read, got %A" other)

    testCase "WHY — and that refusal really does block the migration, so Undecidable is load-bearing" <| fun _ ->
      let source = "module Shop\ntype Order = { Id: int; Name: string }\n"
      let shapeOpt =
        match extractDecls source with
        | Error e -> failtestf "the fixture must parse: %s" e
        | Ok decls ->
          decls.Decls |> List.filter (fun d -> d.Name = "Order")
          |> List.map recordShapeOf |> List.tryHead
      match shapeOpt with
      | None -> failtest "the fixture must declare Order"
      | Some shape ->
        // A migration over UNCHANGED shapes with undecidable fields must refuse.
        // If it did not, "undecidable" would be decorative and a wrong-typed
        // value could be carried into a live object.
        let reported = SageFs.TypeShapeMigration.decideRecord shape shape (fun _ -> false)
        match reported with
        | SageFs.Holder.Migration.Carried _ -> failtest "an undecidable field must not report a carried value"
        | SageFs.Holder.Migration.Refused _ -> ()

    testCase "WHY — a type that is not a record yields NO fields, never an exception" <| fun _ ->
      let source = "module Shop\ntype Marker = class end\ntype Order = { Id: int }\n"
      match extractDecls source with
      | Error e -> failtestf "the fixture must parse: %s" e
      | Ok decls ->
        let marker =
          decls.Decls
          |> List.filter (fun d -> d.Name = "Marker")
          |> List.map recordShapeOf
        match marker with
        | [] -> failtest "the fixture must declare Marker"
        | [ shape ] ->
          (shape.TypeName, shape.Fields)
          |> Expect.equal "a class declares no record fields" ("Marker", [])
        | many -> failtestf "expected exactly one Marker, got %d" many.Length

    testCase "WHY — text that does not parse yields an empty shape, because a caller asking about one type must not fail on another" <| fun _ ->
      let bogus = { Name = "Broken"; Kind = DeclKind.TypeDecl; Access = DeclAccess.Public
                    Container = []; Header = ""; Text = "this is ((( not F#"; StartLine = 0; EndLine = 0 }
      let shape = recordShapeOf bogus
      (shape.TypeName, shape.Fields)
      |> Expect.equal "total, not partial" ("Broken", [])
  ]
