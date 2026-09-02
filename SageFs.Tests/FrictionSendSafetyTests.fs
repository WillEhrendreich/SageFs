module SageFs.Tests.FrictionSendSafetyTests

/// Phase 4 P0 safety tests (quality-gap plan):
/// - Dashboard send destination must be restricted (no SSRF/proxy via an
///   arbitrary client-supplied endpoint; no plaintext token over http).
/// - The F# sanitizer must redact secrets from free text.

open Expecto
open Expecto.Flip
open SageFs.Server.Dashboard
open SageFs.Features.FrictionSanitize

[<Tests>]
let frictionSendSafetyTests =
  testList "Friction send safety" [

    testCase "https endpoints are allowed" <| fun _ ->
      isAllowedFrictionEndpoint "https://sagefs-reports.example.workers.dev/ingest"
      |> Expect.isTrue "https worker endpoint should be allowed"

    testCase "loopback http endpoints are allowed (local receiver dev)" <| fun _ ->
      isAllowedFrictionEndpoint "http://127.0.0.1:8787/ingest"
      |> Expect.isTrue "loopback http should be allowed for wrangler dev"
      isAllowedFrictionEndpoint "http://localhost:8787/ingest"
      |> Expect.isTrue "localhost http should be allowed for wrangler dev"

    testCase "non-loopback http endpoints are rejected (token would cross in plaintext)" <| fun _ ->
      isAllowedFrictionEndpoint "http://sagefs-reports.example.workers.dev/ingest"
      |> Expect.isFalse "plaintext http to a remote host must be rejected"

    testCase "file and other schemes are rejected (no SSRF primitive)" <| fun _ ->
      isAllowedFrictionEndpoint "file:///etc/passwd"
      |> Expect.isFalse "file scheme must be rejected"
      isAllowedFrictionEndpoint "ftp://example.com/x"
      |> Expect.isFalse "ftp scheme must be rejected"
      isAllowedFrictionEndpoint "gopher://example.com/x"
      |> Expect.isFalse "gopher scheme must be rejected"

    testCase "malformed endpoints are rejected" <| fun _ ->
      isAllowedFrictionEndpoint ""
      |> Expect.isFalse "empty endpoint must be rejected"
      isAllowedFrictionEndpoint "not a url"
      |> Expect.isFalse "non-URL string must be rejected"
      isAllowedFrictionEndpoint "https://"
      |> Expect.isFalse "scheme-only URL must be rejected"
  ]

/// F# sanitizer properties — the plan requires shared sanitizer parity and
/// property coverage (previously only the TypeScript side was tested).
[<Tests>]
let frictionSanitizerPropertyTests =
  testList "FrictionSanitize properties" [

    testCase "sanitizeText removes Windows drive paths" <| fun _ ->
      // The redactor keeps the drive letter prefix: C:\Users\... → C:\<path>
      sanitizeText @"error in C:\Users\Will\code\proj\src\Lib.fs happened" MaxTextLen
      |> Expect.equal "drive path should be redacted" @"error in C:\<path> happened"

    testCase "sanitizeText removes UNC and Unix paths" <| fun _ ->
      sanitizeText @"\\server\share\file.fs failed" MaxTextLen
      |> Expect.equal "UNC path should be redacted" @"\\<path> failed"
      sanitizeText "read /home/alice/proj/src/App.fs" MaxTextLen
      |> Expect.equal "unix path should be redacted" "read /<path>"

    testCase "sanitizeText removes IP addresses" <| fun _ ->
      sanitizeText "connected to 192.168.1.50:8080" MaxTextLen
      |> Expect.equal "ipv4 should be redacted" "connected to <ip>:8080"

    testCase "sanitizeText removes emails and session ids" <| fun _ ->
      sanitizeText "contact dev@example.com sid a1b2c3d4e5f6a7b8" MaxTextLen
      |> Expect.equal "email and session id should be redacted" "contact <email> sid <session-id>"

    testCase "sanitizeText never exceeds the length cap" <| fun _ ->
      let long = System.String('x', 1000)
      let result = sanitizeText long MaxTextLen
      (result.Length, MaxTextLen)
      |> Expect.isLessThanOrEqual "sanitized text must respect the cap"

    testCase "sanitizeText is total on arbitrary input (no exceptions, bounded output)" <| fun _ ->
      // Property-style sweep over awkward inputs — the sanitizer must never
      // throw and must always return bounded output.
      let awkward =
        [ null
          ""
          "   "
          @"C:\a\b\c"
          @"\\unc\share\x"
          "mailto:user@example.com"
          "http://127.0.0.1:8080/x"
          "\u0000control\u0001chars"
          (System.String('é', 500)) ]
      for input in awkward do
        let result = sanitizeText input MaxTextLen
        (result = null)
        |> Expect.isFalse "sanitizeText must never return null"
        (result.Length, MaxTextLen)
        |> Expect.isLessThanOrEqual "sanitized output must be bounded"
  ]
