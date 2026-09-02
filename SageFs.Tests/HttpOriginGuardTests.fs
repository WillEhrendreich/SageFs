module SageFs.Tests.HttpOriginGuardTests

open Expecto
open Expecto.Flip
open SageFs.Server

let private decide = HttpOriginGuard.decide
let private Allow = HttpOriginGuard.Verdict.Allow

let private reject reason = HttpOriginGuard.Verdict.Reject reason

[<Tests>]
let gateTests =
  testList "HttpOriginGuard" [

    testCase "plain local tooling (no headers) passes" <| fun _ ->
      decide None None None
      |> Expect.equal "curl/CLI/editor with no browser headers must pass" Allow

    testCase "loopback Host with no Origin passes" <| fun _ ->
      decide (Some "localhost:37749") None None
      |> Expect.equal "local client sending Host must pass" Allow
      decide (Some "127.0.0.1:37749") None None
      |> Expect.equal "127.0.0.1 Host must pass" Allow

    testCase "same-origin browser request passes" <| fun _ ->
      decide (Some "localhost:37750") (Some "same-origin") (Some "http://localhost:37750")
      |> Expect.equal "dashboard's own page fetches must pass" Allow

    testCase "DNS-rebinding attack (cross-site fetch to localhost) is rejected" <| fun _ ->
      decide (Some "localhost:37749") (Some "cross-site") (Some "http://evil.example.com")
      |> Expect.equal "cross-site Sec-Fetch-Site must be rejected" (reject "cross-site Sec-Fetch-Site cross-site")

    testCase "cross-origin page posting to loopback is rejected by Origin" <| fun _ ->
      // Browsers without Sec-Fetch-Site (older) still send Origin on POST.
      decide (Some "localhost:37749") None (Some "http://evil.example.com")
      |> Expect.equal "non-loopback Origin must be rejected" (reject "non-loopback Origin http://evil.example.com")

    testCase "null Origin (sandboxed iframe) is rejected" <| fun _ ->
      decide (Some "localhost:37749") (Some "cross-site") (Some "null")
      |> Expect.equal "null Origin must be rejected" (reject "cross-site Sec-Fetch-Site cross-site")

    testCase "non-loopback Host header is rejected (rebinding via hostname)" <| fun _ ->
      decide (Some "sagefs.evil.com:37749") None None
      |> Expect.equal "non-loopback Host must be rejected" (reject "non-loopback Host sagefs.evil.com:37749")

    testCase "loopback Origin with port passes" <| fun _ ->
      decide (Some "127.0.0.1:37749") (Some "same-origin") (Some "http://127.0.0.1:37749")
      |> Expect.equal "loopback origin must pass" Allow

    testCase "same-site (sibling localhost subdomain) passes" <| fun _ ->
      decide (Some "localhost:37750") (Some "same-site") None
      |> Expect.equal "same-site is not a remote-page attack" Allow
  ]
