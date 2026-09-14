/// Characterization tests for `SageFs.Features.EvalStore`, written BEFORE the
/// data-oriented rework (persistent `Map<int,StoredCell>` history -> a
/// contiguous, chunked ring) so the rework can be proven behaviorally
/// IDENTICAL to what shipped before it. Every property here must pass
/// unchanged against both the old Map-backed store and the new one — that is
/// the whole point: these pin the CURRENT observable behavior of every public
/// EvalStore operation, driven directly at the EvalStore module boundary
/// (not through FeatureHooks), so a storage-representation change with a bug
/// in it fails here even if FeatureHookTests' higher-level oracles happen not
/// to exercise the broken path.
module SageFs.Tests.EvalStoreTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open System
open SageFs.Features

let private capOf cells =
  match EvalStore.HistoryCap.tryCreate cells with
  | Ok cap -> cap
  | Error reason -> failtest reason

// Cells that stress redefinition, shadowing, cross-cell references and
// failed (nothing-bound) evals — the same shapes FeatureHookTests uses to
// pin BindingExplorer/CellDependencyGraph agreement, reused here to exercise
// EvalStore.materializeScope/materializeGraph directly.
let private genName = Gen.elements [ "a"; "b"; "x"; "it"; "x'"; "(+.)" ]

let private genExpr =
  Gen.elements [ "1"; "a"; "b + a"; "x.Length"; "Foo.x"; "x'"; "a(+.)b"; "\"a\""; "it"; "mutable m"; "m"; "// b" ]

let private genStep =
  Gen.oneof [
    Gen.map2 (fun n e -> sprintf "let %s = %s" n e, sprintf "val %s: int = 42" n) genName genExpr
    Gen.map3 (fun n1 n2 e -> sprintf "let %s, %s = %s, 2" n1 n2 e, sprintf "val %s: int = 1\nval %s: int = 2" n1 n2) genName genName genExpr
    Gen.map (fun e -> e, "val it: int = 3") genExpr
    Gen.map (fun e -> e, "error FS0039: The value or constructor is not defined.") genExpr
    Gen.map (fun e -> sprintf "let mutable m = %s" e, "val mutable m: int = 1") genExpr
  ]

let private ts = DateTimeOffset.UnixEpoch

/// Rebuild what `NextId - Cells.Count` retention SHOULD contain, from the raw
/// step list: the newest `min(cap, steps.Length)` steps, each tagged with its
/// original (0-based, never-reused) id.
let private retainedIndexed (cap: int) (steps: (string * string) list) =
  let kept = min cap steps.Length
  steps
  |> List.mapi (fun i (code, result) -> i, code, result)
  |> List.skip (steps.Length - kept)

let private buildAt (cap: int) (steps: (string * string) list) =
  steps
  |> List.fold (fun st (code, result) -> EvalStore.record code result 5L ts st) (EvalStore.empty (capOf cap))

/// From-scratch binding scope of exactly the retained cells — independent of
/// EvalStore's internal indexing, the oracle the store must reproduce.
let private oracleScope (retained: (int * string * string) list) : BindingExplorer.BindingScopeSnapshot =
  retained
  |> List.map (fun (id, code, result) ->
    ({ CellIndex = id; FsiOutput = result; Source = code }: BindingExplorer.CellInput))
  |> BindingExplorer.buildScopeSnapshot

/// From-scratch dependency graph of exactly the retained cells, resolved
/// against the newest-producer-ever KnownBindings the store maintains.
let private oracleGraph (knownBindings: Map<string, int>) (retained: (int * string * string) list) : CellDependencyGraph.CellGraph =
  retained
  |> List.map (fun (id, code, result) -> CellDependencyGraph.analyzeCell knownBindings id code result)
  |> CellDependencyGraph.buildGraph

let private expectedKnownBindings (steps: (string * string) list) =
  steps
  |> List.mapi (fun i (_, result) -> i, result)
  |> List.fold (fun known (i, result) ->
    CellDependencyGraph.producedNames result |> List.fold (fun k name -> Map.add name i k) known) Map.empty

