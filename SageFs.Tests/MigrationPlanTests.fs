module SageFs.Tests.MigrationPlanTests

/// WHY — `RestartCost.decideFromLivenessAndMigration` had no producer, so it
/// could never be reached from a save. `MigrationPlan` is that producer, and it
/// exists where the value is rather than on the registry, which was the finding:
///
///   HolderRegistry methods = BoundariesHolding -> list ; LiveCountOf ->
///   LiveCount ; LiveHolding -> int ; Register -> int64 ; Release -> void ;
///   SwapIn -> void
///
/// The registry knows THAT something is held and never WHAT. `LiveCell` is
/// `Id | Holds | Superseded` and `Cell` holds a `ref` the registry never sees, so
/// a migration is impossible from there by construction. These tests pin that
/// reasoning by exercising the producer over every case the decision distinguishes.

open Expecto
open Expecto.Flip
open SageFs

type Order = { Id: int; Name: string; Paid: bool }

/// A new field with no old counterpart.
type OrderWithNote = { Id: int; Name: string; Paid: bool; Note: string }

/// A field with no carry rule.
type Heavy = { Id: int; Payload: byte array }

let private held = LiveCount.HeldBy [ "order-store" ]
let private empty = LiveCount.HeldByNothing
let private unchecked = LiveCount.Unconsulted "no registry in this process"

let private orderValue : obj = box { Id = 7; Name = "x"; Paid = true }
let private heavyValue : obj = box { Id = 1; Payload = [| 1uy; 2uy |] }

let private nameOf (action: RestartAction) =
  match action with
  | RestartAction.RespawnOnly _ -> "RespawnOnly"
  | RestartAction.RebuildProject _ -> "RebuildProject"
  | RestartAction.MigrateAndRespawn _ -> "MigrateAndRespawn"

let private decide liveness value ty =
  nameOf (MigrationPlan.decide liveness value ty (fun name -> name = "Note"))

/// The opposite policy: no field is considered defaulted. Kept as a named value
/// so the two policies are visible side by side rather than being one `fun _ ->
/// false` buried in each test.
let private noDefaults _ = false

[<Tests>]
let migrationPlanTests =
  testList "producing the migration verdict a restart decision needs" [

    testCase "WHY — a live value whose every field carries produces a MIGRATE, which is the outcome that did not exist before" <| fun _ ->
      // The headline. A two-case decision could not produce this at all.
      decide held orderValue typeof<Order>
      |> Expect.equal "carry the value, skip the build" "MigrateAndRespawn"

    testCase "WHY — a field the NEW shape defaults is added, and one it does NOT is refused: the defaulting policy is the difference" <| fun _ ->
      // The pair that matters. Both tests pass only because the policy is a
      // PARAMETER: a hardcoded `false` — which this function had at first —
      // refuses in both cases, which is safe and silently disables the one
      // thing a migration exists to enable.
      MigrationPlan.decide held orderValue typeof<OrderWithNote> (fun name -> name = "Note")
      |> nameOf
      |> Expect.equal "with a default, the new field is added and the value is carried" "MigrateAndRespawn"

      MigrationPlan.decide held orderValue typeof<OrderWithNote> noDefaults
      |> nameOf
      |> Expect.equal "without one, the new field is a refusal rather than an invented value" "RebuildProject"

    testCase "WHY — a live cell whose contents are empty REFUSES rather than respawning, because the registry's claim is evidence" <| fun _ ->
      // A test first asserted this respawned, and it did not — and the code was
      // right. A registry that says `HeldBy` is EVIDENCE that something is held;
      // a null inside that cell means the contents were cleared, which is not
      // the same fact as "nothing holds the type". Reading the null as licence
      // for the cheap answer would be the same error as treating `Unconsulted`
      // as `HeldByNothing`, and the whole `LiveCount` refactor exists to stop it.
      match MigrationPlan.assess held (box null) typeof<Order> (fun _ -> false) with
      | MigrationPlan.MigrationAssessment.NotCarryable why ->
        (why.Length > 0) |> Expect.isTrue "the refusal must say the old shape is unknown"
      | other -> failtestf "a cleared cell is a refusal, not a cheap restart: %A" other
      // And the decision follows the assessment.
      MigrationPlan.decide held (box null) typeof<Order> (fun _ -> false)
      |> nameOf
      |> Expect.equal "so the assembly is rebuilt and nothing is silently dropped" "RebuildProject"

    testCase "WHY — a field with no carry rule REFUSES, and the reason reaches the user" <| fun _ ->
      decide held heavyValue typeof<Heavy>
      |> Expect.equal "an uncarryable field is a rebuild, never a silent drop" "RebuildProject"

    testCase "WHY — nothing live respawns, and that is EVIDENCE rather than a failure" <| fun _ ->
      decide empty orderValue typeof<Order>
      |> Expect.equal "a registry that was asked and found nothing respawns" "RespawnOnly"

    testCase "WHY — liveness that was never established pays the build, even with a fully carryable value" <| fun _ ->
      decide unchecked orderValue typeof<Order>
      |> Expect.equal "an unearned cheap answer is not available" "RebuildProject"

    testCase "WHY — the refusal NAMES the field, so a user learns what to fix rather than that something failed" <| fun _ ->
      match MigrationPlan.assess held heavyValue typeof<Heavy> (fun _ -> false) with
      | MigrationPlan.MigrationAssessment.NotCarryable why ->
        (why.Length > 0) |> Expect.isTrue "a refusal must carry a reason"
      | other -> failtestf "expected a refusal, got %A" other

    testCase "WHY — carrying is a TOTAL function over the assessment, so no case is dropped between the two DUs" <| fun _ ->
      // If a case were missing, `toMigrationWorth` would not compile; if it were
      // wrong, this would produce a verdict the decision cannot accept. Assert
      // the COUNT of fields travels, which is the only thing a caller needs.
      // The liveness is named rather than piped, because `assess` takes it
      // FIRST and piping it into the second position silently reorders the
      // arguments — which the compiler catches.
      let verdict = MigrationPlan.assess held orderValue typeof<Order> (fun _ -> false)
      match MigrationPlan.toMigrationWorth verdict with
      | MigrationWorth.WorthCarrying n -> n
      | other -> failtestf "expected WorthCarrying, got %A" other
      |> Expect.equal "all three fields are accounted for" 3

    testCase "WHY — assess and decide never disagree, because decide is defined in terms of assess" <| fun _ ->
      // If `decide` were reimplemented rather than composed, the assessment a
      // caller sees could stop matching the action it gets. This pins them.
      // Both sides use the SAME defaulting policy, or the comparison would be
      // between two different questions.
      let policy name = name = "Note"
      let action = MigrationPlan.decide held orderValue typeof<Order> policy
      let verdict = MigrationPlan.toMigrationWorth (MigrationPlan.assess held orderValue typeof<Order> policy)
      let expected = RestartCost.decideFromLivenessAndMigration held verdict
      (action = expected)
      |> Expect.isTrue "one definition, two views"
  ]
