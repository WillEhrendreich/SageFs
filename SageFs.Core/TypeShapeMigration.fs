namespace SageFs

// ── Type-shape migration: derive the mapping by name, refuse the rest ──
//
// `type-migration-direction.md` step 3. The document's rule is the whole
// design: "recognise a decidable subset that is provably safe and refuse
// everything else" — the same shape as rule 2's escape classifier, which
// treats anything it cannot follow as an escape.
//
// Tier 0 is the decidable subset, and it is deliberately small:
//   - a field present in both shapes with the SAME kind: carry it across
//   - a field only in the new shape, and the new shape gives it a DEFAULT:
//     the old value cannot supply it and the new definition's own default is
//     correct, so it is added
// Everything else refuses. There is no "mostly migrated" branch, because a
// migration that silently produces a wrong value is worse than the restart we
// do today.
//
// Pure: shape in, decision out. No reflection, no IO, no clock — so the DST
// folds the real function and the compiler enforces the closed vocabulary.
module TypeShapeMigration =

  /// What we can know about a field from its source shape. `Undecidable` is the
  /// escape hatch: a field the rules cannot reason about refuses the whole
  /// migration rather than being guessed at.
  [<RequireQualifiedAccess>]
  type FieldKind =
    | IntField
    | StringField
    | BoolField
    /// We could not decide what this field is, so nothing migrates.
    | Undecidable of why: string

  /// One field of a record shape. A record rather than a string pair so a field
  /// kind is never a bare literal at a call site.
  type Field =
    { Name: string
      Kind: FieldKind }

  type RecordShape =
    { TypeName: string
      Fields: Field list }

  /// The per-field decision.
  [<RequireQualifiedAccess>]
  type FieldTier =
    /// Present in both, same kind: carry it across.
    | Carried
    /// Only in the new shape, and the new shape gives it a default: add it.
    | Added
    /// Refused, with the reason a user can act on.
    | Refused of reason: string

  /// The name -> kind index. A duplicate field name is impossible, and a lookup
  /// is total.
  let fieldIndex (shape: RecordShape) : Map<string, FieldKind> =
    shape.Fields |> List.map (fun field -> field.Name, field.Kind) |> Map.ofList

  /// Decide one field from the OLD shape's index. The parameters are explicit
  /// so the DST drives this exact function rather than a copy of it.
  let decideField
    (oldIndex: Map<string, FieldKind>)
    (field: Field)
    (hasDefault: bool)
    : FieldTier =
    // An UNDECIDABLE field is a hard stop BEFORE anything else, and a default
    // does not rescue it. "We could not decide what this field is" is not the
    // same claim as "we know what it is, and its default is right" — and a
    // default only says something about a field whose type we understood. The
    // DST found this: a shape with an undecidable-but-defaulted field was
    // reported as carried, which is exactly the "silently wrong value" the
    // design's refuse-everything-else rule exists to prevent.
    match field.Kind with
    | FieldKind.Undecidable why ->
      FieldTier.Refused(sprintf "the field's kind is undecidable (%s), and a default cannot stand in for it" why)
    | _ ->
      match oldIndex |> Map.tryFind field.Name with
      | None ->
        // A new field is only safe when the new definition supplies its own
        // default. Without one, the new record wants a value the old record
        // never held, and inventing it would be a lie.
        if hasDefault then
          FieldTier.Added
        else
          FieldTier.Refused "the new field has no default and the old shape cannot supply one"
      | Some oldKind when oldKind = field.Kind -> FieldTier.Carried
      | Some _ -> FieldTier.Refused "the field's kind changed, so the old value is not the new meaning"

  /// A one-line reason, for a save message.
  let describeTier (tier: FieldTier) : string =
    match tier with
    | FieldTier.Carried -> "carried"
    | FieldTier.Added -> "added from the new definition's default"
    | FieldTier.Refused why -> why

  /// The field-level trace, so a caller can show WHICH fields moved and which
  /// did not — a save that says "migrated" without that is the kind of claim
  /// this work exists to make honest.
  let tiersOf
    (oldShape: RecordShape)
    (newShape: RecordShape)
    (defaultOf: string -> bool)
    : (string * FieldTier) list =
    let oldIndex = fieldIndex oldShape
    newShape.Fields |> List.map (fun field -> field.Name, decideField oldIndex field (defaultOf field.Name))

  /// The whole-record decision: migrate only when EVERY field is decidable,
  /// and report nothing partial when any is not.
  let decideRecord
    (oldShape: RecordShape)
    (newShape: RecordShape)
    (defaultOf: string -> bool)
    : Holder.Migration<RecordShape, RecordShape> =
    let refusals =
      tiersOf oldShape newShape defaultOf
      |> List.choose (fun (name, tier) ->
        match tier with
        | FieldTier.Refused why -> Some(sprintf "'%s': %s" name why)
        | _ -> None)

    match refusals with
    | [] -> Holder.Migration.Carried(oldShape, newShape)
    | reasons -> Holder.Migration.Refused(String.concat "; " reasons)
