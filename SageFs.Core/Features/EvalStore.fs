/// The eval history as a bounded, id-indexed, persistent store.
///
/// Every eval used to cost O(history): the binding scope and dependency graph
/// were patched by scanning (and regex-matching) every retained cell, and once
/// the 10,000-cell cap was reached every eval rebuilt both from scratch —
/// quadratic work on the `/exec` path before the reply was written.
///
/// Here each cell is tokenized ONCE when it is recorded (its `\b` words, its
/// free identifiers, the names it binds) and indexed by id. Ids are dense and
/// monotonic, so the retained window is always `[NextId - count, NextId)` and
/// evicting the oldest cell is a single keyed removal. Recording an eval costs
/// O(size of the new cell × log history) whether the history holds 100 cells
/// or is sitting at the cap. The binding scope and dependency graph are
/// materialized from the index only when someone reads them; every reference
/// check in that materialization is a hash/tree probe, not a regex.
///
/// The store is immutable: an older version handed to another thread never
/// observes later writes.
module SageFs.Features.EvalStore

open System
open System.Collections.Generic

type EvalHistoryEntry = {
  CellIndex: int
  Code: string
  Result: string
  DurationMs: int64
  Timestamp: DateTimeOffset
}

/// How many cells the history retains (at least one).
type HistoryCap = private HistoryCap of int

module HistoryCap =
  [<Literal>]
  let StandardCells = 10_000

  /// The daemon's cap: the most recent 10,000 cells.
  let standard = HistoryCap StandardCells

  let tryCreate (cells: int) : Result<HistoryCap, string> =
    match cells >= 1 with
    | true -> Ok (HistoryCap cells)
    | false -> Error (sprintf "The eval history must retain at least 1 cell; %d was requested." cells)

  let cells (HistoryCap n) = n

/// One retained cell, analysed once when it was recorded.
type StoredCell = {
  Entry: EvalHistoryEntry
  /// `val` lines as the binding explorer reads them: name, type, value.
  ScopeBindings: (string * string * string option) list
  /// Names the dependency graph says this cell binds.
  Produces: string list
  /// Distinct `\b`-bounded words of the source (binding references).
  Words: string[]
  /// Distinct free identifiers of the source (dependency consumption).
  FreeIdentifiers: string[]
}

type Store = {
  Cap: HistoryCap
  /// The id the next recorded cell gets. Never decreases.
  NextId: int
  /// Retained cells by id: exactly the ids `[NextId - Cells.Count, NextId)`.
  Cells: Map<int, StoredCell>
  /// Inverted index: `\b` word -> retained cells whose source contains it.
  CellsByWord: Map<string, Set<int>>
  /// Newest cell (ever recorded, retained or not) that bound each name.
  KnownBindings: Map<string, int>
  /// Known names that are not plain identifiers (operators, primed or
  /// backticked names). The free-identifier index cannot answer for these,
  /// so materialization scans for them literally.
  UnusualKnownNames: Set<string>
}

let empty (cap: HistoryCap) : Store =
  { Cap = cap
    NextId = 0
    Cells = Map.empty
    CellsByWord = Map.empty
    KnownBindings = Map.empty
    UnusualKnownNames = Set.empty }

let count (store: Store) = store.Cells.Count

/// Id of the oldest retained cell (equals `NextId` when nothing is retained).
let oldestId (store: Store) = store.NextId - store.Cells.Count

let private addToIndex (id: int) (words: string[]) (index: Map<string, Set<int>>) =
  words
  |> Array.fold (fun idx word ->
    idx
    |> Map.change word (fun cells ->
      match cells with
      | Some set -> Some (Set.add id set)
      | None -> Some (Set.singleton id))) index

let private removeFromIndex (id: int) (words: string[]) (index: Map<string, Set<int>>) =
  words
  |> Array.fold (fun idx word ->
    idx
    |> Map.change word (fun cells ->
      match cells with
      | Some set ->
        let remaining = Set.remove id set
        match remaining.IsEmpty with
        | true -> None
        | false -> Some remaining
      | None -> None)) index

let private evictOldest (store: Store) : Store =
  let oldest = oldestId store
  match Map.tryFind oldest store.Cells with
  | Some cell ->
    { store with
        Cells = Map.remove oldest store.Cells
        CellsByWord = removeFromIndex oldest cell.Words store.CellsByWord }
  | None -> store

/// Record one eval. Costs O(new cell × log history) at every history size,
/// including at and beyond the cap, where the oldest cell is evicted.
let record (code: string) (result: string) (durationMs: int64) (timestamp: DateTimeOffset) (store: Store) : Store =
  let id = store.NextId
  let produces = CellDependencyGraph.producedNames result
  let cell = {
    Entry = { CellIndex = id; Code = code; Result = result; DurationMs = durationMs; Timestamp = timestamp }
    ScopeBindings = BindingExplorer.parseBindings result
    Produces = produces
    Words = IdentifierScan.boundaryWords code
    FreeIdentifiers = IdentifierScan.freeIdentifiers code
  }
  let recorded =
    { store with
        NextId = id + 1
        Cells = Map.add id cell store.Cells
        CellsByWord = addToIndex id cell.Words store.CellsByWord
        KnownBindings = produces |> List.fold (fun known name -> Map.add name id known) store.KnownBindings
        UnusualKnownNames =
          produces
          |> List.fold (fun names name ->
            match IdentifierScan.isFreeIdentifierToken name with
            | true -> names
            | false -> Set.add name names) store.UnusualKnownNames }
  match recorded.Cells.Count > HistoryCap.cells store.Cap with
  | true -> evictOldest recorded
  | false -> recorded