[<Tests>]
let evalStoreTests =
  testList "EvalStore (characterization — pins behavior across the ring-buffer rework)" [

    testList "empty store" [
      test "WHY — a brand-new store has no cells and NextId 0, because eval ids must start at zero" {
        let store = EvalStore.empty (capOf 10)
        EvalStore.count store |> Expect.equal "no cells retained" 0
        store.NextId |> Expect.equal "ids start at 0" 0
        EvalStore.oldestId store |> Expect.equal "oldest of nothing is NextId" 0
        EvalStore.newest 5 store |> Expect.isEmpty "nothing to return"
        EvalStore.newestFirst store |> Expect.isEmpty "nothing to return"
        EvalStore.chronological store |> Expect.isEmpty "nothing to return"
        (EvalStore.materializeScope store).Bindings |> Expect.isEmpty "no bindings yet"
        (EvalStore.materializeGraph store).Cells |> Expect.isEmpty "no cells yet"
      }
    ]

    testList "HistoryCap" [
      test "WHY — a cap below one cell is refused with a reason, because a history that retains nothing is not a history" {
        EvalStore.HistoryCap.tryCreate 0
        |> Result.mapError (fun reason -> reason.Contains "at least 1 cell")
        |> Expect.equal "zero cells is not a history" (Error true)
      }
      test "WHY — the standard cap is exactly 10,000 cells, because that is the documented retention budget" {
        EvalStore.HistoryCap.cells EvalStore.HistoryCap.standard |> Expect.equal "10,000 standard cells" 10_000
      }
    ]

    testList "ByteBudget (roast-6 #8 — retained history is count-bounded but not byte-bounded)" [
      test "WHY — a budget below one byte is refused with a reason, mirroring HistoryCap.tryCreate" {
        EvalStore.ByteBudget.tryCreate 0L
        |> Result.mapError (fun reason -> reason.Contains "at least 1 byte")
        |> Expect.equal "zero bytes is not a budget" (Error true)
      }

      test "WHY — the standard byte budget is 64 MiB, the documented retention budget" {
        EvalStore.ByteBudget.bytes EvalStore.ByteBudget.standard
        |> Expect.equal "64 MiB standard budget" (64L * 1024L * 1024L)
      }

      test "WHY — entryByteCost is 2 bytes per UTF-16 char of Code+Result, the only unbounded StoredCell fields" {
        let entry : EvalStore.EvalHistoryEntry =
          { CellIndex = 0; Code = "let x = 1"; Result = "val x: int = 1"; DurationMs = 1L; Timestamp = ts }
        EvalStore.entryByteCost entry
        |> Expect.equal "(9 + 15) chars * 2 bytes/char" (int64 (entry.Code.Length + entry.Result.Length) * 2L)
      }

      // ── the roast's own characterization: bytes grow unbounded under the
      // count cap alone, because a count cap says nothing about entry size ──

      test "WHY — count_cap_alone_lets_bytes_grow_unbounded — many large entries all stay retained under a generous count cap with NO byte budget applied, because the count cap has no notion of size" {
        let bigResult = String('x', 100_000) // ~200KB per entry as UTF-16
        let store =
          [ 1 .. 50 ]
          |> List.fold (fun st i -> EvalStore.record (sprintf "let v%d = 1" i) bigResult 1L ts st)
               (EvalStore.emptyWithByteBudget (capOf 10_000) (EvalStore.ByteBudget.standard))
        // 50 * 200KB = ~10MB retained bytes for entries that individually
        // dwarf a "tiny cell" — none were evicted because count (50) is
        // nowhere near the 10,000 cap. This is the growth the byte budget
        // exists to bound once it's small enough to matter; the test below
        // pins that the SAME construction, under a tight budget, evicts.
        EvalStore.count store |> Expect.equal "count cap alone retained every cell" 50
      }

      test "WHY — byteBudget_evicts_oldest_large_entries_even_though_count_cap_not_reached — a tight byte budget must evict well before the count cap, oldest first" {
        let bigResult = String('x', 100_000) // ~200KB per entry as UTF-16
        let tightBudget =
          match EvalStore.ByteBudget.tryCreate (300_000L) with // room for exactly one ~200KB entry, never two
          | Ok b -> b
          | Error msg -> failtest msg
        let store =
          [ 0 .. 9 ]
          |> List.fold (fun st i -> EvalStore.record (sprintf "let v%d = 1" i) bigResult 1L ts st)
               (EvalStore.emptyWithByteBudget (capOf 10_000) tightBudget)
        // The count cap (10,000) never fires; only the byte budget does.
        (EvalStore.count store < 10)
        |> Expect.isTrue "a 300KB budget must not retain all 10 ~200KB entries"
        EvalStore.retainedBytes store <= EvalStore.ByteBudget.bytes tightBudget
        |> Expect.isTrue "retained bytes must fit the budget once more than one cell has been recorded"
        EvalStore.oldestId store |> Expect.equal "eviction drops the OLDEST cells first, same as count-based eviction" 9

      }

      test "WHY — byteBudget_never_evicts_the_last_cell — a single cell larger than the whole budget is still retained, never leaving the history empty" {
        let hugeResult = String('x', 1_000_000)
        let tinyBudget =
          match EvalStore.ByteBudget.tryCreate 10L with
          | Ok b -> b
          | Error msg -> failtest msg
        let store =
          EvalStore.emptyWithByteBudget (capOf 10) tinyBudget
          |> EvalStore.record "let x = 1" hugeResult 1L ts
        EvalStore.count store |> Expect.equal "the just-recorded cell is retained despite exceeding the budget alone" 1
      }

      test "WHY — byteBudget_respects_both_bounds_together — the count cap still applies when bytes are small, and the byte budget still applies when count is small" {
        let store =
          [ 0 .. 4 ]
          |> List.fold (fun st i -> EvalStore.record (sprintf "let v%d = 1" i) "ok" 1L ts st)
               (EvalStore.emptyWithByteBudget (capOf 3) EvalStore.ByteBudget.standard)
        EvalStore.count store |> Expect.equal "the count cap (3) bounds tiny entries just as before" 3
      }

      test "WHY — steady state: a full standard-cap history of ordinary cells is well under the default byte budget — the benchmark that justifies StandardBytes" {
        let ordinaryCode i = sprintf "let v%d = %d + someHelper x y" i i
        let ordinaryResult i = sprintf "val v%d: int = %d" i i
        let store =
          [ 0 .. EvalStore.HistoryCap.StandardCells - 1 ]
          |> List.fold (fun st i -> EvalStore.record (ordinaryCode i) (ordinaryResult i) 1L ts st)
               (EvalStore.empty EvalStore.HistoryCap.standard)
        EvalStore.count store |> Expect.equal "the full standard cap is retained" EvalStore.HistoryCap.StandardCells
        (EvalStore.retainedBytes store < 4L * 1024L * 1024L)
        |> Expect.isTrue
          (sprintf
            "a full 10,000-cell history of ordinary cells measured %d bytes — comfortably under 4MB, which is why the 64MiB default budget never fires for normal use"
            (EvalStore.retainedBytes store))
      }
    ]

    testList "record / eviction (example-based)" [
      test "WHY — recording below the cap never evicts, because eviction only ever removes the OLDEST cell once retention exceeds the cap" {
        let store = buildAt 10 [ for i in 0 .. 4 -> sprintf "let v%d = %d" i i, sprintf "val v%d: int = %d" i i ]
        EvalStore.count store |> Expect.equal "all 5 retained" 5
        EvalStore.oldestId store |> Expect.equal "cell 0 still oldest" 0
        store.NextId |> Expect.equal "5 ids issued" 5
      }

      test "WHY — recording past the cap evicts exactly the single oldest cell, because the retained window always slides by one" {
        let store = buildAt 5 [ for i in 0 .. 5 -> sprintf "let v%d = %d" i i, sprintf "val v%d: int = %d" i i ]
        EvalStore.count store |> Expect.equal "still exactly the cap" 5
        EvalStore.oldestId store |> Expect.equal "cell 0 evicted, cell 1 is now oldest" 1
        store.NextId |> Expect.equal "ids keep counting past the cap" 6
        (EvalStore.materializeScope store).ActiveBindings
        |> Map.containsKey "v0"
        |> Expect.isFalse "the evicted cell's binding left the scope"
      }

      test "WHY — ids never repeat even after many evictions, because a stale SSE/dashboard reference to an old id must never collide with a live cell" {
        let store = buildAt 3 [ for i in 0 .. 99 -> sprintf "let v%d = %d" i i, sprintf "val v%d: int = %d" i i ]
        store.NextId |> Expect.equal "one id issued per record, monotonically" 100
        EvalStore.oldestId store |> Expect.equal "the newest 3 remain" 97
      }
    ]

    testList "newest / newestFirst / chronological (example-based)" [
      test "WHY — newest returns the requested count newest-first, and asking for more than exists returns everything" {
        let store = buildAt 100 [ for i in 0 .. 24 -> sprintf "%d" i, sprintf "val it: int = %d" i ]
        EvalStore.newest 20 store |> List.map (fun e -> e.CellIndex) |> Expect.equal "newest 20, descending" [ 24 .. -1 .. 5 ]
        EvalStore.newest 1000 store |> List.map (fun e -> e.CellIndex) |> Expect.equal "fewer than asked returns them all" [ 24 .. -1 .. 0 ]
        EvalStore.newest 0 store |> Expect.isEmpty "asking for zero returns nothing"
      }

      test "WHY — newestFirst and chronological are exact reverses of each other over the retained window" {
        let store = buildAt 4 [ for i in 0 .. 9 -> sprintf "%d" i, sprintf "val it: int = %d" i ]
        let newestFirst = EvalStore.newestFirst store |> List.map (fun e -> e.CellIndex)
        let chrono = EvalStore.chronological store |> List.map (fun e -> e.CellIndex)
        newestFirst |> Expect.equal "newest-first is the retained window descending" [ 9; 8; 7; 6 ]
        chrono |> Expect.equal "chronological is the retained window ascending" [ 6; 7; 8; 9 ]
        List.rev newestFirst |> Expect.equal "reversing one gives the other" chrono
      }
    ]

    // Shared body for both property tests below: every public read of a
    // built store must agree with a from-scratch rebuild over exactly the
    // retained cells, whatever the cap and however many chunks that cap
    // spans internally.
    let checkAgreesWithRebuild (cap: int, steps: (string * string) list) =
      let store = buildAt cap steps
      let retained = retainedIndexed cap steps
      let kept = List.length retained

      // Counting invariants.
      store.NextId |> Expect.equal "NextId equals the number of records made" steps.Length
      EvalStore.count store |> Expect.equal "count is min(cap, records made)" kept
      EvalStore.oldestId store |> Expect.equal "oldestId is NextId - count" (store.NextId - kept)

      // Ordering: newestFirst/chronological/newest all agree with the plain
      // retained-window rebuild, in the right direction.
      let expectedAscendingIds = retained |> List.map (fun (id, _, _) -> id)
      EvalStore.chronological store |> List.map (fun e -> e.CellIndex)
      |> Expect.equal "chronological is the retained window, oldest first" expectedAscendingIds
      EvalStore.newestFirst store |> List.map (fun e -> e.CellIndex)
      |> Expect.equal "newestFirst is the retained window, newest first" (List.rev expectedAscendingIds)
      EvalStore.newest kept store |> List.map (fun e -> e.CellIndex)
      |> Expect.equal "newest(count) is the same as newestFirst" (List.rev expectedAscendingIds)

      // KnownBindings remembers the newest-ever producer of every bound name,
      // including names bound by cells that have since been evicted.
      store.KnownBindings |> Expect.equal "newest producer of every name ever bound, retained or not" (expectedKnownBindings steps)

      // The two materializations agree with a from-scratch rebuild over
      // exactly the retained cells.
      let retainedTriples = retained |> List.map (fun (id, code, result) -> id, code, result)
      let expectedScope = oracleScope retainedTriples
      let actualScope = EvalStore.materializeScope store
      actualScope.Bindings |> Expect.equal "scope bindings match a rebuild" expectedScope.Bindings
      actualScope.ActiveBindings |> Expect.equal "active bindings match a rebuild" expectedScope.ActiveBindings
      actualScope.ShadowedBindings |> Expect.equal "shadowed bindings match a rebuild" expectedScope.ShadowedBindings

      let expectedGraph = oracleGraph store.KnownBindings retainedTriples
      let actualGraph = EvalStore.materializeGraph store
      actualGraph.Cells |> Expect.equal "graph cells match a rebuild" expectedGraph.Cells
      actualGraph.Edges |> Expect.equal "graph edges match a rebuild" expectedGraph.Edges
      store

    testPropertyWithConfig
      { FsCheckConfig.defaultConfig with maxTest = 300 }
      "random record sequences under a SMALL cap (never spans more than one internal chunk): count/oldestId/NextId, newest*/chronological, KnownBindings, and materializeScope/materializeGraph all agree with a from-scratch rebuild" <|
      Prop.forAll (Arb.fromGen (Gen.zip (Gen.choose (1, 8)) (Gen.listOf genStep))) (fun (cap, steps) ->
        checkAgreesWithRebuild (cap, steps) |> ignore)

    // The property above never exercises more than one internal chunk (the
    // chunk size is 256 cells; caps 1-8 never approach it) — it would pass
    // just as well if chunking, fresh-chunk creation, and multi-chunk
    // eviction were all broken. This property drives caps and step counts
    // well past 256 so appendCell must start new chunks and dropStaleChunks
    // must drop whole chunks, and still checks every read against the same
    // from-scratch rebuild.
    testPropertyWithConfig
      { FsCheckConfig.defaultConfig with maxTest = 60 }
      "random record sequences that span MULTIPLE 256-cell chunks: the same reads still agree with a from-scratch rebuild" <|
      // Chunk size mirrors EvalStore's private ChunkCapacity (256): the cap
      // is always comfortably above it, and the sanity guard below fires
      // once enough cells have been recorded to force a second chunk,
      // independent of cap.
      Prop.forAll
        (Arb.fromGen (
          Gen.choose (300, 600)
          |> Gen.bind (fun cap ->
            Gen.choose (0, cap * 3 + 10)
            |> Gen.bind (fun stepCount -> Gen.listOfLength stepCount genStep)
            |> Gen.map (fun steps -> cap, steps))))
        (fun (cap, steps) ->
          let store = checkAgreesWithRebuild (cap, steps)
          // Sanity on the property itself: recording past 256 cells really
          // did require more than one chunk — otherwise this test would be
          // exercising the same single-chunk path as the property above and
          // the header comment would be a lie.
          match steps.Length > 256 with
          | true -> store.Chunks.Length > 1 |> Expect.isTrue "recording past 256 cells needs more than one chunk"
          | false -> ())
  ]
