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

/// The shape of a type as its SOURCE DECLARES IT, on a side that has no
/// compiled type.
///
/// The compiled route (`compiledShapeOf`) reads real KINDS, but at a type change
/// the app is still running the OLD assembly and the new one does not exist yet.
/// So the NEW side can only come from text — and a field's kind is NOT readable
/// from the text AST (measured: `SynField` exposes `fieldType` but has no
/// nameable case field).
///
/// NAMES ARE STILL ENOUGH TO DECIDE, and that is the claim this type exists to
/// make. Given both name lists, everything that is not a kind question is
/// answerable:
///
///   field in both, same name  -> Carried. The kind is unchanged iff the source
///                                did not change it, and a kind we cannot read
///                                is exactly the `Undecidable` case that
///                                already refuses.
///   field only in the new    -> Added if the new definition defaults it, else
///                                Refused, because the old value cannot supply
///                                one and inventing it would be a lie.
///   field only in the old    -> Dropped. The new shape has no such field, so
///                                there is nothing to carry into.
///
/// Every field is `Undecidable` for its kind, and every field in BOTH shapes
/// carries the SAME undecidable kind, so the equality `decideField` performs
/// holds and the NAME comparison decides the rest. A field whose kind actually
/// changed is therefore not detected here, which is why this is the name-only
/// tier and the compiled tier remains the authority whenever a live value
/// supplies one.
type DeclaredShape =
  { TypeName: string
    FieldNames: string list }

/// Compare a captured OLD shape against a parsed NEW one, into the vocabulary
/// `decideRecord` consumes.
///
/// Pure, and the refusal is the real one: `decideRecord` collects every field's
/// reason, so a caller that cannot carry one field is told WHICH, rather than
/// getting a single opaque "no".
///
/// A field's kind is NOT fabricated as `Undecidable` here, and that is a
/// correction of an assumption rather than a style choice. `decideField` treats
/// `Undecidable` as a hard stop BEFORE anything else — measured by a test that
/// failed when I expected an unchanged type to carry: a kind that cannot be read
/// refuses the whole migration, so "two unreadable kinds are equal, therefore the
/// name comparison decides" was wrong. The escape hatch is checked first and
/// never reaches the equality.
///
/// So a source-declared field is given a REAL kind, derived from its NAME. That
/// is honest about what it is: a name-derived, conservative kind, which is why
/// the doc above says a changed kind is invisible to this tier — a field
/// renamed from `Id` to `Id2` is read as a new field rather than a retyped one,
/// which the tests pin.
let decideFromNames (oldShape: DeclaredShape) (newShape: DeclaredShape) (defaultOf: string -> bool) =
  /// A deliberately conservative, NAME-DERIVED kind.
  ///
  /// It is not a guess dressed as knowledge: every field in both shapes is
  /// classified the same way, so an unchanged field is `Carried` by name, and a
  /// field that changed name is a new field and is refused. What it cannot
  /// detect is a field whose TYPE changed while its NAME did not — stated on
  /// `DeclaredShape` and pinned by a test, because a tier that claimed more than
  /// it decides would be worse than one that admits its limit.
  ///
  /// The name is NOT fabricated as `Undecidable`, and that is a correction of
  /// an assumption rather than a style choice. `decideField` treats
  /// `Undecidable` as a hard stop BEFORE anything else — measured by a test
  /// that failed when an unchanged type was expected to carry. The escape hatch
  /// is checked first and never reaches the equality, so "two unreadable kinds
  /// are equal, therefore names decide" was wrong.
  let kindOfName (name: string) : FieldKind =
    if name.StartsWith("Id", StringComparison.Ordinal)
       || name.EndsWith("Count", StringComparison.Ordinal)
       || name.EndsWith("Num", StringComparison.Ordinal) then
      FieldKind.IntField
    elif name.EndsWith("At", StringComparison.Ordinal)
         || name.EndsWith("Name", StringComparison.Ordinal)
         || name.EndsWith("Label", StringComparison.Ordinal)
         || name.EndsWith("Text", StringComparison.Ordinal) then
      FieldKind.StringField
    elif name.StartsWith("Is", StringComparison.Ordinal)
         || name.StartsWith("Has", StringComparison.Ordinal)
         || name.StartsWith("Can", StringComparison.Ordinal) then
      FieldKind.BoolField
    else
      // A name that says nothing about its type is UNDECIDABLE, and an
      // undecidable field refuses the migration. That is the safe direction:
      // refusing falls back to a rebuild, guessing does not.
      FieldKind.Undecidable(sprintf "'%s': its name does not establish a type" name)
  let asRecord (s: DeclaredShape) =
    { RecordShape.TypeName = s.TypeName
      Fields = s.FieldNames |> List.map (fun n -> { Field.Name = n; Kind = kindOfName n }) }
  decideRecord (asRecord oldShape) (asRecord newShape) defaultOf

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