/// The `n` most recent entries, newest first. O(n log history).
let newest (n: int) (store: Store) : EvalHistoryEntry list =
  let stop = max (oldestId store) (store.NextId - n)
  [ for id in store.NextId - 1 .. -1 .. stop do
      match Map.tryFind id store.Cells with
      | Some cell -> yield cell.Entry
      | None -> () ]

/// Every retained entry, newest first. O(history) — for on-demand readers.
let newestFirst (store: Store) : EvalHistoryEntry list =
  store.Cells |> Map.fold (fun acc _ cell -> cell.Entry :: acc) []

/// Every retained entry, oldest first. O(history) — for on-demand readers.
let chronological (store: Store) : EvalHistoryEntry list =
  Map.foldBack (fun _ cell acc -> cell.Entry :: acc) store.Cells []

/// The binding scope of the retained cells — identical to
/// `BindingExplorer.buildScopeSnapshot` over them, but references come from
/// the word index instead of matching every name against every cell.
let materializeScope (store: Store) : BindingExplorer.BindingScopeSnapshot =
  let cells = store.Cells |> Map.toList
  // Every cell that binds each name, oldest first (repeats kept: a cell that
  // binds a name twice shadows older bindings twice, as the rebuild does).
  let binders = Dictionary<string, ResizeArray<int>>(StringComparer.Ordinal)
  for id, cell in cells do
    for name, _, _ in cell.ScopeBindings do
      match binders.TryGetValue name with
      | true, ids -> ids.Add id
      | false, _ -> binders.[name] <- ResizeArray [ id ]
  // A binding's ShadowedBy is the tail of its name's binder list past its own
  // cell — share that tail instead of copying it per binding.
  let laterBinders = Dictionary<string, int list>(StringComparer.Ordinal)
  for KeyValue (name, ids) in binders do
    laterBinders.[name] <- List.ofSeq ids
  let referencing = Dictionary<string, int list>(StringComparer.Ordinal)
  let cellsReferencing (name: string) =
    match referencing.TryGetValue name with
    | true, ids -> ids
    | false, _ ->
      let ids =
        match IdentifierScan.isBoundaryWordToken name with
        | true ->
          match Map.tryFind name store.CellsByWord with
          | Some set -> Set.toList set
          | None -> []
        | false ->
          cells
          |> List.choose (fun (id, cell) ->
            match IdentifierScan.occursWordBounded name cell.Entry.Code with
            | true -> Some id
            | false -> None)
      referencing.[name] <- ids
      ids
  let bindings : BindingExplorer.BindingInfo list =
    [ for id, cell in cells do
        for name, typeSig, value in cell.ScopeBindings do
          let rec pastOwnCell ids =
            match ids with
            | head :: rest when head <= id -> pastOwnCell rest
            | _ -> ids
          let shadowedBy = pastOwnCell laterBinders.[name]
          laterBinders.[name] <- shadowedBy
          let refs = cellsReferencing name
          let referencedIn =
            match List.contains id refs with
            | true -> refs |> List.filter (fun r -> r <> id)
            | false -> refs
          yield
            { Name = name
              TypeSig = typeSig
              Value = value
              CellIndex = id
              ShadowedBy = shadowedBy
              ReferencedIn = referencedIn } ]
  let active =
    bindings
    |> List.filter (fun b -> List.isEmpty b.ShadowedBy)
    |> List.map (fun b -> (b.Name, b))
    |> Map.ofList
  let shadowed = bindings |> List.filter (fun b -> not (List.isEmpty b.ShadowedBy))
  { Bindings = bindings; ActiveBindings = active; ShadowedBindings = shadowed }

/// The dependency graph of the retained cells — identical to
/// `CellDependencyGraph.buildGraph` over `analyzeCell KnownBindings` of each
/// retained cell, but consumption comes from each cell's identifier set.
let materializeGraph (store: Store) : CellDependencyGraph.CellGraph =
  let oldest = oldestId store
  let producerOf name = Map.tryFind name store.KnownBindings
  let infos : CellDependencyGraph.CellInfo list =
    [ for KeyValue (id, cell) in store.Cells ->
        let plain =
          cell.FreeIdentifiers
          |> Seq.filter (fun name ->
            match producerOf name with
            | Some producer -> producer <> id
            | None -> false)
        let unusual =
          store.UnusualKnownNames
          |> Seq.filter (fun name ->
            match producerOf name with
            | Some producer -> producer <> id && IdentifierScan.occursAsFreeIdentifier name cell.Entry.Code
            | None -> false)
        { Id = id
          Source = cell.Entry.Code
          Produces = cell.Produces
          Consumes = Seq.append plain unusual |> Set.ofSeq |> Set.toList } ]
  let edges =
    [ for info in infos do
        let producers = HashSet<int>()
        for name in info.Consumes do
          match producerOf name with
          // Only a retained producer yields an edge (the rebuild resolves
          // names through retained cells only).
          | Some producer when producer >= oldest && producers.Add producer -> yield (producer, info.Id)
          | _ -> () ]
  { Cells = infos |> List.map (fun info -> (info.Id, info)) |> Map.ofList
    Edges = edges }
