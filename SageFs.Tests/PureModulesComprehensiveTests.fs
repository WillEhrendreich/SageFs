module SageFs.Tests.PureModulesComprehensiveTests

open Expecto
open Expecto.Flip
open FsCheck
open FSharp.Compiler.EditorServices
open SageFs.Middleware.HotReloading
open SageFs.Features.AutoCompletion
open SageFs.AppState
open SageFs.FileWatcher
open SageFs.McpAdapter
open SageFs.FsiRewrite
open SageFs.FrameDiff
open SageFs.Features.Replay
open SageFs.Features.Events
open SageFs.Features.Diagnostics
open SageFs
open SageFs.SessionManager
open SageFs.StandbyPool
open SageFs.Features.LiveTesting
open SageFs.Watchdog
open SageFs.RestartPolicy
open System
open System.Text.Json

// ═══════════════════════════════════════════════════════════
// HotReloading — isTopLevelFunctionBinding
// ═══════════════════════════════════════════════════════════

let isTopLevelFunctionBindingTests = testList "isTopLevelFunctionBinding" [
  test "simple function with parameter" {
    isTopLevelFunctionBinding "let f x = x + 1"
    |> Expect.isTrue "let with param is function"
  }
  test "function with multiple params" {
    isTopLevelFunctionBinding "let add x y = x + y"
    |> Expect.isTrue "multiple params is function"
  }
  test "function with unit param" {
    isTopLevelFunctionBinding "let f () = 42"
    |> Expect.isTrue "unit param is function"
  }
  test "function with typed param" {
    isTopLevelFunctionBinding "let f (x: int) = x"
    |> Expect.isTrue "typed param is function"
  }
  test "value binding is not function" {
    isTopLevelFunctionBinding "let x = 42"
    |> Expect.isFalse "simple value is not function"
  }
  test "typed value binding is not function" {
    isTopLevelFunctionBinding "let x : int = 42"
    |> Expect.isFalse "typed value is not function"
  }
  test "indented binding is not top-level" {
    isTopLevelFunctionBinding "  let f x = x"
    |> Expect.isFalse "indented is not top-level"
  }
  test "let! is not a binding" {
    isTopLevelFunctionBinding "let! x = async { return 1 }"
    |> Expect.isFalse "let! is computation expression"
  }
  test "private function" {
    isTopLevelFunctionBinding "let private f x = x"
    |> Expect.isTrue "private function is still function"
  }
  test "inline function" {
    isTopLevelFunctionBinding "let inline f x = x"
    |> Expect.isTrue "inline function is still function"
  }
  test "rec function" {
    isTopLevelFunctionBinding "let rec f x = f x"
    |> Expect.isTrue "recursive function is still function"
  }
  test "no equals sign" {
    isTopLevelFunctionBinding "let f x"
    |> Expect.isFalse "no equals means not a complete binding"
  }
]

// ═══════════════════════════════════════════════════════════
// HotReloading — isStaticMemberFunction
// ═══════════════════════════════════════════════════════════

let isStaticMemberFunctionTests = testList "isStaticMemberFunction" [
  test "static member with parens" {
    isStaticMemberFunction "  static member Create(x) = x"
    |> Expect.isTrue "static member with parens is function"
  }
  test "static member with named param" {
    isStaticMemberFunction "  static member Add x y = x + y"
    |> Expect.isTrue "static member with params is function"
  }
  test "static member property" {
    isStaticMemberFunction "  static member Value = 42"
    |> Expect.isFalse "static member without params is property"
  }
  test "static member with type annotation" {
    isStaticMemberFunction "  static member Default: int = 0"
    |> Expect.isFalse "type-annotated value is not function"
  }
  test "non-static member is not matched" {
    isStaticMemberFunction "  member this.Foo(x) = x"
    |> Expect.isFalse "instance member is not static"
  }
  test "regular let binding" {
    isStaticMemberFunction "let f x = x"
    |> Expect.isFalse "let binding is not static member"
  }
  test "no equals sign" {
    isStaticMemberFunction "  static member Foo"
    |> Expect.isFalse "no equals, not a complete definition"
  }
]

// ═══════════════════════════════════════════════════════════
// AutoCompletion — CompletionKind.ofGlyph
// ═══════════════════════════════════════════════════════════

let completionKindOfGlyphTests = testList "CompletionKind.ofGlyph" [
  test "Class maps to Class" {
    CompletionKind.ofGlyph FSharpGlyph.Class
    |> Expect.equal "Class" CompletionKind.Class
  }
  test "Method maps to Method" {
    CompletionKind.ofGlyph FSharpGlyph.Method
    |> Expect.equal "Method" CompletionKind.Method
  }
  test "OverridenMethod maps to OverriddenMethod" {
    CompletionKind.ofGlyph FSharpGlyph.OverridenMethod
    |> Expect.equal "OverridenMethod" CompletionKind.OverriddenMethod
  }
  test "NameSpace maps to Namespace" {
    CompletionKind.ofGlyph FSharpGlyph.NameSpace
    |> Expect.equal "Namespace" CompletionKind.Namespace
  }
  test "Error maps to Type" {
    CompletionKind.ofGlyph FSharpGlyph.Error
    |> Expect.equal "Error fallback → Type" CompletionKind.Type
  }
  test "all glyphs handled without exception" {
    let glyphs = [
      FSharpGlyph.Class; FSharpGlyph.Constant; FSharpGlyph.Delegate
      FSharpGlyph.Enum; FSharpGlyph.EnumMember; FSharpGlyph.Event
      FSharpGlyph.Exception; FSharpGlyph.Field; FSharpGlyph.Interface
      FSharpGlyph.Method; FSharpGlyph.OverridenMethod; FSharpGlyph.Module
      FSharpGlyph.NameSpace; FSharpGlyph.Property; FSharpGlyph.Struct
      FSharpGlyph.Typedef; FSharpGlyph.Type; FSharpGlyph.Union
      FSharpGlyph.Variable; FSharpGlyph.ExtensionMethod; FSharpGlyph.Error
      FSharpGlyph.TypeParameter
    ]
    for g in glyphs do
      CompletionKind.ofGlyph g |> ignore
  }
]

// ═══════════════════════════════════════════════════════════
// AppState — stripAnsi
// ═══════════════════════════════════════════════════════════

let stripAnsiTests = testList "stripAnsi" [
  test "removes color escape sequences" {
    stripAnsi "\u001b[31mred text\u001b[0m"
    |> Expect.equal "color stripped" "red text"
  }
  test "removes bold/underline" {
    stripAnsi "\u001b[1mbold\u001b[22m"
    |> Expect.equal "bold stripped" "bold"
  }
  test "cursor reset becomes newline" {
    stripAnsi "line1\u001b[0Gline2"
    |> Expect.stringContains "cursor reset becomes newline" "line1"
  }
  test "plain text unchanged" {
    stripAnsi "hello world"
    |> Expect.equal "no change" "hello world"
  }
  test "empty string unchanged" {
    stripAnsi "" |> Expect.equal "empty" ""
  }
  test "multiple sequences stripped" {
    stripAnsi "\u001b[32mgreen\u001b[0m and \u001b[34mblue\u001b[0m"
    |> Expect.equal "both stripped" "green and blue"
  }
]

// ═══════════════════════════════════════════════════════════
// AppState — reformatExpectoSummary
// ═══════════════════════════════════════════════════════════

let reformatExpectoSummaryTests = testList "reformatExpectoSummary" [
  test "reformats standard expecto summary" {
    let input = "EXPECTO! 10 tests run in 00:00:01.234 for MyTests \u2013 8 passed, 1 ignored, 1 failed, 0 errored. OK!"
    let result = reformatExpectoSummary input
    result |> Expect.stringContains "has test suite name" "MyTests"
    result |> Expect.stringContains "has count" "10"
    result |> Expect.stringContains "has passed" "8"
    result |> Expect.stringContains "has failed" "1"
  }
  test "non-expecto line passes through" {
    let input = "just a regular line"
    reformatExpectoSummary input
    |> Expect.equal "unchanged" "just a regular line"
  }
]

// ═══════════════════════════════════════════════════════════
// Behavioral contract properties (FsCheck)
//
// These test WHY each function exists — the invariants that
// callers depend on. If any of these break, something upstream
// (hot-reload, dashboard, autocomplete, file watcher) will
// silently misbehave.
// ═══════════════════════════════════════════════════════════

let cfg = { FsCheckConfig.defaultConfig with maxTest = 200 }

// --- stripAnsi: the output cycle depends on these ---

/// Callers may process text multiple times (cleanStdout then display).
/// If stripping mutates already-clean text, re-processing corrupts output.
let stripAnsiIdempotent =
  testPropertyWithConfig cfg
    "stripAnsi: stripping twice equals stripping once (idempotent)"
    <| fun (s: NonEmptyString) ->
      let once = stripAnsi s.Get
      stripAnsi once = once

/// Dashboard and MCP tool output must be free of ANSI CSI sequences.
/// A surviving CSI renders as garbage in HTML and JSON contexts.
let stripAnsiNoCompleteCsiSurvives =
  testPropertyWithConfig cfg
    "stripAnsi: no complete ANSI CSI sequence survives"
    <| fun (s: NonEmptyString) ->
      let cleaned = stripAnsi s.Get
      not (Text.RegularExpressions.Regex.IsMatch(cleaned, @"\x1b\[[0-9;]*[a-zA-Z]"))

/// OSC sequences (title changes, hyperlinks) must also be stripped.
let stripAnsiNoOscSurvives =
  testPropertyWithConfig cfg
    "stripAnsi: no OSC sequence (ESC]...BEL) survives"
    <| fun (s: NonEmptyString) ->
      let cleaned = stripAnsi s.Get
      not (Text.RegularExpressions.Regex.IsMatch(cleaned, @"\x1b\].*?\x07"))

/// Most text passing through cleanStdout is already plain ASCII.
/// If the regex accidentally mutates non-ANSI text, real output is corrupted.
let stripAnsiIdentityOnPlainText =
  testPropertyWithConfig cfg
    "stripAnsi: plain ASCII text passes through unchanged"
    <| fun (s: NonEmptyString) ->
      let plain = Text.RegularExpressions.Regex.Replace(s.Get, @"[^\x20-\x7E]", "")
      plain.Length = 0 || stripAnsi plain = plain

// --- cleanStdout: dashboard SSE handler depends on these ---

/// The dashboard applies cleanStdout on every state change. During
/// state replay or snapshot rebuild, output may be re-processed.
/// Idempotency guarantees stability across replays.
let cleanStdoutIdempotent =
  testPropertyWithConfig cfg
    "cleanStdout: cleaning already-clean text is stable (idempotent)"
    <| fun (s: NonEmptyString) ->
      let once = cleanStdout s.Get
      cleanStdout once = once

/// Same rationale as stripAnsi — dashboard HTML must not contain ANSI.
let cleanStdoutNoAnsi =
  testPropertyWithConfig cfg
    "cleanStdout: output never contains complete ANSI sequences"
    <| fun (s: NonEmptyString) ->
      let cleaned = cleanStdout s.Get
      not (Text.RegularExpressions.Regex.IsMatch(cleaned, @"\x1b\[[0-9;]*[a-zA-Z]"))

// --- isTopLevelFunctionBinding: hot-reload patch safety ---

/// Hot reload only needs NoInlining on TOP-LEVEL functions.
/// Nested bindings (inside match/if/function bodies) are never
/// Harmony patch targets. Injecting attributes on them would
/// change indentation semantics and potentially break compilation.
let topLevelRejectsIndented =
  testPropertyWithConfig cfg
    "isTopLevelFunctionBinding: any leading whitespace means not top-level"
    <| fun (NonNegativeInt indent) (name: NonEmptyString) ->
      let spaces = String(' ', indent + 1)
      let safeName = Text.RegularExpressions.Regex.Replace(name.Get, @"[^a-zA-Z_]", "a")
      let line = sprintf "%slet %s x = x" spaces safeName
      isTopLevelFunctionBinding line = false

/// Users mark functions `private`/`inline`/`rec` for their own reasons.
/// The hot-reload detector must classify function-vs-value regardless
/// of modifiers. If modifiers confused it, private functions would
/// silently escape hot-reload — a nasty latent bug.
let modifiersDontAffectClassification =
  let modifiers = ["private "; "internal "; "public "; "inline "; "rec "]
  testList "isTopLevelFunctionBinding: modifiers preserve function-vs-value" [
    for m in modifiers do
      test (sprintf "'let %sf x = x' is still a function" m) {
        isTopLevelFunctionBinding (sprintf "let %sf x = x" m)
        |> Expect.isTrue (sprintf "%s preserves function" m)
      }
      test (sprintf "'let %sx = 42' is still a value" m) {
        isTopLevelFunctionBinding (sprintf "let %sx = 42" m)
        |> Expect.isFalse (sprintf "%s preserves value" m)
      }
  ]

// --- injectNoInlining: transform correctness ---

/// The transform must ONLY add attributes — never modify comments,
/// blank lines, type definitions, or other non-function code. If it
/// accidentally mutates a non-function line, it introduces compile
/// errors or changes the semantics of the user's code.
let injectPreservesNonFunctionLines = testList "injectNoInlining: non-function lines preserved" [
  test "type definition passes through" {
    injectNoInlining "type X = { A: int }" |> Expect.equal "type def unchanged" "type X = { A: int }"
  }
  test "comments pass through" {
    injectNoInlining "// this is a comment\n(* block comment *)"
    |> Expect.equal "comments unchanged" "// this is a comment\n(* block comment *)"
  }
  test "open declarations pass through" {
    injectNoInlining "open System\nopen System.IO"
    |> Expect.equal "opens unchanged" "open System\nopen System.IO"
  }
  test "empty string passes through" {
    injectNoInlining "" |> Expect.equal "empty unchanged" ""
  }
]

/// Each function needs exactly one [<MethodImpl(NoInlining)>] to
/// prevent JIT inlining. Zero = Harmony patch invisible. This
/// structural property ensures the transform is precise.
let injectAttributeCountMatchesFunctionCount =
  test "injectNoInlining: attribute count equals function count" {
    let code = "let f x = x\nlet x = 42\nlet g (y: int) = y * 2\n  static member Create(z) = z"
    let result = injectNoInlining code
    let lines = result.Split('\n')
    let attrCount =
      lines |> Array.filter (fun l -> l.TrimStart().StartsWith("[<MethodImpl")) |> Array.length
    let funcCount =
      code.Split('\n')
      |> Array.filter (fun l -> isTopLevelFunctionBinding l || isStaticMemberFunction l)
      |> Array.length
    attrCount |> Expect.equal "one attribute per function" funcCount
  }

// --- shouldExcludeFile: glob matching contracts ---

/// The default WatchConfig has no exclusions. If empty patterns
/// accidentally matched everything, hot-reload would silently
/// stop watching all files.
let emptyPatternsExcludeNothing =
  testPropertyWithConfig cfg
    "shouldExcludeFile: empty pattern list never excludes"
    <| fun (s: NonEmptyString) ->
      shouldExcludeFile [] s.Get = false

/// Windows file systems are case-insensitive. Patterns like "Bin/*"
/// must match "bin/Debug/test.dll". Without case-insensitive matching,
/// exclusion patterns silently fail on Windows.
let excludeIsCaseInsensitive = testList "shouldExcludeFile: case insensitive matching" [
  test "uppercase pattern matches lowercase path" {
    shouldExcludeFile ["BIN/*"] "bin/Debug/test.dll" |> Expect.isTrue "BIN/* matches bin/"
  }
  test "lowercase pattern matches uppercase path" {
    shouldExcludeFile ["bin/*"] "BIN/Debug/test.dll" |> Expect.isTrue "bin/* matches BIN/"
  }
]

