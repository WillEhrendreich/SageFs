module SageFs.MigrationPlan

/// WHY — `RestartCost.decideFromLivenessAndMigration` decides a restart from a
/// `MigrationWorth`, and NOTHING computed one. A decision function with no
/// producer is a lookup nobody performs, which is the exact failure this
/// series has been closing.
///
/// THE OBVIOUS PLACE IS WRONG, and that was measured in a live session rather
/// than assumed:
///
///   HolderRegistry methods = BoundariesHolding -> FSharpList`1 ; LiveCountOf ->
///   LiveCount ; LiveHolding -> Int32 ; Register -> Int64 ; Release -> Void ;
///   SwapIn -> Void
///
/// `LiveCell` is `Id | Holds | Superseded` and `Cell` holds only a `ref` the
/// registry never sees. The registry therefore knows THAT something is held and
/// never WHAT — it is a counting device, and counting is not enough to migrate.
/// A migration needs the old VALUE and its COMPILED TYPE, and both are
/// reachable only from the `Held` the caller already holds.
///
/// So the producer belongs HERE, next to the caller that has the value, rather
/// than in the registry. That is a better shape anyway: the registry stays a
/// cheap index and this stays a policy.

open System
open SageFs
open SageFs.TypeShapeMigration

/// What a caller holding a live cell was able to say about migrating it.
///
/// The three cases are distinct because the ACTIONS differ: nothing to carry
/// means respawn; worth carrying means a migration; not worth carrying means
/// rebuild, and the user deserves to know which field stopped it.
[<RequireQualifiedAccess>]
type MigrationAssessment =
  /// Nothing alive holds the old shape. Evidence, not absence — a registry was
  /// consulted and found nothing.
  | NothingLive of boundary: string
  /// A live value exists and every field of it can be carried.
  | Carryable of fields: int
  /// A live value exists and cannot be carried, and the reason says which field.
  | NotCarryable of because: string

/// Assess a live value against the type it would have to be carried into.
///
/// `oldValue` is the value as it exists TODAY and `newType` the shape it must
/// become. Both are supplied by the caller because only the caller holds them —
/// which is the whole finding above, and the reason this is a function taking
/// two values rather than a method on the registry.
///
/// `defaultOf` answers "does the NEW definition give this field a default of its
/// own?", which is what makes a field the old value cannot supply SAFE to add.
/// It is a parameter rather than a hardcoded `false` because the rule is
/// explicit about it: a new field with a default is added, and one without is
/// refused as "the new field has no default and the old shape cannot supply
/// one". Hardcoding false — which this function did at first — makes EVERY added
/// field a refusal, which is safe but wrong, and it silently disables the one
/// case a migration exists to enable.
///
/// A value types' `Unchecked.defaultof` is a real default, so it is `true`.
let assess
    (liveness: LiveCount)
    (oldValue: obj)
    (newType: Type)
    (defaultOf: string -> bool)
    : MigrationAssessment =
  match liveness, isNull (box oldValue) with
  | LiveCount.HeldByNothing, _ ->
    MigrationAssessment.NothingLive "a liveness source was consulted and nothing holds the old shape"
  | LiveCount.HeldBy _, true ->
    // The registry says HELD and the cell is empty. Those are not in conflict:
    // the cell exists and its contents were cleared, which is NOT the same fact
    // as "nothing holds the type".
    //
    // A test initially asserted this respawns, and it does not — the liveness
    // branch wins, which is correct. Reading the null as licence for the cheap
    // answer is the "accident buys the cheap branch" move, and it is the same
    // error as treating `Unconsulted` as if it were `HeldByNothing`.
    //
    // So it is a REFUSAL: the old shape is unknown, the assembly is rebuilt, and
    // nothing is silently carried or silently dropped.
    MigrationAssessment.NotCarryable "a registry reports a live cell whose contents are empty, so the old shape is unknown"
  | LiveCount.HeldBy _, false ->
    let oldType = oldValue.GetType()
    match compiledShapeOf oldType, compiledShapeOf newType with
    | Some oldShape, Some newShape ->
      match decideRecord oldShape newShape defaultOf with
      | Holder.Migration.Carried _ ->
        MigrationAssessment.Carryable(newShape.Fields.Length)
      | Holder.Migration.Refused why -> MigrationAssessment.NotCarryable why
    | None, _ ->
      // A value that is not a record cannot be carried field-by-field, and
      // saying so is better than pretending an empty shape carried it.
      MigrationAssessment.NotCarryable(sprintf "'%s' is not a record, so there are no fields to carry" oldType.Name)
    | _, None ->
      MigrationAssessment.NotCarryable(sprintf "'%s' is not a record, so it cannot be the new shape" newType.Name)
  | LiveCount.Unconsulted why, _ -> MigrationAssessment.NotCarryable(sprintf "liveness was never established (%s)" why)
  | LiveCount.SourceFailed why, _ -> MigrationAssessment.NotCarryable(sprintf "the liveness source could not answer (%s)" why)

/// `assess` expressed in the vocabulary the restart decision consumes.
///
/// The translation is total and one-directional, so the two DUs cannot drift: a
/// caller that has an assessment always has an action, and the action's `because`
/// always carries the same words the assessment did.
///
/// `MigrationWorth` is declared at the `SageFs` NAMESPACE level, not inside the
/// `RestartCost` module — the compiler says so plainly, which is why it is
/// written unqualified here rather than as `RestartCost.MigrationWorth`.
let toMigrationWorth (answer: MigrationAssessment) : MigrationWorth =
  match answer with
  | MigrationAssessment.NothingLive because -> MigrationWorth.NoValueToMigrate because
  | MigrationAssessment.Carryable fields -> MigrationWorth.WorthCarrying fields
  | MigrationAssessment.NotCarryable because -> MigrationWorth.NotWorthCarrying because

/// The whole decision in one call, so a caller has a single thing to invoke and
/// cannot forget to translate — which is how the earlier version stayed
/// unwired.
///
/// The pipeline order is explicit rather than clever: `assess` takes the
/// liveness FIRST, so `toMigrationWorth` produces a verdict and the decision
/// consumes it with the liveness it was derived from. Piping `assess` in and
/// then reusing the piped value is a mistake the compiler caught — the piped
/// `obj` and the `Type` do not line up — so the two are named.
///
/// `RestartAction` is namespace-level like `MigrationWorth`; only the
/// FUNCTIONS live in the `RestartCost` module.
let decide
    (liveness: LiveCount)
    (oldValue: obj)
    (newType: Type)
    (defaultOf: string -> bool)
    : RestartAction =
  let verdict = toMigrationWorth (assess liveness oldValue newType defaultOf)
  RestartCost.decideFromLivenessAndMigration liveness verdict
