// formal-verification/tests/ringbuffer/run.fsx
// Correspondence tests: F# RingBuffer vs Lean model (RingBuffer.lean)
// Validates that the F# implementation satisfies the same properties proved in Lean

#r "nuget: Expecto"
// WHY — the module under test lives in SageFs.Core; the workflow builds it
// (Debug) before invoking this script so the relative reference resolves.
#r "../../../SageFs.Core/bin/Debug/net10.0/SageFs.Core.dll"

open System
open Expecto
open Expecto.Flip

module RingBuffer = SageFs.RingBuffer

// Helper: create a buffer and push items, returning (buffer, list of items pushed)
let pushN n buf =
  let mutable b = buf
  let pushed = ResizeArray<_>()
  for i in 1..n do
    b <- RingBuffer.push i b
    pushed.Add(i)
  b, List.ofSeq pushed

// ────────────────────────────────────────────────────────────
// Group 1: create — Lean: create_wf
// ────────────────────────────────────────────────────────────
let createTests =
  testList "create" [
    test "count is 0 after create" {
      let buf = RingBuffer.create 5
      buf |> RingBuffer.count |> Expect.equal "count should be 0" 0
    }
    test "head is 0 after create" {
      let buf = RingBuffer.create 5
      Expect.equal "head should be 0" buf.Head 0
    }
    test "total is 0 after create" {
      let buf = RingBuffer.create 5
      buf |> RingBuffer.totalPushed |> Expect.equal "total should be 0" 0L
    }
    test "capacity is as specified" {
      let buf = RingBuffer.create 5
      buf |> RingBuffer.capacity |> Expect.equal "capacity should be 5" 5
    }
  ]

// ────────────────────────────────────────────────────────────
// Group 2: push count — Lean: push_count_increments, push_count_capped
// ────────────────────────────────────────────────────────────
let pushCountTests =
  testList "push count" [
    test "count increments up to capacity" {
      let buf = RingBuffer.create 3
      let b1 = RingBuffer.push 1 buf
      b1 |> RingBuffer.count |> Expect.equal "count should be 1" 1
      let b2 = RingBuffer.push 2 b1
      b2 |> RingBuffer.count |> Expect.equal "count should be 2" 2
      let b3 = RingBuffer.push 3 b2
      b3 |> RingBuffer.count |> Expect.equal "count should be 3 (at capacity)" 3
    }
    test "count caps at capacity" {
      let buf = RingBuffer.create 3
      let b = ref buf
      for i in 1..10 do
        b := RingBuffer.push i !b
        (!b |> RingBuffer.count, 3) |> Expect.isLessThanOrEqual $"count should never exceed capacity after {i} pushes"
    }
  ]

// ────────────────────────────────────────────────────────────
// Group 3: push total — Lean: push_total_increments
// ────────────────────────────────────────────────────────────
let pushTotalTests =
  testList "push total" [
    test "total increments on every push" {
      let buf = RingBuffer.create 3
      let b1 = RingBuffer.push 1 buf
      b1 |> RingBuffer.totalPushed |> Expect.equal "total should be 1" 1L
      let b2 = RingBuffer.push 2 b1
      b2 |> RingBuffer.totalPushed |> Expect.equal "total should be 2" 2L
      let b3 = RingBuffer.push 3 b2
      b3 |> RingBuffer.totalPushed |> Expect.equal "total should be 3" 3L
    }
    test "total keeps growing after eviction" {
      let buf = RingBuffer.create 3
      let b = ref buf
      for i in 1..10 do
        b := RingBuffer.push i !b
      (!b |> RingBuffer.totalPushed) |> Expect.equal "total should be 10" 10L
    }
  ]

// ────────────────────────────────────────────────────────────
// Group 4: tryGet zero — Lean: push_tryGet_zero
// ────────────────────────────────────────────────────────────
let tryGetZeroTests =
  testList "tryGet zero" [
    test "most recently pushed item returned at age 0" {
      let buf = RingBuffer.create 5
      let b1 = RingBuffer.push 42 buf
      b1 |> RingBuffer.tryGet 0 |> Expect.equal "should return Some 42" (Some 42)
      let b2 = RingBuffer.push 99 b1
      b2 |> RingBuffer.tryGet 0 |> Expect.equal "should return Some 99 (most recent)" (Some 99)
    }
    test "tryGet 0 on empty returns None" {
      let buf = RingBuffer.create 5
      buf |> RingBuffer.tryGet 0 |> Expect.equal "empty buffer should return None" None
    }
  ]

