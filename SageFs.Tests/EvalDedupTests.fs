module SageFs.Tests.EvalDedupTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.EvalDedup

[<Tests>]
let evalDedupTests = testList "EvalDedup" [
  testCase "cache miss on first eval" (fun () ->
    let cache = DedupCache.create 2000
    let now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    DedupCache.tryGet cache "s1" "let x = 1;;" now
    |> Expect.isNone "first eval should be a miss")

  testCase "cache hit within window" (fun () ->
    let cache = DedupCache.create 2000
    let t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    DedupCache.record cache "s1" "let x = 1;;" "val x: int = 1" t0
    let t1 = t0.AddMilliseconds 500.0
    DedupCache.tryGet cache "s1" "let x = 1;;" t1
    |> Expect.isSome "should hit within 500ms")

  testCase "cache miss after window expires" (fun () ->
    let cache = DedupCache.create 2000
    let t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    DedupCache.record cache "s1" "let x = 1;;" "val x: int = 1" t0
    let t1 = t0.AddMilliseconds 2500.0
    DedupCache.tryGet cache "s1" "let x = 1;;" t1
    |> Expect.isNone "should miss after 2.5s with 2s window")

  testCase "different code is cache miss" (fun () ->
    let cache = DedupCache.create 2000
    let t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    DedupCache.record cache "s1" "let x = 1;;" "val x: int = 1" t0
    let t1 = t0.AddMilliseconds 500.0
    DedupCache.tryGet cache "s1" "let x = 2;;" t1
    |> Expect.isNone "different code should miss")

  testCase "different session is cache miss" (fun () ->
    let cache = DedupCache.create 2000
    let t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    DedupCache.record cache "s1" "let x = 1;;" "val x: int = 1" t0
    let t1 = t0.AddMilliseconds 500.0
    DedupCache.tryGet cache "s2" "let x = 1;;" t1
    |> Expect.isNone "different session should miss")

  testCase "hit returns cached result" (fun () ->
    let cache = DedupCache.create 2000
    let t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    DedupCache.record cache "s1" "let x = 1;;" "val x: int = 1" t0
    let t1 = t0.AddMilliseconds 100.0
    DedupCache.tryGet cache "s1" "let x = 1;;" t1
    |> Option.get
    |> Expect.equal "should return cached result" "val x: int = 1")

  testCase "evictStale removes old entries" (fun () ->
    let cache = DedupCache.create 1000
    let t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    DedupCache.record cache "s1" "old code;;" "old result" t0
    DedupCache.record cache "s1" "new code;;" "new result" (t0.AddMilliseconds 1500.0)
    DedupCache.evictStale cache (t0.AddMilliseconds 3000.0)
    cache.Entries.Count
    |> Expect.equal "old entry evicted, new entry kept" 1)

  testCase "record overwrites previous entry for same code" (fun () ->
    let cache = DedupCache.create 2000
    let t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    DedupCache.record cache "s1" "let x = 1;;" "first result" t0
    DedupCache.record cache "s1" "let x = 1;;" "second result" (t0.AddMilliseconds 100.0)
    let t1 = t0.AddMilliseconds 200.0
    DedupCache.tryGet cache "s1" "let x = 1;;" t1
    |> Option.get
    |> Expect.equal "should return latest result" "second result")

  testCase "boundary: exactly at window edge is miss" (fun () ->
    let cache = DedupCache.create 2000
    let t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    DedupCache.record cache "s1" "let x = 1;;" "val x: int = 1" t0
    let t1 = t0.AddMilliseconds 2000.0
    DedupCache.tryGet cache "s1" "let x = 1;;" t1
    |> Expect.isNone "exactly at window boundary should miss")

  testCase "window of 0ms means no dedup" (fun () ->
    let cache = DedupCache.create 0
    let t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    DedupCache.record cache "s1" "let x = 1;;" "val x: int = 1" t0
    DedupCache.tryGet cache "s1" "let x = 1;;" t0
    |> Expect.isNone "zero window means never dedup")

  testCase "clearSession removes all entries for that session" (fun () ->
    let cache = DedupCache.create 5000
    let t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    DedupCache.record cache "s1" "code1;;" "r1" t0
    DedupCache.record cache "s1" "code2;;" "r2" t0
    DedupCache.record cache "s2" "code1;;" "r3" t0
    DedupCache.clearSession cache "s1"
    DedupCache.tryGet cache "s1" "code1;;" t0 |> Expect.isNone "s1 code1 should be cleared"
    DedupCache.tryGet cache "s1" "code2;;" t0 |> Expect.isNone "s1 code2 should be cleared"
    DedupCache.tryGet cache "s2" "code1;;" t0 |> Expect.isSome "s2 code1 should remain")
]

[<Tests>]
let evalDedupPropertyTests = testList "EvalDedup properties" [
  testProperty "record then immediate tryGet always hits" (fun (code: NonEmptyString) (result: NonEmptyString) ->
    let cache = DedupCache.create 2000
    let t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let c = code.Get
    let r = result.Get
    DedupCache.record cache "s1" c r t0
    let got = DedupCache.tryGet cache "s1" c t0
    match got with
    | Some v -> v = r
    | None -> false)

  testProperty "tryGet after window always misses" (fun (code: NonEmptyString) (windowMs: PositiveInt) ->
    let w = windowMs.Get
    let cache = DedupCache.create w
    let t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let c = code.Get
    DedupCache.record cache "s1" c "result" t0
    let t1 = t0.AddMilliseconds (float w + 1.0)
    DedupCache.tryGet cache "s1" c t1 |> Option.isNone)

  testProperty "different sessions never interfere" (fun (code: NonEmptyString) (s1: NonEmptyString) (s2: NonEmptyString) ->
    let sid1 = "a-" + s1.Get
    let sid2 = "b-" + s2.Get
    match sid1 = sid2 with
    | true -> true
    | false ->
      let cache = DedupCache.create 2000
      let t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
      let c = code.Get
      DedupCache.record cache sid1 c "result1" t0
      DedupCache.tryGet cache sid2 c t0 |> Option.isNone)

  testProperty "evictStale never removes entries within window" (fun (code: NonEmptyString) ->
    let cache = DedupCache.create 5000
    let t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let c = code.Get
    DedupCache.record cache "s1" c "result" t0
    DedupCache.evictStale cache (t0.AddMilliseconds 4000.0)
    DedupCache.tryGet cache "s1" c (t0.AddMilliseconds 4000.0) |> Option.isSome)
]
