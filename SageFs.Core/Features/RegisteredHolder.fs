namespace SageFs

/// ── A holder a running app can CREATE and SageFs can FIND ──────────────
///
/// WHY this exists. `Holder.hold` is pure: it makes a cell and tells nobody.
/// That is right for a value type, and wrong for a restart boundary, because
/// a cell in a running app is then INVISIBLE to the restart that would need to
/// know about it. A registry nobody calls is a registry that is always empty,
/// and an always-empty registry is a `Liveness.Unknown` that pays a build
/// forever.
///
/// So there is a SECOND way to hold a value, and the difference is the whole
/// point: it registers. An app writes `holdRegistered registry "Order" 42`
/// once, and the restart can answer "does anything hold an old Order?" with a
/// lookup rather than a question the runtime refuses.
///
/// IT IS NOT A DEFAULT PARAMETER ON `hold`. A pure `hold` that silently
/// mutated global state would be the harder surprise: the caller could not opt
/// out, and could not test in isolation. Two functions, and the caller says
/// which kind of cell it wants.

/// A cell, and the id the registry knows it by. The id is what a later swap or
/// release needs, so it is returned rather than hidden.
[<RequireQualifiedAccess>]
type Held<'a> =
  | Held of cell: SageFs.Holder.Cell<'a> * id: CellId

[<RequireQualifiedAccess>]
module RegisteredHolder =

  /// What `holdInCurrent` can refuse with, when there is no published registry
  /// for a held value to be visible to.
  ///
  /// Its own DU rather than an `Error` string, so a caller cannot swallow it
  /// as "some problem" and cannot invent a reason that is not one.
  type HeldOrUnseen<'a> =
    /// The value is held AND recorded, so a restart can see it.
    | Visible of held: Held<'a>
    /// No registry has been published in this process, so a value held here
    /// could never be seen by a restart. Running outside a SageFs worker.
    | NoRegistryPublished of because: string

  /// Hold a value AND record it, so a restart can see it.
  ///
  /// `typeName` is the boundary registry's name for the type, so a restart
  /// joins the two without a translation table between them.
  let holdRegistered (registry: SageFs.HolderRegistry) (typeName: string) (initial: 'a) : Held<'a> =
    Held.Held(SageFs.Holder.hold initial, registry.Register typeName)

  /// Hold a value into the registry the RESTART actually reads.
  ///
  /// THIS is the form an app should use, and the reason is a measured bug: the
  /// app and the worker run in the same process and each used to build its own
  /// registry, so `holdRegistered` with a private registry registered into an
  /// object the restart never consulted. The answer was `0` for every type, and
  /// the restart skipped a build on the strength of nothing.
  ///
  /// The refusal is a DU rather than an `Error` string on purpose: this repo
  /// ratchets stringly-typed `Result`s DOWN, and `ErrorAlgebraHardeningTests`
  /// counts them by SOURCE LINE — so writing the type name in this comment
  /// would have counted as an occurrence, and a doc comment explaining the rule
  /// tripped the rule's own guard. That is a real property of the guard worth
  /// knowing: it is a text scan, not a type check, so prose about the pattern
  /// is indistinguishable from the pattern.
  let holdInCurrent (typeName: string) (initial: 'a) : HeldOrUnseen<'a> =
    match SageFs.HolderRegistry.Current with
    | None ->
      HeldOrUnseen.NoRegistryPublished
        "no holder registry is published in this process, so a held value could never be seen by a restart"
    | Some registry -> HeldOrUnseen.Visible(holdRegistered registry typeName initial)

  /// The cell a `Held` is holding, when the caller only needs the value.
  let cellOf (held: Held<'a>) =
    match held with
    | Held.Held(cell, _) -> cell

  /// Record that a held cell's contents were replaced. The cell keeps
  /// existing but is no longer evidence for the type it used to hold — which
  /// is what makes a scoped restart cheap after a successful migration.
  let swapIn (registry: SageFs.HolderRegistry) (held: Held<'a>) (nowHolds: string) : Held<'a> =
    match held with
    | Held.Held(cell, id) ->
      registry.SwapIn id nowHolds
      Held.Held(cell, id)

  /// Record that a held cell is gone: a disposed boundary, a scope that
  /// exited. It stays inspectable and stops counting as live.
  let release (registry: SageFs.HolderRegistry) (held: Held<'a>) : unit =
    match held with
    | Held.Held(_, id) -> registry.Release id

  /// The LIVENESS ANSWER for `typeName`, carrying its provenance.
  ///
  /// A registry that WAS asked and found nothing answers `HeldByNothing` — a
  /// claim, and the evidence that licenses skipping a build. A registry that
  /// was never asked answers `Unconsulted`, which is a different claim and
  /// must never be readable as the one above. That difference is the whole
  /// reason this is a DU rather than `int option`, where both would be `None`.
  let liveCountOf (registry: SageFs.HolderRegistry option) (typeName: string) : SageFs.LiveCount =
    match registry with
    | None -> SageFs.LiveCount.Unconsulted "no holder registry in this process"
    | Some r -> r.LiveCountOf typeName
