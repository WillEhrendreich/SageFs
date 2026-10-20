/// How the pipeline learns how many cases the whole host tier registers: the listing process prints the
/// number, and the pipeline reads that line back.
///
/// WHY THIS IS NOT A COUNT OF LINES. The pipeline used to count the listing's lines that start with a
/// kind marker (`N `, `P `, `F `). Expecto prints that marker only when the tree carries a focus state
/// (a focused, pending or skipped test), and the skip-removal wave took the last pending test out of
/// the host suite, so the listing changed shape, the count became 0, and the trust report read that as
/// "the host suites register 0 cases" and called it a coverage gap: a suite was dropped. Nothing was
/// dropped. A count that depends on how a dependency prints its output is not a count, so the process
/// that knows the number prints it.
///
/// Pure, so the pipeline script and the test assembly read the same marker. Loaded by ci-pipeline.fsx
/// (`#load`) and compiled into SageFs.Tests.
module SageFs.Build.HostListing

/// The prefix of the line the listing process prints with the case count: `<marker> <count>`.
[<Literal>]
let marker = "SAGEFS LISTED CASES:"

/// The number of cases the listing reported, or None when the log has no marker line: the listing did
/// not run, or it ran a build that does not print one. None is "we could not tell", which is not the
/// same as 0, and the trust report says which one it is.
let countOf (lines: string seq) : int option =
  lines
  |> Seq.tryFind (fun line -> line.StartsWith(marker, System.StringComparison.Ordinal))
  |> Option.bind (fun line ->
    match System.Int32.TryParse (line.Substring(marker.Length).Trim()) with
    | true, count -> Some count
    | _ -> None)
