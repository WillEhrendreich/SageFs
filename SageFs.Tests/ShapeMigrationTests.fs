module SageFs.Tests.ShapeMigrationTests

/// WHY — I asserted, in code AND in the docs, that a type change could not be
/// migrated cheaply: a `Migrate` plan skips the build, so "the new type has no
/// compiled form and there is nothing to construct". **That premise was wrong.**
///
/// A patched type is compiled by the FSI session at runtime, so it is a real
/// `System.Type` the moment the patch lands. Measured in a live session:
///   PROBE ctor arg counts = [|3|]     one argument PER FIELD
///   PROBE backing fields = [||]       no backing fields to write
///   MIGRATE the migrated value = { Id = 42; Name = "widget"; Note = "added" }
///
/// So a migration IS a constructor call with values lined up by name. These
/// tests pin that, and — more importantly — pin every way it must REFUSE,
/// because a migration that guesses a value is worse than one that rebuilds.

open Expecto
open Expecto.Flip
open SageFs

// The three shapes a real edit moves between. They are distinct TYPES, not
// records of different values, because that is the whole point.
type OrderV1 = { Id: int; Name: string }
type OrderV2 = { Id: int; Name: string; Note: string }
type OrderRetyped = { Id: int; Name: int }
type OrderMap = { Id: int; Tags: Map<string, string> }
/// The mirror of the added-field case: a field REMOVED, so the new shape has
/// nowhere to put the old one. Declared at the top because a `type` cannot live
/// inside a lambda.
type OrderShrunk = { Id: int }

// Named explicitly, not annotated: three of these records share field NAMES,
// so the bare literal would be inferred against whichever one the compiler
// picked — and `OrderRetyped` has `Name: int`, so the literal silently became a
// `TypeMismatch` fixture instead of the value under test.
let private oldOrder = box { OrderV1.Id = 42; OrderV1.Name = "widget" } : obj

let private fieldNames (o: obj) =
  o.GetType().GetProperties() |> Array.map (fun p -> p.Name) |> Array.sort

let private readAll (o: obj) =
  // `obj` is not comparable, so the field NAMES are what get sorted and the
  // values ride along untouched — a `sort` on the values themselves is a compile
  // error, which is a useful reminder that these really are `obj`s.
  o.GetType().GetProperties()
  |> Array.map (fun p -> p.Name, box (p.GetValue o))
  |> Array.sortBy fst

