module SageFs.Tests.MessageJournalTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.MessageJournal

[<Tests>]
let journalBasicTests =
  testList "MessageJournal basics" [

    testCase "empty journal has zero entries" <| fun _ ->
      Journal.create 100
      |> Journal.count
      |> Expect.equal "empty" 0

    testCase "record adds entry" <| fun _ ->
      Journal.create 100
      |> Journal.record JournalLevel.Info "eval" "let x = 1"
      |> Journal.count
      |> Expect.equal "one entry" 1

    testCase "entries are ordered newest-first" <| fun _ ->
      let j =
        Journal.create 100
        |> Journal.record JournalLevel.Info "eval" "first"
        |> Journal.record JournalLevel.Info "eval" "second"
      let entries = Journal.entries j
      entries.[0].Message |> Expect.equal "newest first" "second"
      entries.[1].Message |> Expect.equal "oldest second" "first"

    testCase "capacity limits entries" <| fun _ ->
      let j =
        [1..5]
        |> List.fold (fun j i ->
          Journal.record JournalLevel.Info "eval" (sprintf "msg %d" i) j)
          (Journal.create 3)
      Journal.count j |> Expect.equal "capped at 3" 3
      let entries = Journal.entries j
      entries.[0].Message |> Expect.equal "newest" "msg 5"

    testCase "entries have timestamps" <| fun _ ->
      let before = DateTimeOffset.UtcNow
      let j =
        Journal.create 10
        |> Journal.record JournalLevel.Info "test" "hello"
      let entries = Journal.entries j
      (entries.[0].Timestamp, before) |> Expect.isGreaterThanOrEqual "timestamp >= before"
  ]

[<Tests>]
let journalFilterTests =
  testList "MessageJournal filtering" [

    testCase "filter by level" <| fun _ ->
      let j =
        Journal.create 100
        |> Journal.record JournalLevel.Debug "a" "debug msg"
        |> Journal.record JournalLevel.Info "b" "info msg"
        |> Journal.record JournalLevel.Failure "c" "error msg"
      Journal.filterByLevel JournalLevel.Failure j
      |> Expect.hasLength "only error" 1

    testCase "filter by source" <| fun _ ->
      let j =
        Journal.create 100
        |> Journal.record JournalLevel.Info "eval" "eval msg"
        |> Journal.record JournalLevel.Info "hotreload" "reload msg"
        |> Journal.record JournalLevel.Info "eval" "eval msg 2"
      Journal.filterBySource "eval" j
      |> Expect.hasLength "two eval entries" 2

    testCase "filter by level includes higher severity" <| fun _ ->
      let j =
        Journal.create 100
        |> Journal.record JournalLevel.Debug "a" "d"
        |> Journal.record JournalLevel.Info "b" "i"
        |> Journal.record JournalLevel.Warn "c" "w"
        |> Journal.record JournalLevel.Failure "d" "e"
      Journal.filterByMinLevel JournalLevel.Warn j
      |> Expect.hasLength "warn + error" 2
  ]

[<Tests>]
let journalFormatTests =
  testList "MessageJournal format" [

    testCase "formatEntry includes all fields" <| fun _ ->
      let entry = {
        JournalEntry.Timestamp = DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero)
        Level = JournalLevel.Info
        Source = "eval"
        Message = "let x = 42"
      }
      let formatted = JournalEntry.format entry
      formatted |> Expect.stringContains "has level" "INFO"
      formatted |> Expect.stringContains "has source" "eval"
      formatted |> Expect.stringContains "has message" "let x = 42"

    testCase "level labels are correct" <| fun _ ->
      JournalLevel.label JournalLevel.Debug |> Expect.equal "debug" "DEBUG"
      JournalLevel.label JournalLevel.Info |> Expect.equal "info" "INFO"
      JournalLevel.label JournalLevel.Warn |> Expect.equal "warn" "WARN"
      JournalLevel.label JournalLevel.Failure |> Expect.equal "error" "ERROR"

    testCase "formatAll produces multi-line output" <| fun _ ->
      let j =
        Journal.create 100
        |> Journal.record JournalLevel.Info "eval" "msg1"
        |> Journal.record JournalLevel.Failure "test" "msg2"
      let output = Journal.formatAll j
      output |> Expect.stringContains "has msg1" "msg1"
      output |> Expect.stringContains "has msg2" "msg2"
  ]

