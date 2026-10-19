module SageFs.Tests.FlakeClassificationTests

open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Build
open SageFs.Build.FlakeClassification

/// The TRUST line TrustSignal.record prints, with the counts a rerun can come back with.
let private trustLine (ran: int) (passed: int) (failed: int) (errored: int) (ignored: int) =
  sprintf "TRUST tier=default registered=13482 ran=%d passed=%d failed=%d errored=%d ignored=%d verdict=NarrowedRun (passed, but filtered: not an acceptance signal)" ran passed failed errored ignored

let private isInvalid (outcome: RerunOutcome) =
  match outcome with
  | RerunInvalid _ -> true
  | RerunPassed | RerunFailed -> false

let private outcomeOfBool (failed: bool) =
  match failed with
  | true -> RerunFailed
  | false -> RerunPassed

let private sample : CaseHistory.Entry =
  { Case = "List A.case \"quoted\" b"
    Tier = "default"
    Commit = "1397b55cdeadbeef"
    At = "2026-10-19T00:00:00+00:00"
    Result = Result.Ok (Flaky (1, 3)) }

/// Text that has to survive a JSON line: quotes, newlines, a backslash, unicode, braces and the empty string.
let private trickyText : Gen<string> =
  Gen.elements
    [ ""; "plain"; "List A.case \"quoted\" b"; "two\nlines"; "back\\slash"; "tab\there"; "{\"json\":1}"; "naive café ✓"; "a | b" ]

let private verdictGen : Gen<Result<Verdict, Unclassifiable>> =
  gen {
    let! runs = Gen.choose (1, 9)
    let! failed = Gen.choose (1, runs)
    let! rerun = Gen.choose (1, 9)
    let! reason = trickyText
    return!
      Gen.elements
        [ Result.Ok (Deterministic runs)
          Result.Ok (Flaky (failed, runs + 1))
          Result.Ok (NotReproduced runs)
          Result.Error NoReruns
          Result.Error (InvalidRerun (rerun, reason)) ]
  }

let private entryGen : Gen<CaseHistory.Entry> =
  gen {
    let! case = trickyText
    let! tier = trickyText
    let! commit = trickyText
    let! at = trickyText
    let! result = verdictGen
    return { Case = case; Tier = tier; Commit = commit; At = at; Result = result }
  }

