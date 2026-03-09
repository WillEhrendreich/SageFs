module SageFs.Tests.McpFormatterPropertyTests

open System
open System.Text.Json
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Tests.SharedGenerators

// ── Helpers ──

let private pick gen = (Gen.sample 1 gen).[0]

/// Generator for DateTime values safe for ISO 8601 roundtrip
let private genSafeDateTime =
  gen {
    let! year = Gen.choose (2000, 2030)
    let! month = Gen.choose (1, 12)
    let! day = Gen.choose (1, 28)
    let! hour = Gen.choose (0, 23)
    let! minute = Gen.choose (0, 59)
    let! second = Gen.choose (0, 59)
    return DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc)
  }

/// Generator for safe ASCII event strings (no control chars)
let private genSafeString =
  Gen.elements (['a'..'z'] @ ['A'..'Z'] @ ['0'..'9'] @ [' '; '.'; '-'; '_'])
  |> Gen.listOf
  |> Gen.map (fun cs -> String(cs |> List.toArray))

/// Generator for event triples
let private genEvent =
  gen {
    let! dt = genSafeDateTime
    let! src = genSafeString
    let! txt = genSafeString
    return (dt, src, txt)
  }

/// Generator for event lists (0..20 items)
let private genEventList =
  Gen.choose (0, 20)
  |> Gen.bind (fun n -> Gen.listOfLength n genEvent)

/// Generator for safe F# code fragments (no control chars, uses ;; and common tokens)
let private genCodeFragment =
  Gen.elements (
    ['a'..'z'] @ ['A'..'Z'] @ ['0'..'9']
    @ [' '; '\n'; '('; ')'; '+'; '-'; '='; '.'; ',']
  )
  |> Gen.listOf
  |> Gen.map (fun cs -> String(cs |> List.toArray))

/// Generator for code containing ;; delimiters
let private genCodeWithDelimiters =
  gen {
    let! parts = Gen.listOfLength 3 genCodeFragment
    return parts |> String.concat ";;"
  }

// ── escapeJson tests ──

let private escapeJsonTests =
  testList "escapeJson" [

    testPropertyWithConfig propConfig "roundtrip through JsonDocument.Parse recovers original" <|
      fun (s: string) ->
        match s with
        | null -> true
        | _ ->
          let escaped = McpAdapter.escapeJson s
          let json = sprintf """{"t":"%s"}""" escaped
          let doc = JsonDocument.Parse(json)
          let recovered = doc.RootElement.GetProperty("t").GetString()
          recovered = s

    testPropertyWithConfig propConfig "output has no unescaped control chars" <|
      fun (s: string) ->
        match s with
        | null -> true
        | _ ->
          let escaped = McpAdapter.escapeJson s
          escaped
          |> Seq.indexed
          |> Seq.forall (fun (i, c) ->
            match c < '\u0020' with
            | false -> true
            | true -> false)

    testPropertyWithConfig propConfig "output has no unescaped double quotes" <|
      fun (s: string) ->
        match s with
        | null -> true
        | _ ->
          let escaped = McpAdapter.escapeJson s
          let mutable i = 0
          let mutable ok = true
          while i < escaped.Length do
            match escaped.[i] with
            | '"' -> ok <- false; i <- escaped.Length
            | '\\' -> i <- i + 2
            | _ -> i <- i + 1
          ok

    testPropertyWithConfig propConfig "every backslash is followed by an escape char" <|
      fun (s: string) ->
        match s with
        | null -> true
        | _ ->
          let escaped = McpAdapter.escapeJson s
          let validFollowers = set [ '\\'; '"'; 'n'; 'r'; 't'; 'b'; 'f'; 'u' ]
          let mutable i = 0
          let mutable ok = true
          while i < escaped.Length do
            match escaped.[i] with
            | '\\' ->
              match i + 1 < escaped.Length && validFollowers.Contains(escaped.[i + 1]) with
              | true -> i <- i + 2
              | false -> ok <- false; i <- escaped.Length
            | _ -> i <- i + 1
          ok

    testPropertyWithConfig propConfig "idempotence of meaning: double-escape parses to single-escaped" <|
      fun (s: string) ->
        match s with
        | null -> true
        | _ ->
          let once = McpAdapter.escapeJson s
          let twice = McpAdapter.escapeJson once
          let json = sprintf """{"t":"%s"}""" twice
          let doc = JsonDocument.Parse(json)
          let recovered = doc.RootElement.GetProperty("t").GetString()
          recovered = once
  ]

// ── splitStatements tests ──

let private splitStatementsTests =
  testList "splitStatements" [

    testPropertyWithConfig propConfig "join preserves non-whitespace content" <|
      fun () ->
        let code = pick genCodeWithDelimiters
        let result = McpAdapter.splitStatements code
        let joined = result |> String.concat ""
        let stripWs (s: string) =
          s |> Seq.filter (fun c -> Char.IsWhiteSpace c |> not) |> Seq.toArray |> String
        let originalNonWs = stripWs (code.Replace(";;", ""))
        let joinedNonWs = stripWs (joined.Replace(";;", ""))
        joinedNonWs = originalNonWs

    testPropertyWithConfig propConfig "no statement has unbalanced triple-quote" <|
      fun () ->
        let code = pick genCodeFragment
        let result = McpAdapter.splitStatements code
        result
        |> List.forall (fun stmt ->
          let count =
            let mutable c = 0
            let mutable j = 0
            while j + 2 < stmt.Length do
              match stmt.[j] = '"' && stmt.[j+1] = '"' && stmt.[j+2] = '"' with
              | true -> c <- c + 1; j <- j + 3
              | false -> j <- j + 1
            c
          count % 2 = 0)

    testCase "empty input yields no non-empty statements" <| fun _ ->
      McpAdapter.splitStatements ""
      |> List.filter (fun s -> s.Trim() <> "")
      |> List.length
      |> Expect.equal "should be empty" 0

    testPropertyWithConfig propConfig "code with no ;; yields single statement or empty" <|
      fun () ->
        let code = pick genCodeFragment
        let noDelim = code.Replace(";;", "")
        let result = McpAdapter.splitStatements noDelim
        match noDelim.Trim() with
        | "" -> result.Length <= 1
        | _ -> result.Length = 1
  ]

// ── formatEventsJson tests ──

let private formatEventsJsonTests =
  testList "formatEventsJson" [

    testPropertyWithConfig propConfig "output is valid JSON" <|
      fun () ->
        let events = pick genEventList
        let json = McpAdapter.formatEventsJson events
        let parsed =
          try JsonDocument.Parse(json) |> ignore; true
          with _ -> false
        parsed

    testPropertyWithConfig propConfig "count field matches input length" <|
      fun () ->
        let events = pick genEventList
        let json = McpAdapter.formatEventsJson events
        let doc = JsonDocument.Parse(json)
        let count = doc.RootElement.GetProperty("count").GetInt32()
        count = events.Length

    testPropertyWithConfig propConfig "all escaped source strings appear in output" <|
      fun () ->
        let events = pick genEventList
        let json = McpAdapter.formatEventsJson events
        events
        |> List.forall (fun (_, src, _) ->
          let escaped = McpAdapter.escapeJson src
          json.Contains(escaped))
  ]

// ── Top-level test list ──

[<Tests>]
let mcpFormatterPropertyTests =
  testList "McpAdapter formatters" [
    escapeJsonTests
    splitStatementsTests
    formatEventsJsonTests
  ]
