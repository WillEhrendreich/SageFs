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
/// O(size of the new cell) whether the history holds 100 cells or is sitting
/// at the cap. The binding scope and dependency graph are materialized from
/// the index only when someone reads them; every reference check in that
/// materialization is a hash/tree probe, not a regex.
///
/// Retained cells live in fixed-size CHUNKS (plain arrays), not a
/// `Map<int,StoredCell>`. A 10,000-cell persistent balanced tree is ~10,000
/// separate small node objects that every gen0/gen1 GC has to trace to find
/// out what's still reachable — allocation per eval was already flat (each
/// `record` only ever allocates O(log history) new tree nodes), but the LIVE
/// object graph the collector scans every pass grows with history size, and
/// that scan cost is what made steady-state recordEval ~5x slower at 10,000
/// cells than at 1,000 even though the algorithm itself is unchanged. A chunk
/// of `ChunkCapacity` cells is ONE array (one object header, contiguous,
/// cache-friendly) instead of `ChunkCapacity` tree-node objects, so the live
/// object count for the retained history drops from O(history) to
/// O(history / ChunkCapacity) — tens of objects instead of tens of thousands.
///
/// Only the tail chunk is ever written to, and only by copying it (bounded by
/// `ChunkCapacity`, never by history size) into a brand new array — existing
/// chunks are never mutated in place, so an older `Store` handed to another
/// thread never observes a later record's writes, exactly as the persistent
/// Map did. Eviction only ever drops whole chunks once every cell in them has
/// aged out, so up to `ChunkCapacity - 1` already-evicted cells' memory can
/// linger briefly (a bounded, constant amount, independent of the cap) before
/// the chunk holding them is finally dropped — every public read (`newest`,
/// `chronological`, `materializeScope`/`materializeGraph`, ...) still only
/// ever sees exactly the logical window `[NextId - Count, NextId)`.
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

/// Approximate live-object bytes one retained entry contributes: `Code` and
/// `Result` are the only fields on a `StoredCell` whose size is unbounded —
/// an eval that prints megabytes of output is exactly the case the byte
/// budget below exists to bound, independent of how many OTHER small cells
/// are also retained. UTF-16 strings cost 2 bytes/char; this deliberately
/// ignores per-string object overhead (~24-26 bytes) and the small,
/// bounded-per-cell ScopeBindings/Words/FreeIdentifiers, which are dwarfed
/// by a large Result string in exactly the pathological case this exists
/// for.
let entryByteCost (entry: EvalHistoryEntry) : int64 =
  (int64 entry.Code.Length + int64 entry.Result.Length) * 2L

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

/// How many bytes of retained Code+Result text the history may hold (at
/// least one byte) — bounds retention independent of cell COUNT, so a
/// handful of huge results evict sooner than ten thousand tiny ones.
type ByteBudget = private ByteBudget of int64

module ByteBudget =
  /// 64 MiB. A standard-cap (10,000-cell) history of ordinary `let`/`val`
  /// cells — tens to low hundreds of bytes of Code+Result each — totals
  /// well under 4 MB (measured directly in
  /// EvalStoreByteBudgetTests.``steady state: a full standard-cap history of
  /// ordinary cells is well under the default byte budget``). 64 MiB leaves
  /// comfortable headroom for normal use while still bounding runaway
  /// growth from a handful of megabyte-sized printed results.
  [<Literal>]
  let StandardBytes = 67_108_864L

  let standard = ByteBudget StandardBytes

  let tryCreate (bytes: int64) : Result<ByteBudget, string> =
    match bytes >= 1L with
    | true -> Ok (ByteBudget bytes)
    | false -> Error (sprintf "The eval history byte budget must be at least 1 byte; %d was requested." bytes)

  let bytes (ByteBudget b) = b

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

/// How many cells one chunk array holds. Bounds the cost of every write
/// (append copies at most this many cells, never the whole history) and the
/// lingering-after-eviction memory (at most this many cells' worth).
[<Literal>]
let private ChunkCapacity = 256

/// A fixed-size, append-only slice of the retained history: cells
/// `[0, FilledCount)` of `Cells` are populated, starting at cell id `BaseId`.
/// Every chunk but the most recently created one is completely full. Chunks
/// are never mutated after being taken over by a new `Store` value —
/// `record` and eviction always build a new array (or a new chunk) rather
/// than write through an existing one.
type Chunk = {
  BaseId: int
  Cells: StoredCell[]
  FilledCount: int
}

