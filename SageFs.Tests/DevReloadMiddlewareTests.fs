module SageFs.Tests.DevReloadMiddlewareTests

open Expecto
open Expecto.Flip
open SageFs.DevReloadMiddleware

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

// ── Combined ────────────────────────────────────────────────────────────

[<Tests>]
let devReloadMiddlewareTests = testList "DevReloadMiddleware" [
  cspNonceTests
  parseEncodingTests
]
