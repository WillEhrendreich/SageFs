/// Property tests for `Cadence.keys` (demo-gif-plan.md §4.3, §9): all
/// delays positive, deterministic by seed, one `(Key, Delay)` per input
/// character, and the specific "longer after punctuation / think before an
/// identifier" shaping the plan calls out.
module SageFs.Demos.Tests.CadenceTests

open Expecto
open Expecto.Flip
open SageFs.Demos.Domain

/// Restricts an arbitrary string to printable ASCII so generated text always
/// contains plannable characters (no surrogate pairs, control chars, etc.),
/// while still exercising the punctuation/identifier cases below.
let private sanitize (raw: string) : string =
  raw
  |> Seq.map (fun c -> if c >= ' ' && c <= '~' then c else 'x')
  |> Seq.toArray
  |> System.String

let private mkSeed (raw: int) : Seed = Seed(uint64 (abs (int64 raw)))

let private delayMs (Delay ms) = ms

[<Tests>]
let tests =
  testList "Cadence" [

    testProperty "one (Key, Delay) per input character"
    <| fun (raw: string) (seedRaw: int) ->
      let text = Text.mk (sanitize raw)
      let result = SageFs.Demos.Cadence.keys text (mkSeed seedRaw)
      result.Length = (sanitize raw).Length

    testProperty "all delays are strictly positive"
    <| fun (raw: string) (seedRaw: int) ->
      let text = Text.mk (sanitize raw)
      let result = SageFs.Demos.Cadence.keys text (mkSeed seedRaw)
      result |> List.forall (fun (_, delay) -> delayMs delay > 0)

    testProperty "deterministic by seed"
    <| fun (raw: string) (seedRaw: int) ->
      let text = Text.mk (sanitize raw)
      let seed = mkSeed seedRaw
      let a = SageFs.Demos.Cadence.keys text seed
      let b = SageFs.Demos.Cadence.keys text seed
      a = b

    testCase "an empty string produces no keys" <| fun _ ->
      SageFs.Demos.Cadence.keys (Text.mk "") (Seed 1UL)
      |> Expect.isEmpty "no characters, no keys"

    testCase "median delay is in the ballpark of 55ms for plain lowercase text" <| fun _ ->
      // A long run of plain lowercase letters (no punctuation/identifier
      // "think" triggers apply to interior chars beyond the first) should
      // land its median close to the documented 55ms (§9), not e.g. 550ms
      // or 5.5ms — a coarse sanity check on the log-normal's shape, not an
      // exact-value assertion (§9 says "median ~55ms").
      let text = Text.mk (String.replicate 400 "e")
      let delays =
        SageFs.Demos.Cadence.keys text (Seed 7UL)
        |> List.map (fun (_, d) -> delayMs d)
        |> List.sort
      let median = delays.[delays.Length / 2]
      (median, 20) |> Expect.isGreaterThanOrEqual "median delay is not absurdly small"
      (median, 200) |> Expect.isLessThanOrEqual "median delay is not absurdly large"

    testCase "a char right after a period gets a longer delay than plain text (§4.3)" <| fun _ ->
      let plain = SageFs.Demos.Cadence.keys (Text.mk "ee") (Seed 3UL) |> List.map (snd >> delayMs)
      let afterDot = SageFs.Demos.Cadence.keys (Text.mk ".e") (Seed 3UL) |> List.map (snd >> delayMs)
      // Compare the delay of the *second* character in each case: plain
      // "e" following "e" vs. "e" following ".".
      (afterDot.[1], plain.[1]) |> Expect.isGreaterThan "delay after '.' is longer than plain"

    testCase "a char starting an identifier after a space gets a 'think' delay (§4.3)" <| fun _ ->
      let plain = SageFs.Demos.Cadence.keys (Text.mk "  ") (Seed 5UL) |> List.map (snd >> delayMs)
      let beforeIdentifier = SageFs.Demos.Cadence.keys (Text.mk " x") (Seed 5UL) |> List.map (snd >> delayMs)
      (beforeIdentifier.[1], plain.[1]) |> Expect.isGreaterThan "think delay before an identifier is longer than plain"
  ]
