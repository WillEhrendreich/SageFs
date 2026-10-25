module SageFs.Tests.SampleReadmeTests

/// The READMEs under samples/ say what each sample is for, in words. A count typed into prose ("162 tests",
/// "22 exercises") is a claim nothing checks: the sample gains a case and the page keeps saying the old number.
/// Nothing here derives a count, so no README states one.

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

/// A number directly before the noun that counts samples' content: "162 tests", "22 progressive exercises".
let countClaim = Regex(@"\b\d+\s+(?:\w+\s+)?(?:tests?|exercises?|koans?)\b", RegexOptions.IgnoreCase)

let countClaimsIn (line: string) : string list =
  countClaim.Matches line |> Seq.map (fun m -> m.Value) |> List.ofSeq

let sampleReadmes () : string list =
  let samples = RepoPaths.repoPathFull [| "samples" |]
  Directory.EnumerateFiles(samples, "README.md", SearchOption.AllDirectories)
  |> Seq.filter (fun path ->
    let normal = path.Replace('\\', '/')
    not (normal.Contains "/bin/") && not (normal.Contains "/obj/") && not (normal.Contains "/node_modules/"))
  |> List.ofSeq

[<Tests>]
let tests =
  testList "Sample READMEs state no counts" [

    testCase "WHY — the matcher finds the count shapes the samples' pages used to carry, and leaves ordinary numbers alone" <| fun () ->
      countClaimsIn "# Run the F# Koans (22 exercises, 162 tests):" |> Expect.equal "both counts" [ "22 exercises"; "162 tests" ]
      countClaimsIn "from-csharp/SageFs.Samples.FromCSharp/          11 tests" |> Expect.equal "a column of counts" [ "11 tests" ]
      countClaimsIn "22 progressive exercises teaching F# from scratch." |> Expect.equal "an adjective between" [ "22 progressive exercises" ]
      countClaimsIn "EXPECTO! 162 tests run" |> Expect.equal "the runner's own line" [ "162 tests" ]
      countClaimsIn "net11.0 as of 2026-10-01, in 2-space files, 0.7 s after the edit" |> Expect.isEmpty "versions, dates and timings are not counts"
      countClaimsIn "Each bridge shows F# equivalents, as tests you can run." |> Expect.isEmpty "the word alone is fine"

    testCase "WHY — every README under samples/ describes its sample without a typed test or exercise count" <| fun () ->
      let readmes = sampleReadmes ()
      readmes |> Expect.isNonEmpty "the samples have READMEs to check"
      let found =
        [ for path in readmes do
            for line in File.ReadAllLines path do
              for claim in countClaimsIn line do
                yield sprintf "%s: %s" (Path.GetRelativePath(RepoPaths.repoPathFull [||], path)) claim ]
      found |> Expect.isEmpty "a count in a sample README drifts; say what the sample is for instead"
  ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant tests |> ignore
