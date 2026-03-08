module SageFs.Tests.DevReloadMiddlewareTests

open Expecto
open Expecto.Flip
open SageFs.DevReloadMiddleware
open SageFs.DevReload

// ── CSP Nonce Injection ─────────────────────────────────────────────────

let cspNonceTests = testList "CSP nonce injection" [
  test "generateNonce produces base64 of 16 bytes (24 chars)" {
    let nonce = generateNonce()
    nonce.Length |> Expect.equal "base64 of 16 bytes" 24
  }
  test "generateNonce produces unique values" {
    let n1 = generateNonce()
    let n2 = generateNonce()
    n1 |> Expect.notEqual "should be unique" n2
  }
  test "injectScriptNonce adds nonce attribute to script tag" {
    let script = "<script data-sagefs-injected=\"devreload\">console.log('hi');</script>"
    let result = injectScriptNonce "abc123" script
    result.Contains("nonce=\"abc123\"")
    |> Expect.isTrue "should contain nonce attribute"
  }
  test "injectScriptNonce preserves existing attributes" {
    let script = "<script data-sagefs-injected=\"devreload\">content</script>"
    let result = injectScriptNonce "test" script
    result.Contains("data-sagefs-injected")
    |> Expect.isTrue "should keep data attribute"
  }
  test "addNonceToCsp adds nonce to existing script-src" {
    let csp = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self'"
    let result = addNonceToCsp "abc123" csp
    result.Contains("'nonce-abc123'")
    |> Expect.isTrue "should contain nonce in script-src"
    result.Contains("script-src 'nonce-abc123' 'self'")
    |> Expect.isTrue "should be inside script-src directive"
  }
  test "addNonceToCsp adds script-src when only default-src exists" {
    let csp = "default-src 'self'"
    let result = addNonceToCsp "xyz789" csp
    result.Contains("script-src 'nonce-xyz789'")
    |> Expect.isTrue "should add script-src directive with nonce"
  }
  test "addNonceToCsp handles empty CSP" {
    let result = addNonceToCsp "test" ""
    result.Contains("script-src 'nonce-test'")
    |> Expect.isTrue "should add script-src to empty CSP"
  }
  test "addNonceToCsp preserves other directives" {
    let csp = "default-src 'self'; style-src 'unsafe-inline'; script-src 'self'"
    let result = addNonceToCsp "n1" csp
    result.Contains("style-src 'unsafe-inline'")
    |> Expect.isTrue "should preserve style-src"
    result.Contains("default-src 'self'")
    |> Expect.isTrue "should preserve default-src"
  }

  // W3: regression tests — substring replace must not corrupt script-src-elem/script-src-attr
  test "addNonceToCsp does not corrupt script-src-elem directive" {
    let csp = "default-src 'self'; script-src 'self'; script-src-elem 'self'"
    let result = addNonceToCsp "abc123" csp
    result.Contains("script-src-elem 'nonce-abc123'")
    |> Expect.isFalse "nonce must NOT be injected into script-src-elem"
    result.Contains("'nonce-abc123'")
    |> Expect.isTrue "nonce must still appear in script-src"
  }

  test "addNonceToCsp does not corrupt script-src-attr directive" {
    let csp = "default-src 'self'; script-src 'self'; script-src-attr 'none'"
    let result = addNonceToCsp "test" csp
    result.Contains("script-src-attr 'nonce-test'")
    |> Expect.isFalse "nonce must NOT be injected into script-src-attr"
    result.Contains("script-src-attr 'none'")
    |> Expect.isTrue "script-src-attr value must be unchanged"
  }

  test "addNonceToCsp handles all three script-src directives together" {
    let csp = "default-src 'self'; script-src 'self'; script-src-elem 'self'; script-src-attr 'none'"
    let result = addNonceToCsp "n99" csp
    result |> Expect.stringContains "nonce in script-src" "script-src 'nonce-n99' 'self'"
    (result.Contains("script-src-elem 'nonce-n99'"))
    |> Expect.isFalse "script-src-elem unchanged"
    (result.Contains("script-src-attr 'nonce-n99'"))
    |> Expect.isFalse "script-src-attr unchanged"
  }
]

// ── Encoding Parsing ────────────────────────────────────────────────────

let parseEncodingTests = testList "parseEncoding" [
  test "null content-type returns UTF8" {
    let enc = parseEncoding null
    enc.WebName |> Expect.equal "should be utf-8" "utf-8"
  }
  test "empty content-type returns UTF8" {
    let enc = parseEncoding ""
    enc.WebName |> Expect.equal "should be utf-8" "utf-8"
  }
  test "text/html without charset returns UTF8" {
    let enc = parseEncoding "text/html"
    enc.WebName |> Expect.equal "should be utf-8" "utf-8"
  }
  test "text/html with iso-8859-1 charset" {
    let enc = parseEncoding "text/html; charset=iso-8859-1"
    enc.WebName |> Expect.equal "should be iso-8859-1" "iso-8859-1"
  }
  test "text/html with utf-8 charset" {
    let enc = parseEncoding "text/html; charset=utf-8"
    enc.WebName |> Expect.equal "should be utf-8" "utf-8"
  }
  test "bogus charset falls back to UTF8" {
    let enc = parseEncoding "text/html; charset=not-a-real-encoding"
    enc.WebName |> Expect.equal "should fallback to utf-8" "utf-8"
  }
]

