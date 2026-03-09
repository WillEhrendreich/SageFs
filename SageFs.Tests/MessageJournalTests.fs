module SageFs.Tests.MessageJournalTests

open System
open Expecto
open Expecto.Flip
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
        |> Journal.record JournalLevel.Error "c" "error msg"
      Journal.filterByLevel JournalLevel.Error j
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
        |> Journal.record JournalLevel.Error "d" "e"
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
      JournalLevel.label JournalLevel.Error |> Expect.equal "error" "ERROR"

    testCase "formatAll produces multi-line output" <| fun _ ->
      let j =
        Journal.create 100
        |> Journal.record JournalLevel.Info "eval" "msg1"
        |> Journal.record JournalLevel.Error "test" "msg2"
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
        |> Journal.record JournalLevel.Error "c" "3"
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