/// FileSystemWatcher on Windows reports backslashes, but users write
/// glob patterns with forward slashes. Without normalization, the same
/// path would match or not depending on OS.
let excludeNormalizesSlashes = testList "shouldExcludeFile: slash normalization" [
  test "forward-slash pattern matches backslash path" {
    shouldExcludeFile ["bin/*"] @"bin\Debug\test.dll"
    |> Expect.isTrue "forward matches backslash"
  }
  test "backslash pattern matches forward-slash path" {
    shouldExcludeFile [@"bin\*"] "bin/Debug/test.dll"
    |> Expect.isTrue "backslash matches forward"
  }
]

// --- fileChangeAction: hot-reload routing safety ---

/// When a .fs file is deleted, its old definitions remain valid in FSI.
/// Trying to #load a deleted file would crash. The hot-reload system
/// MUST treat deletes as no-ops regardless of extension.
let deletedFilesAlwaysIgnored =
  testPropertyWithConfig cfg
    "fileChangeAction: deleted files are always Ignore regardless of extension"
    <| fun (s: NonEmptyString) ->
      let exts = [".fs"; ".fsx"; ".fsproj"; ".txt"; ".dll"; ""]
      exts |> List.forall (fun ext ->
        let change = { FilePath = s.Get + ext; Kind = FileChangeKind.Deleted; Timestamp = DateTimeOffset.UtcNow }
        fileChangeAction change = FileChangeAction.Ignore
      )

/// The escalation hierarchy is a critical design decision:
///   Reload = cheapest (re-eval one file via #load)
///   SoftReset = heavier (rebuild all assembly references)
/// Wrong routing means either wasted rebuilds or stale code.
let fileChangeActionEscalation = testList "fileChangeAction: escalation hierarchy" [
  for ext, expectReload, label in [
    ".fs", true, "source → Reload"
    ".fsx", true, "script → Reload"
    ".fsproj", false, "project → SoftReset"
    ".txt", false, "unrelated → Ignore"
  ] do
    test label {
      let change = { FilePath = sprintf "test%s" ext; Kind = FileChangeKind.Changed; Timestamp = DateTimeOffset.UtcNow }
      let result = fileChangeAction change
      if expectReload then
        match result with
        | FileChangeAction.Reload _ -> ()
        | other -> failwith (sprintf "expected Reload, got %A" other)
      elif ext = ".fsproj" then
        result |> Expect.equal label FileChangeAction.SoftReset
      else
        result |> Expect.equal label FileChangeAction.Ignore
    }
]

// --- shouldTriggerRebuild: build noise suppression ---

/// IDEs create temp files (~foo.fs, foo.tmp), and build output lands
/// in bin/obj. Without this filter, every IDE save causes a double-
/// rebuild and every build triggers an infinite rebuild loop.
let tempFilesNeverTriggerRebuild = testList "shouldTriggerRebuild: temp/build artifacts filtered" [
  let config = { Directories = []; Extensions = [".fs"; ".fsx"; ".fsproj"]; ExcludePatterns = []; DebounceMs = 500 }
  for path, label in [
    "~MyFile.fs", "tilde-prefix"; "scratch.fs.tmp", "tmp-suffix"
    "src/obj/Debug/net10.0/test.fs", "obj dir"; "src/bin/Debug/net10.0/SageFs.dll", "bin dir"
    @"C:\proj\bin\Release\net10.0\app.fs", "Win bin"; @"C:\proj\obj\Debug\net10.0\temp.fsx", "Win obj"
  ] do
    test label { shouldTriggerRebuild config path |> Expect.isFalse label }
]

/// Users configure exclusion patterns to suppress rebuilds for generated
/// code, vendored deps, etc. If patterns were ignored after a refactor,
/// generated files would trigger spurious rebuilds.
let exclusionPatternsHonored = testList "shouldTriggerRebuild: exclusion patterns respected" [
  let config ext pats = { Directories = []; Extensions = ext; ExcludePatterns = pats; DebounceMs = 500 }
  test "excluded .fs file doesn't trigger" {
    shouldTriggerRebuild (config [".fs"] ["**/Generated/*"]) "src/Generated/Code.fs"
    |> Expect.isFalse "excluded"
  }
  test "non-excluded .fs file triggers" {
    shouldTriggerRebuild (config [".fs"] ["**/Generated/*"]) "src/MyCode.fs"
    |> Expect.isTrue "not excluded"
  }
]

/// Only configured extensions trigger rebuilds. Firing on unconfigured
/// extensions means spurious rebuilds (slow). NOT firing on configured
/// ones means stale code (dangerous).
let onlyConfiguredExtensionsTrigger = testList "shouldTriggerRebuild: only configured extensions" [
  let config exts = { Directories = []; Extensions = exts; ExcludePatterns = []; DebounceMs = 500 }
  test ".fs fires when configured" {
    shouldTriggerRebuild (config [".fs"]) "src/App.fs" |> Expect.isTrue ".fs"
  }
  test ".cs silent when only .fs configured" {
    shouldTriggerRebuild (config [".fs"]) "src/App.cs" |> Expect.isFalse ".cs"
  }
  test "empty extensions means nothing fires" {
    shouldTriggerRebuild (config []) "src/App.fs" |> Expect.isFalse "empty"
  }
]

// --- scoreCandidate: autocomplete ranking contracts ---

/// When a user types "get" in autocomplete, they expect "getUser" to
/// rank above "budgetItem" even though "budget" fuzzy-matches "get".
/// The 200-point prefix bonus makes autocomplete feel responsive.
let prefixMatchBeatsFuzzy =
  testPropertyWithConfig cfg
    "scoreCandidate: prefix match always outscores non-prefix of same length"
    <| fun (NonEmptyString prefix) ->
      let safePfx = Text.RegularExpressions.Regex.Replace(prefix, @"[^a-zA-Z]", "a")
      if safePfx.Length = 0 then true
      else
        let prefixCandidate = safePfx + "Suffix"
        let nonPrefixCandidate = "zz" + safePfx + "Suffix"
        scoreCandidate safePfx prefixCandidate > scoreCandidate safePfx nonPrefixCandidate

/// When two candidates both prefix-match, the shorter one is more
/// likely what the user wants. The brevity bonus (100/(len+1))
/// ensures "getX" ranks above "getVeryLongMethodName".
let shorterCandidateScoresHigher =
  testPropertyWithConfig cfg
    "scoreCandidate: shorter candidate scores >= longer for same prefix"
    <| fun (NonEmptyString prefix) (PositiveInt extra) ->
      let safePfx = Text.RegularExpressions.Regex.Replace(prefix, @"[^a-zA-Z]", "a")
      if safePfx.Length = 0 then true
      else
        let short = safePfx + "x"
        let long = safePfx + String('x', extra + 2)
        scoreCandidate safePfx short >= scoreCandidate safePfx long

// --- reformatExpectoSummary: output passthrough safety ---

/// cleanStdout applies reformatExpectoSummary to every line containing
/// "EXPECTO!". Non-matching lines MUST pass through unchanged — if the
/// regex accidentally matches non-Expecto output, it mangles it.
let reformatPassthroughOnNonExpecto =
  testPropertyWithConfig cfg
    "reformatExpectoSummary: non-matching lines pass through verbatim"
    <| fun (s: NonEmptyString) ->
      let plain = Text.RegularExpressions.Regex.Replace(s.Get, @"EXPECTO!", "test")
      reformatExpectoSummary plain = plain

// ═══════════════════════════════════════════════════════════
// McpAdapter — escapeJson (RFC 8259 §7 compliance)
//
// WHY: escapeJson is used at 14+ call sites to serialize
// arbitrary text into JSON strings for MCP clients and
// browser dashboards. If control characters slip through,
// JSON parsing fails silently in downstream consumers.
// ═══════════════════════════════════════════════════════════

let escapeJsonPropertyCfg = { FsCheckConfig.defaultConfig with maxTest = 500 }

let escapeJsonProducesValidJson =
  testPropertyWithConfig escapeJsonPropertyCfg
    "escapeJson: output always produces parseable JSON string"
    <| fun (s: NonEmptyString) ->
      let escaped = escapeJson s.Get
      let json = sprintf "\"%s\"" escaped
      try
        use doc = JsonDocument.Parse(json)
        doc.RootElement.GetString() |> ignore
        true
      with _ -> false

let escapeJsonRoundtripPreservesContent =
  testPropertyWithConfig escapeJsonPropertyCfg
    "escapeJson: JSON parse roundtrip preserves original content"
    <| fun (s: NonEmptyString) ->
      let escaped = escapeJson s.Get
      let json = sprintf "\"%s\"" escaped
      use doc = JsonDocument.Parse(json)
      doc.RootElement.GetString() = s.Get

let escapeJsonControlCharExamples = testList "escapeJson: control character examples" [
  test "backspace is escaped" {
    let result = escapeJson "hello\bworld"
    use doc = JsonDocument.Parse(sprintf "\"%s\"" result)
    doc.RootElement.GetString() |> Expect.equal "roundtrip" "hello\bworld"
  }
  test "form feed is escaped" {
    let result = escapeJson "page\x0Cbreak"
    use doc = JsonDocument.Parse(sprintf "\"%s\"" result)
    doc.RootElement.GetString() |> Expect.equal "roundtrip" "page\x0Cbreak"
  }
  test "null byte is escaped" {
    let result = escapeJson "null\x00here"
    use doc = JsonDocument.Parse(sprintf "\"%s\"" result)
    doc.RootElement.GetString() |> Expect.equal "roundtrip" "null\x00here"
  }
  test "all standard escapes present" {
    let input = "\"\\\n\r\t\b\x0C"
    let escaped = escapeJson input
    escaped |> Expect.stringContains "has quote" "\\\""
    escaped |> Expect.stringContains "has backslash" "\\\\"
    escaped |> Expect.stringContains "has newline" "\\n"
    escaped |> Expect.stringContains "has return" "\\r"
    escaped |> Expect.stringContains "has tab" "\\t"
  }
]

// ═══════════════════════════════════════════════════════════
// FsiRewrite — rewriteInlineUseStatements
//
// WHY: FSI doesn't support `use` at top level. The rewrite
// replaces leading `use ` with `let ` for FSI eval. A bug
// that replaces ALL occurrences of "use " in the line
// corrupts string literals containing "use ".
// ═══════════════════════════════════════════════════════════

let rewriteInlineUseStatementTests = testList "rewriteInlineUseStatements" [
  test "leading use becomes let" {
    let input = "    use svc = createService()"
    let result = rewriteInlineUseStatements input
    result |> Expect.equal "rewritten" "    let svc = createService()"
  }
  test "use inside string literal is NOT replaced" {
    let input = """    use svc = create "use this service" """
    let result = rewriteInlineUseStatements input
    result |> Expect.stringContains "string preserved" "\"use this service\""
  }
  test "line without use is unchanged" {
    let input = "    let x = 42"
    let result = rewriteInlineUseStatements input
    result |> Expect.equal "unchanged" "    let x = 42"
  }
  test "use! is not rewritten" {
    let input = "    use! conn = getConnectionAsync()"
    let result = rewriteInlineUseStatements input
    result |> Expect.equal "use! unchanged" "    use! conn = getConnectionAsync()"
  }
  test "multiple lines only rewrites the use lines" {
    let input = "    use x = a()\n    let y = 42\n    use z = b()"
    let result = rewriteInlineUseStatements input
    result |> Expect.stringContains "first rewritten" "    let x = a()"
    result |> Expect.stringContains "middle unchanged" "    let y = 42"
    result |> Expect.stringContains "third rewritten" "    let z = b()"
  }
  test "idempotent — rewriting twice gives same result" {
    let input = "    use svc = createService()"
    let once = rewriteInlineUseStatements input
    let twice = rewriteInlineUseStatements once
    twice |> Expect.equal "idempotent" once
  }
]

// ═══════════════════════════════════════════════════════════
// McpAdapter — splitStatements (gap-closing examples)
//
// WHY: splitStatements parses F# code on `;;` boundaries
// for FSI evaluation. Incorrect splitting inside strings,
// comments, or nested constructs would send broken code
// to the compiler.
// ═══════════════════════════════════════════════════════════

let splitStatementsGapTests = testList "splitStatements: gap-closing examples" [
  test "double-semicolon inside string literal is not a boundary" {
    let code = """let x = "hello;; world";; """
    let parts = splitStatements code
    parts.Length |> Expect.equal "one statement" 1
  }
  test "double-semicolon inside line comment is not a boundary" {
    let code = "// this is a comment with ;;\nlet x = 1;;"
    let parts = splitStatements code
    parts.Length |> Expect.equal "one statement" 1
  }
  test "double-semicolon inside block comment is not a boundary" {
    let code = "(* comment with ;; inside *)\nlet x = 1;;"
    let parts = splitStatements code
    parts.Length |> Expect.equal "one statement" 1
  }
  test "multiple real boundaries produce multiple statements" {
    let code = "let x = 1;;\nlet y = 2;;"
    let parts = splitStatements code
    parts.Length |> Expect.equal "two statements" 2
  }
  test "nested parentheses don't confuse parser" {
    let code = "let f (x: (int * int)) = fst x;;"
    let parts = splitStatements code
    parts.Length |> Expect.equal "one statement" 1
  }
  test "triple-quoted string with semicolons" {
    let code = "let x = \"\"\"a;;b;;c\"\"\";;"
    let parts = splitStatements code
    parts.Length |> Expect.equal "one statement" 1
  }
  test "empty input produces no statements" {
    let parts = splitStatements ""
    parts.Length |> Expect.equal "no statements" 0
  }
  test "whitespace-only input produces no statements" {
    let parts = splitStatements "   \n  \n  "
    parts.Length |> Expect.equal "no statements" 0
  }
]

// ═══════════════════════════════════════════════════════════
// KeyMap — parseConfigLines
//
// WHY: parseConfigLines reads user keybinding configs. Zero
// coverage means we don't know if the hand-rolled parser
// handles edge cases (empty input, malformed lines, unknown
// keys). A crash here blocks SageFs startup.
// ═══════════════════════════════════════════════════════════

let parseConfigLinesTests = testList "SageFs.KeyMap.parseConfigLines contracts" [
  test "valid two-binding config produces two entries" {
    let lines = [|
      "let keybindings = ["
      "  \"Ctrl+Q\", \"Quit\""
      "  \"Ctrl+S\", \"CycleFocus\""
      "]"
    |]
    let result = SageFs.KeyMap.parseConfigLines lines
    result.Count |> Expect.equal "two bindings" 2
  }
  test "lines before 'let keybindings' are ignored" {
    let lines = [|
      "// this is a comment"
      "let x = 42"
      "let keybindings = ["
      "  \"Ctrl+Q\", \"Quit\""
      "]"
    |]
    let result = SageFs.KeyMap.parseConfigLines lines
    result.Count |> Expect.equal "one binding" 1
  }
  test "empty keybindings block produces empty map" {
    let lines = [| "let keybindings = ["; "]" |]
    let result = SageFs.KeyMap.parseConfigLines lines
    result.Count |> Expect.equal "no bindings" 0
  }
  test "unrecognized key combo is silently skipped" {
    let lines = [|
      "let keybindings = ["
      "  \"NotAKey+Combo\", \"Quit\""
      "  \"Ctrl+Q\", \"Quit\""
      "]"
    |]
    let result = SageFs.KeyMap.parseConfigLines lines
    result.Count |> Expect.equal "only valid binding" 1
  }
  test "unrecognized action is silently skipped" {
    let lines = [|
      "let keybindings = ["
      "  \"Ctrl+Q\", \"NotAnAction\""
      "  \"Ctrl+Q\", \"Quit\""
      "]"
    |]
    let result = SageFs.KeyMap.parseConfigLines lines
    result.Count |> Expect.equal "only valid binding" 1
  }
  test "never crashes on empty input" {
    let result = SageFs.KeyMap.parseConfigLines [||]
    result.Count |> Expect.equal "empty" 0
  }
  test "never crashes on arbitrary non-config text" {
    let lines = [| "random text"; "more stuff"; "123"; "" |]
    let result = SageFs.KeyMap.parseConfigLines lines
    result.Count |> Expect.equal "no bindings" 0
  }
  test "closing bracket stops parsing" {
    let lines = [|
      "let keybindings = ["
      "  \"Ctrl+Q\", \"Quit\""
      "]"
      "  \"Ctrl+S\", \"CycleFocus\""
    |]
    let result = SageFs.KeyMap.parseConfigLines lines
    result.Count |> Expect.equal "stops at bracket" 1
  }
  test "case-insensitive 'Keybindings' detection" {
    let lines = [|
      "let Keybindings = ["
      "  \"Ctrl+Q\", \"Quit\""
      "]"
    |]
    let result = SageFs.KeyMap.parseConfigLines lines
    result.Count |> Expect.equal "case insensitive" 1
  }
]

