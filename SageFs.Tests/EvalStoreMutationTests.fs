/// ## EvalStore Mutation Tests
///
/// EvalStoreTests.fs already carries extensive FsCheck characterization
/// properties for this module (oracle-vs-store equivalence across random
/// eval sequences). This file adds small, hand-picked exact-equality pins
/// for the specific behaviors the roast calls out by name — at-cap
/// eviction, newest-vs-chronological ordering, and KnownBindings
/// "latest wins" — so a targeted mutant (e.g. evicting the NEWEST cell
/// instead of the oldest, or reversing `newest`) is killed by a test whose
/// failure immediately names the exact wrong behavior.
module EvalStoreMutationTests

open Expecto
open Expecto.Flip
open System
open SageFs.Features

let private cap n =
  match EvalStore.HistoryCap.tryCreate n with
  | Ok c -> c
  | Error msg -> failtest msg

let private ts = DateTimeOffset.UnixEpoch

let evalStoreMutationTests = testList "EvalStore mutations" [

  // ── At-cap eviction: the OLDEST cell must go, never the newest ─────────

  testCase "WHY — atCap_evicts_oldest_not_newest — recording past the cap must drop cell 0, keeping the two newest" <| fun () ->
    let store =
      EvalStore.empty (cap 2)
      |> EvalStore.record "let a = 1" "val a: int = 1" 1L ts
      |> EvalStore.record "let b = 2" "val b: int = 2" 1L ts
      |> EvalStore.record "let c = 3" "val c: int = 3" 1L ts
    store |> EvalStore.chronological |> List.map (fun e -> e.CellIndex)
    |> Expect.equal "with cap=2, after 3 records the retained ids must be [1;2] (the two NEWEST), never [0;1]" [1; 2]

  testCase "WHY — count_never_exceeds_cap — Count must be clamped to the cap after eviction, not grow unbounded" <| fun () ->
    let store =
      EvalStore.empty (cap 2)
      |> EvalStore.record "a" "" 1L ts
      |> EvalStore.record "b" "" 1L ts
      |> EvalStore.record "c" "" 1L ts
    EvalStore.count store |> Expect.equal "Count must be exactly the cap (2), not 3" 2

  testCase "WHY — oldestId_tracks_NextId_minus_Count — the retention window's lower bound must be derived, not stale" <| fun () ->
    let store =
      EvalStore.empty (cap 2)
      |> EvalStore.record "a" "" 1L ts
      |> EvalStore.record "b" "" 1L ts
      |> EvalStore.record "c" "" 1L ts
    (EvalStore.oldestId store, store.NextId)
    |> Expect.equal "oldestId must be NextId(3) - Count(2) = 1" (1, 3)

  // ── newest: newest-FIRST order, bounded by n ─────────────────────────────

  testCase "WHY — newest_returns_newest_first_not_chronological — newest and chronological must be REVERSES of each other" <| fun () ->
    let store =
      EvalStore.empty (cap 10)
      |> EvalStore.record "a" "" 1L ts
      |> EvalStore.record "b" "" 1L ts
      |> EvalStore.record "c" "" 1L ts
    EvalStore.newest 3 store |> List.map (fun e -> e.CellIndex)
    |> Expect.equal "newest 3 must be [2;1;0] — newest first — not [0;1;2]" [2; 1; 0]

  testCase "WHY — newest_n_smaller_than_history_returns_only_n — asking for fewer than retained must not return everything" <| fun () ->
    let store =
      EvalStore.empty (cap 10)
      |> EvalStore.record "a" "" 1L ts
      |> EvalStore.record "b" "" 1L ts
      |> EvalStore.record "c" "" 1L ts
    EvalStore.newest 1 store |> List.map (fun e -> e.CellIndex)
    |> Expect.equal "newest 1 must return only the single most recent cell [2]" [2]

  testCase "WHY — chronological_is_oldest_first — chronological must never be newest-first" <| fun () ->
    let store =
      EvalStore.empty (cap 10)
      |> EvalStore.record "a" "" 1L ts
      |> EvalStore.record "b" "" 1L ts
      |> EvalStore.record "c" "" 1L ts
    EvalStore.chronological store |> List.map (fun e -> e.CellIndex)
    |> Expect.equal "chronological must be [0;1;2] — oldest first" [0; 1; 2]

  // ── KnownBindings: latest producer wins ──────────────────────────────────

  testCase "WHY — knownBindings_latest_producer_wins — rebinding a name must update KnownBindings to the NEW cell, not keep the old one" <| fun () ->
    let store =
      EvalStore.empty (cap 10)
      |> EvalStore.record "let x = 1" "val x: int = 1" 1L ts
      |> EvalStore.record "let y = 2" "val y: int = 2" 1L ts
      |> EvalStore.record "let x = 3" "val x: int = 3" 1L ts
    Map.tryFind "x" store.KnownBindings
    |> Expect.equal "after re-binding x in cell 2, KnownBindings.[\"x\"] must be 2, not the original 0" (Some 2)

  testCase "WHY — knownBindings_unaffected_name_keeps_original_producer" <| fun () ->
    let store =
      EvalStore.empty (cap 10)
      |> EvalStore.record "let x = 1" "val x: int = 1" 1L ts
      |> EvalStore.record "let y = 2" "val y: int = 2" 1L ts
    Map.tryFind "y" store.KnownBindings
    |> Expect.equal "y was only ever bound in cell 1" (Some 1)

  // ── recentPair: (older, newer), from a newest-first list ────────────────

  testCase "WHY — recentPair_orders_as_older_then_newer_not_reversed — the roast calls out exactly this historical bug (rev/truncate swap)" <| fun () ->
    let store =
      EvalStore.empty (cap 10)
      |> EvalStore.record "let ok x = 1" "" 1L ts
      |> EvalStore.record "skip me" "" 1L ts
      |> EvalStore.record "let ok x = 2" "" 1L ts
    let history = EvalStore.newest 10 store
    EvalStore.recentPair (fun e -> e.Code.StartsWith "let ok") history
    |> Option.map (fun (older, newer) -> older.CellIndex, newer.CellIndex)
    |> Expect.equal "the two matches at cell 0 and cell 2 must come back as (older=0, newer=2), never swapped" (Some (0, 2))

  testCase "WHY — recentPair_none_when_fewer_than_two_matches" <| fun () ->
    let store = EvalStore.empty (cap 10) |> EvalStore.record "let ok x = 1" "" 1L ts
    let history = EvalStore.newest 10 store
    EvalStore.recentPair (fun e -> e.Code.StartsWith "let ok") history
    |> Expect.equal "with only one match, recentPair must be None, not pair the single entry with itself" None

  // ── empty store invariants ───────────────────────────────────────────────

  testCase "WHY — empty_store_has_zero_count_and_nextId_zero" <| fun () ->
    let store = EvalStore.empty (cap 5)
    (EvalStore.count store, store.NextId, EvalStore.oldestId store)
    |> Expect.equal "a fresh empty store must have Count=0, NextId=0, oldestId=0" (0, 0, 0)

  testCase "WHY — record_assigns_dense_monotonic_ids_starting_at_zero" <| fun () ->
    let store =
      EvalStore.empty (cap 10)
      |> EvalStore.record "a" "" 1L ts
      |> EvalStore.record "b" "" 1L ts
    store |> EvalStore.chronological |> List.map (fun e -> e.CellIndex)
    |> Expect.equal "the first two recorded cells must get ids 0 and 1, in order" [0; 1]
]