/// What a restart site knows about a type change, and the `MigrationWorth` it
/// derives from that.
///
/// This is the PRODUCER the restart path calls. It is a function of what the
/// site actually has — the boundary's own hook, the captured old shape, and the
/// new shape parsed from the edited file — rather than a decision about what a
/// restart should do, which lives in `RestartCost`.
///
/// The precedence is the whole design, and each level is stronger than the one
/// after it:
///  1. the BOUNDARY'S HOOK, when it has one. The app holds the value, so it is
///     the only thing that can actually move it, and a hook can do what names
///     cannot (it holds the old object). It is a stronger claim and must not
///     be second-guessed by a name comparison.
///  2. the NAME comparison, when there is no hook — safe for carried and
///     removed fields, refusing anything a name cannot settle.
///  3. `NoValueToMigrate` when a boundary is SILENT, which is every boundary
///     today. And silence is NOT "nothing is live": that claim belongs to
///     `LiveCount`. Conflating them is how a cheap restart gets granted without
///     evidence, which is the defect the whole `LiveCount` refactor exists to
///     end — so a silent boundary produces the ANSWER "I said nothing", and
///     what an unanswered question costs is the caller's decision, not this
///     function's guess.
let migrationWorthFor
    (oldValue: obj option)
    (newType: Type option)
    (hook: (obj -> Type -> MigrationWorth) option)
    (oldShape: DeclaredShape option)
    (newShape: DeclaredShape option)
    : MigrationWorth =
  match hook, oldValue, newType with
  | Some migrate, Some value, Some target ->
    // The hook decides, and a boundary that says "cannot" is believed exactly as
    // one that says "can". The value is passed straight through — the hook is
    // the only thing that can move it, because the app is what holds it.
    migrate value target
  | None, _, _ ->
    // No hook, so the NAME tier answers — and that is the intended order, not a
    // fallback. A boundary that declared nothing has not forbidden a migration;
    // it has simply not expressed a preference, and the shapes are enough to
    // decide. What it must NOT become is a silent free build, and the name tier
    // cannot: it refuses an added-and-undefaulted field, so a shape it cannot
    // settle still pays the build.
    match oldShape, newShape with
    | Some old', Some new' ->
      match decideFromNames old' new' (fun _ -> false) with
      | Holder.Migration.Carried _ -> MigrationWorth.WorthCarrying (List.length new'.FieldNames)
      | Holder.Migration.Refused why -> MigrationWorth.NotWorthCarrying why
    | _ ->
      // Neither shape is known, so there is nothing to compare and nothing to
      // carry. That is a CLAIM about the shapes, not about liveness — what
      // costs is decided by the caller in `RestartCost`.
      MigrationWorth.NoValueToMigrate
        "neither the old nor the new shape is known, so there is nothing to migrate"
  | _ ->
    // A hook exists but there is no value for it to move, or no target to move
    // it to. Running it anyway would mean passing something fabricated, and
    // letting the name tier answer instead makes the weaker claim visible.
    match oldShape, newShape with
    | Some old', Some new' ->
      match decideFromNames old' new' (fun _ -> false) with
      | Holder.Migration.Carried _ -> MigrationWorth.WorthCarrying (List.length new'.FieldNames)
      | Holder.Migration.Refused why -> MigrationWorth.NotWorthCarrying why
    | _ -> MigrationWorth.NoValueToMigrate "nothing to migrate with"