let parseConfigLinesNeverCrashes =
  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 300 }
    "parseConfigLines: never crashes on arbitrary string arrays"
    <| fun (lines: string[]) ->
      let safeLines = lines |> Array.filter (not << isNull)
      try
        SageFs.KeyMap.parseConfigLines safeLines |> ignore
        true
      with _ -> false

// ═══════════════════════════════════════════════════════════
// McpAdapter — formatEventsJson / formatCompletionsJson
// (MCP protocol contract tests)
//
// WHY: These functions serialize data for MCP clients. If the
// JSON shape changes, all MCP consumers (VS Code, Neovim,
// Visual Studio) break silently.
// ═══════════════════════════════════════════════════════════

let formatEventsJsonTests = testList "formatEventsJson: MCP protocol contract" [
  test "empty events produces valid JSON with count 0" {
    let result = formatEventsJson []
    use doc = JsonDocument.Parse(result)
    let root = doc.RootElement
    root.GetProperty("count").GetInt32() |> Expect.equal "count" 0
    root.GetProperty("events").GetArrayLength() |> Expect.equal "array" 0
  }
  test "single event produces correct JSON shape" {
    let ts = DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc)
    let result = formatEventsJson [(ts, "mcp:test", "hello world")]
    use doc = JsonDocument.Parse(result)
    let root = doc.RootElement
    root.GetProperty("count").GetInt32() |> Expect.equal "count" 1
    let ev = root.GetProperty("events").[0]
    ev.GetProperty("source").GetString() |> Expect.equal "source" "mcp:test"
    ev.GetProperty("text").GetString() |> Expect.equal "text" "hello world"
    ev.GetProperty("timestamp").GetString().Length > 0 |> Expect.isTrue "has timestamp"
  }
  test "special characters in text are properly escaped" {
    let ts = DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc)
    let result = formatEventsJson [(ts, "src", "line1\nline2\ttab\"quote")]
    use doc = JsonDocument.Parse(result)
    let text = doc.RootElement.GetProperty("events").[0].GetProperty("text").GetString()
    text |> Expect.equal "roundtrip" "line1\nline2\ttab\"quote"
  }
]

let formatCompletionsJsonTests = testList "formatCompletionsJson: MCP protocol contract" [
  test "empty completions produces valid JSON with count 0" {
    let result = formatCompletionsJson []
    use doc = JsonDocument.Parse(result)
    let root = doc.RootElement
    root.GetProperty("count").GetInt32() |> Expect.equal "count" 0
    root.GetProperty("completions").GetArrayLength() |> Expect.equal "array" 0
  }
  test "completion item has label, kind, insertText fields" {
    let item = {
      DisplayText = "myFunction"
      ReplacementText = "myFunction"
      Kind = CompletionKind.Method
      GetDescription = None
    }
    let result = formatCompletionsJson [item]
    use doc = JsonDocument.Parse(result)
    let comp = doc.RootElement.GetProperty("completions").[0]
    comp.GetProperty("label").GetString() |> Expect.equal "label" "myFunction"
    comp.GetProperty("kind").GetString() |> Expect.equal "kind" "Method"
    comp.GetProperty("insertText").GetString() |> Expect.equal "insert" "myFunction"
  }
  test "special characters in display text are properly escaped" {
    let item = {
      DisplayText = "func<'a>"
      ReplacementText = "func"
      Kind = CompletionKind.Union
      GetDescription = None
    }
    let result = formatCompletionsJson [item]
    use doc = JsonDocument.Parse(result)
    let label = doc.RootElement.GetProperty("completions").[0].GetProperty("label").GetString()
    label |> Expect.equal "roundtrip" "func<'a>"
  }
]

// ═══════════════════════════════════════════════════════════
// FrameDiff — parsePositionedLines / diff
// ═══════════════════════════════════════════════════════════

let moveTo row col = sprintf "\x1b[%d;%dH" row col

let parsePositionedLinesTests = testList "parsePositionedLines" [
  test "empty string produces empty map" {
    parsePositionedLines "" |> Expect.isEmpty "no rows"
  }
  test "single row with moveTo" {
    let frame = moveTo 1 1 + "Hello"
    let result = parsePositionedLines frame
    result |> Map.find 1 |> Expect.stringContains "should have content" "Hello"
  }
  test "multiple distinct rows" {
    let frame = moveTo 1 1 + "Row1" + moveTo 3 1 + "Row3"
    let result = parsePositionedLines frame
    result |> Map.count |> Expect.equal "two rows" 2
    result |> Map.find 1 |> Expect.stringContains "row 1" "Row1"
    result |> Map.find 3 |> Expect.stringContains "row 3" "Row3"
  }
  test "same row appearing twice appends content" {
    let frame = moveTo 2 1 + "Part1" + moveTo 2 10 + "Part2"
    let result = parsePositionedLines frame
    result |> Map.count |> Expect.equal "one row key" 1
    let content = result |> Map.find 2
    content |> Expect.stringContains "has Part1" "Part1"
    content |> Expect.stringContains "has Part2" "Part2"
  }
  test "color codes preserved in row content" {
    let frame = moveTo 1 1 + "\x1b[32mGreen\x1b[0m"
    let result = parsePositionedLines frame
    let content = result |> Map.find 1
    content |> Expect.stringContains "has green" "\x1b[32m"
    content |> Expect.stringContains "has reset" "\x1b[0m"
  }
  test "erase K code preserved in row" {
    let frame = moveTo 1 1 + "\x1b[K"
    let result = parsePositionedLines frame
    let content = result |> Map.find 1
    content |> Expect.stringContains "has K seq" "\x1b[K"
  }
  test "text before any moveTo is discarded" {
    let frame = "OrphanText" + moveTo 1 1 + "Hello"
    let result = parsePositionedLines frame
    result |> Map.find 1 |> Expect.stringContains "has Hello" "Hello"
    let content = result |> Map.find 1
    content.Contains("Orphan") |> Expect.isFalse "orphan discarded"
  }
  test "row-only moveTo uses row with default col" {
    let frame = "\x1b[5H" + "Content"
    let result = parsePositionedLines frame
    result |> Map.containsKey 5 |> Expect.isTrue "row 5 exists"
  }
]

let frameDiffExampleTests = testList "diff examples" [
  test "identical frames produce empty diff" {
    let frame = moveTo 1 1 + "Hello" + moveTo 2 1 + "World"
    diff frame frame |> Expect.equal "no changes" ""
  }
  test "changed rows only in diff" {
    let prev = moveTo 1 1 + "Same" + moveTo 2 1 + "Old"
    let next = moveTo 1 1 + "Same" + moveTo 2 1 + "New"
    let result = diff prev next
    result.Contains("New") |> Expect.isTrue "has new content"
    result.Contains("Same") |> Expect.isFalse "no unchanged rows"
  }
  test "new rows appear in diff" {
    let prev = moveTo 1 1 + "Row1"
    let next = moveTo 1 1 + "Row1" + moveTo 3 1 + "Row3"
    let result = diff prev next
    result.Contains("Row3") |> Expect.isTrue "has new row"
    result.Contains("Row1") |> Expect.isFalse "no unchanged"
  }
  test "empty previous means all rows appear" {
    let next = moveTo 1 1 + "Hello"
    let result = diff "" next
    result.Contains("Hello") |> Expect.isTrue "has all content"
  }
  test "both empty gives empty diff" {
    diff "" "" |> Expect.equal "empty" ""
  }
]

let frameDiffProperties = testList "diff properties" [
  testProperty "diffing identical frames always produces empty string" <| fun (s: string) ->
    let s = if isNull s then "" else s
    let frame = moveTo 1 1 + s
    diff frame frame = ""
]

// ═══════════════════════════════════════════════════════════
// Replay — SessionReplayState / DaemonReplayState
// ═══════════════════════════════════════════════════════════

let replayTs = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
let mkDiag sev msg : DiagnosticEvent =
  { Severity = sev; Message = msg; StartLine = 1; StartColumn = 1; EndLine = 1; EndColumn = 5 }

let sessionReplayTests = testList "SessionReplayState.applyEvent" [
  test "SessionStarted sets WarmingUp and StartedAt" {
    let evt = SessionStarted {| Config = Map.empty; StartedAt = replayTs |}
    let s = SessionReplayState.applyEvent replayTs SessionReplayState.empty evt
    s.Status |> Expect.equal "status" ReplayStatus.WarmingUp
    s.StartedAt |> Expect.equal "started" (Some replayTs)
    s.LastActivity |> Expect.equal "activity" (Some replayTs)
  }
  test "SessionWarmUpCompleted stores errors" {
    let evt = SessionWarmUpCompleted {| Duration = TimeSpan.FromSeconds 1.0; Errors = ["err1"] |}
    let s = SessionReplayState.applyEvent replayTs SessionReplayState.empty evt
    s.WarmupErrors |> Expect.equal "errors" ["err1"]
  }
  test "SessionWarmUpProgress updates LastActivity" {
    let evt = SessionWarmUpProgress {| Step = 1; Total = 5; Message = "step" |}
    let s = SessionReplayState.applyEvent replayTs SessionReplayState.empty evt
    s.LastActivity |> Expect.equal "activity" (Some replayTs)
  }
  test "SessionReady sets Ready" {
    let s = SessionReplayState.applyEvent replayTs SessionReplayState.empty SessionReady
    s.Status |> Expect.equal "status" ReplayStatus.Ready
  }
  test "SessionFaulted sets Faulted with error" {
    let evt = SessionFaulted {| Error = "boom"; StackTrace = None |}
    let s = SessionReplayState.applyEvent replayTs SessionReplayState.empty evt
    s.Status |> Expect.equal "status" (ReplayStatus.Faulted "boom")
  }
  test "SessionReset increments ResetCount and sets WarmingUp" {
    let s = SessionReplayState.applyEvent replayTs SessionReplayState.empty SessionReset
    s.Status |> Expect.equal "status" ReplayStatus.WarmingUp
    s.ResetCount |> Expect.equal "count" 1
  }
  test "SessionHardReset increments HardResetCount" {
    let evt = SessionHardReset {| Rebuild = true |}
    let s = SessionReplayState.applyEvent replayTs SessionReplayState.empty evt
    s.HardResetCount |> Expect.equal "count" 1
    s.Status |> Expect.equal "status" ReplayStatus.WarmingUp
  }
  test "EvalRequested sets Evaluating" {
    let evt = EvalRequested {| Code = "1+1"; Source = EventSource.Console |}
    let s = SessionReplayState.applyEvent replayTs SessionReplayState.empty evt
    s.Status |> Expect.equal "status" ReplayStatus.Evaluating
  }
  test "EvalCompleted increments count and records history" {
    let evt = EvalCompleted {| Code = "1+1"; Result = "2"; TypeSignature = Some "int"; Duration = TimeSpan.FromMilliseconds 50.0 |}
    let s = SessionReplayState.applyEvent replayTs SessionReplayState.empty evt
    s.EvalCount |> Expect.equal "count" 1
    s.LastEvalResult |> Expect.equal "result" (Some "2")
    s.EvalHistory |> Expect.hasLength "history" 1
    s.Status |> Expect.equal "status" ReplayStatus.Ready
  }
  test "EvalFailed increments failed count and stores diagnostics" {
    let evt = EvalFailed {| Code = "bad"; Error = "oops"; Diagnostics = [mkDiag DiagnosticSeverity.Error "oops"] |}
    let s = SessionReplayState.applyEvent replayTs SessionReplayState.empty evt
    s.FailedEvalCount |> Expect.equal "count" 1
    s.LastDiagnostics |> Expect.hasLength "diags" 1
    s.Status |> Expect.equal "status" ReplayStatus.Ready
  }
  test "DiagnosticsChecked stores diagnostics" {
    let evt = DiagnosticsChecked {| Code = "x"; Diagnostics = [mkDiag DiagnosticSeverity.Warning "warn"]; Source = EventSource.Console |}
    let s = SessionReplayState.applyEvent replayTs SessionReplayState.empty evt
    s.LastDiagnostics |> Expect.hasLength "diags" 1
  }
  test "DiagnosticsCleared empties diagnostics" {
    let init = { SessionReplayState.empty with LastDiagnostics = [mkDiag DiagnosticSeverity.Error "e"] }
    let s = SessionReplayState.applyEvent replayTs init DiagnosticsCleared
    s.LastDiagnostics |> Expect.isEmpty "cleared"
  }
  test "ScriptLoaded updates activity only" {
    let evt = ScriptLoaded {| FilePath = "test.fsx"; StatementCount = 3; Source = EventSource.Console |}
    let s = SessionReplayState.applyEvent replayTs SessionReplayState.empty evt
    s.LastActivity |> Expect.equal "activity" (Some replayTs)
    s.Status |> Expect.equal "status unchanged" ReplayStatus.NotStarted
  }
  test "ScriptLoadFailed updates activity only" {
    let evt = ScriptLoadFailed {| FilePath = "bad.fsx"; Error = "parse error" |}
    let s = SessionReplayState.applyEvent replayTs SessionReplayState.empty evt
    s.LastActivity |> Expect.equal "activity" (Some replayTs)
  }
  test "McpInputReceived updates activity only" {
    let evt = McpInputReceived {| Source = EventSource.Console; Content = "hello" |}
    let s = SessionReplayState.applyEvent replayTs SessionReplayState.empty evt
    s.LastActivity |> Expect.equal "activity" (Some replayTs)
  }
  test "McpOutputSent updates activity only" {
    let evt = McpOutputSent {| Source = EventSource.Console; Content = "result" |}
    let s = SessionReplayState.applyEvent replayTs SessionReplayState.empty evt
    s.LastActivity |> Expect.equal "activity" (Some replayTs)
  }
  test "DaemonSessionCreated is ignored by session replay" {
    let evt = DaemonSessionCreated {| SessionId = "s1"; Projects = ["p.fsproj"]; WorkingDir = "/tmp"; CreatedAt = replayTs |}
    let s = SessionReplayState.applyEvent replayTs SessionReplayState.empty evt
    s |> Expect.equal "unchanged" SessionReplayState.empty
  }
]

