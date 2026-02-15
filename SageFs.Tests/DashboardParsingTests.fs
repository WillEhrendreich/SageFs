module SageFs.Tests.DashboardParsingTests

open Expecto
open System.Text.RegularExpressions

/// Dashboard output/diagnostics parsers — mirrors Dashboard.fs logic.
/// Tests validate the regex-based parsing produces correct structured data.
module DashboardParsing =
  let parseOutputLines (content: string) =
    let tsKindRegex = Regex(@"^\[(\d{2}:\d{2}:\d{2})\]\s*\[(\w+)\]\s*(.*)", RegexOptions.Singleline)
    let kindOnlyRegex = Regex(@"^\[(\w+)\]\s*(.*)", RegexOptions.Singleline)
    content.Split('\n')
    |> Array.filter (fun (l: string) -> l.Length > 0)
    |> Array.map (fun (l: string) ->
      let m = tsKindRegex.Match(l)
      if m.Success then
        let kind =
          match m.Groups.[2].Value.ToLowerInvariant() with
          | "result" -> "Result"
          | "error" -> "Error"
          | "info" -> "Info"
          | _ -> "System"
        Some m.Groups.[1].Value, kind, m.Groups.[3].Value
      else
        let m2 = kindOnlyRegex.Match(l)
        if m2.Success then
          let kind =
            match m2.Groups.[1].Value.ToLowerInvariant() with
            | "result" -> "Result"
            | "error" -> "Error"
            | "info" -> "Info"
            | _ -> "System"
          None, kind, m2.Groups.[2].Value
        else
          None, "Result", l)
    |> Array.toList

  let parseDiagLines (content: string) =
    let diagRegex = Regex(@"^\[(\w+)\]\s*\((\d+),(\d+)\)\s*(.*)")
    content.Split('\n')
    |> Array.filter (fun (l: string) -> l.Length > 0)
    |> Array.map (fun (l: string) ->
      let m = diagRegex.Match(l)
      if m.Success then
        let severity = if m.Groups.[1].Value = "error" then "Error" else "Warning"
        let line = int m.Groups.[2].Value
        let col = int m.Groups.[3].Value
        let message = m.Groups.[4].Value
        severity, message, line, col
      else
        let severity = if l.Contains("[error]") then "Error" else "Warning"
        severity, l, 0, 0)
    |> Array.toList

[<Tests>]
let tests = testList "Dashboard parsing" [
  testCase "output: parses timestamped result line" (fun () ->
    let result = DashboardParsing.parseOutputLines "[14:30:05] [result] val x: int = 42"
    Expect.equal result [(Some "14:30:05", "Result", "val x: int = 42")] "extract timestamp, kind, text")

  testCase "output: parses result line without timestamp" (fun () ->
    let result = DashboardParsing.parseOutputLines "[result] val x: int = 42"
    Expect.equal result [(None, "Result", "val x: int = 42")] "fallback without timestamp")

  testCase "output: parses timestamped error line" (fun () ->
    let result = DashboardParsing.parseOutputLines "[09:15:00] [error] Something went wrong"
    Expect.equal result [(Some "09:15:00", "Error", "Something went wrong")] "extract error kind with timestamp")

  testCase "output: parses info line" (fun () ->
    let result = DashboardParsing.parseOutputLines "[12:00:00] [info] Loading..."
    Expect.equal result [(Some "12:00:00", "Info", "Loading...")] "extract info kind")

  testCase "output: parses system line" (fun () ->
    let result = DashboardParsing.parseOutputLines "[08:00:00] [system] let x = 1"
    Expect.equal result [(Some "08:00:00", "System", "let x = 1")] "extract system kind")

  testCase "output: non-prefixed line defaults to Result" (fun () ->
    let result = DashboardParsing.parseOutputLines "plain text"
    Expect.equal result [(None, "Result", "plain text")] "fallback to Result")

  testCase "output: skips empty lines" (fun () ->
    let lines = DashboardParsing.parseOutputLines "[14:30:05] [result] a\n\n[14:30:06] [error] b"
    Expect.equal lines.Length 2 "should skip empty lines")

  testCase "output: multiple timestamped lines" (fun () ->
    let result = DashboardParsing.parseOutputLines "[14:30:05] [result] a\n[14:30:06] [error] b\n[14:30:07] [info] c"
    Expect.equal result.Length 3 "should have 3 lines"
    let (ts1, k1, _) = result.[0]
    Expect.equal (ts1, k1) (Some "14:30:05", "Result") "first line"
    let (ts2, k2, _) = result.[1]
    Expect.equal (ts2, k2) (Some "14:30:06", "Error") "second line"
    let (ts3, k3, _) = result.[2]
    Expect.equal (ts3, k3) (Some "14:30:07", "Info") "third line")

  testCase "diag: extracts line and col from error" (fun () ->
    let result = DashboardParsing.parseDiagLines "[error] (5,12) Type not defined"
    Expect.equal result [("Error", "Type not defined", 5, 12)] "extract severity, msg, line, col")

  testCase "diag: extracts line and col from warning" (fun () ->
    let result = DashboardParsing.parseDiagLines "[warning] (1,0) Value unused"
    Expect.equal result [("Warning", "Value unused", 1, 0)] "parse warning")

  testCase "diag: multiple diagnostics" (fun () ->
    let result = DashboardParsing.parseDiagLines "[error] (5,12) Bad\n[warning] (10,3) Suspicious"
    Expect.equal result.Length 2 "should have 2 diagnostics"
    let (s1, _, l1, c1) = result.[0]
    Expect.equal (s1, l1, c1) ("Error", 5, 12) "first diagnostic"
    let (s2, _, l2, c2) = result.[1]
    Expect.equal (s2, l2, c2) ("Warning", 10, 3) "second diagnostic")

  testCase "diag: fallback for non-standard format" (fun () ->
    let result = DashboardParsing.parseDiagLines "some random diagnostic"
    Expect.equal result [("Warning", "some random diagnostic", 0, 0)] "fallback to Warning 0,0")
]