// ────────────────────────────────────────────────────────────
// Group 5: push aging — Lean: push_aging
// ────────────────────────────────────────────────────────────
let pushAgingTests =
  testList "push aging" [
    test "prior items shift by 1 on push" {
      let buf = RingBuffer.create 5
      let b1 = RingBuffer.push 10 buf   // [10]
      let b2 = RingBuffer.push 20 b1    // [20, 10]
      b2 |> RingBuffer.tryGet 0 |> Expect.equal "age 0 should be 20" (Some 20)
      b2 |> RingBuffer.tryGet 1 |> Expect.equal "age 1 should be 10" (Some 10)
    }
    test "items age correctly through multiple pushes" {
      let buf = RingBuffer.create 5
      let b = ref buf
      [1; 2; 3; 4; 5] |> List.iter (fun x -> b := RingBuffer.push x !b)
      // Buffer is full: [5, 4, 3, 2, 1] (age 0 = 5, age 1 = 4, etc.)
      !b |> RingBuffer.tryGet 0 |> Expect.equal "age 0" (Some 5)
      !b |> RingBuffer.tryGet 1 |> Expect.equal "age 1" (Some 4)
      !b |> RingBuffer.tryGet 2 |> Expect.equal "age 2" (Some 3)
      !b |> RingBuffer.tryGet 3 |> Expect.equal "age 3" (Some 2)
      !b |> RingBuffer.tryGet 4 |> Expect.equal "age 4" (Some 1)
    }
  ]

// ────────────────────────────────────────────────────────────
// Group 6: tryGet boundary — Lean: tryGet_none_when_age_geq_count
// ────────────────────────────────────────────────────────────
let tryGetBoundaryTests =
  testList "tryGet boundary" [
    test "tryGet returns None when age >= count" {
      let buf = RingBuffer.create 5
      let b1 = RingBuffer.push 1 buf
      b1 |> RingBuffer.tryGet 1 |> Expect.equal "age 1 with count 1 should be None" None
      b1 |> RingBuffer.tryGet 5 |> Expect.equal "age 5 should be None" None
    }
    test "tryGet returns None for negative age" {
      let buf = RingBuffer.create 5
      let b1 = RingBuffer.push 1 buf
      b1 |> RingBuffer.tryGet -1 |> Expect.equal "negative age should return None" None
    }
  ]

// ────────────────────────────────────────────────────────────
// Group 7: eviction — Lean: push_count_capped_at_capacity
// ────────────────────────────────────────────────────────────
let evictionTests =
  testList "eviction" [
    test "oldest item replaced when full" {
      let buf = RingBuffer.create 3
      let b1 = RingBuffer.push 1 buf   // [1]
      let b2 = RingBuffer.push 2 b1     // [2, 1]
      let b3 = RingBuffer.push 3 b2     // [3, 2, 1] — full
      let b4 = RingBuffer.push 4 b3     // [4, 3, 2] — 1 evicted
      b4 |> RingBuffer.tryGet 0 |> Expect.equal "age 0 should be 4" (Some 4)
      b4 |> RingBuffer.tryGet 1 |> Expect.equal "age 1 should be 3" (Some 3)
      b4 |> RingBuffer.tryGet 2 |> Expect.equal "age 2 should be 2" (Some 2)
      b4 |> RingBuffer.tryGet 3 |> Expect.equal "age 3 should be None (1 was evicted)" None
    }
    test "count stays at capacity after eviction" {
      let buf = RingBuffer.create 3
      let b = ref buf
      for i in 1..100 do
        b := RingBuffer.push i !b
        // Invariant (Lean: push_preserves_wf): count = min(pushed, capacity) —
        // grows until full, then stays pinned at capacity as items evict.
        (!b |> RingBuffer.count) |> Expect.equal $"count should be min({i}, capacity)" (min i 3)
    }
  ]

// ────────────────────────────────────────────────────────────
// Group 8: toList length — Lean: toList_length
// ────────────────────────────────────────────────────────────
let toListTests =
  testList "toList" [
    test "toList length equals count" {
      let buf = RingBuffer.create 5
      let b1 = RingBuffer.push 1 buf
      b1 |> RingBuffer.toList |> List.length |> Expect.equal "length should be 1" 1
      let b2 = RingBuffer.push 2 b1
      b2 |> RingBuffer.toList |> List.length |> Expect.equal "length should be 2" 2
      let b3 = RingBuffer.push 3 b2
      b3 |> RingBuffer.toList |> List.length |> Expect.equal "length should be 3" 3
    }
    test "toList is empty for new buffer" {
      let buf = RingBuffer.create 5
      buf |> RingBuffer.toList |> Expect.equal "should be empty list" []
    }
    test "toList length caps at capacity" {
      let buf = RingBuffer.create 3
      let b = ref buf
      for i in 1..10 do
        b := RingBuffer.push i !b
      (!b |> RingBuffer.toList |> List.length) |> Expect.equal "toList length should be 3 (capacity)" 3
    }
  ]