// ── Embedded JS Resource ────────────────────────────────────────────────

let embeddedJsTests = testList "Embedded JS resource" [
  test "reloadScript contains EventSource with same-origin URL when port 0" {
    let script = reloadScript 0
    script |> Expect.stringContains "should have same-origin SSE URL" "new EventSource('/__sagefs__/reload')"
  }
  test "reloadScript contains EventSource with cross-origin URL when port > 0" {
    let script = reloadScript 5050
    script |> Expect.stringContains "should have cross-origin SSE URL" "new EventSource('http://127.0.0.1:5050/__sagefs__/reload')"
  }
  test "reloadScript wraps JS in script tag" {
    let script = reloadScript 0
    script |> Expect.stringContains "should have opening script tag" """<script data-sagefs-injected="devreload">"""
    script |> Expect.stringContains "should have closing script tag" "</script>"
  }
  test "reloadScript templates config values from DevReloadConfig.defaults" {
    let script = reloadScript 0
    // Verify config values are templated (no raw placeholders remain)
    script.Contains("{{SSE_URL}}") |> Expect.isFalse "SSE_URL placeholder should be replaced"
    script.Contains("{{RELOAD_GUARD_THRESHOLD}}") |> Expect.isFalse "threshold placeholder should be replaced"
    script.Contains("{{RELOAD_RESET_WINDOW_MS}}") |> Expect.isFalse "reset window placeholder should be replaced"
    script.Contains("{{SSE_TIMEOUT_MS}}") |> Expect.isFalse "timeout placeholder should be replaced"
    script.Contains("{{COMPILE_TIMER_MS}}") |> Expect.isFalse "timer placeholder should be replaced"
  }
  test "reloadScript contains default config numeric values" {
    let script = reloadScript 0
    let cfg = SageFs.DevReload.DevReloadConfig.defaults
    script |> Expect.stringContains "should have reload guard threshold" (sprintf "reloadCount > %d" cfg.ReloadGuardThreshold)
    script |> Expect.stringContains "should have reset window" (sprintf "}, %d)" cfg.ReloadCountResetWindowMs)
    script |> Expect.stringContains "should have connection timeout" (sprintf "}, %d)" cfg.SseConnectionTimeoutMs)
  }
  test "reloadScript contains IIFE wrapper" {
    let script = reloadScript 0
    script |> Expect.stringContains "should have IIFE start" "(function(){"
    script |> Expect.stringContains "should have IIFE end" "})();"
  }
  test "reloadScript contains error panel renderer" {
    let script = reloadScript 0
    script |> Expect.stringContains "should have renderErrorPanel" "renderErrorPanel"
    script |> Expect.stringContains "should have sf-diag class" "sf-diag"
  }
  test "reloadScript contains form state preservation" {
    let script = reloadScript 0
    script |> Expect.stringContains "should have saveFormState" "saveFormState"
    script |> Expect.stringContains "should have restoreFormState" "restoreFormState"
  }
]

