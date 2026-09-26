namespace SageFs.Features

// ── Which unit declares a type, from what the planner already parsed ──
//
// WHY this module exists: granular restart was unreachable because nothing
// ever constructed a `KnownUnit`. `RestartScope.infer` was correct and
// completely uncalled-for, and the live path at
// `SageFs.Host/WorkerMain.fs` called the UNATTRIBUTED translation, so every
// type change restarted the whole app exactly as it always had.
//
// The attribution does not need new machinery, because the planner ALREADY
// parsed every watched file: `FileDecls.ModulePath` plus each `SourceDecl`'s
// `Container` say which module a declaration lives in. A module is the natural
// unit — it is an observable boundary in F#, it owns the types, and it is what
// a DI singleton, an `IOptionsMonitor` registration, or an agent boundary
// wraps. The data was in hand and nobody read it.
//
// Verified in the live REPL on real parsed source, not assumed:
//
//   namespace Shop / module Orders / type Order  ->  Scoped "Shop.Orders"
//   namespace Shop / module Cart   / type Cart    ->  Scoped "Shop.Cart"
//   Todo (declared nowhere)                       ->  Everything
//
// Fails safe by construction: a type no unit declares is `Everything`, and so
// is an empty registry. A wrongly-narrowed restart would leave a half-restarted
// app holding a value laid out by the old type, which is strictly worse than a
// full restart — so the win is never bought with safety.

module RestartAttribution =

  /// The unit a declaration belongs to: the file's own module path, plus the
  /// nested modules it sits inside. `namespace Shop` + `module Orders` +
  /// `type Order` is `Shop.Orders`, which is the name a user would recognise
  /// and the name a boundary registration would use.
  let unitNameOf (decls: ReloadPlanning.FileDecls) (decl: ReloadPlanning.SourceDecl) =
    (decls.ModulePath @ decl.Container) |> String.concat "."

  /// The registry, built from every watched file's parse. Only TYPE
  /// declarations matter: a unit is attributed by the types it owns, and a
  /// value-only module has nothing to attribute a type change to.
  let knownUnitsOf (allDecls: ReloadPlanning.FileDecls list) : KnownUnit list =
    allDecls
    |> List.collect (fun decls ->
      decls.Decls
      |> List.filter (fun d -> d.Kind = ReloadPlanning.DeclKind.TypeDecl)
      |> List.map (fun d ->
        { Name = unitNameOf decls d
          DeclaresType = d.Name }))
    |> List.distinct

  /// Parse sources into a registry. Kept as a function of TEXT so a caller can
  /// build the registry from whatever it already has (the worker's baselines,
  /// the watched-file set) without this module needing to know how files are
  /// read. An unreadable or unparseable source contributes nothing rather than
  /// failing the whole registry: a missing attribution is the safe direction.
  let knownUnitsOfSources (sources: string list) : KnownUnit list =
    sources
    |> List.choose (fun text ->
      try
        match ReloadPlanning.extractDecls text with
        | Ok decls -> Some decls
        | Error _ -> None
      with _ -> None)
    |> knownUnitsOf

  /// Translate a change with whatever attribution the caller actually has.
  ///
  /// The caller is expected to pass a REAL registry. An empty one is
  /// meaningful — "we looked and found nothing" — and yields `Everything`,
  /// which is why this is not a defaulted optional parameter: that would make
  /// "did not thread attribution through" indistinguishable from "found
  /// nothing", and the first would silently pass for the second.
  let restartReasonFor (knownUnits: KnownUnit list) (change: ReloadPlanning.ReloadChange) =
    ReloadPlanning.ReloadChange.restartReasonAttributed knownUnits change

  let restartReasonsFor (knownUnits: KnownUnit list) (first: ReloadPlanning.ReloadChange) (rest: ReloadPlanning.ReloadChange list) =
    ReloadPlanning.ReloadChange.restartReasonsAttributed knownUnits first rest