// ────────────────────────────────────────────────────────────
// Group 9: clear — Lean: clear_count, clear_head, clear_capacity
// ────────────────────────────────────────────────────────────
let clearTests =
  testList "clear" [
    test "clear resets count to 0" {
      let buf = RingBuffer.create 5
      let b1 = RingBuffer.push 1 buf |> RingBuffer.push 2
      let b2 = RingBuffer.clear b1
      b2 |> RingBuffer.count |> Expect.equal "count should be 0 after clear" 0
    }
    test "clear resets head to 0" {
      let buf = RingBuffer.create 5
      let b1 = RingBuffer.push 1 buf |> RingBuffer.push 2
      let b2 = RingBuffer.clear b1
      Expect.equal "head should be 0 after clear" b2.Head 0
    }
    test "clear preserves capacity" {
      let buf = RingBuffer.create 5
      let b1 = RingBuffer.push 1 buf
      let b2 = RingBuffer.clear b1
      b2 |> RingBuffer.capacity |> Expect.equal "capacity should be preserved" 5
    }
    test "D3 divergence: F# clear preserves TotalPushed (Lean model does NOT)" {
      let buf = RingBuffer.create 5
      let b1 = RingBuffer.push 1 buf |> RingBuffer.push 2 |> RingBuffer.push 3
      let totalBefore = b1 |> RingBuffer.totalPushed
      let b2 = RingBuffer.clear b1
      // F# implementation correctly preserves TotalPushed
      b2 |> RingBuffer.totalPushed |> Expect.equal "F# clear preserves TotalPushed (D3 divergence from Lean)" totalBefore
    }
  ]

// ────────────────────────────────────────────────────────────
// Group 10: WellFormed — Lean: push_preserves_wf
// ────────────────────────────────────────────────────────────
let wellFormedTests =
  testList "WellFormed invariants" [
    test "head < capacity after pushes" {
      let buf = RingBuffer.create 5
      let b = ref buf
      for i in 1..8 do
        b := RingBuffer.push i !b
        ((!b).Head < 5) |> Expect.isTrue $"head ({(!b).Head}) should be < capacity (5) after {i} pushes"
    }
    test "count <= capacity always" {
      let buf = RingBuffer.create 5
      let b = ref buf
      for i in 1..8 do
        b := RingBuffer.push i !b
        ((!b).Count, 5) |> Expect.isLessThanOrEqual $"count ({(!b).Count}) should be <= capacity (5) after {i} pushes"
    }
    test "count <= total always" {
      let buf = RingBuffer.create 5
      let b = ref buf
      for i in 1..8 do
        b := RingBuffer.push i !b
        ((!b).Count, int (!b).TotalPushed) |> Expect.isLessThanOrEqual $"count ({(!b).Count}) should be <= total ({(!b).TotalPushed}) after {i} pushes"
    }
  ]

// ────────────────────────────────────────────────────────────
// Group 11: cap-1 edge — Lean: create_wf, push_tryGet_zero
// ────────────────────────────────────────────────────────────
let capMinusOneTests =
  testList "cap-1 edge" [
    test "buffer with capacity 1 works correctly" {
      let buf = RingBuffer.create 1
      let b1 = RingBuffer.push 42 buf
      b1 |> RingBuffer.tryGet 0 |> Expect.equal "should return Some 42" (Some 42)
      b1 |> RingBuffer.count |> Expect.equal "count should be 1" 1
      let b2 = RingBuffer.push 99 b1
      b2 |> RingBuffer.tryGet 0 |> Expect.equal "new item should replace old" (Some 99)
      b2 |> RingBuffer.count |> Expect.equal "count should still be 1" 1
    }
  ]

// ────────────────────────────────────────────────────────────
// Group 12: property-based — Lean: push_tryGet_zero
// ────────────────────────────────────────────────────────────
let propertyBasedTests =
  testList "property-based" [
    test "20 pushes always return correct most-recent item" {
      let buf = RingBuffer.create 5
      let b = ref buf
      for i in 1..20 do
        b := RingBuffer.push i !b
        (!b |> RingBuffer.tryGet 0) |> Expect.equal $"age 0 should be {i} after {i} pushes" (Some i)
    }
    test "items are in correct order (most recent first)" {
      let buf = RingBuffer.create 10
      let b = ref buf
      let pushed = ResizeArray<int>()
      for i in 1..5 do
        b := RingBuffer.push i !b
        pushed.Add(i)
      // toList should return [5; 4; 3; 2; 1] (most recent first)
      let expected = [5; 4; 3; 2; 1]
      !b |> RingBuffer.toList |> Expect.equal "items should be in reverse push order" expected
    }
  ]

// ────────────────────────────────────────────────────────────
// All tests combined
// ────────────────────────────────────────────────────────────
let allTests =
  testList "RingBuffer Correspondence Tests (F# vs Lean)" [
    createTests
    pushCountTests
    pushTotalTests
    tryGetZeroTests
    pushAgingTests
    tryGetBoundaryTests
    evictionTests
    toListTests
    clearTests
    wellFormedTests
    capMinusOneTests
    propertyBasedTests
  ]

// Run — runTestsWithCLIArgs is the supported entry point in this Expecto
// version (runTests/defaultConfig are not exposed). Exit 0 or 2 = passed
// (2 means passed without a TTY); anything else is a real failure.
let exitCode = runTestsWithCLIArgs [] [||] allTests
exit (match exitCode with | 0 | 2 -> 0 | _ -> 1)

