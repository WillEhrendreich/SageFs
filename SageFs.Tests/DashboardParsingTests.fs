module SageFs.Tests.DashboardParsingTests

open Expecto
open System.Text.RegularExpressions

/// Dashboard output/diagnostics parsers — mirrors Dashboard.fs logic.
/// Tests validate the regex-based parsing produces correct structured data.
module DashboardParsing =
  let parseOutputLines (content: string) =
    let outputRegex = Regex(@"^\[(\w+)\]\s*(.*)", RegexOptions.Singleline)
    content.Split('\n')
    |> Array.filter (fun (l: string) -> l.Length > 0)
    |> Array.map (fun (l: string) ->
      let m = outputRegex.Match(l)
      if m.Success then
        let kind =
          match m.Groups.[1].Value.ToLowerInvariant() with
          | "result" -> "Result"
          | "error" -> "Error"
          | "info" -> "Info"
          | _ -> "System"
        kind, m.Groups.[2].Value
      else
        "Result", l)
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
  testCase "output: parses result line" (fun () ->
    let result = DashboardParsing.parseOutputLines "[result] val x: int = 42"
    Expect.equal result [("Result", "val x: int = 42")] "extract result kind")

  testCase "output: parses error line" (fun () ->
    let result = DashboardParsing.parseOutputLines "[error] Something went wrong"
    Expect.equal result [("Error", "Something went wrong")] "extract error kind")

  testCase "output: parses info line" (fun () ->
    let result = DashboardParsing.parseOutputLines "[info] Loading..."
    Expect.equal result [("Info", "Loading...")] "extract info kind")

  testCase "output: parses system line" (fun () ->
    let result = DashboardParsing.parseOutputLines "[system] let x = 1"
    Expect.equal result [("System", "let x = 1")] "extract system kind")

  testCase "output: non-prefixed line defaults to Result" (fun () ->
    let result = DashboardParsing.parseOutputLines "plain text"
    Expect.equal result [("Result", "plain text")] "fallback to Result")

  testCase "output: skips empty lines" (fun () ->
    let lines = DashboardParsing.parseOutputLines "[result] a\n\n[error] b"
    Expect.equal lines.Length 2 "should skip empty lines")

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