type Store = {
  Cap: HistoryCap
  /// Byte budget on the retained cells' Code+Result text — enforced
  /// alongside `Cap`, never in place of it.
  ByteBudget: ByteBudget
  /// The id the next recorded cell gets. Never decreases.
  NextId: int
  /// Logical count of retained cells: exactly the ids `[NextId - Count,
  /// NextId)`. May be smaller than the total cells still physically present
  /// in `Chunks` — see the module doc on lingering eviction.
  Count: int
  /// Sum of `entryByteCost` over exactly the logically retained cells.
  /// Maintained incrementally on record/evict — never recomputed by
  /// scanning history.
  RetainedBytes: int64
  /// Oldest-to-newest chunks of retained (and briefly, just-evicted) cells.
  /// Not part of the public contract — treat as an implementation detail of
  /// this module; use `count`/`newest`/`chronological`/`materializeScope`/
  /// `materializeGraph` to read the retained history.
  Chunks: Chunk[]
  /// Inverted index: `\b` word -> retained cells whose source contains it.
  CellsByWord: Map<string, Set<int>>
  /// Newest cell (ever recorded, retained or not) that bound each name.
  KnownBindings: Map<string, int>
  /// Known names that are not plain identifiers (operators, primed or
  /// backticked names). The free-identifier index cannot answer for these,
  /// so materialization scans for them literally.
  UnusualKnownNames: Set<string>
}

/// A store with a custom byte budget alongside its cell-count cap. Tests
/// use this to exercise the byte-eviction path with small budgets; `empty`
/// (below) is the production entry point and always applies the standard
/// 64 MiB budget.
let emptyWithByteBudget (cap: HistoryCap) (byteBudget: ByteBudget) : Store =
  { Cap = cap
    ByteBudget = byteBudget
    NextId = 0
    Count = 0
    RetainedBytes = 0L
    Chunks = [||]
    CellsByWord = Map.empty
    KnownBindings = Map.empty
    UnusualKnownNames = Set.empty }

let empty (cap: HistoryCap) : Store = emptyWithByteBudget cap ByteBudget.standard

let count (store: Store) = store.Count

/// Sum of `entryByteCost` over the logically retained cells.
let retainedBytes (store: Store) = store.RetainedBytes

/// Id of the oldest retained cell (equals `NextId` when nothing is retained).
let oldestId (store: Store) = store.NextId - store.Count

/// Find a cell by id among the physically-present chunks (which may
/// momentarily include a handful of already-evicted cells — see the module
/// doc). Callers only ever pass ids within `[oldestId store, NextId)`.
let private tryFindCell (id: int) (store: Store) : StoredCell option =
  store.Chunks
  |> Array.tryPick (fun c ->
    match id >= c.BaseId && id < c.BaseId + c.FilledCount with
    | true -> Some c.Cells.[id - c.BaseId]
    | false -> None)

/// Every LOGICALLY retained cell, ascending by id — i.e. exactly `[NextId -
/// Count, NextId)`, skipping any cells a chunk is still physically holding
/// past their eviction.
let private allCellsAscending (store: Store) : (int * StoredCell) seq =
  let oldest = oldestId store
  seq {
    for c in store.Chunks do
      let startOffset = max 0 (oldest - c.BaseId)
      for i in startOffset .. c.FilledCount - 1 do
        yield (c.BaseId + i, c.Cells.[i])
  }

/// Append one cell, copying only the tail chunk (or starting a fresh one) —
/// cost bounded by `ChunkCapacity`, never by history size. Every other chunk
/// is shared, unmutated, with whichever `Store` still references it.
let private appendCell (cell: StoredCell) (chunks: Chunk[]) : Chunk[] =
  let id = cell.Entry.CellIndex
  let freshChunk () =
    let arr : StoredCell[] = Array.zeroCreate ChunkCapacity
    arr.[0] <- cell
    { BaseId = id; Cells = arr; FilledCount = 1 }
  match chunks.Length with
  | 0 -> [| freshChunk () |]
  | n ->
    let last = chunks.[n - 1]
    match last.FilledCount = ChunkCapacity with
    | true -> Array.append chunks [| freshChunk () |]
    | false ->
      let arr = Array.copy last.Cells
      arr.[last.FilledCount] <- cell
      let copy = Array.copy chunks
      copy.[n - 1] <- { last with Cells = arr; FilledCount = last.FilledCount + 1 }
      copy

