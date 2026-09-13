module SageFs.Tests.ThemesGutterAndDiagnosticsTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Features.Diagnostics
open SageFs.Features
open SageFs.Features.LiveTesting

// --- Helpers ---

let mkDiag msg sev : Diagnostic = {
  Message = msg; Subcategory = "test"
  Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 5 }
  Severity = sev
  ErrorNumber = 0
}

// --- ThemePresets ---

let themePresetsTests = testList "ThemePresets" [
  testCase "tryFind returns Some for known theme" <| fun _ ->
    ThemePresets.tryFind "kanagawa"
    |> Expect.isSome "should find kanagawa"

  testCase "tryFind is case-insensitive" <| fun _ ->
    ThemePresets.tryFind "KANAGAWA"
    |> Expect.isSome "should find case-insensitive"

  testCase "tryFind returns None for unknown theme" <| fun _ ->
    ThemePresets.tryFind "nonexistent"
    |> Expect.isNone "should not find unknown"

  testCase "cycleNext returns next theme" <| fun _ ->
    let first = ThemePresets.all |> List.head |> snd
    let (name, _) = ThemePresets.cycleNext first
    let secondName = ThemePresets.all.[1] |> fst
    name |> Expect.equal "should be second theme" secondName

  testCase "cycleNext wraps around from last" <| fun _ ->
    let last = ThemePresets.all |> List.last |> snd
    let (name, _) = ThemePresets.cycleNext last
    let firstName = ThemePresets.all |> List.head |> fst
    name |> Expect.equal "should wrap to first" firstName

  testCase "cycleNext unknown config returns first" <| fun _ ->
    let fake = { ThemePresets.kanagawa with FgDefault = "#000000" }
    let (name, _) = ThemePresets.cycleNext fake
    let firstName = ThemePresets.all |> List.head |> fst
    name |> Expect.equal "unknown goes to first" firstName

  testCase "all presets have distinct names" <| fun _ ->
    let names = ThemePresets.all |> List.map fst
    let distinct = names |> List.distinct
    distinct.Length |> Expect.equal "all names distinct" names.Length

  testCase "all has at least 5 themes" <| fun _ ->
    (ThemePresets.all.Length, 5) |> Expect.isGreaterThanOrEqual "at least 5 themes"
]

// --- DiagnosticsStore ---

let diagnosticsStoreTests = testList "DiagnosticsStore" [
  testCase "empty store has no diagnostics" <| fun _ ->
    DiagnosticsStore.all DiagnosticsStore.empty
    |> Expect.isEmpty "should be empty"

  testCase "codeHash returns 8-char hex" <| fun _ ->
    let h = DiagnosticsStore.codeHash "hello"
    h.Length |> Expect.equal "should be 8 chars" 8

  testCase "codeHash is deterministic" <| fun _ ->
    let h1 = DiagnosticsStore.codeHash "test code"
    let h2 = DiagnosticsStore.codeHash "test code"
    h1 |> Expect.equal "same input same hash" h2

  testCase "codeHash differs for different inputs" <| fun _ ->
    let h1 = DiagnosticsStore.codeHash "code A"
    let h2 = DiagnosticsStore.codeHash "code B"
    h1 |> Expect.notEqual "different inputs differ" h2

  testCase "add stores diagnostics" <| fun _ ->
    let d = mkDiag "err1" DiagnosticSeverity.Error
    let store = DiagnosticsStore.add "code1" [| d |] DiagnosticsStore.empty
    DiagnosticsStore.forCode "code1" store
    |> Expect.hasLength "should have 1" 1

  testCase "add with empty array removes entry" <| fun _ ->
    let d = mkDiag "err1" DiagnosticSeverity.Error
    let store =
      DiagnosticsStore.empty
      |> DiagnosticsStore.add "code1" [| d |]
      |> DiagnosticsStore.add "code1" [||]
    DiagnosticsStore.forCode "code1" store
    |> Expect.isEmpty "should be removed"

  testCase "forCode returns empty for unknown code" <| fun _ ->
    DiagnosticsStore.forCode "nonexistent" DiagnosticsStore.empty
    |> Expect.isEmpty "should be empty for unknown"

  testCase "allFlat collects all diagnostics" <| fun _ ->
    let d1 = mkDiag "err1" DiagnosticSeverity.Error
    let d2 = mkDiag "warn1" DiagnosticSeverity.Warning
    let store =
      DiagnosticsStore.empty
      |> DiagnosticsStore.add "c1" [| d1 |]
      |> DiagnosticsStore.add "c2" [| d2 |]
    DiagnosticsStore.allFlat store
    |> Expect.hasLength "should have 2 total" 2

  testCase "clear returns empty store" <| fun _ ->
    let d = mkDiag "err1" DiagnosticSeverity.Error
    let store = DiagnosticsStore.add "code1" [| d |] DiagnosticsStore.empty
    DiagnosticsStore.clear store
    |> DiagnosticsStore.all
    |> Expect.isEmpty "should be empty after clear"

  testCase "DiagnosticSeverity.label maps all cases" <| fun _ ->
    DiagnosticSeverity.label DiagnosticSeverity.Error |> Expect.equal "error" "error"
    DiagnosticSeverity.label DiagnosticSeverity.Warning |> Expect.equal "warning" "warning"
    DiagnosticSeverity.label DiagnosticSeverity.Info |> Expect.equal "info" "info"
    DiagnosticSeverity.label DiagnosticSeverity.Hidden |> Expect.equal "hidden" "hidden"
]