[<Tests>]
let tests =
  testList "Flake classification" [

    testProperty "WHY — a list of valid reruns gets exactly the verdict its failure count names, so the rate is never invented" <|
      fun (failures: bool list) ->
        match failures with
        | [] -> classify [] = Result.Error NoReruns
        | _ ->
          let runs = failures.Length
          let failed = failures |> List.filter id |> List.length
          let expected =
            match failed with
            | 0 -> NotReproduced runs
            | n when n = runs -> Deterministic runs
            | n -> Flaky (n, runs)
          classify (failures |> List.map outcomeOfBool) = Result.Ok expected

    testProperty "WHY — a flaky verdict always has some failures and some passes, never all or none" <|
      fun (failures: bool list) ->
        match classify (failures |> List.map outcomeOfBool) with
        | Result.Ok (Flaky (failed, runs)) -> failed > 0 && failed < runs && runs = failures.Length
        | Result.Ok (Deterministic runs) -> runs = failures.Length && (failures |> List.forall id)
        | Result.Ok (NotReproduced runs) -> runs = failures.Length && (failures |> List.forall not)
        | Result.Error NoReruns -> failures.IsEmpty
        | Result.Error (InvalidRerun _) -> false

    testProperty "WHY — the order the reruns happened in does not change the verdict" <|
      fun (failures: bool list) ->
        classify (failures |> List.map outcomeOfBool) = classify (failures |> List.rev |> List.map outcomeOfBool)

    testProperty "WHY — one rerun that ran nothing makes the case unclassifiable, whatever the others said, and names the first such rerun" <|
      fun (before: bool list) (after: bool list) ->
        let outcomes = (before |> List.map outcomeOfBool) @ [ RerunInvalid "ran 0 tests" ] @ (after |> List.map outcomeOfBool)
        classify outcomes = Result.Error (InvalidRerun (before.Length + 1, "ran 0 tests"))

    testProperty "WHY — a classification never claims a pass it did not see: an invalid rerun is never counted as passed" <|
      fun (outcomes: bool option list) ->
        let mapped =
          outcomes
          |> List.map (fun o ->
            match o with
            | Some failed -> outcomeOfBool failed
            | None -> RerunInvalid "x")
        match classify mapped with
        | Result.Ok (NotReproduced runs) -> runs = mapped.Length && not (mapped |> List.exists isInvalid)
        | Result.Ok _ -> not (mapped |> List.exists isInvalid)
        | Result.Error _ -> true

    testCase "WHY — a rerun that matched nothing (ran=0, exit 0 or 3) is an invalid rerun, not a pass" <| fun _ ->
      for code in [ 0; 3 ] do
        match outcomeOf code [ "noise"; trustLine 0 0 0 0 0 ] with
        | RerunInvalid reason -> reason |> Expect.stringContains "the reason says nothing ran" "ran 0 tests"
        | other -> failtestf "ran=0 with exit %d must be invalid, got %A" code other

    testCase "WHY — a failed or errored case on rerun is a failure, a clean case is a pass" <| fun _ ->
      outcomeOf 1 [ trustLine 1 0 1 0 0 ] |> Expect.equal "failed" RerunFailed
      outcomeOf 1 [ trustLine 1 0 0 1 0 ] |> Expect.equal "errored" RerunFailed
      outcomeOf 0 [ trustLine 1 1 0 0 0 ] |> Expect.equal "passed" RerunPassed

    testCase "WHY — a case that only skipped itself on rerun is not a pass, so a self-skipping case cannot look reproducible-as-green" <| fun _ ->
      match outcomeOf 0 [ trustLine 1 0 0 0 1 ] with
      | RerunInvalid reason -> reason |> Expect.stringContains "the reason says it was ignored" "ignored"
      | other -> failtestf "an ignored-only rerun must be invalid, got %A" other

    testCase "WHY — a process that died before it printed a TRUST line is an invalid rerun, with its exit code in the reason" <| fun _ ->
      match outcomeOf 124 [ "KILLED: no exit after 900s." ] with
      | RerunInvalid reason -> reason |> Expect.stringContains "the exit code is named" "exit 124"
      | other -> failtestf "no TRUST line must be invalid, got %A" other

    testCase "WHY — a clean report with a non-zero exit is an invalid rerun, never a pass" <| fun _ ->
      match outcomeOf 124 [ trustLine 1 1 0 0 0 ] with
      | RerunInvalid _ -> ()
      | other -> failtestf "a disagreeing exit code must be invalid, got %A" other

    testCase "WHY — colour around the TRUST line does not hide it" <| fun _ ->
      outcomeOf 1 [ "\u001b[31m" + trustLine 1 0 1 0 0 + "\u001b[0m" ] |> Expect.equal "the line is still read" RerunFailed

    testCase "WHY — the last TRUST line wins, because a nested run inside the case may print its own" <| fun _ ->
      outcomeOf 1 [ trustLine 1 1 0 0 0; trustLine 1 0 1 0 0 ] |> Expect.equal "the final report decides" RerunFailed

    testCase "WHY — the report names the kind, the rate and that the gate stays red" <| fun _ ->
      let text (result: Result<Verdict, Unclassifiable>) = String.concat "\n" (reportLines result)
      text (Result.Ok (Flaky (1, 3))) |> Expect.stringContains "flaky with its rate" "flaky: failed 1 of 3 reruns alone"
      text (Result.Ok (Deterministic 3)) |> Expect.stringContains "deterministic with its rate" "deterministic: failed 3 of 3 reruns alone"
      text (Result.Ok (NotReproduced 3)) |> Expect.stringContains "not reproduced with its rate" "not reproduced: passed 3 of 3 reruns alone"
      text (Result.Error (InvalidRerun (2, "ran 0 tests"))) |> Expect.stringContains "unclassified carries why" "ran 0 tests"
      for result in [ Result.Ok (Flaky (1, 3)); Result.Ok (NotReproduced 3); Result.Error NoReruns ] do
        text result |> Expect.stringContains "no verdict clears the case" stillRed
      reportLines (Result.Ok (Flaky (1, 3)))
      |> List.forall (fun l -> l.StartsWith FailureReport.linePrefix)
      |> Expect.isTrue "every line carries the console prefix"

    testCase "WHY — the table cell shows the kind and the rate in a few characters" <| fun _ ->
      shortName (Result.Ok (Flaky (1, 3))) |> Expect.equal "flaky" "flaky 1/3"
      shortName (Result.Ok (Deterministic 3)) |> Expect.equal "deterministic" "deterministic 3/3"
      shortName (Result.Ok (NotReproduced 3)) |> Expect.equal "not reproduced" "not reproduced 0/3"
      shortName (Result.Error NoReruns) |> Expect.equal "unclassified" "unclassified"

    testCase "WHY — the rerun count is a named value and a pass classifies at most a handful of cases, so a red is never made slower by its own diagnosis without bound" <| fun _ ->
      defaultReruns |> Expect.equal "three reruns, the number the roast asked for" 3
      (maxCasesClassified > 0) |> Expect.isTrue "at least the first failing case is classified"
  ]

