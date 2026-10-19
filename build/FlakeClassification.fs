/// What a red case does when it is run again on its own, so the person reading the gate starts the investigation
/// at the right place.
///
/// On 2026-10-18 ten gate passes failed in ten different places, every failed case passed when it was run alone, and
/// each one was investigated by hand because a red could not be told from a flake. When a tier goes red, the gate
/// runs ONLY the failing case again, `defaultReruns` times, in the same checkout, and this module turns the outcomes
/// into a closed verdict: `Deterministic` (it failed every time), `Flaky` (some of the time) or `NotReproduced` (it
/// passed every time).
///
/// A verdict never turns a red into a green. A flaky case is a defect the gate does not get to wave through (rerunning
/// until it passes proves nothing), so the pass still fails; the verdict only says which kind of red it is.
///
/// A rerun that ran nothing says nothing about the case. A filter that matches nothing prints `0 failed` and exits 0,
/// which is why `outcomeOf` reads the TRUST line and calls `ran=0` an invalid rerun, never a pass. An invalid rerun makes
/// the case `Unclassifiable`: the honest answer when the evidence is missing.
///
/// Pure, so the pipeline script and the tests share it. Loaded by ci-pipeline.fsx (`#load`).
module SageFs.Build.FlakeClassification

open System.Text.RegularExpressions

/// How many times a failing case is run again on its own.
let defaultReruns = 3

/// How many failing cases of one gate pass are rerun. Each rerun starts the whole test assembly, so a pass that failed
/// in dozens of places is classified for the first few and the rest are named.
let maxCasesClassified = 5

/// What one rerun of the case, alone, came to.
type RerunOutcome =
  | RerunPassed
  | RerunFailed
  /// The rerun cannot be counted either way: it ran nothing, only skipped the case, or died before reporting.
  | RerunInvalid of reason: string

/// The closed set of things a red case can turn out to be.
type Verdict =
  | Deterministic of runs: int
  | Flaky of failed: int * runs: int
  | NotReproduced of runs: int

/// Why no verdict could be given.
type Unclassifiable =
  | NoReruns
  | InvalidRerun of rerun: int * reason: string

/// The verdict for a list of rerun outcomes. An invalid rerun is never counted as a pass: one of them makes the
/// whole classification an error naming it.
let classify (outcomes: RerunOutcome list) : Result<Verdict, Unclassifiable> =
  match outcomes with
  | [] -> Result.Error NoReruns
  | _ ->
    let invalid =
      outcomes
      |> List.indexed
      |> List.tryPick (fun (index, outcome) ->
        match outcome with
        | RerunInvalid reason -> Some (InvalidRerun (index + 1, reason))
        | RerunPassed | RerunFailed -> None)
    match invalid with
    | Some unclassifiable -> Result.Error unclassifiable
    | None ->
      let runs = outcomes.Length
      let failed = outcomes |> List.filter (fun o -> o = RerunFailed) |> List.length
      match failed with
      | 0 -> Result.Ok (NotReproduced runs)
      | n when n = runs -> Result.Ok (Deterministic runs)
      | n -> Result.Ok (Flaky (n, runs))

let trustPattern =
  Regex(
    @"TRUST tier=.*? registered=\d+ ran=(?<ran>\d+) passed=(?<passed>\d+) failed=(?<failed>\d+) errored=(?<errored>\d+) ignored=(?<ignored>\d+) verdict=",
    RegexOptions.Compiled)

/// One rerun's outcome from the process's exit code and output. The TRUST line (TestInfrastructure.TrustSignal) is
/// the source of truth: `ran=0` is an invalid rerun, a case that only skipped itself is an invalid rerun, and an exit
/// code that disagrees with a clean report is an invalid rerun.
let outcomeOf (exitCode: int) (lines: string list) : RerunOutcome =
  let trust =
    lines
    |> List.map FailureReport.stripAnsi
    |> List.choose (fun l ->
      let m = trustPattern.Match l
      match m.Success with
      | true -> Some m
      | false -> None)
    |> List.tryLast
  match trust with
  | None -> RerunInvalid (sprintf "no TRUST line in the output (exit %d): the process died or timed out before it reported" exitCode)
  | Some m ->
    let count (name: string) = int m.Groups[name].Value
    match count "ran", count "failed" + count "errored", count "passed" with
    | 0, _, _ -> RerunInvalid "ran 0 tests (NothingRan): the filter matched nothing, so this rerun says nothing about the case"
    | _, failedOrErrored, _ when failedOrErrored > 0 -> RerunFailed
    | _, _, 0 -> RerunInvalid "every case that ran was ignored, so none passed: the case skipped itself"
    | _ when exitCode <> 0 -> RerunInvalid (sprintf "the run reported no failure but exited %d" exitCode)
    | _ -> RerunPassed

/// The verdict in a sentence: its kind, its rate over the reruns, and where to start looking.
let describe (result: Result<Verdict, Unclassifiable>) : string =
  match result with
  | Result.Ok (Deterministic runs) ->
    sprintf "deterministic: failed %d of %d reruns alone. It fails by itself every time, so start at the case." runs runs
  | Result.Ok (Flaky (failed, runs)) ->
    sprintf "flaky: failed %d of %d reruns alone. It fails by itself only some of the time, so start at timing, load or state shared between runs." failed runs
  | Result.Ok (NotReproduced runs) ->
    sprintf "not reproduced: passed %d of %d reruns alone. It did not fail by itself, so start at the machine's load or at what ran beside it." runs runs
  | Result.Error NoReruns -> "unclassified: no rerun was made."
  | Result.Error (InvalidRerun (index, reason)) -> sprintf "unclassified: rerun %d says nothing about the case (%s)." index reason

/// Said beside every verdict, because a verdict that sounds like an excuse becomes one.
let stillRed = "the gate pass stays red: the verdict says which kind of red this is, it does not clear the case"

/// The lines that go under the first failure's report, with the console prefix.
let reportLines (result: Result<Verdict, Unclassifiable>) : string list =
  [ FailureReport.linePrefix + "verdict: " + describe result
    FailureReport.linePrefix + "gate: " + stillRed ]

/// The short form for a table cell: `flaky 1/3`, `deterministic 3/3`, `not reproduced 0/3`, `unclassified`.
let shortName (result: Result<Verdict, Unclassifiable>) : string =
  match result with
  | Result.Ok (Deterministic runs) -> sprintf "deterministic %d/%d" runs runs
  | Result.Ok (Flaky (failed, runs)) -> sprintf "flaky %d/%d" failed runs
  | Result.Ok (NotReproduced runs) -> sprintf "not reproduced 0/%d" runs
  | Result.Error _ -> "unclassified"
