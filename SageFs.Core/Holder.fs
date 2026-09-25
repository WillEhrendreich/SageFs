namespace SageFs

// ── The holder: state that survives a redefinition ───────────────────
//
// `type-migration-direction.md` step 2. Today a `let mutable` whose type
// changes can only be replaced — carrying the old value forward ignores your
// edit, resetting it destroys live state, so SageFs refuses both and restarts.
// That refusal is honest but expensive.
//
// A holder makes a third option representable: state lives behind a boundary
// SageFs owns, and a redefinition BUILDS a new value, MIGRATES the old one
// into it, and SWAPS the cell. The read is one field access; the swap is one
// assignment the running app observes.
//
// The safety property is in the types, not in a runtime check. `Swap` carries
// two DIFFERENT holder types — `Swapped` hands back a `Holder<'after>`, `Held`
// hands back the original `Holder<'before>` — so a caller cannot read a value
// of a type a refused migration never produced. A `Result` would have allowed
// exactly that mistake.
//
// Pure: no IO, no ambient clock, no reflection. The DST harness folds it
// directly.
module Holder =

  /// What a migration produced. There is deliberately no "probably fine" case:
  /// a migration either carried state or said why it could not.
  [<RequireQualifiedAccess>]
  type Migration<'before, 'after> =
    /// The old value's state was carried into a value of the new shape.
    | Carried of from: 'before * into: 'after
    /// State could not be carried. The holder must not pretend otherwise.
    | Refused of because: string

  /// A cell holding a value, owned by whoever rebuilt it. Parameterized, so a
  /// holder of the new shape is a DIFFERENT type from a holder of the old.
  type Cell<'a> = { Value: 'a ref }

  /// The outcome of attempting a swap.
  ///
  /// Each branch carries the cell at the type it actually holds, which is
  /// what makes "read the wrong shape" unrepresentable rather than merely
  /// discouraged. The payloads are NOT `Migration<'before, 'after>`: a
  /// `Swapped` of a refused migration is a contradiction the type must not
  /// admit, so the two shapes are stated separately.
  [<RequireQualifiedAccess>]
  type Swap<'before, 'after> =
    /// The migration carried state forward; the cell now holds `'after`.
    | Swapped of from: 'before * into: 'after * cell: Cell<'after>
    /// The migration refused; the cell still holds the original `'before`.
    | Held of because: string * cell: Cell<'before>

  /// Hold an initial value behind a cell.
  let hold (initial: 'a) : Cell<'a> = { Value = ref initial }

  /// Read the current value.
  let read (cell: Cell<'a>) : 'a = cell.Value.Value

  /// Attempt the swap. The check and the swap live in ONE function so no
  /// caller can swap without deciding the outcome first: a refusal returns the
  /// original value at its original type, and never a half-applied change.
  let swapIfMigrated
    (cell: Cell<'before>)
    (migrate: 'before -> Migration<'before, 'after>)
    : Swap<'before, 'after> =
    let before = cell.Value.Value

    match migrate before with
    | Migration.Carried(carried, into) -> Swap.Swapped(carried, into, hold into)
    | Migration.Refused why -> Swap.Held(why, hold before)

  /// The cell a swap ended up holding, when the caller does not care which.
  /// Only sound when the two types unify; the typed path is `Swap` itself.
  let cellOf (swap: Swap<'before, 'before>) : Cell<'before> =
    match swap with
    | Swap.Swapped(_, _, cell) -> cell
    | Swap.Held(_, cell) -> cell

  /// A one-line description of what a migration did, for a save message.
  let describe (migration: Migration<'before, 'after>) : string =
    match migration with
    | Migration.Carried(_, _) -> "carried the running value's state into the new definition"
    | Migration.Refused why -> sprintf "could not carry the running value forward: %s" why
