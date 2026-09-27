namespace SageFs.Features

// ── Restart scope: how wide a type change's restart has to be ─────────
//
// `type-migration-direction.md` step 1: the payoff of granular restart is
// that the remedy can be narrower than "restart the app". This DU makes that
// narrowness representable, and `infer` is the only way to produce one — so a
// scope can never be narrower than the evidence supports.
//
// It is its own file, compiled before `ReloadOutcome.fs`, because it depends
// on nothing and is exactly the shape the DST harness folds: a pure decision
// from evidence to a scope, with one failure direction.

/// How wide a restart has to be when a type's shape changed.
[<RequireQualifiedAccess>]
type RestartScope =
  /// Only the named unit can hold old instances of the type, so only it
  /// restarts.
  | Scoped of unitName: string
  /// We could not attribute the change to a unit, so everything restarts.
  ///
  /// This is the direction the inference fails on purpose. A too-wide restart
  /// costs time; a too-narrow one leaves instances laid out by the old
  /// definition alive somewhere in the process, which is the exact bug this
  /// work exists to remove.
  | Everything

/// One unit and the type it is declared to hold. The pair IS the attribution
/// input: nothing beyond what is declared here can narrow a restart.
type KnownUnit =
  { Name: string
    DeclaresType: string
    /// The record fields this type DECLARED AT PARSE TIME, in source order.
    ///
    /// This is the OLD shape, captured when the file was parsed, and it exists
    /// because a type change cannot be migrated without it: the new shape has no
    /// compiled type until a build produces one, and a name like "Order" carries
    /// no field list. So the old side has to be RETAINED, and the parse that
    /// sees it is already running.
    ///
    /// `None` means the declaration is not a record, or has no fields — which
    /// is a different claim from "we did not look", and a type carrying a
    /// migration is refused rather than assumed empty.
    DeclaresFields: string list option }

module RestartScope =

  /// The scopes the DST harness folds through, so a new scope kind cannot ship
  /// without being exercised.
  let all : RestartScope list =
    [ RestartScope.Scoped "order-store"
      RestartScope.Everything ]

  /// Narrow a type change to a unit only when a unit actually declares it.
  /// Anything unattributable is `Everything`.
  let infer (knownUnits: KnownUnit list) (typeName: string) : RestartScope =
    knownUnits
    |> List.tryFind (fun unit -> unit.DeclaresType = typeName)
    |> Option.map (fun unit -> RestartScope.Scoped unit.Name)
    |> Option.defaultValue RestartScope.Everything