[<Tests>]
let journalStatsTests =
  testList "MessageJournal stats" [

    testCase "stats counts by level" <| fun _ ->
      let j =
        Journal.create 100
        |> Journal.record JournalLevel.Info "a" "1"
        |> Journal.record JournalLevel.Info "b" "2"
        |> Journal.record JournalLevel.Failure "c" "3"
      let stats = Journal.stats j
      stats.InfoCount |> Expect.equal "2 info" 2
      stats.ErrorCount |> Expect.equal "1 error" 1
      stats.WarnCount |> Expect.equal "0 warn" 0

    testCase "empty stats" <| fun _ ->
      let stats = Journal.create 10 |> Journal.stats
      stats.Total |> Expect.equal "0 total" 0

    testCase "evicted count tracks overflow" <| fun _ ->
      let j =
        [1..10]
        |> List.fold (fun j i ->
          Journal.record JournalLevel.Info "x" (sprintf "%d" i) j)
          (Journal.create 3)
      let stats = Journal.stats j
      stats.Evicted |> Expect.equal "7 evicted" 7L
  ]

/// The shape of the recordAll checks, named so no number hides at a call site.
module RecordAllShape =
  let MaxCapacity = 40
  let SmallBatch = 2_000
  /// How many times larger the large batch is than the small one.
  let BatchFactor = 4
  let LargeBatch = SmallBatch * BatchFactor
  /// Linear growth is BatchFactor times, quadratic is its square. Anything under
  /// the midpoint is on the linear side.
  let GrowthCeiling = float (BatchFactor + BatchFactor * BatchFactor) / 2.0

/// What a reader of the journal can see of an entry. The clock stamp is left out:
/// each entry takes its own `UtcNow`, so two builds never agree on it.
let visible (j: Journal) =
  Journal.entries j |> List.map (fun e -> e.Level, e.Source, e.Message)

[<Tests>]
let journalRecordAllTests =
  testList "MessageJournal recordAll" [

    // The oracle is the definition the get_message_journal tool used before:
    // fold `record` over the messages, oldest first.
    testProperty "recordAll shows the same journal as folding record over the messages"
      (fun (cap: PositiveInt) (messages: int list) ->
        let c = min cap.Get RecordAllShape.MaxCapacity
        let texts = messages |> List.map (sprintf "msg %d")
        let folded =
          texts
          |> List.fold (fun j m -> Journal.record JournalLevel.Warn "eval" m j) (Journal.create c)
        let batched = Journal.create c |> Journal.recordAll JournalLevel.Warn "eval" texts
        visible batched = visible folded
        && Journal.stats batched = Journal.stats folded
        && Journal.count batched = Journal.count folded
      )

    testCase "recordAll keeps the newest entries first and drops the oldest on overflow" <| fun _ ->
      let j = Journal.create 3 |> Journal.recordAll JournalLevel.Info "eval" [ "1"; "2"; "3"; "4"; "5" ]
      Journal.entries j |> List.map (fun e -> e.Message) |> Expect.equal "newest three, newest first" [ "5"; "4"; "3" ]
      (Journal.stats j).Evicted |> Expect.equal "two evicted" 2L

    // Structural, not wall-clock: bytes allocated on this thread are exact. A
    // copy of the whole ring per entry makes them grow with the SQUARE of the
    // history; one copy makes them grow in a straight line.
    testCase "recordAll allocates in proportion to the history, not its square" <| fun _ ->
      let allocated (n: int) =
        let messages = List.init n (sprintf "let x%d = 1")
        Journal.create n |> Journal.recordAll JournalLevel.Info "eval" messages |> ignore
        let before = System.GC.GetAllocatedBytesForCurrentThread()
        Journal.create n |> Journal.recordAll JournalLevel.Info "eval" messages |> ignore
        System.GC.GetAllocatedBytesForCurrentThread() - before
      let small = allocated RecordAllShape.SmallBatch
      let large = allocated RecordAllShape.LargeBatch
      let growth = float large / float small
      (growth, RecordAllShape.GrowthCeiling)
      |> Expect.isLessThan
           (sprintf "bytes grew %.1fx for a %dx history; one copy grows about %dx, a copy per entry about %dx"
              growth RecordAllShape.BatchFactor RecordAllShape.BatchFactor (RecordAllShape.BatchFactor * RecordAllShape.BatchFactor))
  ]
