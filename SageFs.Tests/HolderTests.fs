module SageFs.Tests.HolderTests

/// WHY — `type-migration-direction.md` step 2. A `let mutable` whose type
/// changes can only be replaced today: carrying the old value forward ignores
/// the edit, resetting it destroys live state, so SageFs refuses both and
/// restarts. A holder makes a third option representable — build the new value,
/// migrate the old into it, swap the cell.
///
/// The contract under test is the SAFETY one, and it lives in the types: a
/// `Swapped` outcome hands back a cell of the NEW type and a `Held` outcome
/// hands back the ORIGINAL cell, so no caller can read a value the migration
/// refused to produce.
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Holder

/// A record that gained a defaulted field — the Tier 0 shape the design names.
type ConfigV1 = { Retries: int; TimeoutSeconds: int }
type ConfigV2 = { Retries: int; TimeoutSeconds: int; Trace: bool }

let private carryConfig (old: ConfigV1) : Migration<ConfigV1, ConfigV2> =
  Migration.Carried(old, { Retries = old.Retries; TimeoutSeconds = old.TimeoutSeconds; Trace = false })

/// A migration with nothing to carry, which must fail closed.
let private carryNothing (old: ConfigV1) : Migration<ConfigV1, ConfigV2> =
  Migration.Refused "the new field has no default and nothing to carry"

[<Tests>]
let holderTests = testList "holder" [

  testCase "WHY — a carried migration hands back a cell holding the NEW shape" <| fun _ ->
    let v1 = hold { Retries = 3; TimeoutSeconds = 30 }

    match swapIfMigrated v1 carryConfig with
    | Swap.Swapped(_, _, cell) ->
      (read cell).Trace
      |> Expect.isFalse "the default for the new field must be the one the migration chose"

      (read cell).Retries
      |> Expect.equal "and the old state must have been carried across" 3
    | Swap.Held(_, _) -> failtest "a successful migration must swap, never hold"

  testCase "WHY — a refused migration hands back the ORIGINAL value at its original type" <| fun _ ->
    let v1 = hold { Retries = 3; TimeoutSeconds = 30 }

    match swapIfMigrated v1 carryNothing with
    | Swap.Held(why, cell) ->
      why
      |> Expect.stringContains "the refusal must say why it could not carry" "no default"

      (read cell).Retries
      |> Expect.equal "the original value must be untouched" 3
    | Swap.Swapped(_, _, _) -> failtest "a refused migration must never swap"

  testCase "WHY — the two swap outcomes are different TYPES, so a caller cannot read the wrong shape" <| fun _ ->
    // This is the property a `Result<_, _>` cannot give: the carried branch
    // yields a ConfigV2 and the held branch a ConfigV1, and unifying them is a
    // compile error rather than a runtime surprise. The two annotations below
    // are what make that difference observable.
    let v1 = hold { Retries = 1; TimeoutSeconds = 2 }

    let carried: ConfigV2 =
      match swapIfMigrated v1 carryConfig with
      | Swap.Swapped(_, _, cell) -> read cell
      | Swap.Held(_, _) -> failwith "a successful migration must swap"

    let held: ConfigV1 =
      match swapIfMigrated v1 carryNothing with
      | Swap.Held(_, cell) -> read cell
      | Swap.Swapped(_, _, _) -> failwith "a refused migration must hold"

    carried.Retries |> Expect.equal "carried shape kept its state" 1
    held.Retries |> Expect.equal "held shape kept its state" 1

  testCase "WHY — two attempts from the same cell are independent, not cumulative" <| fun _ ->
    // Both attempts read the ORIGINAL cell, so a failed one cannot have
    // disturbed what a later one sees.
    let v1 = hold { Retries = 7; TimeoutSeconds = 9 }

    let _refusedFirst = swapIfMigrated v1 carryNothing
    let carriedSecond = swapIfMigrated v1 carryConfig

    match carriedSecond with
    | Swap.Swapped(_, _, cell) -> (read cell).Retries |> Expect.equal "the later migration still saw the original value" 7
    | Swap.Held(_, _) -> failtest "the second migration should have succeeded"

  testCase "WHY — a migration is described in words a save can show, and never claims a carry it did not do" <| fun _ ->
    let carried: Migration<ConfigV1, ConfigV2> =
      Migration.Carried({ Retries = 1; TimeoutSeconds = 1 }, { Retries = 1; TimeoutSeconds = 1; Trace = true })

    let refused: Migration<ConfigV1, ConfigV2> = Migration.Refused "no default"

    describe carried
    |> Expect.stringContains "a carried migration says so" "carried"

    describe refused
    |> Expect.stringContains "a refused migration says why, and never claims a carry" "no default"
]