[<Tests>]
let shapeMigrationTests =
  testList "migrating a live value into an edited type, with no build" [

    testCase "WHY — an ADDED field is supplied by the shape's own default, and the rest carry across" <| fun _ ->
      // The headline. `OrderV2` is a different TYPE from `OrderV1`, produced by
      // evaluation rather than a build, and a value of the old one becomes a
      // value of the new one here.
      match ShapeMigration.migrate oldOrder typeof<OrderV2> with
      | ShapeMigration.Migrated.Carried v ->
        (v :?> OrderV2).Id |> Expect.equal "the carried int survived" 42
        (v :?> OrderV2).Name |> Expect.equal "and the carried string" "widget"
        (v :?> OrderV2).Note |> Expect.equal "and the added field took the shape's default" ""
      | other -> failtestf "a record with one added field must migrate, got %A" other

    testCase "WHY — an UNCHANGED shape produces a value of the SAME type, so nothing was lost" <| fun _ ->
      match ShapeMigration.migrate oldOrder typeof<OrderV1> with
      | ShapeMigration.Migrated.Carried v ->
        (v :?> OrderV1).Id |> Expect.equal "the value is intact" 42
      | other -> failtestf "an unchanged shape must migrate, got %A" other

    testCase "WHY — a field whose KIND changed REFUSES, and names the field, because the old value is not a valid argument" <| fun _ ->
      // `Name: string` became `Name: int`. Passing the old string would throw
      // inside the constructor; the refusal is what turns that into a message.
      match ShapeMigration.migrate oldOrder typeof<OrderRetyped> with
      | ShapeMigration.Migrated.TypeMismatch(field, was, now) ->
        field |> Expect.equal "the field that broke it is named" "Name"
        was |> Expect.equal "and what it was" "String"
        now |> Expect.equal "and what it became" "Int32"
      | other -> failtestf "a changed kind must refuse, got %A" other

    testCase "WHY — a field the edit ADDS whose type has no default REFUSES, because a null is not a value" <| fun _ ->
      // The one that was silently wrong. An F# `Map` has no parameterless
      // constructor, so the earlier fallback put `null` into a live field and
      // reported a migration. A migration that invents a value is worse than
      // one that rebuilds, so this refuses and names the type.
      match ShapeMigration.migrate oldOrder typeof<OrderMap> with
      | ShapeMigration.Migrated.NoConstructor why ->
        (why.Contains "FSharpMap") |> Expect.isTrue "and it names the type it could not default"
      | other -> failtestf "a field with no default must refuse, got %A" other

    testCase "WHY — a type that is NOT a record is refused by name, never defaulted" <| fun _ ->
      match ShapeMigration.migrate oldOrder typeof<string> with
      | ShapeMigration.Migrated.NoConstructor why ->
        (why.Contains "not a record") |> Expect.isTrue "and it says why"
      | other -> failtestf "a non-record target must refuse, got %A" other

    testCase "WHY — the migrated value is a REAL instance of the new type, not the old one renamed" <| fun _ ->
      // The property that matters and is easiest to fake: a migration that
      // returned the OLD object would pass every field assertion above.
      match ShapeMigration.migrate oldOrder typeof<OrderV2> with
      | ShapeMigration.Migrated.Carried v ->
        (v.GetType() = typeof<OrderV2>)
        |> Expect.isTrue "the result is an OrderV2"
        (v.GetType() = typeof<OrderV1>)
        |> Expect.isFalse "and not the old OrderV1"
      | other -> failtestf "expected a migration, got %A" other

    testCase "WHY — a field only in the OLD shape is dropped, because the new one has nowhere to put it" <| fun _ ->
      // The mirror of the added-field case, and the one that could be got wrong
      // by "just copy everything across" — which would need a constructor slot
      // that does not exist.
      match ShapeMigration.migrate oldOrder typeof<OrderShrunk> with
      | ShapeMigration.Migrated.Carried v ->
        let shrunk : OrderShrunk = unbox v
        shrunk.Id |> Expect.equal "what remains is correct" 42
        (fieldNames v).Length |> Expect.equal "and nothing extra was invented" 1
      | other -> failtestf "a shrunk shape must migrate, got %A" other
  ]