let daemonReplayTests = testList "DaemonReplayState.applyEvent" [
  test "DaemonSessionCreated adds session and sets active" {
    let evt = DaemonSessionCreated {| SessionId = "s1"; Projects = ["p.fsproj"]; WorkingDir = "/tmp"; CreatedAt = replayTs |}
    let s = DaemonReplayState.applyEvent DaemonReplayState.empty evt
    s.Sessions |> Map.containsKey "s1" |> Expect.isTrue "session added"
    s.ActiveSessionId |> Expect.equal "active" (Some "s1")
  }
  test "DaemonSessionStopped picks next alive session as active" {
    let created1 = DaemonSessionCreated {| SessionId = "s1"; Projects = []; WorkingDir = "/"; CreatedAt = replayTs |}
    let created2 = DaemonSessionCreated {| SessionId = "s2"; Projects = []; WorkingDir = "/"; CreatedAt = replayTs |}
    let stopped = DaemonSessionStopped {| SessionId = "s1"; StoppedAt = replayTs.AddHours(1.0) |}
    let s =
      DaemonReplayState.empty
      |> fun s -> DaemonReplayState.applyEvent s created1
      |> fun s -> DaemonReplayState.applyEvent s created2
      |> fun s -> DaemonReplayState.applyEvent s stopped
    s.Sessions.["s1"].StoppedAt.IsSome |> Expect.isTrue "s1 stopped"
    s.ActiveSessionId |> Expect.equal "s2 now active" (Some "s2")
  }
  test "DaemonSessionSwitched sets active" {
    let evt = DaemonSessionSwitched {| FromId = Some "s1"; ToId = "s2"; SwitchedAt = replayTs |}
    let s = DaemonReplayState.applyEvent DaemonReplayState.empty evt
    s.ActiveSessionId |> Expect.equal "active" (Some "s2")
  }
  test "non-daemon events are ignored" {
    let s = DaemonReplayState.applyEvent DaemonReplayState.empty SessionReady
    s |> Expect.equal "unchanged" DaemonReplayState.empty
  }
  test "aliveSessions filters out stopped" {
    let created1 = DaemonSessionCreated {| SessionId = "s1"; Projects = []; WorkingDir = "/"; CreatedAt = replayTs |}
    let created2 = DaemonSessionCreated {| SessionId = "s2"; Projects = []; WorkingDir = "/"; CreatedAt = replayTs |}
    let stopped = DaemonSessionStopped {| SessionId = "s1"; StoppedAt = replayTs.AddHours(1.0) |}
    let s =
      DaemonReplayState.empty
      |> fun s -> DaemonReplayState.applyEvent s created1
      |> fun s -> DaemonReplayState.applyEvent s created2
      |> fun s -> DaemonReplayState.applyEvent s stopped
    let alive = s.Sessions |> Map.filter (fun _ r -> r.StoppedAt.IsNone)
    alive |> Map.count |> Expect.equal "one alive" 1
    alive |> Map.containsKey "s2" |> Expect.isTrue "s2 alive"
  }
]

let replayStreamIntegrationTests = testList "replayStream integration" [
  test "full lifecycle produces correct final state" {
    let events = [
      replayTs, SessionStarted {| Config = Map.empty; StartedAt = replayTs |}
      replayTs.AddSeconds(1.0), SessionWarmUpCompleted {| Duration = TimeSpan.FromSeconds 1.0; Errors = [] |}
      replayTs.AddSeconds(2.0), SessionReady
      replayTs.AddSeconds(3.0), EvalRequested {| Code = "1+1;;"; Source = EventSource.Console |}
      replayTs.AddSeconds(4.0), EvalCompleted {| Code = "1+1;;"; Result = "2"; TypeSignature = Some "int"; Duration = TimeSpan.FromMilliseconds 50.0 |}
      replayTs.AddSeconds(5.0), EvalRequested {| Code = "bad;;"; Source = EventSource.Console |}
      replayTs.AddSeconds(6.0), EvalFailed {| Code = "bad;;"; Error = "not defined"; Diagnostics = [] |}
      replayTs.AddSeconds(7.0), SessionReset
      replayTs.AddSeconds(8.0), SessionReady
    ]
    let final = SessionReplayState.replayStream events
    final.Status |> Expect.equal "status" ReplayStatus.Ready
    final.EvalCount |> Expect.equal "evals" 1
    final.FailedEvalCount |> Expect.equal "failed" 1
    final.ResetCount |> Expect.equal "resets" 1
    final.HardResetCount |> Expect.equal "hard resets" 0
    final.EvalHistory |> Expect.hasLength "history" 1
    final.StartedAt |> Expect.equal "started" (Some replayTs)
    final.LastActivity |> Expect.isSome "has last activity"
  }
]

// ═══════════════════════════════════════════════════════════
// ValidatedBuffer — cursor invariants across all operations
// ═══════════════════════════════════════════════════════════

let validatedBufferTests = testList "ValidatedBuffer" [
  let pos l c = { Line = l; Column = c } : CursorPosition
  let ok lines l c = ValidatedBuffer.create lines (pos l c) |> Result.defaultWith (sprintf "%A" >> failwith)
  testList "create" [
    test "empty lines returns EmptyLines error" {
      ValidatedBuffer.create [] (pos 0 0)
      |> Expect.isError "should fail on empty"
    }
    test "valid input succeeds" {
      ValidatedBuffer.create ["hello"] (pos 0 3)
      |> Expect.isOk "should succeed"
    }
    test "out-of-bounds cursor returns CursorOutOfBounds" {
      ValidatedBuffer.create ["hi"] (pos 0 5)
      |> Expect.isError "cursor past line end"
    }
    test "negative line returns error" {
      ValidatedBuffer.create ["hi"] (pos -1 0)
      |> Expect.isError "negative line"
    }
    test "cursor at end of line is valid" {
      ValidatedBuffer.create ["abc"] (pos 0 3)
      |> Expect.isOk "cursor at length"
    }
  ]
  testList "setCursor clamping" [
    test "setCursor clamps line to bounds" {
      let vb = ok ["ab"; "cd"] 0 0
      let vb' = ValidatedBuffer.setCursor (pos 99 0) vb
      vb'.Cursor.Line |> Expect.equal "clamped to last line" 1
    }
    test "setCursor clamps column to line length" {
      let vb = ok ["ab"] 0 0
      let vb' = ValidatedBuffer.setCursor (pos 0 99) vb
      vb'.Cursor.Column |> Expect.equal "clamped to line length" 2
    }
    test "setCursor clamps negative to zero" {
      let vb = ok ["ab"] 0 0
      let vb' = ValidatedBuffer.setCursor (pos -5 -3) vb
      vb'.Cursor.Line |> Expect.equal "line clamped" 0
      vb'.Cursor.Column |> Expect.equal "col clamped" 0
    }
    testProperty "setCursor is idempotent" <| fun (line: int) (col: int) ->
      let vb = ok ["hello"; "world"] 0 0
      let once = ValidatedBuffer.setCursor (pos line col) vb
      let twice = ValidatedBuffer.setCursor once.Cursor once
      once.Cursor.Line = twice.Cursor.Line && once.Cursor.Column = twice.Cursor.Column
  ]
  testList "insertChar" [
    test "inserts at cursor and advances" {
      let vb = ok ["ac"] 0 1
      let vb' = ValidatedBuffer.insertChar 'b' vb
      vb'.Lines.[0] |> Expect.equal "inserted" "abc"
      vb'.Cursor.Column |> Expect.equal "advanced" 2
    }
    test "inserts at beginning" {
      let vb = ok ["bc"] 0 0
      let vb' = ValidatedBuffer.insertChar 'a' vb
      vb'.Lines.[0] |> Expect.equal "prepended" "abc"
    }
    test "inserts at end" {
      let vb = ok ["ab"] 0 2
      let vb' = ValidatedBuffer.insertChar 'c' vb
      vb'.Lines.[0] |> Expect.equal "appended" "abc"
    }
  ]
  testList "deleteBackward" [
    test "at (0,0) is no-op" {
      let vb = ok ["abc"] 0 0
      let vb' = ValidatedBuffer.deleteBackward vb
      vb'.Lines.[0] |> Expect.equal "unchanged" "abc"
      vb'.Cursor.Column |> Expect.equal "still 0" 0
    }
    test "mid-line deletes character before cursor" {
      let vb = ok ["abc"] 0 2
      let vb' = ValidatedBuffer.deleteBackward vb
      vb'.Lines.[0] |> Expect.equal "deleted b" "ac"
      vb'.Cursor.Column |> Expect.equal "moved back" 1
    }
    test "at col 0 joins with previous line" {
      let vb = ok ["ab"; "cd"] 1 0
      let vb' = ValidatedBuffer.deleteBackward vb
      vb'.Lines |> Expect.hasLength "merged" 1
      vb'.Lines.[0] |> Expect.equal "joined" "abcd"
      vb'.Cursor.Column |> Expect.equal "at join point" 2
    }
  ]
  testList "newLine" [
    test "splits line at cursor" {
      let vb = ok ["abcd"] 0 2
      let vb' = ValidatedBuffer.newLine vb
      vb'.Lines |> Expect.hasLength "two lines" 2
      vb'.Lines.[0] |> Expect.equal "before" "ab"
      vb'.Lines.[1] |> Expect.equal "after" "cd"
    }
    test "at col 0 inserts empty line before" {
      let vb = ok ["abc"] 0 0
      let vb' = ValidatedBuffer.newLine vb
      vb'.Lines.[0] |> Expect.equal "empty before" ""
      vb'.Lines.[1] |> Expect.equal "original" "abc"
    }
    test "at end of line inserts empty line after" {
      let vb = ok ["abc"] 0 3
      let vb' = ValidatedBuffer.newLine vb
      vb'.Lines.[0] |> Expect.equal "original" "abc"
      vb'.Lines.[1] |> Expect.equal "empty after" ""
    }
  ]
  testList "moveCursor" [
    test "Right advances column" {
      let vb = ok ["abc"] 0 0
      let vb' = ValidatedBuffer.moveCursor Direction.Right vb
      vb'.Cursor.Column |> Expect.equal "moved right" 1
    }
    test "Left decrements column" {
      let vb = ok ["abc"] 0 2
      let vb' = ValidatedBuffer.moveCursor Direction.Left vb
      vb'.Cursor.Column |> Expect.equal "moved left" 1
    }
    test "Down advances line" {
      let vb = ok ["ab"; "cd"] 0 0
      let vb' = ValidatedBuffer.moveCursor Direction.Down vb
      vb'.Cursor.Line |> Expect.equal "moved down" 1
    }
    test "Up decrements line" {
      let vb = ok ["ab"; "cd"] 1 0
      let vb' = ValidatedBuffer.moveCursor Direction.Up vb
      vb'.Cursor.Line |> Expect.equal "moved up" 0
    }
    test "Right at end of line wraps to next" {
      let vb = ok ["ab"; "cd"] 0 2
      let vb' = ValidatedBuffer.moveCursor Direction.Right vb
      vb'.Cursor.Line |> Expect.equal "next line" 1
      vb'.Cursor.Column |> Expect.equal "start of line" 0
    }
    test "Left at start wraps to previous line end" {
      let vb = ok ["ab"; "cd"] 1 0
      let vb' = ValidatedBuffer.moveCursor Direction.Left vb
      vb'.Cursor.Line |> Expect.equal "prev line" 0
      vb'.Cursor.Column |> Expect.equal "end of prev" 2
    }
    test "Down at last line is no-op" {
      let vb = ok ["ab"] 0 0
      let vb' = ValidatedBuffer.moveCursor Direction.Down vb
      vb'.Cursor.Line |> Expect.equal "still 0" 0
    }
    test "Up at first line is no-op" {
      let vb = ok ["ab"] 0 1
      let vb' = ValidatedBuffer.moveCursor Direction.Up vb
      vb'.Cursor.Line |> Expect.equal "still 0" 0
    }
  ]
  testList "properties" [
    testProperty "random ops preserve cursor invariant" <| fun (ops: int list) ->
      let mutable vb = ok ["hello"; "world"] 0 0
      for op in ops |> List.truncate 20 do
        vb <-
          match abs op % 5 with
          | 0 -> ValidatedBuffer.insertChar 'x' vb
          | 1 -> ValidatedBuffer.deleteBackward vb
          | 2 -> ValidatedBuffer.newLine vb
          | 3 -> ValidatedBuffer.moveCursor Direction.Right vb
          | _ -> ValidatedBuffer.moveCursor Direction.Down vb
      vb.Cursor.Line >= 0
      && vb.Cursor.Line < vb.Lines.Length
      && vb.Cursor.Column >= 0
      && vb.Cursor.Column <= vb.Lines.[vb.Cursor.Line].Length
  ]
]

// ═══════════════════════════════════════════════════════════
// Rect — layout geometry, area conservation
// ═══════════════════════════════════════════════════════════

let rectTests = testList "Rect" [
  testList "create" [
    test "basic create" {
      let r = Rect.create 1 2 10 20
      r.Row |> Expect.equal "row" 1
      r.Col |> Expect.equal "col" 2
      r.Width |> Expect.equal "w" 10
      r.Height |> Expect.equal "h" 20
    }
    test "negative values clamped to 0" {
      let r = Rect.create -1 -2 -3 -4
      r.Row |> Expect.equal "row" 0
      r.Col |> Expect.equal "col" 0
      r.Width |> Expect.equal "w" 0
      r.Height |> Expect.equal "h" 0
    }
  ]
  testList "isEmpty" [
    test "zero width is empty" {
      Rect.create 0 0 0 10 |> Rect.isEmpty |> Expect.isTrue "w=0"
    }
    test "zero height is empty" {
      Rect.create 0 0 10 0 |> Rect.isEmpty |> Expect.isTrue "h=0"
    }
    test "positive dims is not empty" {
      Rect.create 0 0 5 5 |> Rect.isEmpty |> Expect.isFalse "has area"
    }
  ]
  testList "splitH" [
    test "split at valid position" {
      let top, bot = Rect.create 0 0 10 10 |> Rect.splitH 3
      top.Height |> Expect.equal "top h" 3
      bot.Height |> Expect.equal "bot h" 7
      bot.Row |> Expect.equal "bot row" 3
    }
    test "split at 0 gives empty top" {
      let top, _ = Rect.create 0 0 10 10 |> Rect.splitH 0
      top |> Rect.isEmpty |> Expect.isTrue "top is empty"
    }
    test "split past height gives empty bottom" {
      let _, bot = Rect.create 0 0 10 10 |> Rect.splitH 15
      bot |> Rect.isEmpty |> Expect.isTrue "bot is empty"
    }
    test "split preserves width" {
      let top, bot = Rect.create 5 5 20 10 |> Rect.splitH 4
      top.Width |> Expect.equal "top w" 20
      bot.Width |> Expect.equal "bot w" 20
    }
    test "split preserves col offset" {
      let top, bot = Rect.create 5 5 20 10 |> Rect.splitH 4
      top.Col |> Expect.equal "top col" 5
      bot.Col |> Expect.equal "bot col" 5
    }
  ]
  testList "splitV" [
    test "split at valid position" {
      let left, right = Rect.create 0 0 10 10 |> Rect.splitV 4
      left.Width |> Expect.equal "left w" 4
      right.Width |> Expect.equal "right w" 6
      right.Col |> Expect.equal "right col" 4
    }
    test "split at 0 gives empty left" {
      let left, _ = Rect.create 0 0 10 10 |> Rect.splitV 0
      left |> Rect.isEmpty |> Expect.isTrue "left is empty"
    }
    test "split past width gives empty right" {
      let _, right = Rect.create 0 0 10 10 |> Rect.splitV 15
      right |> Rect.isEmpty |> Expect.isTrue "right is empty"
    }
  ]
  testList "splitHProp and splitVProp" [
    test "splitHProp 50% gives equal halves" {
      let top, bot = Rect.create 0 0 10 10 |> Rect.splitHProp 0.5
      top.Height |> Expect.equal "top h" 5
      bot.Height |> Expect.equal "bot h" 5
    }
    test "splitVProp 50% gives equal halves" {
      let left, right = Rect.create 0 0 10 10 |> Rect.splitVProp 0.5
      left.Width |> Expect.equal "left w" 5
      right.Width |> Expect.equal "right w" 5
    }
  ]
  testList "inset" [
    test "inset reduces all edges" {
      let r = Rect.create 0 0 20 20 |> Rect.inset 2
      r.Row |> Expect.equal "row" 2
      r.Col |> Expect.equal "col" 2
      r.Width |> Expect.equal "w" 16
      r.Height |> Expect.equal "h" 16
    }
    test "inset larger than half clamps to empty" {
      let r = Rect.create 0 0 4 4 |> Rect.inset 5
      r |> Rect.isEmpty |> Expect.isTrue "over-inset is empty"
    }
  ]
  testList "properties" [
    testProperty "splitH area conservation" <| fun (w: PositiveInt) (h: PositiveInt) (at: PositiveInt) ->
      let r = Rect.create 0 0 w.Get h.Get
      let top, bot = Rect.splitH (at.Get % (h.Get + 1)) r
      top.Width * top.Height + bot.Width * bot.Height = r.Width * r.Height
  ]
]