let uxFeatureTests = testList "UX feature tests" [
  test "P0-1: WCAG AA contrast — new green, no old green" {
    let s = reloadScript 0
    s |> Expect.stringContains "WCAG green" "#15803d"
    (s.Contains("#16a34a")) |> Expect.isFalse "old green removed"
  }
  test "P0-2: ARIA attributes on overlay" {
    let s = reloadScript 0
    s |> Expect.stringContains "role attr" "setAttribute('role'"
    s |> Expect.stringContains "aria-live" "aria-live"
  }
  test "P0-3: Smart auto-reload with threshold" {
    let s = reloadScript 0
    s |> Expect.stringContains "threshold var" "autoReloadThresholdMs"
    s |> Expect.stringContains "click to reload" "click to reload"
  }
  test "P1-1: Clickable editor links via editorUrlPattern" {
    let s = reloadScript 0
    s |> Expect.stringContains "editor URL" "editorUrlPattern"
  }
  test "P1-2: Source context rendering" {
    let s = reloadScript 0
    s |> Expect.stringContains "sf-src class" "sf-src"
    s |> Expect.stringContains "SourceContext" "SourceContext"
  }
  test "P1-3: Warning banner on successful reload" {
    let s = reloadScript 0
    s |> Expect.stringContains "warnings" "msg.warnings"
  }
  test "P1-4: Escape key dismisses panel" {
    let s = reloadScript 0
    s |> Expect.stringContains "Escape key" "Escape"
  }
  test "P1-5: Tab title updated on failure" {
    let s = reloadScript 0
    s |> Expect.stringContains "document.title" "document.title"
  }
  test "P1-6: Long compile visual escalation" {
    let s = reloadScript 0
    s |> Expect.stringContains "long compile" "longCompileWarningMs"
  }
  test "P2-1: Consecutive failure counter" {
    let s = reloadScript 0
    s |> Expect.stringContains "failureCount" "failureCount"
    s |> Expect.stringContains "attempt display" "attempt #"
  }
  test "P2-2: Focus trap — tabindex on close button" {
    let s = reloadScript 0
    s |> Expect.stringContains "tabindex" "tabindex"
  }
  test "P2-3: Slide-in animation" {
    let s = reloadScript 0
    s |> Expect.stringContains "slide animation" "sagefs-slide"
  }
  test "Config defaults include new fields" {
    let cfg = DevReloadConfig.defaults
    cfg.AutoReloadThresholdMs |> Expect.equal "auto-reload" 3000
    cfg.LongCompileWarningMs |> Expect.equal "long compile" 5000
  }
  test "Diagnostic SourceContext fields default to None" {
    let diag : DevReloadDiagnostic = {
      File = "t.fs"; Line = 1; Column = 1; EndLine = 1; EndColumn = 1
      Severity = "Error"; DiagCode = None; Message = "m"
      SourceContext = None; SourceContextStartLine = None
    }
    diag.SourceContext |> Expect.isNone "ctx"
    diag.SourceContextStartLine |> Expect.isNone "ctxLine"
  }
  test "addSourceContext enriches from real file" {
    let tmp = System.IO.Path.GetTempFileName()
    try
      System.IO.File.WriteAllLines(tmp, [| "a"; "b"; "c"; "d"; "e" |])
      let diag = {
        File = tmp; Line = 3; Column = 1; EndLine = 3; EndColumn = 1
        Severity = "Error"; DiagCode = None; Message = "m"
        SourceContext = None; SourceContextStartLine = None
      }
      let enriched = DevReloadDiagnostic.addSourceContext diag
      enriched.SourceContext |> Expect.isSome "has context"
      enriched.SourceContext.Value |> Expect.hasLength "±1 = 3" 3
      enriched.SourceContextStartLine.Value |> Expect.equal "starts at 2" 2
    finally
      System.IO.File.Delete(tmp)
  }
  test "addSourceContext returns original for missing file" {
    let diag = {
      File = @"C:\no\such\file.fs"; Line = 1; Column = 1; EndLine = 1; EndColumn = 1
      Severity = "Error"; DiagCode = None; Message = "m"
      SourceContext = None; SourceContextStartLine = None
    }
    let result = DevReloadDiagnostic.addSourceContext diag
    result.SourceContext |> Expect.isNone "stays None"
  }
  test "editorUrlPattern default returns vscode scheme" {
    let p = editorUrlPattern ()
    p |> Expect.stringContains "vscode" "vscode://file/"
  }
  test "No raw template placeholders in output" {
    let s = reloadScript 0
    (s.Contains("{{")) |> Expect.isFalse "no raw {{}}"
  }
  // W5: defense-in-depth JS escaping
  test "jsStringEscape escapes backslash" {
    jsStringEscape @"C:\path\file.fs" |> Expect.stringContains "escapes backslash" @"C:\\path\\file.fs"
  }
  test "jsStringEscape escapes double quote" {
    jsStringEscape "say \"hi\"" |> Expect.stringContains "escapes dquote" "say \\\"hi\\\""
  }
  test "jsStringEscape prevents </script> injection via < escape" {
    let malicious = "</script><script>alert(1)"
    let escaped = jsStringEscape malicious
    escaped.Contains("</script>") |> Expect.isFalse "must not contain </script>"
    escaped |> Expect.stringContains "contains escaped <" "\\x3C"
  }
  test "reloadScript does not inject raw < characters from editorUrlPattern" {
    let s = reloadScript 0
    // The outer </script> is the expected wrapper closing tag.
    // Check that the JS body (content inside the script tags) has no </script> injection.
    let scriptOpen = """<script data-sagefs-injected="devreload">"""
    let jsBody = s.[scriptOpen.Length .. s.LastIndexOf("</script>") - 1]
    jsBody.Contains("</script>") |> Expect.isFalse "JS body must not contain closing script tag"
  }
]

// ── Combined ────────────────────────────────────────────────────────────

[<Tests>]
let devReloadMiddlewareTests = testList "DevReloadMiddleware" [
  cspNonceTests
  parseEncodingTests
  embeddedJsTests
  uxFeatureTests
]