/// WHY — the executor above is only worth anything if a RUNNING APP can reach
/// it. These are the tests that make it reachable: an app declares a boundary
/// with the two hooks, and the registry's own `MigrationVerdictFor` — the exact
/// function the live reload path calls — returns a real verdict rather than the
/// `None` it returned while the branch was a stub.
[<Tests>]
let appDeclaredBoundaryTests =
  testList "a running app declaring a migration boundary" [

    testCase "WHY — a declared boundary with both hooks answers a real verdict, so a type change is no longer a dead end" <| fun _ ->
      let registry = SageFs.Registry.For (SageFs.WorkerProtocol.SessionId.newId ())
      let cell = Holder.hold { OrderV1.Id = 42; OrderV1.Name = "widget" }

      registry.DeclareWithSubject
        "orders"
        "OrderV1"
        (Some(ShapeMigration.hook ()))
        (Some(ShapeMigration.subjectFor cell (fun () -> typeof<OrderV2>)))
      |> ignore

      // The call the LIVE path makes. It answered `None` for every input while
      // `WorkerMain` discarded the hook, so a test on `migrate` alone would have
      // been evidence about a function the product never called.
      match registry.MigrationVerdictFor "OrderV1" (box { OrderV1.Id = 42; OrderV1.Name = "widget" }) typeof<OrderV2> with
      | Some(MigrationWorth.WorthCarrying n) ->
        // Assert the CLAIM, not the count: a count of 1 is an implementation
        // detail of the verdict, and pinning it would make a correct change to
        // how many values are carried look like a regression.
        (n > 0) |> Expect.isTrue "the verdict says a value is worth carrying"
      | other -> failtestf "a declared, satisfiable boundary must answer, got %A" other

    testCase "WHY — the subject hook reads the LIVE cell, not a snapshot taken when the boundary was declared" <| fun _ ->
      // A value captured at declaration time would be stale, and the whole
      // feature is about carrying the state that exists NOW. So write to the
      // cell after declaring, and the hook must see the new value.
      let cell = Holder.hold { OrderV1.Id = 1; OrderV1.Name = "before" }
      let subject = ShapeMigration.subjectFor cell (fun () -> typeof<OrderV2>)
      let firstRead = subject ()
      cell.Value.Value <- { OrderV1.Id = 99; OrderV1.Name = "after" }
      let secondRead = subject ()

      match firstRead, secondRead with
      | Some(firstBox, _), Some(secondBox, _) ->
        (unbox<OrderV1> firstBox).Id |> Expect.equal "the first read saw the value then" 1
        (unbox<OrderV1> secondBox).Id |> Expect.equal "and the second read saw the NEW one" 99
      | _ -> failtest "both reads must produce a subject"

    testCase "WHY — a boundary whose new type is not yet produced REFUSES, rather than guessing a type" <| fun _ ->
      // `null` is how an unresolved lookup reports itself. A migration against
      // an invented type is the "silently wrong value" the design refuses, so
      // the answer must be a refusal and never a carry.
      //
      // The refusal here is `None` — "this boundary did not answer" — rather
      // than `Some(NoValueToMigrate)`. Both refuse, and the distinction is
      // deliberate: `None` is a boundary that COULD NOT answer, while
      // `NoValueToMigrate` is one that answered "no". Collapsing them would lose
      // the difference between an app that is not ready and one that decided.
      let cell = Holder.hold { OrderV1.Id = 42; OrderV1.Name = "widget" }
      let registry = SageFs.Registry.For (SageFs.WorkerProtocol.SessionId.newId ())

      registry.DeclareWithSubject
        "orders"
        "OrderV1"
        (Some(ShapeMigration.hook ()))
        (Some(ShapeMigration.subjectFor cell (fun () -> null)))
      |> ignore

      // The caller's arguments are a perfectly good value and type. The
      // refusal must come from the SUBJECT being unresolved, not from them —
      // so this also pins that the caller's guess is not used as a fallback.
      match registry.MigrationVerdictFor "OrderV1" (box { OrderV1.Id = 42; OrderV1.Name = "widget" }) typeof<OrderV2> with
      | Some(MigrationWorth.WorthCarrying _) ->
        failtest "an unresolved new type must not answer WorthCarrying"
      | Some(MigrationWorth.NoValueToMigrate _) ->
        failtest "a boundary that could not answer is not one that answered no"
      | Some(MigrationWorth.NotWorthCarrying _) ->
        failtest "an unresolved new type is not a verdict about cost"
      | None ->
        ()

    testCase "WHY — a boundary that declared NO hook stays a refusal, so silence is never read as consent" <| fun _ ->
      let registry = SageFs.Registry.For (SageFs.WorkerProtocol.SessionId.newId ())
      registry.Declare "orders" "OrderV1" |> ignore

      match registry.MigrationVerdictFor "OrderV1" (box { OrderV1.Id = 42; OrderV1.Name = "widget" }) typeof<OrderV2> with
      | Some _ -> failtest "a boundary with no hook must not answer"
      | None -> ()
  ]