// ═══════════════════════════════════════════════════════════
// RestartPolicy — supervision correctness
// ═══════════════════════════════════════════════════════════

let restartPolicyTests = testList "RestartPolicy" [
  let policy : RestartPolicy.Policy = {
    MaxRestarts = 3
    BackoffBase = TimeSpan.FromSeconds 1.0
    BackoffMax = TimeSpan.FromSeconds 30.0
    ResetWindow = TimeSpan.FromMinutes 1.0
  }
  testList "nextBackoff" [
    test "count 0 returns base delay" {
      let d = RestartPolicy.nextBackoff policy 0
      d |> Expect.equal "base" policy.BackoffBase
    }
    test "exponential growth" {
      let d1 = RestartPolicy.nextBackoff policy 1
      let d2 = RestartPolicy.nextBackoff policy 2
      (d2, d1) |> Expect.isGreaterThan "grows"
    }
    test "capped at max" {
      let d = RestartPolicy.nextBackoff policy 20
      (d, policy.BackoffMax) |> Expect.isLessThanOrEqual "capped"
    }
    test "large count does not overflow" {
      let bigPolicy = { policy with MaxRestarts = 100 }
      let d = RestartPolicy.nextBackoff bigPolicy 50
      (d, TimeSpan.Zero) |> Expect.isGreaterThan "positive"
    }
  ]
  testList "decide" [
    let now = DateTime(2024, 1, 1, 12, 0, 0)
    test "fresh state allows restart" {
      match RestartPolicy.decide policy RestartPolicy.emptyState now with
      | RestartPolicy.Decision.Restart _, _ -> ()
      | RestartPolicy.Decision.GiveUp e, _ -> failwith (sprintf "expected restart, got %A" e)
    }
    test "within limit allows restart" {
      let state = { RestartPolicy.emptyState with RestartCount = 2; WindowStart = Some now }
      match RestartPolicy.decide policy state now with
      | RestartPolicy.Decision.Restart _, _ -> ()
      | RestartPolicy.Decision.GiveUp e, _ -> failwith (sprintf "expected restart, got %A" e)
    }
    test "at limit gives up" {
      let state = { RestartPolicy.emptyState with RestartCount = 3; WindowStart = Some now }
      match RestartPolicy.decide policy state now with
      | RestartPolicy.Decision.GiveUp _, _ -> ()
      | RestartPolicy.Decision.Restart _, _ -> failwith "expected give up at limit"
    }
    test "past limit gives up" {
      let state = { RestartPolicy.emptyState with RestartCount = 5; WindowStart = Some now }
      match RestartPolicy.decide policy state now with
      | RestartPolicy.Decision.GiveUp _, _ -> ()
      | RestartPolicy.Decision.Restart _, _ -> failwith "expected give up past limit"
    }
    test "window expiry resets count" {
      let oldStart = now.AddMinutes(-2.0)
      let state = { RestartPolicy.emptyState with RestartCount = 3; WindowStart = Some oldStart }
      match RestartPolicy.decide policy state now with
      | RestartPolicy.Decision.Restart _, newState ->
        newState.RestartCount |> Expect.equal "reset count" 1
      | RestartPolicy.Decision.GiveUp e, _ -> failwith (sprintf "expected reset, got %A" e)
    }
    test "window boundary is strict greater-than" {
      let exactBoundary = now.AddMinutes(-1.0)
      let state = { RestartPolicy.emptyState with RestartCount = 3; WindowStart = Some exactBoundary }
      match RestartPolicy.decide policy state now with
      | RestartPolicy.Decision.GiveUp _, _ -> ()
      | RestartPolicy.Decision.Restart _, _ -> failwith "exactly at boundary should NOT reset"
    }
    test "decide increments count" {
      let state = { RestartPolicy.emptyState with RestartCount = 1; WindowStart = Some now }
      match RestartPolicy.decide policy state now with
      | RestartPolicy.Decision.Restart _, newState ->
        newState.RestartCount |> Expect.equal "incremented" 2
      | RestartPolicy.Decision.GiveUp e, _ -> failwith (sprintf "expected ok, got %A" e)
    }
  ]
]

// ═══════════════════════════════════════════════════════════
// CellGrid — rendering oracle: toText, toTextTrimmed, toTextRange
// ═══════════════════════════════════════════════════════════

let cellGridTests = testList "CellGrid" [
  testList "toText" [
    test "empty grid returns empty string" {
      let grid = CellGrid.create 0 0
      CellGrid.toText grid |> Expect.equal "empty" ""
    }
    test "known pattern produces exact string" {
      let grid = CellGrid.create 2 3
      CellGrid.set grid 0 0 { Cell.empty with Char = 'A' }
      CellGrid.set grid 0 1 { Cell.empty with Char = 'B' }
      CellGrid.set grid 0 2 { Cell.empty with Char = 'C' }
      CellGrid.set grid 1 0 { Cell.empty with Char = 'D' }
      CellGrid.set grid 1 1 { Cell.empty with Char = 'E' }
      CellGrid.set grid 1 2 { Cell.empty with Char = 'F' }
      let text = CellGrid.toText grid
      text |> Expect.equal "grid text" "ABC\r\nDEF"
    }
  ]
  testList "toTextTrimmed" [
    test "strips trailing spaces" {
      let grid = CellGrid.create 1 5
      CellGrid.set grid 0 0 { Cell.empty with Char = 'H' }
      CellGrid.set grid 0 1 { Cell.empty with Char = 'i' }
      let text = CellGrid.toTextTrimmed grid
      text |> Expect.equal "trimmed" "Hi"
    }
    test "preserves leading spaces" {
      let grid = CellGrid.create 1 4
      CellGrid.set grid 0 1 { Cell.empty with Char = 'X' }
      let text = CellGrid.toTextTrimmed grid
      text |> Expect.stringContains "has leading space" " X"
    }
    test "all-space row becomes empty" {
      let grid = CellGrid.create 1 5
      let text = CellGrid.toTextTrimmed grid
      text.Trim() |> Expect.equal "all spaces trimmed" ""
    }
  ]
  testList "toTextRange" [
    test "extracts subregion" {
      let grid = CellGrid.create 3 4
      for r in 0..2 do
        for c in 0..3 do
          CellGrid.set grid r c { Cell.empty with Char = char (65 + r * 4 + c) }
      let text = CellGrid.toTextRange grid 1 1 2 2
      text |> Expect.stringContains "has F" "F"
    }
    test "clamps OOB coordinates" {
      let grid = CellGrid.create 2 3
      CellGrid.set grid 0 0 { Cell.empty with Char = 'X' }
      let text = CellGrid.toTextRange grid 0 0 99 99
      text |> Expect.stringContains "has X" "X"
    }
    test "inverted start/end is normalized" {
      let grid = CellGrid.create 2 3
      CellGrid.set grid 0 0 { Cell.empty with Char = 'A' }
      CellGrid.set grid 1 2 { Cell.empty with Char = 'Z' }
      let text = CellGrid.toTextRange grid 1 2 0 0
      text |> Expect.stringContains "has A" "A"
    }
  ]
]

// ═══════════════════════════════════════════════════════════
// Theme — color parsing and token-to-color mapping
// ═══════════════════════════════════════════════════════════

let themeTests = testList "Theme" [
  testList "hexToRgb" [
    test "red" { Theme.hexToRgb "#FF0000" |> Expect.equal "red" 0x00FF0000u }
    test "green" { Theme.hexToRgb "#00FF00" |> Expect.equal "green" 0x0000FF00u }
    test "blue" { Theme.hexToRgb "#0000FF" |> Expect.equal "blue" 0x000000FFu }
    test "white" { Theme.hexToRgb "#FFFFFF" |> Expect.equal "white" 0x00FFFFFFu }
    test "black" { Theme.hexToRgb "#000000" |> Expect.equal "black" 0u }
    test "invalid returns 0" { Theme.hexToRgb "invalid" |> Expect.equal "fallback" 0u }
    test "short hex returns 0" { Theme.hexToRgb "#FFF" |> Expect.equal "too short" 0u }
    test "empty returns 0" { Theme.hexToRgb "" |> Expect.equal "empty" 0u }
  ]
  testList "tokenColorOfCapture" [
    let theme = Theme.defaults
    test "keyword maps to SynKeyword" {
      Theme.tokenColorOfCapture theme "keyword.control"
      |> Expect.equal "keyword" theme.SynKeyword
    }
    test "string maps to SynString" {
      Theme.tokenColorOfCapture theme "string.quoted"
      |> Expect.equal "string" theme.SynString
    }
    test "comment maps to SynComment" {
      Theme.tokenColorOfCapture theme "comment.line"
      |> Expect.equal "comment" theme.SynComment
    }
    test "variable.parameter beats generic variable" {
      Theme.tokenColorOfCapture theme "variable.parameter"
      |> Expect.equal "param" theme.SynVariable
    }
    test "variable.member maps to SynProperty" {
      Theme.tokenColorOfCapture theme "variable.member"
      |> Expect.equal "member" theme.SynProperty
    }
    test "generic variable maps to SynVariable" {
      Theme.tokenColorOfCapture theme "variable"
      |> Expect.equal "var" theme.SynVariable
    }
    test "unknown capture maps to FgDefault" {
      Theme.tokenColorOfCapture theme "totally.unknown"
      |> Expect.equal "default" theme.FgDefault
    }
    test "spell maps to FgDefault" {
      Theme.tokenColorOfCapture theme "spell"
      |> Expect.equal "spell" theme.FgDefault
    }
  ]
]

// --- Round 4: KeyCombo, UiAction, Layout, PaneId ---

let rectsOverlap (a: Rect) (b: Rect) =
  let r1 = max a.Row b.Row
  let c1 = max a.Col b.Col
  let r2 = min (a.Row + a.Height) (b.Row + b.Height)
  let c2 = min (a.Col + a.Width) (b.Col + b.Width)
  r2 > r1 && c2 > c1

let rectWithin (outer: Rect) (inner: Rect) =
  inner.Row >= outer.Row &&
  inner.Col >= outer.Col &&
  inner.Row + inner.Height <= outer.Row + outer.Height &&
  inner.Col + inner.Width <= outer.Col + outer.Width

let keyComboTests = testList "KeyCombo" [
  testList "tryParse basics" [
    test "single letter" {
      KeyCombo.tryParse "A"
      |> Expect.isSome "should parse single letter"
    }
    test "ctrl modifier" {
      let kc = KeyCombo.tryParse "Ctrl+S" |> Option.get
      kc.Modifiers.HasFlag(ConsoleModifiers.Control) |> Expect.isTrue "has Ctrl"
    }
    test "shift modifier" {
      let kc = KeyCombo.tryParse "Shift+Tab" |> Option.get
      kc.Modifiers.HasFlag(ConsoleModifiers.Shift) |> Expect.isTrue "has Shift"
    }
    test "alt modifier" {
      let kc = KeyCombo.tryParse "Alt+A" |> Option.get
      kc.Modifiers.HasFlag(ConsoleModifiers.Alt) |> Expect.isTrue "has Alt"
    }
    test "two modifiers" {
      let kc = KeyCombo.tryParse "Ctrl+Shift+A" |> Option.get
      kc.Modifiers.HasFlag(ConsoleModifiers.Control) |> Expect.isTrue "has Ctrl"
      kc.Modifiers.HasFlag(ConsoleModifiers.Shift) |> Expect.isTrue "has Shift"
    }
    test "case insensitive" {
      KeyCombo.tryParse "ctrl+s"
      |> Expect.isSome "lowercase should parse"
    }
    test "control alias" {
      let kc = KeyCombo.tryParse "Control+S" |> Option.get
      kc.Modifiers.HasFlag(ConsoleModifiers.Control) |> Expect.isTrue "has Ctrl"
    }
    test "return alias for enter" {
      let kc = KeyCombo.tryParse "Return" |> Option.get
      kc.Key |> Expect.equal "maps to Enter" ConsoleKey.Enter
    }
    test "named keys parse" {
      let cases = [
        "Enter", ConsoleKey.Enter
        "Tab", ConsoleKey.Tab
        "Escape", ConsoleKey.Escape
        "Space", ConsoleKey.Spacebar
        "Backspace", ConsoleKey.Backspace
        "Delete", ConsoleKey.Delete
        "Up", ConsoleKey.UpArrow
        "Down", ConsoleKey.DownArrow
        "Left", ConsoleKey.LeftArrow
        "Right", ConsoleKey.RightArrow
        "Home", ConsoleKey.Home
        "End", ConsoleKey.End
        "PageUp", ConsoleKey.PageUp
        "PageDown", ConsoleKey.PageDown
      ]
      for (name, expected) in cases do
        let kc = KeyCombo.tryParse name |> Option.get
        kc.Key |> Expect.equal (sprintf "%s should map" name) expected
    }
    test "digit keys parse" {
      for c in '0'..'9' do
        KeyCombo.tryParse (string c)
        |> Expect.isSome (sprintf "%c should parse" c)
    }
    test "empty string returns None" {
      KeyCombo.tryParse "" |> Expect.isNone "empty"
    }
    test "whitespace only returns None" {
      KeyCombo.tryParse "   " |> Expect.isNone "whitespace"
    }
    test "modifier only returns None" {
      KeyCombo.tryParse "Ctrl" |> Expect.isNone "just modifier"
    }
    test "unknown key returns None" {
      KeyCombo.tryParse "Ctrl+Zzzzz" |> Expect.isNone "unknown"
    }
    test "f-keys are not supported" {
      KeyCombo.tryParse "F1" |> Expect.isNone "F-keys not in parser"
    }
    test "modifier order does not matter" {
      let a = KeyCombo.tryParse "Ctrl+Shift+A" |> Option.get
      let b = KeyCombo.tryParse "Shift+Ctrl+A" |> Option.get
      a.Key |> Expect.equal "same key" b.Key
      a.Modifiers |> Expect.equal "same mods" b.Modifiers
    }
    test "whitespace around parts is trimmed" {
      KeyCombo.tryParse " Ctrl + S "
      |> Expect.isSome "trimmed"
    }
  ]
  testList "format" [
    test "single key" {
      let kc = KeyCombo.tryParse "A" |> Option.get
      KeyCombo.format kc |> Expect.equal "formats" "A"
    }
    test "ctrl+key" {
      let kc = KeyCombo.tryParse "Ctrl+S" |> Option.get
      KeyCombo.format kc |> Expect.equal "formats" "Ctrl+S"
    }
    test "shift+key" {
      let kc = KeyCombo.tryParse "Shift+Tab" |> Option.get
      KeyCombo.format kc |> Expect.equal "formats" "Shift+Tab"
    }
    test "two mods" {
      let kc = KeyCombo.tryParse "Ctrl+Shift+A" |> Option.get
      let fmt = KeyCombo.format kc
      fmt |> Expect.stringContains "has Ctrl" "Ctrl"
      fmt |> Expect.stringContains "has Shift" "Shift"
      fmt |> Expect.stringContains "has A" "A"
    }
    test "round trip for named keys" {
      let cases = ["A"; "Ctrl+S"; "Shift+Tab"; "Escape"; "Enter"; "Space"]
      for c in cases do
        let kc = KeyCombo.tryParse c |> Option.get
        let fmt = KeyCombo.format kc
        let kc2 = KeyCombo.tryParse fmt |> Option.get
        kc2.Key |> Expect.equal (sprintf "round-trip key %s" c) kc.Key
        kc2.Modifiers |> Expect.equal (sprintf "round-trip mods %s" c) kc.Modifiers
    }
    test "special key formatting" {
      let cases = [
        ConsoleKey.OemPlus, "="
        ConsoleKey.OemMinus, "-"
        ConsoleKey.UpArrow, "Up"
        ConsoleKey.DownArrow, "Down"
        ConsoleKey.Spacebar, "Space"
      ]
      for (key, expected) in cases do
        let kc = { Key = key; Modifiers = enum<ConsoleModifiers> 0; Char = None }
        KeyCombo.format kc |> Expect.equal (sprintf "%A formats" key) expected
    }
  ]
]

