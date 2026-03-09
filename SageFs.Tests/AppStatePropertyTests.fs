module SageFs.Tests.AppStatePropertyTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.AppState
open SageFs.Tests.SharedGenerators

// ── Generators ──

/// Printable ASCII characters (no ANSI escape sequences)
let private genPrintableAsciiChar =
  Gen.choose (32, 126) |> Gen.map char

/// String composed only of printable ASCII (guaranteed ANSI-free)
let private genPlainString =
  Gen.choose (0, 50)
  |> Gen.bind (fun len ->
    Gen.listOfLength len genPrintableAsciiChar
    |> Gen.map (fun cs -> System.String(cs |> List.toArray)))

/// String that may contain embedded ANSI escape sequences
let private genAnsiFragment =
  Gen.oneof [
    Gen.constant "\x1b[31m"
    Gen.constant "\x1b[0m"
    Gen.constant "\x1b[1;32;40m"
    Gen.constant "\x1b[?25h"
    Gen.constant "\x1b[?25l"
    Gen.constant "\x1b[10D"
    Gen.constant "\x1b]0;title\x07"
  ]

let private genStringWithAnsi =
  Gen.choose (1, 6)
  |> Gen.bind (fun parts ->
    Gen.listOfLength parts (
      Gen.frequency [
        3, genPlainString
        2, genAnsiFragment
      ])
    |> Gen.map (String.concat ""))

// ── stripAnsi properties ──

let stripAnsiTests =
  testList "stripAnsi properties" [

    testPropertyWithConfig propConfig "never introduces ANSI escape sequences" <|
      fun (s: string) ->
        match s with
        | null -> true
        | _ -> (stripAnsi s).Contains("\x1b[") |> not

    testPropertyWithConfig propConfig "idempotent — double strip equals single strip" <|
      fun (s: string) ->
        match s with
        | null -> true
        | _ -> stripAnsi (stripAnsi s) = stripAnsi s

    testPropertyWithConfig propConfig "preserves plain text (no ANSI codes)" <|
      fun () ->
        let s = (Gen.sample 1 genPlainString).[0]
        let result = stripAnsi s
        // plain string may gain newlines from cursor-reset regex on empty match,
        // but for truly plain text (no \x1b) it should be identity
        match s.Contains('\x1b') with
        | true -> true
        | false -> result = s

    testPropertyWithConfig propConfig "output length never exceeds input length" <|
      fun (s: string) ->
        match s with
        | null -> true
        | _ -> (stripAnsi s).Length <= s.Length
  ]

// ── cleanStdout properties ──

let cleanStdoutTests =
  testList "cleanStdout properties" [

    testPropertyWithConfig propConfig "idempotent — double clean equals single clean" <|
      fun (s: string) ->
        match s with
        | null -> true
        | _ -> cleanStdout (cleanStdout s) = cleanStdout s

    testPropertyWithConfig propConfig "never increases line count" <|
      fun (s: string) ->
        match s with
        | null -> true
        | _ ->
          let inputLines =
            s.Split([| '\n'; '\r' |], System.StringSplitOptions.RemoveEmptyEntries).Length
          let outputLines =
            match cleanStdout s with
            | "" -> 0
            | out -> out.Split([| '\n' |]).Length
          outputLines <= inputLines

    testPropertyWithConfig propConfig "output is ANSI-free" <|
      fun (s: string) ->
        match s with
        | null -> true
        | _ -> (cleanStdout s).Contains("\x1b[") |> not
  ]

// ── reformatExpectoSummary properties ──

let reformatExpectoSummaryTests =
  testList "reformatExpectoSummary properties" [

    testPropertyWithConfig propConfig "preserves non-matching lines unchanged" <|
      fun () ->
        let s = (Gen.sample 1 genPlainString).[0]
        // plain ASCII strings won't match the EXPECTO! regex
        match s.Contains("EXPECTO!") with
        | true -> true
        | false -> reformatExpectoSummary s = s

    testPropertyWithConfig propConfig "non-EXPECTO lines are identity" <|
      fun (s: string) ->
        match s with
        | null -> true
        | _ ->
          match s.Contains("EXPECTO!") with
          | true -> true // skip strings that happen to contain the marker
          | false -> reformatExpectoSummary s = s
  ]

// ── Top-level ──

[<Tests>]
let allAppStatePropertyTests =
  testList "AppState property tests" [
    stripAnsiTests
    cleanStdoutTests
    reformatExpectoSummaryTests
  ]