/// Drop every leading chunk that is now ENTIRELY below `newOldest` — never
/// the tail chunk, which always holds the just-recorded cell. Most evictions
/// drop nothing here (a chunk only empties out once every ChunkCapacity
/// evictions); this is what lets those already-evicted cells linger briefly
/// rather than force a copy on every single eviction.
let private dropStaleChunks (newOldest: int) (chunks: Chunk[]) : Chunk[] =
  let rec staleCount i =
    match i < chunks.Length - 1 && chunks.[i].BaseId + chunks.[i].FilledCount <= newOldest with
    | true -> staleCount (i + 1)
    | false -> i
  match staleCount 0 with
  | 0 -> chunks
  | n -> chunks.[n ..]

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
  match tryFindCell oldest store with
  | Some cell ->
    let newOldest = oldest + 1
    { store with
        Count = store.Count - 1
        RetainedBytes = store.RetainedBytes - entryByteCost cell.Entry
        Chunks = dropStaleChunks newOldest store.Chunks
        CellsByWord = removeFromIndex oldest cell.Words store.CellsByWord }
  | None -> store

/// Evict oldest-first until BOTH the count cap and the byte budget are
/// satisfied — never below one retained cell, so a single cell larger than
/// the byte budget on its own is still retained rather than leaving the
/// history empty. Usually evicts at most one cell per `record` (the cap can
/// only be exceeded by exactly one); a byte budget crossed by a single
/// large cell can evict several smaller ones in one call.
let rec private evictWhileOverBudget (store: Store) : Store =
  match store.Count > 1
        && (store.Count > HistoryCap.cells store.Cap
            || store.RetainedBytes > ByteBudget.bytes store.ByteBudget) with
  | true -> evictWhileOverBudget (evictOldest store)
  | false -> store

/// Record one eval. Costs O(size of the new cell), bounded independent of
/// history size, at every history size including at and beyond the cap or
/// the byte budget, where the oldest cell(s) are evicted.
let record (code: string) (result: string) (durationMs: int64) (timestamp: DateTimeOffset) (store: Store) : Store =
  let id = store.NextId
  let produces = CellDependencyGraph.producedNames result
  let entry = { CellIndex = id; Code = code; Result = result; DurationMs = durationMs; Timestamp = timestamp }
  let cell = {
    Entry = entry
    ScopeBindings = BindingExplorer.parseBindings result
    Produces = produces
    Words = IdentifierScan.boundaryWords code
    FreeIdentifiers = IdentifierScan.freeIdentifiers code
  }
  let recorded =
    { store with
        NextId = id + 1
        Count = store.Count + 1
        RetainedBytes = store.RetainedBytes + entryByteCost entry
        Chunks = appendCell cell store.Chunks
        CellsByWord = addToIndex id cell.Words store.CellsByWord
        KnownBindings = produces |> List.fold (fun known name -> Map.add name id known) store.KnownBindings
        UnusualKnownNames =
          produces
          |> List.fold (fun names name ->
            match IdentifierScan.isFreeIdentifierToken name with
            | true -> names
            | false -> Set.add name names) store.UnusualKnownNames }
  evictWhileOverBudget recorded

/// The `n` most recent entries, newest first. O(n).
let newest (n: int) (store: Store) : EvalHistoryEntry list =
  let stop = max (oldestId store) (store.NextId - n)
  [ for id in store.NextId - 1 .. -1 .. stop do
      match tryFindCell id store with
      | Some cell -> yield cell.Entry
      | None -> () ]

/// Every retained entry, newest first. O(history) — for on-demand readers.
let newestFirst (store: Store) : EvalHistoryEntry list =
  allCellsAscending store |> Seq.fold (fun acc (_, cell) -> cell.Entry :: acc) []

/// Every retained entry, oldest first. O(history) — for on-demand readers.
let chronological (store: Store) : EvalHistoryEntry list =
  allCellsAscending store |> Seq.map (fun (_, cell) -> cell.Entry) |> Seq.toList

/// The two most recent entries matching `predicate`, as (older, newer) — for
/// diffing consecutive evals. `history` must be newest-first (as
/// `newestFirst`/`newest` produce it); filtering a newest-first list
/// preserves newest-first order among the matches, so the first two matches
/// are directly (newer, older) with no reversal needed — the bug this
/// replaces was a manual List.rev/truncate dance that silently swapped which
/// entry was "old" and which was "new".
let recentPair (predicate: EvalHistoryEntry -> bool) (history: EvalHistoryEntry list) : (EvalHistoryEntry * EvalHistoryEntry) option =
  match history |> List.filter predicate with
  | newer :: older :: _ -> Some (older, newer)
  | _ -> None

/// The binding scope of the retained cells — identical to
/// `BindingExplorer.buildScopeSnapshot` over them, but references come from
/// the word index instead of matching every name against every cell.
let materializeScope (store: Store) : BindingExplorer.BindingScopeSnapshot =
  let cells = allCellsAscending store |> List.ofSeq
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
    [ for id, cell in allCellsAscending store ->
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
