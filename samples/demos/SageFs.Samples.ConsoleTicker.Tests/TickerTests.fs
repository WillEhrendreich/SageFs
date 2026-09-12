module SageFs.Samples.ConsoleTicker.Tests.TickerTests

open System
open Expecto
open Expecto.Flip
open SageFs.Samples.ConsoleTicker.Ticker

[<Tests>]
let tests =
  testList "Ticker" [

    testCase "renderLine formats a fixed count and timestamp exactly" <| fun _ ->
      let now = DateTime(2026, 9, 12, 8, 30, 5, 700)
      renderLine 3 now
      |> Expect.equal "should format as HH:mm:ss.f  #n  message" "08:30:05.7  #3  SageFs keeps ticking"

    // This pins the CURRENT message literal from Ticker.fs's `renderLine` body
    // as its own segment of the line (everything after the second "  "). If a
    // future edit changes that literal — the exact edit SageFs's source
    // hot-reload is meant to patch live — this assertion must change with it,
    // which is the proof that the constant actually reaches the printed
    // output rather than being dead code.
    testCase "the message constant is the tail of the rendered line" <| fun _ ->
      let now = DateTime(2026, 9, 12, 8, 30, 5, 700)
      let line = renderLine 3 now
      let messageSegment = line.Split([| "  " |], StringSplitOptions.None) |> Array.last
      messageSegment
      |> Expect.equal "message segment should match the literal in renderLine's body" "SageFs keeps ticking"

    testCase "parseIntervalMs falls back to the default when unset" <| fun _ ->
      parseIntervalMs None
      |> Expect.equal "should be the documented default" DefaultIntervalMs

    testCase "parseIntervalMs falls back to the default for garbage input" <| fun _ ->
      parseIntervalMs (Some "not-a-number")
      |> Expect.equal "should be the documented default" DefaultIntervalMs

    testCase "parseIntervalMs falls back to the default for non-positive input" <| fun _ ->
      parseIntervalMs (Some "0")
      |> Expect.equal "should be the documented default" DefaultIntervalMs

    testCase "parseIntervalMs honors a valid override" <| fun _ ->
      parseIntervalMs (Some "250")
      |> Expect.equal "should use the parsed value" 250
  ]
