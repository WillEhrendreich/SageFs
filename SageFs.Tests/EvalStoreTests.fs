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

    testPropertyWithConfig
      { FsCheckConfig.defaultConfig with maxTest = 300 }
      "random record sequences under any cap: count/oldestId/NextId, newest*/chronological, KnownBindings, and materializeScope/materializeGraph all agree with a from-scratch rebuild" <|
      Prop.forAll (Arb.fromGen (Gen.zip (Gen.choose (1, 8)) (Gen.listOf genStep))) (fun (cap, steps) ->
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

        // The two materializations agree with an from-scratch rebuild over
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
        actualGraph.Edges |> Expect.equal "graph edges match a rebuild" expectedGraph.Edges)
  ]