let uiActionParseTests = testList "UiAction.tryParse" [
  test "Quit" {
    UiAction.tryParse "Quit" |> Expect.equal "parses Quit" (Some UiAction.Quit)
  }
  test "CycleFocus" {
    UiAction.tryParse "CycleFocus" |> Expect.equal "parses" (Some UiAction.CycleFocus)
  }
  test "FocusDir maps direction" {
    UiAction.tryParse "FocusLeft" |> Expect.equal "parses" (Some (UiAction.FocusDir Direction.Left))
    UiAction.tryParse "FocusUp" |> Expect.equal "parses" (Some (UiAction.FocusDir Direction.Up))
  }
  test "ScrollUp" {
    UiAction.tryParse "ScrollUp" |> Expect.equal "parses" (Some UiAction.ScrollUp)
  }
  test "editor actions" {
    UiAction.tryParse "Submit" |> Expect.equal "Submit" (Some (UiAction.Editor EditorAction.Submit))
    UiAction.tryParse "Undo" |> Expect.equal "Undo" (Some (UiAction.Editor EditorAction.Undo))
    UiAction.tryParse "DeleteBackward" |> Expect.equal "DeleteBackward" (Some (UiAction.Editor EditorAction.DeleteBackward))
  }
  test "TogglePane with suffix" {
    UiAction.tryParse "TogglePane.Output"
    |> Expect.equal "parses" (Some (UiAction.TogglePane PaneId.Output))
  }
  test "LayoutPreset with suffix" {
    UiAction.tryParse "Layout.focus"
    |> Expect.equal "parses" (Some (UiAction.LayoutPreset "focus"))
  }
  test "ResizeH grow/shrink" {
    UiAction.tryParse "ResizeHGrow" |> Expect.equal "grow" (Some (UiAction.ResizeH 1))
    UiAction.tryParse "ResizeHShrink" |> Expect.equal "shrink" (Some (UiAction.ResizeH -1))
  }
  test "unknown returns None" {
    UiAction.tryParse "NotARealAction" |> Expect.isNone "unknown"
  }
  test "empty returns None" {
    UiAction.tryParse "" |> Expect.isNone "empty"
  }
  test "case sensitive" {
    UiAction.tryParse "quit" |> Expect.isNone "lowercase Quit fails"
  }
  test "all simple actions parse" {
    let simpleActions = [
      "Quit"; "CycleFocus"; "ScrollUp"; "ScrollDown"; "Redraw"
      "FontSizeUp"; "FontSizeDown"; "CycleTheme"
      "HotReloadWatchAll"; "HotReloadUnwatchAll"
      "EnableLiveTesting"; "DisableLiveTesting"; "CycleRunPolicy"; "ToggleCoverage"
      "TimeTravelBack"; "TimeTravelForward"; "TimeTravelGoLive"
    ]
    for a in simpleActions do
      UiAction.tryParse a
      |> Expect.isSome (sprintf "%s should parse" a)
  }
]

let keyMapParseTests = testList "KeyMap.parseConfigLines examples" [
  test "well-formed config" {
    let lines = [|
      """let keybindings = [ "Ctrl+Q", "Quit"; "Ctrl+S", "Submit" ]"""
    |]
    let result = KeyMap.parseConfigLines lines
    result.Count |> Expect.equal "two bindings" 2
  }
  test "empty config" {
    let result = KeyMap.parseConfigLines [||]
    result |> Expect.isEmpty "no bindings"
  }
  test "no keybindings section" {
    let lines = [| "let theme = \"dark\""; "let fontSize = 14" |]
    let result = KeyMap.parseConfigLines lines
    result |> Expect.isEmpty "nothing parsed"
  }
  test "invalid key skipped" {
    let lines = [| """let keybindings = [ "Zzzzz", "Quit" ]""" |]
    let result = KeyMap.parseConfigLines lines
    result |> Expect.isEmpty "invalid key"
  }
  test "invalid action skipped" {
    let lines = [| """let keybindings = [ "Ctrl+Q", "NotReal" ]""" |]
    let result = KeyMap.parseConfigLines lines
    result |> Expect.isEmpty "invalid action"
  }
  test "multi-line config" {
    let lines = [|
      """let keybindings = ["""
      """  "Ctrl+Q", "Quit" """
      """  "Ctrl+Z", "Undo" """
      """]"""
    |]
    let result = KeyMap.parseConfigLines lines
    result.Count |> Expect.equal "two bindings" 2
  }
]

let layoutComputeTests = testList "Layout" [
  testList "computeLayoutWith" [
    test "default config produces non-empty panes" {
      let panes, statusBar = Screen.computeLayoutWith LayoutConfig.defaults 40 120
      panes |> Expect.isNonEmpty "should have panes"
      statusBar.Height |> Expect.equal "status bar is 1 row" 1
      statusBar.Row |> Expect.equal "status bar on last row" 39
    }
    test "all pane rects are within content area" {
      let rows, cols = 40, 120
      let contentArea = Rect.create 0 0 cols (rows - 1)
      let panes, _ = Screen.computeLayoutWith LayoutConfig.defaults rows cols
      for (pid, rect) in panes do
        rectWithin contentArea rect
        |> Expect.isTrue (sprintf "%A should be within content area" pid)
    }
    test "no two panes overlap" {
      let panes, _ = Screen.computeLayoutWith LayoutConfig.defaults 40 120
      let rects = panes |> List.map snd
      for i in 0 .. rects.Length - 2 do
        for j in i + 1 .. rects.Length - 1 do
          rectsOverlap rects.[i] rects.[j]
          |> Expect.isFalse (sprintf "panes %d and %d should not overlap" i j)
    }
    test "focus preset shows only output + editor" {
      let panes, _ = Screen.computeLayoutWith LayoutConfig.focus 40 120
      let ids = panes |> List.map fst |> Set.ofList
      ids |> Expect.equal "output + editor" (Set.ofList [PaneId.Output; PaneId.Editor])
    }
    test "minimal preset shows only editor" {
      let panes, _ = Screen.computeLayoutWith LayoutConfig.minimal 40 120
      let ids = panes |> List.map fst |> Set.ofList
      ids |> Expect.equal "editor only" (Set.singleton PaneId.Editor)
    }
    test "all five panes visible" {
      let cfg = { LayoutConfig.defaults with VisiblePanes = Set.ofArray PaneId.all }
      let panes, _ = Screen.computeLayoutWith cfg 40 120
      let ids = panes |> List.map fst |> Set.ofList
      ids |> Expect.equal "all panes" (Set.ofArray PaneId.all)
    }
    test "right-only layout uses full content area" {
      let cfg = { LayoutConfig.defaults with VisiblePanes = Set.singleton PaneId.Sessions }
      let panes, _ = Screen.computeLayoutWith cfg 40 120
      panes |> Expect.hasLength "one pane" 1
      let _, rect = panes.Head
      rect.Width |> Expect.equal "full width" 120
      rect.Height |> Expect.equal "full content height" 39
    }
  ]
  testList "LayoutConfig.resizeH" [
    test "grow increases split" {
      let cfg2 = LayoutConfig.resizeH 1 LayoutConfig.defaults
      Expect.isGreaterThan "split grew" (cfg2.LeftRightSplit, LayoutConfig.defaults.LeftRightSplit)
    }
    test "shrink decreases split" {
      let cfg2 = LayoutConfig.resizeH -1 LayoutConfig.defaults
      Expect.isLessThan "split shrank" (cfg2.LeftRightSplit, LayoutConfig.defaults.LeftRightSplit)
    }
    test "clamps at 0.2 minimum" {
      let cfg = { LayoutConfig.defaults with LeftRightSplit = 0.2 }
      let cfg2 = LayoutConfig.resizeH -10 cfg
      Expect.isGreaterThanOrEqual "at least 0.2" (cfg2.LeftRightSplit, 0.2)
    }
    test "clamps at 0.9 maximum" {
      let cfg = { LayoutConfig.defaults with LeftRightSplit = 0.9 }
      let cfg2 = LayoutConfig.resizeH 10 cfg
      Expect.isLessThanOrEqual "at most 0.9" (cfg2.LeftRightSplit, 0.9)
    }
  ]
  testList "LayoutConfig.resizeV" [
    test "grow increases editor rows" {
      let cfg2 = LayoutConfig.resizeV 1 LayoutConfig.defaults
      Expect.isGreaterThan "grew" (cfg2.OutputEditorSplit, LayoutConfig.defaults.OutputEditorSplit)
    }
    test "clamps at 2 minimum" {
      let cfg = { LayoutConfig.defaults with OutputEditorSplit = 2 }
      let cfg2 = LayoutConfig.resizeV -10 cfg
      Expect.isGreaterThanOrEqual "at least 2" (cfg2.OutputEditorSplit, 2)
    }
  ]
  testList "togglePane" [
    test "toggle on adds pane" {
      let cfg = { LayoutConfig.defaults with VisiblePanes = Set.empty }
      let cfg2 = LayoutConfig.togglePane PaneId.Output cfg
      cfg2.VisiblePanes |> Expect.contains "has Output" PaneId.Output
    }
    test "toggle off removes pane" {
      let cfg2 = LayoutConfig.togglePane PaneId.Output LayoutConfig.defaults
      cfg2.VisiblePanes.Contains PaneId.Output |> Expect.isFalse "removed"
    }
  ]
]

let paneIdTests = testList "PaneId" [
  testList "toRegionId / fromRegionId" [
    test "round-trip all panes" {
      for p in PaneId.all do
        let regionId = PaneId.toRegionId p
        PaneId.fromRegionId regionId
        |> Expect.equal (sprintf "%A round-trips" p) (Some p)
    }
    test "unknown regionId returns None" {
      PaneId.fromRegionId "unknown" |> Expect.isNone "unknown"
    }
  ]
  testList "next" [
    test "cycles through all panes" {
      let mutable current = PaneId.Output
      for _ in 1 .. PaneId.all.Length do
        current <- PaneId.next current
      current |> Expect.equal "wraps to Output" PaneId.Output
    }
    test "every pane has a next" {
      for p in PaneId.all do
        let n = PaneId.next p
        PaneId.all |> Expect.contains (sprintf "%A.next is valid" p) n
    }
  ]
  testList "nextVisible" [
    test "cycles within visible set" {
      let visible = Set.ofList [PaneId.Output; PaneId.Editor]
      PaneId.nextVisible visible PaneId.Output
      |> Expect.equal "next visible" PaneId.Editor
      PaneId.nextVisible visible PaneId.Editor
      |> Expect.equal "wraps" PaneId.Output
    }
    test "returns current if visible is empty" {
      PaneId.nextVisible Set.empty PaneId.Output
      |> Expect.equal "stays" PaneId.Output
    }
    test "returns first visible if current not in set" {
      let visible = Set.ofList [PaneId.Sessions; PaneId.Diagnostics]
      PaneId.nextVisible visible PaneId.Output
      |> Expect.equal "jumps to first visible" PaneId.Sessions
    }
  ]
  testList "navigate" [
    test "navigate right from left column" {
      let panes, _ = Screen.computeLayoutWith LayoutConfig.defaults 40 120
      let result = PaneId.navigate Direction.Right PaneId.Output panes
      result |> Expect.notEqual "moves right" PaneId.Output
    }
    test "navigate left from right column" {
      let panes, _ = Screen.computeLayoutWith LayoutConfig.defaults 40 120
      let result = PaneId.navigate Direction.Left PaneId.Sessions panes
      result |> Expect.equal "moves to Output" PaneId.Output
    }
    test "navigate returns current when no neighbor" {
      let panes = [(PaneId.Output, Rect.create 0 0 120 39)]
      PaneId.navigate Direction.Right PaneId.Output panes
      |> Expect.equal "stays" PaneId.Output
    }
    test "navigate returns current when pane not in layout" {
      PaneId.navigate Direction.Right PaneId.Output []
      |> Expect.equal "stays" PaneId.Output
    }
  ]
  testList "firstVisible" [
    test "returns first in canonical order" {
      let visible = Set.ofList [PaneId.Editor; PaneId.Sessions; PaneId.Output]
      PaneId.firstVisible visible
      |> Expect.equal "Output is first canonical" PaneId.Output
    }
    test "defaults to Output for empty set" {
      PaneId.firstVisible Set.empty
      |> Expect.equal "defaults" PaneId.Output
    }
  ]
]

// ═══════════════════════════════════════════════════════════
// SessionManager — computeStandbyInfo state machine
// ═══════════════════════════════════════════════════════════

let private mkStandbyKey wd name =
  { StandbyKey.Projects = [name]; WorkingDir = wd; AutoOpenNamespaces = true }

let private mkStandbySession state progress =
  { StandbySession.Process = Unchecked.defaultof<_>
    Proxy = None
    BaseUrl = ""
    State = state
    WarmupProgress = progress
    Projects = ["test"]
    WorkingDir = "C:\\test"
    CreatedAt = DateTime.UtcNow }