[<Tests>]
let historyTests =
  testList "Case history" [

    testProperty "WHY — every entry survives the file: one line, no newline, and it parses back to the same entry" <|
      Prop.forAll (Arb.fromGen entryGen) (fun entry ->
        let line = CaseHistory.serialize entry
        not (line.Contains "\n") && CaseHistory.parse line = Result.Ok entry)

    testCase "WHY — a line that is not history is an Error naming why, never a made-up entry" <| fun _ ->
      match CaseHistory.parse "{not json" with
      | Result.Error reason -> reason |> Expect.stringContains "it says it is not a history line" "not a history line"
      | Result.Ok entry -> failtestf "expected an error, got %A" entry
      match CaseHistory.parse """{"case":"a","tier":"t","commit":"c","at":"x","kind":"mystery","failed":0,"runs":0,"rerun":0,"reason":""}""" with
      | Result.Error reason -> reason |> Expect.stringContains "an unknown kind is named" "mystery"
      | Result.Ok entry -> failtestf "expected an error, got %A" entry

    testCase "WHY — with no history the report says it is the first time, and another case's lines are not this case's" <| fun _ ->
      CaseHistory.summarize [] "x" |> Expect.stringContains "empty history" "first time"
      CaseHistory.summarize [ { sample with Case = "elsewhere" } ] sample.Case |> Expect.stringContains "someone else's lines" "first time"

    testCase "WHY — the summary lists the commits this case was red on, newest first, with each verdict and rate" <| fun _ ->
      let older = { sample with Commit = "aaaaaaaabbbbbbbb"; Result = Result.Ok (Deterministic 3) }
      let newer = { sample with Commit = "cccccccc00000000"; Result = Result.Ok (NotReproduced 3) }
      CaseHistory.summarize [ older; sample; newer ] sample.Case
      |> Expect.equal "newest first, short shas" "red before 3 time(s), newest first: cccccccc not reproduced 0/3; 1397b55c flaky 1/3; aaaaaaaa deterministic 3/3"

    testCase "WHY — the summary shows a bounded number of lines and says how many it left out" <| fun _ ->
      let many = [ for i in 1 .. CaseHistory.historyShown + 2 -> { sample with Commit = sprintf "%08d" i } ]
      CaseHistory.summarize many sample.Case |> Expect.stringContains "the rest is counted" "(and 2 older)"

    testCase "WHY — the file is append-only and in order, created with its directory, and a line that does not parse is skipped, not fatal" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "case-history-" + System.Guid.NewGuid().ToString "N")
      let path = Path.Combine(dir, "nested", CaseHistory.fileName)
      try
        CaseHistory.read path |> Expect.isEmpty "a file that is not there is no history"
        let second = { sample with Commit = "aaaaaaaabbbbbbbb" }
        CaseHistory.append path sample
        File.AppendAllText(path, "garbage line\n")
        CaseHistory.append path second
        CaseHistory.read path |> Expect.equal "both entries, in the order they were appended, the garbage skipped" [ sample; second ]
        File.ReadAllLines path |> Expect.hasLength "three lines on disk: nothing was rewritten" 3
      finally
        try Directory.Delete(dir, true) with _ -> ()

    testCase "WHY — the report's history line says what the ledger says, and the stub's wording is gone" <| fun _ ->
      let f : FailureReport.Failure = { Name = "A.b"; Outcome = FailureReport.Failed; Message = [ "m" ]; Frames = [] }
      let lines = FailureReport.render "default" "/checkout" "dotnet x" (FailureReport.Ledgered "red before 1 time(s)") f
      lines |> List.exists (fun l -> l.EndsWith "history: red before 1 time(s)")
      |> Expect.isTrue "the ledger's sentence is the history line"
      FailureReport.render "default" "/checkout" "dotnet x" FailureReport.NotRecorded f
      |> String.concat "\n"
      |> fun text -> text.Contains "no per-case ledger yet"
      |> Expect.isFalse "the stub text no longer exists"
  ]
