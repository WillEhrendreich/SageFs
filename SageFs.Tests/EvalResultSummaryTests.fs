/// RED-then-GREEN for the "huge eval result" half of the eval-actor fix
/// (roast-8): printing something like a `ProjectOptions` with no display
/// truncation configured produced a single ~90,000-character result — a
/// wall of text with no real way to see more of it. `EvalResultSummary.bound`
/// is the fix: cap what comes back, say honestly what was cut, and point at
/// a drill-in that actually works (`it`, the value FSI already bound).
module SageFs.Tests.EvalResultSummaryTests

open Expecto
open Expecto.Flip
open SageFs.Features.EvalResultSummary

[<Tests>]
let evalResultSummaryTests =
  testList "EvalResultSummary.bound" [

    testCase "WHY — a result under the cap comes back untouched, because most evals never need summarising" <| fun _ ->
      let raw = "val it: int = 2"
      let result = bound maxResultChars raw
      result.Text |> Expect.equal "text is exactly the raw string" raw
      result.WasTruncated |> Expect.isFalse "not truncated"
      result.OriginalLength |> Expect.equal "original length is the raw length" raw.Length
      result.KeptLength |> Expect.equal "kept length equals the raw length when nothing was cut" raw.Length

    testCase "WHY — a result exactly at the cap is not truncated, because the cap is inclusive" <| fun _ ->
      let raw = String.replicate 100 "x"
      let result = bound 100 raw
      result.WasTruncated |> Expect.isFalse "exactly-at-cap is not truncated"
      result.Text |> Expect.equal "text is untouched" raw

    testCase "WHY — a ~90,000-char printed value (the ProjectOptions repro) comes back bounded, not as a wall of text" <| fun _ ->
      let raw = String.replicate 90_000 "a"
      let result = bound maxResultChars raw
      result.WasTruncated |> Expect.isTrue "truncated"
      result.OriginalLength |> Expect.equal "original length is reported honestly" 90_000
      result.KeptLength |> Expect.equal "kept length is the configured cap" maxResultChars
      (result.Text.Length > maxResultChars) |> Expect.isTrue "the notice adds some bytes on top of the kept prefix"
      result.Text.StartsWith(String.replicate maxResultChars "a") |> Expect.isTrue "the kept prefix is the head of the original, not a summary of it"

    testCase "WHY — the notice states the real size and the kept size, not just \"truncated\"" <| fun _ ->
      let raw = String.replicate 5_000 "b"
      let result = bound 4_000 raw
      result.Text.Contains("5000") |> Expect.isTrue "the original length appears in the notice"
      result.Text.Contains("4000") |> Expect.isTrue "the kept length appears in the notice"

    testCase "WHY — the drill-in points at `it`, the value FSI actually bound, not generic advice" <| fun _ ->
      let raw = String.replicate 10_000 "c"
      let result = bound 4_000 raw
      result.Text.Contains("it") |> Expect.isTrue "the notice names the real FSI binding the caller can act on"

    testCase "WHY — maxChars <= 0 never throws, because this runs on the eval reply path" <| fun _ ->
      let raw = "some result"
      let result = bound 0 raw
      result.WasTruncated |> Expect.isTrue "zero-cap truncates everything"
      result.KeptLength |> Expect.equal "kept length floors at zero" 0
      let negative = bound -5 raw
      negative.KeptLength |> Expect.equal "a negative cap is floored to zero, not treated as unlimited" 0

    testProperty "WHY — OriginalLength always equals the input length, whatever the cap" <| fun (raw: string) (cap: int) ->
      let raw = raw |> Option.ofObj |> Option.defaultValue ""
      let result = bound cap raw
      result.OriginalLength = raw.Length

    testProperty "WHY — Text always starts with the kept prefix, so truncation never rewrites what it does return" <| fun (raw: string) (cap: int) ->
      let raw = raw |> Option.ofObj |> Option.defaultValue ""
      let cap = max 0 cap
      let result = bound cap raw
      let expectedPrefix = raw.Substring(0, min cap raw.Length)
      result.Text.StartsWith(expectedPrefix, System.StringComparison.Ordinal)

    testProperty "WHY — WasTruncated is exactly \"the input was longer than the cap\"" <| fun (raw: string) (cap: int) ->
      let raw = raw |> Option.ofObj |> Option.defaultValue ""
      let cap = max 0 cap
      let result = bound cap raw
      result.WasTruncated = (raw.Length > cap)
  ]