// --- CellGrid ---

let cellGridTests = testList "CellGrid" [
  testCase "create makes grid of given dimensions" <| fun _ ->
    let g = CellGrid.create 5 10
    CellGrid.rows g |> Expect.equal "5 rows" 5
    CellGrid.cols g |> Expect.equal "10 cols" 10

  testCase "inBounds true for valid coordinates" <| fun _ ->
    let g = CellGrid.create 3 4
    CellGrid.inBounds g 0 0 |> Expect.isTrue "0,0 in bounds"
    CellGrid.inBounds g 2 3 |> Expect.isTrue "2,3 in bounds"

  testCase "inBounds false for out-of-range" <| fun _ ->
    let g = CellGrid.create 3 4
    CellGrid.inBounds g -1 0 |> Expect.isFalse "negative row"
    CellGrid.inBounds g 0 -1 |> Expect.isFalse "negative col"
    CellGrid.inBounds g 3 0 |> Expect.isFalse "row = rows"
    CellGrid.inBounds g 0 4 |> Expect.isFalse "col = cols"

  testCase "get returns empty for out-of-bounds" <| fun _ ->
    let g = CellGrid.create 2 2
    let c = CellGrid.get g 5 5
    c.Char |> Expect.equal "empty char" ' '

  testCase "clone creates independent copy" <| fun _ ->
    let g = CellGrid.create 2 2
    let g2 = CellGrid.clone g
    CellGrid.rows g2 |> Expect.equal "same rows" 2
    CellGrid.cols g2 |> Expect.equal "same cols" 2

  testCase "toText produces correct number of lines" <| fun _ ->
    let g = CellGrid.create 2 3
    let text = CellGrid.toText g
    let lines = text.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
    lines.Length |> Expect.equal "2 lines" 2

  testCase "toTextTrimmed trims trailing spaces" <| fun _ ->
    let g = CellGrid.create 2 5
    let text = CellGrid.toTextTrimmed g
    // All spaces trimmed means each line is empty
    text.Trim() |> Expect.equal "trimmed to empty lines" ""

  testCase "toTextRange extracts subregion" <| fun _ ->
    let g = CellGrid.create 3 10
    CellGrid.writeString g 1 2 0xFFFFFFu 0u CellAttrs.None "hello"
    let text = CellGrid.toTextRange g 1 2 1 6
    text.Trim() |> Expect.equal "extracted text" "hello"

  testProperty "create dimensions match" <| fun (r: NonNegativeInt) (c: NonNegativeInt) ->
    let rows = min r.Get 100
    let cols = min c.Get 100
    let g = CellGrid.create rows cols
    CellGrid.rows g = rows && CellGrid.cols g = cols
]

// --- GutterRender ---