let computeStandbyInfoTests = testList "computeStandbyInfo" [
  test "disabled pool returns NoPool" {
    let pool = { PoolState.Standbys = Map.empty; Enabled = false }
    computeStandbyInfo pool |> Expect.equal "nopool" StandbyInfo.NoPool
  }
  test "enabled empty standbys returns NoPool" {
    let pool = { PoolState.Standbys = Map.empty; Enabled = true }
    computeStandbyInfo pool |> Expect.equal "nopool" StandbyInfo.NoPool
  }
  test "all Ready returns Ready" {
    let pool =
      { PoolState.Standbys =
          Map.ofList [
            mkStandbyKey "C:\\a" "p1", mkStandbySession StandbyState.Ready None
            mkStandbyKey "C:\\b" "p2", mkStandbySession StandbyState.Ready None ]
        Enabled = true }
    computeStandbyInfo pool |> Expect.equal "ready" StandbyInfo.Ready
  }
  test "any Invalidated returns Invalidated" {
    let pool =
      { PoolState.Standbys =
          Map.ofList [
            mkStandbyKey "C:\\a" "p1", mkStandbySession StandbyState.Ready None
            mkStandbyKey "C:\\b" "p2", mkStandbySession StandbyState.Invalidated None ]
        Enabled = true }
    computeStandbyInfo pool |> Expect.equal "invalidated" StandbyInfo.Invalidated
  }
  test "Warming with progress returns Warming msg" {
    let pool =
      { PoolState.Standbys =
          Map.ofList [
            mkStandbyKey "C:\\a" "p1", mkStandbySession StandbyState.Warming (Some "loading refs") ]
        Enabled = true }
    match computeStandbyInfo pool with
    | StandbyInfo.Warming msg -> msg |> Expect.stringContains "has loading" "loading"
    | other -> failwithf "Expected Warming, got %A" other
  }
  test "Warming without progress returns Warming" {
    let pool =
      { PoolState.Standbys =
          Map.ofList [
            mkStandbyKey "C:\\a" "p1", mkStandbySession StandbyState.Warming None ]
        Enabled = true }
    match computeStandbyInfo pool with
    | StandbyInfo.Warming _ -> ()
    | other -> failwithf "Expected Warming, got %A" other
  }
  test "Invalidated takes priority over Warming" {
    let pool =
      { PoolState.Standbys =
          Map.ofList [
            mkStandbyKey "C:\\a" "p1", mkStandbySession StandbyState.Warming (Some "x")
            mkStandbyKey "C:\\b" "p2", mkStandbySession StandbyState.Invalidated None ]
        Enabled = true }
    computeStandbyInfo pool |> Expect.equal "invalidated wins" StandbyInfo.Invalidated
  }
  test "Invalidated takes priority over Ready" {
    let pool =
      { PoolState.Standbys =
          Map.ofList [
            mkStandbyKey "C:\\a" "p1", mkStandbySession StandbyState.Ready None
            mkStandbyKey "C:\\b" "p2", mkStandbySession StandbyState.Invalidated None
            mkStandbyKey "C:\\c" "p3", mkStandbySession StandbyState.Ready None ]
        Enabled = true }
    computeStandbyInfo pool |> Expect.equal "invalidated wins" StandbyInfo.Invalidated
  }
]

// ═══════════════════════════════════════════════════════════
// ManagerState — empty state safety
// ═══════════════════════════════════════════════════════════

let managerStateTests = testList "ManagerState" [
  test "empty has no sessions" {
    ManagerState.empty.Sessions |> Expect.isEmpty "no sessions"
  }
  test "empty tryGetSession returns None" {
    ManagerState.empty |> ManagerState.tryGetSession (SageFs.WorkerProtocol.SessionId.newId())
    |> Expect.isNone "no session"
  }
  test "removeSession on empty is safe" {
    let result = ManagerState.empty |> ManagerState.removeSession (SageFs.WorkerProtocol.SessionId.newId())
    result.Sessions |> Expect.isEmpty "still empty"
  }
  test "empty pool is enabled by default" {
    ManagerState.empty.Pool.Enabled |> Expect.isTrue "enabled"
  }
  test "empty pool has no standbys" {
    ManagerState.empty.Pool.Standbys |> Expect.isEmpty "no standbys"
  }
]

// ═══════════════════════════════════════════════════════════
// StandbyInfo.label — display string for each state
// ═══════════════════════════════════════════════════════════

let standbyInfoLabelTests = testList "StandbyInfo.label" [
  test "NoPool is empty" {
    StandbyInfo.label StandbyInfo.NoPool |> Expect.equal "empty" ""
  }
  test "Ready shows checkmark" {
    StandbyInfo.label StandbyInfo.Ready |> Expect.stringContains "has check" "✓"
  }
  test "Invalidated shows warning" {
    StandbyInfo.label StandbyInfo.Invalidated |> Expect.stringContains "has warning" "⚠"
  }
  test "Warming with message shows phase" {
    StandbyInfo.label (StandbyInfo.Warming "loading") |> Expect.stringContains "has phase" "loading"
  }
  test "Warming with empty message shows standby" {
    StandbyInfo.label (StandbyInfo.Warming "") |> Expect.stringContains "has standby" "standby"
  }
]

// ═══════════════════════════════════════════════════════════
// GutterRender — gutter width, lookup, and icon colors
// ═══════════════════════════════════════════════════════════

let gutterRenderTests = testList "GutterRender" [
  test "gutterWidth is 0 for empty annotations" {
    GutterRender.gutterWidth [||] |> Expect.equal "zero" 0
  }
  test "gutterWidth is 2 for non-empty annotations" {
    let ann = [| { LineAnnotation.Line = 1; Icon = GutterIcon.TestPassed; Tooltip = "" } |]
    GutterRender.gutterWidth ann |> Expect.equal "two" 2
  }
  test "buildLookup maps line to annotation" {
    let ann = [|
      { LineAnnotation.Line = 5; Icon = GutterIcon.TestPassed; Tooltip = "pass" }
      { LineAnnotation.Line = 10; Icon = GutterIcon.TestFailed; Tooltip = "fail" }
    |]
    let lookup = GutterRender.buildLookup ann
    lookup.Count |> Expect.equal "two entries" 2
    lookup.[5].Icon |> Expect.equal "line 5" GutterIcon.TestPassed
    lookup.[10].Icon |> Expect.equal "line 10" GutterIcon.TestFailed
  }
  test "buildLookup last wins for duplicate lines" {
    let ann = [|
      { LineAnnotation.Line = 1; Icon = GutterIcon.TestPassed; Tooltip = "first" }
      { LineAnnotation.Line = 1; Icon = GutterIcon.TestFailed; Tooltip = "second" }
    |]
    let lookup = GutterRender.buildLookup ann
    lookup.[1].Icon |> Expect.equal "last wins" GutterIcon.TestFailed
  }
  test "buildLookup empty input gives empty map" {
    GutterRender.buildLookup [||] |> Expect.isEmpty "empty"
  }
  test "iconFgColor maps each icon to a color byte" {
    let theme = Theme.defaults
    let icons = [
      GutterIcon.TestPassed; GutterIcon.TestFailed; GutterIcon.TestDiscovered
      GutterIcon.TestRunning; GutterIcon.TestSkipped; GutterIcon.TestFlaky
      GutterIcon.Covered; GutterIcon.NotCovered
    ]
    for icon in icons do
      let color = GutterRender.iconFgColor theme icon
      (int color, 0) |> Expect.isGreaterThanOrEqual "non-negative color"
  }
  test "TestPassed and Covered share same color" {
    let theme = Theme.defaults
    GutterRender.iconFgColor theme GutterIcon.Covered
    |> Expect.equal "same success color" (GutterRender.iconFgColor theme GutterIcon.TestPassed)
  }
  test "TestFailed uses different color than TestPassed" {
    let theme = Theme.defaults
    GutterRender.iconFgColor theme GutterIcon.TestFailed
    |> Expect.notEqual "different colors" (GutterRender.iconFgColor theme GutterIcon.TestPassed)
  }
]

// ═══════════════════════════════════════════════════════════
// McpAdapter — file classification and JSON formatting
// ═══════════════════════════════════════════════════════════

let mcpAdapterPureTests = testList "McpAdapter pure" [
  testList "isSolutionFile" [
    test "sln" { McpAdapter.isSolutionFile "foo.sln" |> Expect.isTrue ".sln" }
    test "slnx" { McpAdapter.isSolutionFile "foo.slnx" |> Expect.isTrue ".slnx" }
    test "fsproj is not" { McpAdapter.isSolutionFile "foo.fsproj" |> Expect.isFalse "not sln" }
    test "empty" { McpAdapter.isSolutionFile "" |> Expect.isFalse "empty" }
    test "case sensitive" { McpAdapter.isSolutionFile "foo.SLN" |> Expect.isFalse "case matters" }
  ]
  testList "isProjectFile" [
    test "fsproj" { McpAdapter.isProjectFile "foo.fsproj" |> Expect.isTrue ".fsproj" }
    test "sln is not" { McpAdapter.isProjectFile "foo.sln" |> Expect.isFalse "not proj" }
    test "csproj is not" { McpAdapter.isProjectFile "foo.csproj" |> Expect.isFalse "not csproj" }
    test "case sensitive" { McpAdapter.isProjectFile "foo.FSPROJ" |> Expect.isFalse "case matters" }
  ]
  testList "formatAvailableProjects" [
    test "lists projects and solutions" {
      let result = McpAdapter.formatAvailableProjects "C:\\test" [|"a.fsproj"|] [|"b.sln"|]
      result |> Expect.stringContains "has project" "a.fsproj"
      result |> Expect.stringContains "has solution" "b.sln"
      result |> Expect.stringContains "has dir" "C:\\test"
    }
    test "empty arrays show none found" {
      let result = McpAdapter.formatAvailableProjects "C:\\test" [||] [||]
      result |> Expect.stringContains "no projects" "(none found)"
    }
    test "multiple projects listed" {
      let result = McpAdapter.formatAvailableProjects "C:\\test" [|"a.fsproj";"b.fsproj"|] [||]
      result |> Expect.stringContains "has a" "a.fsproj"
      result |> Expect.stringContains "has b" "b.fsproj"
    }
  ]
  testList "formatDiagnosticsResultJson" [
    test "empty diagnostics produces valid JSON" {
      let json = McpAdapter.formatDiagnosticsResultJson [||]
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("count").GetInt32() |> Expect.equal "count" 0
    }
    test "single diagnostic has required JSON fields" {
      let diag = {
        Diagnostic.Message = "test error"
        Severity = DiagnosticSeverity.Error
        Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 10 }
        Subcategory = ""
      }
      let json = McpAdapter.formatDiagnosticsResultJson [| diag |]
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("count").GetInt32() |> Expect.equal "count" 1
      let first = doc.RootElement.GetProperty("diagnostics").EnumerateArray() |> Seq.head
      first.GetProperty("message").GetString() |> Expect.equal "msg" "test error"
      first.GetProperty("severity").GetString() |> Expect.equal "sev" "error"
    }
    test "count matches array length" {
      let mkDiag msg = {
        Diagnostic.Message = msg
        Severity = DiagnosticSeverity.Warning
        Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 5 }
        Subcategory = ""
      }
      let json = McpAdapter.formatDiagnosticsResultJson [| mkDiag "a"; mkDiag "b"; mkDiag "c" |]
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("count").GetInt32() |> Expect.equal "count" 3
    }
  ]
]

// ═══════════════════════════════════════════════════════════
// Watchdog — daemon supervision state machine
// ═══════════════════════════════════════════════════════════

let private mkWatchdogConfig maxRestarts (grace: float) =
  { Watchdog.Config.CheckInterval = TimeSpan.FromSeconds(5.0: float)
    RestartPolicy =
      { Policy.MaxRestarts = maxRestarts
        BackoffBase = TimeSpan.FromSeconds(1.0: float)
        BackoffMax = TimeSpan.FromSeconds(30.0: float)
        ResetWindow = TimeSpan.FromMinutes(5.0: float) }
    GracePeriod = TimeSpan.FromSeconds(grace) }

let private mkWatchdogState pid lastStarted =
  { Watchdog.State.DaemonPid = pid
    RestartState = { RestartPolicy.State.RestartCount = 0; LastRestartAt = None; WindowStart = None }
    LastStartedAt = lastStarted
    WatchdogStartedAt = DateTime(2025,1,1) }

let watchdogDecideTests = testList "Watchdog.decide" [
  test "Running daemon returns Wait" {
    let cfg = mkWatchdogConfig 3 10.0
    let st = mkWatchdogState (Some 1234) (Some (DateTime(2025,1,1)))
    let now = DateTime(2025,1,1,0,1,0)
    let action, _ = Watchdog.decide cfg st Watchdog.DaemonStatus.Running now
    action |> Expect.equal "wait" Watchdog.Action.Wait
  }
  test "NotRunning with no PID returns StartDaemon" {
    let cfg = mkWatchdogConfig 3 10.0
    let st = mkWatchdogState None None
    let now = DateTime(2025,1,1,0,1,0)
    let action, _ = Watchdog.decide cfg st Watchdog.DaemonStatus.NotRunning now
    action |> Expect.equal "start" Watchdog.Action.StartDaemon
  }
  test "NotRunning within grace period returns Wait" {
    let cfg = mkWatchdogConfig 3 10.0
    let now = DateTime(2025,1,1,0,1,0)
    let st = mkWatchdogState (Some 1234) (Some (now - TimeSpan.FromSeconds(3.0: float)))
    let action, _ = Watchdog.decide cfg st Watchdog.DaemonStatus.NotRunning now
    action |> Expect.equal "wait during grace" Watchdog.Action.Wait
  }
  test "NotRunning after grace period returns RestartDaemon" {
    let cfg = mkWatchdogConfig 3 10.0
    let now = DateTime(2025,1,1,0,1,0)
    let st = mkWatchdogState (Some 1234) (Some (now - TimeSpan.FromSeconds(20.0: float)))
    let action, _ = Watchdog.decide cfg st Watchdog.DaemonStatus.NotRunning now
    match action with
    | Watchdog.Action.RestartDaemon delay ->
      (delay.TotalSeconds, 0.0) |> Expect.isGreaterThan "positive delay"
    | other -> failwithf "Expected RestartDaemon, got %A" other
  }
  test "Max restarts reached returns GiveUp" {
    let cfg = mkWatchdogConfig 3 10.0
    let now = DateTime(2025,1,1,0,1,0)
    let st =
      { mkWatchdogState (Some 1234) (Some (now - TimeSpan.FromSeconds(20.0: float))) with
          RestartState = { RestartCount = 3; LastRestartAt = Some now; WindowStart = Some now } }
    let action, _ = Watchdog.decide cfg st Watchdog.DaemonStatus.NotRunning now
    match action with
    | Watchdog.Action.GiveUp _ -> ()
    | other -> failwithf "Expected GiveUp, got %A" other
  }
  test "Unknown status returns Wait" {
    let cfg = mkWatchdogConfig 3 10.0
    let st = mkWatchdogState (Some 1234) (Some (DateTime(2025,1,1)))
    let now = DateTime(2025,1,1,0,1,0)
    let action, _ = Watchdog.decide cfg st Watchdog.DaemonStatus.Unknown now
    action |> Expect.equal "wait on unknown" Watchdog.Action.Wait
  }
  test "Restart increments restart count" {
    let cfg = mkWatchdogConfig 5 10.0
    let now = DateTime(2025,1,1,0,1,0)
    let st = mkWatchdogState (Some 1234) (Some (now - TimeSpan.FromSeconds(20.0: float)))
    let _, newState = Watchdog.decide cfg st Watchdog.DaemonStatus.NotRunning now
    (newState.RestartState.RestartCount, 0) |> Expect.isGreaterThan "count incremented"
  }
  test "StartDaemon on no PID keeps no PID in state" {
    let cfg = mkWatchdogConfig 3 10.0
    let st = mkWatchdogState None None
    let now = DateTime(2025,1,1,0,1,0)
    let _, newState = Watchdog.decide cfg st Watchdog.DaemonStatus.NotRunning now
    newState.DaemonPid |> Expect.isNone "no PID yet after start decision"
  }
]

// ═══════════════════════════════════════════════════════════
// SageFsError.describe — error contract documentation
// ═══════════════════════════════════════════════════════════