let gutterTests = testList "GutterRender" [
  testCase "gutterWidth is 0 for empty annotations" <| fun _ ->
    GutterRender.gutterWidth [||]
    |> Expect.equal "no gutter" 0

  testCase "gutterWidth is 2 for non-empty annotations" <| fun _ ->
    let ann = [| { Line = 1; Icon = GutterIcon.TestPassed; Tooltip = "" } |]
    GutterRender.gutterWidth ann
    |> Expect.equal "gutter width 2" 2

  testCase "buildLookup maps line to annotation" <| fun _ ->
    let ann = [| { Line = 5; Icon = GutterIcon.TestFailed; Tooltip = "fail" } |]
    let lookup = GutterRender.buildLookup ann
    Map.containsKey 5 lookup |> Expect.isTrue "should contain line 5"

  testCase "buildLookup empty array gives empty map" <| fun _ ->
    GutterRender.buildLookup [||]
    |> Map.isEmpty
    |> Expect.isTrue "should be empty"

  testCase "iconFgColor maps TestPassed to pass color" <| fun _ ->
    let theme = Theme.defaults
    let color = GutterRender.iconFgColor theme GutterIcon.TestPassed
    color |> Expect.notEqual "should not be zero" 0u

  testCase "iconFgColor maps TestFailed to fail color" <| fun _ ->
    let theme = Theme.defaults
    let passColor = GutterRender.iconFgColor theme GutterIcon.TestPassed
    let failColor = GutterRender.iconFgColor theme GutterIcon.TestFailed
    passColor |> Expect.notEqual "pass and fail differ" failColor

  testProperty "buildLookup preserves all lines" <| fun (lines: int list) ->
    let distinct = lines |> List.distinct
    let anns =
      distinct
      |> List.map (fun l -> { Line = l; Icon = GutterIcon.TestPassed; Tooltip = "" })
      |> Array.ofList
    let lookup = GutterRender.buildLookup anns
    Map.count lookup = distinct.Length
]

// --- StatusHints ---

let statusHintsTests = testList "StatusHints" [
  testCase "shortFormat Ctrl+C" <| fun _ ->
    let kc = { Key = ConsoleKey.C; Modifiers = ConsoleModifiers.Control; Char = Some '\003' }
    StatusHints.shortFormat kc
    |> Expect.equal "Ctrl+C" "^C"

  testCase "shortFormat Alt+T" <| fun _ ->
    let kc = { Key = ConsoleKey.T; Modifiers = ConsoleModifiers.Alt; Char = Some 't' }
    StatusHints.shortFormat kc
    |> Expect.equal "Alt+T" "A-T"

  testCase "shortFormat Enter" <| fun _ ->
    let kc = { Key = ConsoleKey.Enter; Modifiers = enum 0; Char = None }
    StatusHints.shortFormat kc
    |> Expect.equal "Enter" "Enter"

  testCase "shortFormat Escape" <| fun _ ->
    let kc = { Key = ConsoleKey.Escape; Modifiers = enum 0; Char = None }
    StatusHints.shortFormat kc
    |> Expect.equal "Escape" "Esc"

  testCase "shortFormat Shift+Tab" <| fun _ ->
    let kc = { Key = ConsoleKey.Tab; Modifiers = ConsoleModifiers.Shift; Char = None }
    StatusHints.shortFormat kc
    |> Expect.equal "Shift+Tab" "S-Tab"
]

// --- WarmUp ---

let warmUpTests = testList "WarmUp" [
  testCase "isBenignOpenError true for RequireQualifiedAccess" <| fun _ ->
    WarmUp.isBenignOpenError "The namespace or module 'Foo' has RequireQualifiedAccess"
    |> Expect.isTrue "should be benign"

  testCase "isBenignOpenError false for other errors" <| fun _ ->
    WarmUp.isBenignOpenError "Type not found"
    |> Expect.isFalse "should not be benign"

  testCase "isBenignOpenError false for empty string" <| fun _ ->
    WarmUp.isBenignOpenError ""
    |> Expect.isFalse "empty is not benign"
]

// --- All ---

[<Tests>]
let allTests = testList "Misc coverage" [
  themePresetsTests
  diagnosticsStoreTests
  cellGridTests
  gutterTests
  statusHintsTests
  warmUpTests
]