let sageFsErrorDescribeTests = testList "SageFsError.describe" [
  test "ToolNotAvailable mentions tool, state, and alternatives" {
    let msg = SageFsError.describe (SageFsError.ToolNotAvailable("eval", SessionState.WarmingUp, ["status";"cancel"]))
    msg |> Expect.stringContains "has tool" "eval"
    msg |> Expect.stringContains "has alternatives" "status, cancel"
  }
  test "SessionNotFound includes session id" {
    SageFsError.describe (SageFsError.SessionNotFound "abc123")
    |> Expect.stringContains "has id" "abc123"
  }
  test "NoActiveSessions gives actionable message" {
    SageFsError.describe SageFsError.NoActiveSessions
    |> Expect.stringContains "suggests create" "create_session"
  }
  test "AmbiguousSessions lists descriptions" {
    SageFsError.describe (SageFsError.AmbiguousSessions ["session1"; "session2"])
    |> Expect.stringContains "has listing" "session1"
  }
  test "SessionCreationFailed includes reason" {
    SageFsError.describe (SageFsError.SessionCreationFailed "out of memory")
    |> Expect.stringContains "has reason" "out of memory"
  }
  test "SessionStopFailed includes id and reason" {
    let msg = SageFsError.describe (SageFsError.SessionStopFailed("xyz", "timeout"))
    msg |> Expect.stringContains "has id" "xyz"
    msg |> Expect.stringContains "has reason" "timeout"
  }
  test "WorkerCommunicationFailed includes id and reason" {
    let msg = SageFsError.describe (SageFsError.WorkerCommunicationFailed("s1", "pipe broken"))
    msg |> Expect.stringContains "has id" "s1"
    msg |> Expect.stringContains "has reason" "pipe broken"
  }
  test "WorkerSpawnFailed includes reason" {
    SageFsError.describe (SageFsError.WorkerSpawnFailed "no dotnet")
    |> Expect.stringContains "has reason" "no dotnet"
  }
  test "PipeClosed gives fixed message" {
    SageFsError.describe SageFsError.PipeClosed
    |> Expect.stringContains "mentions pipe" "Pipe closed"
  }
  test "EvalFailed includes reason" {
    SageFsError.describe (SageFsError.EvalFailed "type error")
    |> Expect.stringContains "has reason" "type error"
  }
  test "ResetFailed includes reason" {
    SageFsError.describe (SageFsError.ResetFailed "locked")
    |> Expect.stringContains "has reason" "locked"
  }
  test "HardResetFailed includes reason" {
    SageFsError.describe (SageFsError.HardResetFailed "build error")
    |> Expect.stringContains "has reason" "build error"
  }
  test "ScriptLoadFailed includes reason" {
    SageFsError.describe (SageFsError.ScriptLoadFailed "file not found")
    |> Expect.stringContains "has reason" "file not found"
  }
  test "WarmupOpenFailed includes name and reason" {
    let msg = SageFsError.describe (SageFsError.WarmupOpenFailed("MyModule", "ambiguous"))
    msg |> Expect.stringContains "has name" "MyModule"
    msg |> Expect.stringContains "has reason" "ambiguous"
  }
  test "RestartLimitExceeded includes count and window" {
    let msg = SageFsError.describe (SageFsError.RestartLimitExceeded(5, 10.0))
    msg |> Expect.stringContains "has count" "5"
    msg |> Expect.stringContains "has window" "10"
  }
  test "DaemonStartFailed includes reason" {
    SageFsError.describe (SageFsError.DaemonStartFailed "port in use")
    |> Expect.stringContains "has reason" "port in use"
  }
  test "Unexpected includes exception message" {
    SageFsError.describe (SageFsError.Unexpected (exn "kaboom"))
    |> Expect.stringContains "has message" "kaboom"
  }
]

// ═══════════════════════════════════════════════════════════
// ErrorMessages.getSuggestion — user-facing error advice
// ═══════════════════════════════════════════════════════════

let errorMessagesSuggestionTests = testList "ErrorMessages.getSuggestion" [
  test "earlier error gives session-not-corrupted advice" {
    let msg = ErrorMessages.categorize "due to earlier error" |> ErrorMessages.getSuggestion
    msg |> Expect.stringContains "earlier error tip" "earlier error"
    msg |> Expect.stringContains "no reset" "NOT corrupted"
  }
  test "name error gives namespace tip" {
    let msg = ErrorMessages.categorize "Foo is not defined or not found" |> ErrorMessages.getSuggestion
    msg |> Expect.stringContains "name tip" "namespace"
  }
  test "type error gives type mismatch tip" {
    let msg = ErrorMessages.categorize "type mismatch between int and string" |> ErrorMessages.getSuggestion
    msg |> Expect.stringContains "type tip" "Type mismatch"
  }
  test "syntax error gives syntax tip" {
    let msg = ErrorMessages.categorize "unexpected token in expression" |> ErrorMessages.getSuggestion
    msg |> Expect.stringContains "syntax tip" "Syntax"
  }
  test "generic error gives break-it-down tip" {
    let msg = ErrorMessages.categorize "something went wrong" |> ErrorMessages.getSuggestion
    msg |> Expect.stringContains "generic tip" "smaller pieces"
  }
]

// ═══════════════════════════════════════════════════════════
// EditorUpdate — string surgery edge cases
// ═══════════════════════════════════════════════════════════

let private mkBuf (text: string) =
  let lines = text.Split('\n') |> Array.toList
  match ValidatedBuffer.create lines { Line = 0; Column = 0 } with
  | Ok vb -> vb
  | Error e -> failwithf "Failed to create buffer: %A" e

let private mkBufAt (text: string) line col =
  let lines = text.Split('\n') |> Array.toList
  match ValidatedBuffer.create lines { Line = line; Column = col } with
  | Ok vb -> vb
  | Error e -> failwithf "Failed to create buffer: %A" e

let editorSurgeryTests = testList "EditorUpdate string surgery" [
  testList "deleteForward" [
    test "deletes char at cursor" {
      let buf = mkBufAt "hello" 0 1
      let result = EditorUpdate.deleteForward buf
      ValidatedBuffer.text result |> Expect.equal "removed e" "hllo"
    }
    test "at end of line joins with next" {
      let buf = mkBufAt "hello\nworld" 0 5
      let result = EditorUpdate.deleteForward buf
      ValidatedBuffer.lines result |> List.length |> Expect.equal "1 line" 1
      ValidatedBuffer.text result |> Expect.equal "joined" "helloworld"
    }
    test "at end of last line does nothing" {
      let buf = mkBufAt "hello" 0 5
      let result = EditorUpdate.deleteForward buf
      ValidatedBuffer.text result |> Expect.equal "unchanged" "hello"
    }
    test "at col 0 deletes first char" {
      let buf = mkBufAt "hello" 0 0
      let result = EditorUpdate.deleteForward buf
      ValidatedBuffer.text result |> Expect.equal "removed h" "ello"
    }
    test "cursor stays at same position" {
      let buf = mkBufAt "hello" 0 2
      let result = EditorUpdate.deleteForward buf
      (ValidatedBuffer.cursor result).Column |> Expect.equal "col 2" 2
    }
  ]
  testList "DeleteToEndOfLine" [
    test "deletes from cursor to end" {
      let buf = mkBufAt "hello world" 0 5
      let st = { EditorState.initial with Buffer = buf }
      let st', _ = EditorUpdate.update EditorAction.DeleteToEndOfLine st
      ValidatedBuffer.text st'.Buffer |> Expect.equal "truncated" "hello"
    }
    test "at col 0 clears entire line" {
      let buf = mkBufAt "hello" 0 0
      let st = { EditorState.initial with Buffer = buf }
      let st', _ = EditorUpdate.update EditorAction.DeleteToEndOfLine st
      ValidatedBuffer.text st'.Buffer |> Expect.equal "empty" ""
    }
    test "at end of line does nothing" {
      let buf = mkBufAt "hello" 0 5
      let st = { EditorState.initial with Buffer = buf }
      let st', _ = EditorUpdate.update EditorAction.DeleteToEndOfLine st
      ValidatedBuffer.text st'.Buffer |> Expect.equal "unchanged" "hello"
    }
    test "preserves other lines" {
      let buf = mkBufAt "line1\nline2\nline3" 1 3
      let st = { EditorState.initial with Buffer = buf }
      let st', _ = EditorUpdate.update EditorAction.DeleteToEndOfLine st
      ValidatedBuffer.lines st'.Buffer |> List.length |> Expect.equal "still 3 lines" 3
      (ValidatedBuffer.lines st'.Buffer).[1] |> Expect.equal "truncated line2" "lin"
    }
  ]
  testList "AcceptCompletion" [
    test "no menu does nothing" {
      let buf = mkBufAt "hel" 0 3
      let st = { EditorState.initial with Buffer = buf; CompletionMenu = None }
      let st', _ = EditorUpdate.update EditorAction.AcceptCompletion st
      ValidatedBuffer.text st'.Buffer |> Expect.equal "unchanged" "hel"
      st'.CompletionMenu |> Expect.isNone "menu cleared"
    }
    test "replaces filter text with completion label" {
      let buf = mkBufAt "hel" 0 3
      let menu =
        { CompletionMenu.Items = [ { CompletionItem.Label = "hello"; Detail = None; Kind = "" } ]
          SelectedIndex = 0
          FilterText = "hel" }
      let st = { EditorState.initial with Buffer = buf; CompletionMenu = Some menu }
      let st', _ = EditorUpdate.update EditorAction.AcceptCompletion st
      ValidatedBuffer.text st'.Buffer |> Expect.equal "completed" "hello"
      st'.CompletionMenu |> Expect.isNone "menu cleared"
    }
    test "cursor advances past completion" {
      let buf = mkBufAt "hel" 0 3
      let menu =
        { CompletionMenu.Items = [ { CompletionItem.Label = "hello"; Detail = None; Kind = "" } ]
          SelectedIndex = 0
          FilterText = "hel" }
      let st = { EditorState.initial with Buffer = buf; CompletionMenu = Some menu }
      let st', _ = EditorUpdate.update EditorAction.AcceptCompletion st
      (ValidatedBuffer.cursor st'.Buffer).Column |> Expect.equal "at end of hello" 5
    }
    test "preserves text after cursor" {
      let buf = mkBufAt "hel world" 0 3
      let menu =
        { CompletionMenu.Items = [ { CompletionItem.Label = "hello"; Detail = None; Kind = "" } ]
          SelectedIndex = 0
          FilterText = "hel" }
      let st = { EditorState.initial with Buffer = buf; CompletionMenu = Some menu }
      let st', _ = EditorUpdate.update EditorAction.AcceptCompletion st
      ValidatedBuffer.text st'.Buffer |> Expect.equal "completed with suffix" "hello world"
    }
  ]
  testList "moveToLineStart" [
    test "moves cursor to column 0" {
      let buf = mkBufAt "hello" 0 3
      let result = EditorUpdate.moveToLineStart buf
      (ValidatedBuffer.cursor result).Column |> Expect.equal "col 0" 0
    }
  ]
  testList "moveToLineEnd" [
    test "moves cursor to end of line" {
      let buf = mkBufAt "hello" 0 2
      let result = EditorUpdate.moveToLineEnd buf
      (ValidatedBuffer.cursor result).Column |> Expect.equal "col 5" 5
    }
  ]
]

// ═══════════════════════════════════════════════════════════
// SseWriter — SSE protocol compliance
// ═══════════════════════════════════════════════════════════

let sseWriterFormatTests = testList "SseWriter format" [
  test "single line event has event: and data: fields" {
    let result = SseWriter.formatSseEvent "status" "ready"
    result |> Expect.equal "SSE format" "event: status\ndata: ready\n\n"
  }
  test "multiline data splits into multiple data: lines" {
    let result = SseWriter.formatSseEvent "output" "line1\nline2"
    result |> Expect.stringContains "first data line" "data: line1"
    result |> Expect.stringContains "second data line" "data: line2"
    result |> Expect.stringContains "has event" "event: output"
  }
  test "formatSseEventMultiline with empty list has no data lines" {
    let result = SseWriter.formatSseEventMultiline "ping" []
    result |> Expect.equal "event only" "event: ping\n\n"
  }
  test "formatSseEventMultiline with multiple lines" {
    let result = SseWriter.formatSseEventMultiline "data" ["a";"b";"c"]
    result |> Expect.stringContains "data a" "data: a"
    result |> Expect.stringContains "data b" "data: b"
    result |> Expect.stringContains "data c" "data: c"
  }
  test "event ends with double newline" {
    let result = SseWriter.formatSseEvent "test" "data"
    result.EndsWith("\n\n") |> Expect.isTrue "double newline terminator"
  }
]

[<Tests>]
let allPureModulesTests = testList "Pure modules comprehensive" [
  testList "HotReloading" [
    isTopLevelFunctionBindingTests
    isStaticMemberFunctionTests
  ]
  testList "AutoCompletion" [
    completionKindOfGlyphTests
  ]
  testList "AppState" [
    stripAnsiTests
    reformatExpectoSummaryTests
  ]
  testList "Behavioral contracts" [
    testList "stripAnsi" [
      stripAnsiIdempotent
      stripAnsiNoCompleteCsiSurvives
      stripAnsiNoOscSurvives
      stripAnsiIdentityOnPlainText
    ]
    testList "cleanStdout" [
      cleanStdoutIdempotent
      cleanStdoutNoAnsi
    ]
    testList "isTopLevelFunctionBinding" [
      topLevelRejectsIndented
      modifiersDontAffectClassification
    ]
    testList "injectNoInlining" [
      injectPreservesNonFunctionLines
      injectAttributeCountMatchesFunctionCount
    ]
    testList "shouldExcludeFile" [
      emptyPatternsExcludeNothing
      excludeIsCaseInsensitive
      excludeNormalizesSlashes
    ]
    testList "fileChangeAction" [
      deletedFilesAlwaysIgnored
      fileChangeActionEscalation
    ]
    testList "shouldTriggerRebuild" [
      tempFilesNeverTriggerRebuild
      exclusionPatternsHonored
      onlyConfiguredExtensionsTrigger
    ]
    testList "scoreCandidate" [
      prefixMatchBeatsFuzzy
      shorterCandidateScoresHigher
    ]
    testList "reformatExpectoSummary" [
      reformatPassthroughOnNonExpecto
    ]
  ]
  testList "Expert-guided priority tests" [
    testList "escapeJson" [
      escapeJsonProducesValidJson
      escapeJsonRoundtripPreservesContent
      escapeJsonControlCharExamples
    ]
    testList "rewriteInlineUseStatements" [
      rewriteInlineUseStatementTests
    ]
    testList "splitStatements" [
      splitStatementsGapTests
    ]
    testList "parseConfigLines" [
      parseConfigLinesTests
      parseConfigLinesNeverCrashes
    ]
    testList "formatEventsJson" [
      formatEventsJsonTests
    ]
    testList "formatCompletionsJson" [
      formatCompletionsJsonTests
    ]
  ]
  testList "FrameDiff and Replay tests" [
    testList "FrameDiff" [
      parsePositionedLinesTests
      frameDiffExampleTests
      frameDiffProperties
    ]
    testList "Replay" [
      sessionReplayTests
      daemonReplayTests
      replayStreamIntegrationTests
    ]
  ]
  testList "ValidatedBuffer, Rect, RestartPolicy, CellGrid, Theme" [
    validatedBufferTests
    rectTests
    restartPolicyTests
    cellGridTests
    themeTests
  ]
  testList "KeyCombo, UiAction, Layout, PaneId" [
    keyComboTests
    uiActionParseTests
    keyMapParseTests
    layoutComputeTests
    paneIdTests
  ]
  testList "SessionManager, StandbyPool, GutterRender, McpAdapter" [
    computeStandbyInfoTests
    managerStateTests
    standbyInfoLabelTests
    gutterRenderTests
    mcpAdapterPureTests
    watchdogDecideTests
    sageFsErrorDescribeTests
    errorMessagesSuggestionTests
    editorSurgeryTests
    sseWriterFormatTests
  ]
]
